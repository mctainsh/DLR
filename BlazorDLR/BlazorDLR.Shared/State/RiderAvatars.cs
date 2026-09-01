using System.Net.Http.Headers;
using BlazorDLR.Shared.Services;
using DLR.Core.Contracts.Identity;

namespace BlazorDLR.Shared.State;

/// <summary>
/// Username to profile photograph, for every screen that draws somebody's name (§7.3).
/// <para>
/// <strong>This exists because the alternative does not scale.</strong> A ride thread, a member
/// list and a browse page each render dozens of names at once, and the obvious implementation -
/// each little avatar fetching its own - is forty round trips to open a screen, then forty more
/// when the list re-renders. Worse, the same photograph is downloaded once per row it appears in.
/// </para>
/// <para>
/// So this does three things and nothing else:
/// </para>
/// <list type="number">
///   <item><description>
///     <strong>Batches.</strong> Names asked for within <see cref="BatchWindowMs"/> of each other
///     go up as one <c>GET /users/avatars</c>. That window is what turns a render pass into a
///     single request; it is short enough that nobody sees it and long enough to catch a whole
///     list, whose rows resolve as separate async continuations rather than one burst.
///   </description></item>
///   <item><description>
///     <strong>Remembers, including the negative answers.</strong> "This rider has no photograph"
///     is the common case and is worth caching exactly as hard as a photo id - without it, every
///     re-render asks the server about the same names again.
///   </description></item>
///   <item><description>
///     <strong>Downloads each photograph once.</strong> The thumbnail endpoint is behind the
///     bearer token, so an <c>&lt;img src&gt;</c> cannot reach it (see <c>AuthedImage</c>); the
///     bytes are fetched here and handed out as a <c>data:</c> URL that any number of rows can
///     share.
///   </description></item>
/// </list>
/// <para>
/// Scoped, like <see cref="AuthState"/>: one cache per browser tab or per app session, emptied
/// with it. Nothing here is persisted - a stale avatar surviving a restart would be the one bug
/// worth avoiding, and the cost of not persisting is one small request at startup.
/// </para>
/// </summary>
public sealed class RiderAvatars : IDisposable
{
	/// <summary>
	/// How long a name waits for company before its lookup is sent.
	/// <para>
	/// Blazor resolves a list's rows as separate continuations rather than in one synchronous
	/// burst, so a zero-length window would send one request per row and defeat the point. Forty
	/// milliseconds is below the threshold at which anybody perceives a delay and comfortably
	/// wider than the gap between two rows of the same list.
	/// </para>
	/// </summary>
	public const int BatchWindowMs = 40;

	/// <summary>
	/// The most photographs held as decoded <c>data:</c> URLs at once.
	/// <para>
	/// A thumbnail is a few tens of kilobytes and base64 adds a third, so this is single-figure
	/// megabytes at the worst - a bound rather than a budget. Past it the cache is emptied whole
	/// rather than evicted cleverly: an LRU here would be machinery in service of a case (a
	/// session that scrolls past two hundred distinct riders) that costs one refetch when it is
	/// wrong.
	/// </para>
	/// </summary>
	public const int MaxCachedImages = 200;

	private readonly IApiClient _api;
	private readonly HttpClient _http;
	private readonly TimeProvider _clock;

	/// <summary>
	/// Guards every dictionary below. The batch timer fires on a pool thread while components read
	/// on the renderer's synchronisation context, so "Blazor is single-threaded" is not true of
	/// this type in particular.
	/// </summary>
	private readonly Lock _gate = new();

	/// <summary>Answered names, including the ones whose answer is "no photograph".</summary>
	private readonly Dictionary<string, Guid?> _photoIdByName = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Names waiting for the next batch, each with the callers waiting on it.</summary>
	private readonly Dictionary<string, TaskCompletionSource<Guid?>> _waiting = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Downloaded thumbnails as <c>data:</c> URLs, shared by every row that draws one.</summary>
	private readonly Dictionary<Guid, string> _imageByPhotoId = [];

	/// <summary>In-flight downloads, so twenty rows of one rider produce one request.</summary>
	private readonly Dictionary<Guid, Task<string?>> _downloading = [];

	private ITimer? _batch;
	private bool _disposed;

	/// <summary>
	/// Raised when a cached answer stops being true - today, when the signed-in rider changes
	/// their own photograph. Rendered avatars subscribe so the change is visible on the screen
	/// that made it without a reload.
	/// </summary>
	public event Action? Changed;

	/// <summary>Builds the cache.</summary>
	/// <param name="api">Where the batch lookup goes.</param>
	/// <param name="http">The bearer-decorated client the thumbnails are fetched with.</param>
	/// <param name="clock">The project's clock (§10.4) - this is what arms the batch window.</param>
	public RiderAvatars(IApiClient api, HttpClient http, TimeProvider clock)
	{
		_api = api;
		_http = http;
		_clock = clock;
	}

	/// <summary>
	/// The photograph for one rider, ready to put in an <c>&lt;img src&gt;</c>, or null when they
	/// have not added one.
	/// </summary>
	/// <param name="userName">Whose. Matched without regard to case (§7.2).</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <remarks>
	/// Never throws. An avatar that could not be resolved is drawn as no avatar, which is the same
	/// thing the screen shows for the riders who have not set one - a name with a broken image
	/// beside it would be worse than a name.
	/// </remarks>
	public async Task<string?> ImageUrlAsync(string? userName, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(userName))
		{
			return null;
		}

		try
		{
			if (await PhotoIdAsync(userName.Trim(), cancellationToken) is not { } photoId)
			{
				return null;
			}

			return await ImageAsync(photoId, cancellationToken);
		}
		catch (OperationCanceledException)
		{
			return null;
		}
		catch (Exception)
		{
			// Deliberately swallowed, and deliberately not cached as a negative: the next render
			// asks again, so a photograph missed because the connection dropped comes back when it
			// returns rather than staying missing for the session.
			return null;
		}
	}

	/// <summary>
	/// Forgets what is known about one rider, so the next render asks again.
	/// </summary>
	/// <param name="userName">Whose. Null or blank forgets nothing.</param>
	/// <remarks>
	/// Called after the signed-in rider changes their own photograph. Only the name is forgotten,
	/// not the downloaded images - the bytes behind a photo id never change, because ingest gives
	/// every upload a new id (§16.4).
	/// </remarks>
	public void Forget(string? userName)
	{
		if (string.IsNullOrWhiteSpace(userName))
		{
			return;
		}

		lock (_gate)
		{
			_photoIdByName.Remove(userName.Trim());
		}

		Changed?.Invoke();
	}

	/// <summary>Empties the cache entirely. For sign-out, where nothing cached is the caller's any more.</summary>
	public void Clear()
	{
		lock (_gate)
		{
			_photoIdByName.Clear();
			_imageByPhotoId.Clear();
		}

		Changed?.Invoke();
	}

	/// <summary>
	/// Resolves one name to a photo id, joining whatever batch is being assembled.
	/// </summary>
	private Task<Guid?> PhotoIdAsync(string userName, CancellationToken cancellationToken)
	{
		TaskCompletionSource<Guid?> waiter;

		lock (_gate)
		{
			if (_photoIdByName.TryGetValue(userName, out Guid? known))
			{
				return Task.FromResult(known);
			}

			if (!_waiting.TryGetValue(userName, out TaskCompletionSource<Guid?>? existing))
			{
				// RunContinuationsAsynchronously, so completing a hundred waiters from the timer
				// callback does not run a hundred component re-renders on the timer's thread.
				existing = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
				_waiting[userName] = existing;
			}

			waiter = existing;

			// One-shot, re-armed rather than restarted: the window opens when the first unanswered
			// name arrives and closes once, which is what makes a whole list one request instead of
			// a window that keeps being pushed out by each new row.
			_batch ??= _clock.CreateTimer(
				_ => _ = FlushAsync(),
				null,
				TimeSpan.FromMilliseconds(BatchWindowMs),
				Timeout.InfiniteTimeSpan);
		}

		return waiter.Task.WaitAsync(cancellationToken);
	}

	/// <summary>
	/// Sends one lookup for everything waiting and answers every waiter - including on failure,
	/// where the answer is "no photograph". A waiter left hanging is a row that never renders.
	/// </summary>
	private async Task FlushAsync()
	{
		List<string> names;

		lock (_gate)
		{
			_batch?.Dispose();
			_batch = null;

			if (_waiting.Count == 0)
			{
				return;
			}

			names = [.. _waiting.Keys.Take(AvatarLookup.MaxNames)];
		}

		IReadOnlyList<RiderAvatarDto> answers;

		try
		{
			answers = await _api.GetRiderAvatarsAsync(names);
		}
		catch (Exception)
		{
			// Not cached. See ImageUrlAsync - a failed lookup must not become a permanent "no
			// photograph" for the rest of the session.
			Answer(names, answers: [], remember: false);

			return;
		}

		Answer(names, answers, remember: true);

		Changed?.Invoke();
	}

	/// <summary>Completes the waiters for one batch, and rearms if more names arrived while it was in flight.</summary>
	private void Answer(List<string> names, IReadOnlyList<RiderAvatarDto> answers, bool remember)
	{
		Dictionary<string, Guid?> byName = new(StringComparer.OrdinalIgnoreCase);

		foreach (RiderAvatarDto answer in answers)
		{
			byName[answer.UserName] = answer.PhotoId;
		}

		List<(TaskCompletionSource<Guid?> Waiter, Guid? PhotoId)> toComplete = [];

		lock (_gate)
		{
			foreach (string name in names)
			{
				// A name the server did not answer for is treated as "no photograph". The endpoint
				// answers for every name it is asked about, so this is the shape of a client and a
				// server that disagree - and a missing avatar is the right way to be wrong.
				Guid? photoId = byName.TryGetValue(name, out Guid? found) ? found : null;

				if (remember)
				{
					_photoIdByName[name] = photoId;
				}

				if (_waiting.Remove(name, out TaskCompletionSource<Guid?>? waiter))
				{
					toComplete.Add((waiter, photoId));
				}
			}

			// Names that arrived while the request was in flight. They have no timer of their own,
			// because one was already armed when they joined - so without this they wait forever.
			if (_waiting.Count > 0 && !_disposed)
			{
				_batch ??= _clock.CreateTimer(
					_ => _ = FlushAsync(),
					null,
					TimeSpan.FromMilliseconds(BatchWindowMs),
					Timeout.InfiniteTimeSpan);
			}
		}

		// Outside the lock: completing a waiter runs its continuations, and a component's
		// re-render must never happen with this type's lock held.
		foreach ((TaskCompletionSource<Guid?> waiter, Guid? photoId) in toComplete)
		{
			waiter.TrySetResult(photoId);
		}
	}

	/// <summary>
	/// Fetches one thumbnail and keeps it as a <c>data:</c> URL, or hands back the one already
	/// held. Concurrent callers for the same photograph share a single download.
	/// </summary>
	private Task<string?> ImageAsync(Guid photoId, CancellationToken cancellationToken)
	{
		Task<string?> download;

		lock (_gate)
		{
			if (_imageByPhotoId.TryGetValue(photoId, out string? cached))
			{
				return Task.FromResult<string?>(cached);
			}

			if (!_downloading.TryGetValue(photoId, out Task<string?>? existing))
			{
				existing = DownloadAsync(photoId);
				_downloading[photoId] = existing;
			}

			download = existing;
		}

		// The shared download is never cancelled by one caller - a row that went away must not
		// abort the fetch the other nineteen rows are waiting on. Only this caller's wait ends.
		return download.WaitAsync(cancellationToken);
	}

	private async Task<string?> DownloadAsync(Guid photoId)
	{
		try
		{
			// The thumbnail, not the full image: this is drawn at about two lines of text, and the
			// stored image is up to 2048 px on its long edge (§16.4).
			using HttpResponseMessage response = await _http.GetAsync($"/api/v1/photos/{photoId}/thumbnail");

			if (!response.IsSuccessStatusCode)
			{
				return null;
			}

			byte[] bytes = await response.Content.ReadAsByteArrayAsync();
			MediaTypeHeaderValue? contentType = response.Content.Headers.ContentType;
			string dataUrl = $"data:{contentType?.MediaType ?? "image/jpeg"};base64,{Convert.ToBase64String(bytes)}";

			lock (_gate)
			{
				// Emptied whole rather than evicted - see MaxCachedImages.
				if (_imageByPhotoId.Count >= MaxCachedImages)
				{
					_imageByPhotoId.Clear();
				}

				_imageByPhotoId[photoId] = dataUrl;
			}

			return dataUrl;
		}
		catch (Exception)
		{
			return null;
		}
		finally
		{
			lock (_gate)
			{
				_downloading.Remove(photoId);
			}
		}
	}

	/// <inheritdoc />
	public void Dispose()
	{
		List<TaskCompletionSource<Guid?>> stranded;

		lock (_gate)
		{
			_disposed = true;
			_batch?.Dispose();
			_batch = null;

			stranded = [.. _waiting.Values];
			_waiting.Clear();
		}

		// Answered rather than abandoned. A waiter left hanging on a disposed scope is a component
		// awaiting a task that can never complete, which on a page being torn down is a leak.
		foreach (TaskCompletionSource<Guid?> waiter in stranded)
		{
			waiter.TrySetResult(null);
		}
	}
}
