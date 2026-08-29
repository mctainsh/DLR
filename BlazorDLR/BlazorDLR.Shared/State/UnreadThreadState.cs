using System.Globalization;
using BlazorDLR.Shared.Services;
using DLR.Core.Contracts.Comments;

namespace BlazorDLR.Shared.State;

/// <summary>
/// How many posts have landed in an adventure's thread since the rider last had it open — the
/// number in the red bubble on the rail's speech bubbles (§17.6, §18.6).
/// <para>
/// <strong>Counted on the device, not asked of the server.</strong> There is no read marker in the
/// schema and no endpoint that answers "how many since"; what there is, is the post itself arriving
/// on the hub the ride screens already hold (§5.3). So this counts what it is told about, which is
/// the same reach <see cref="CommentNotifier"/> has and the same trade §17.6 already makes: it
/// works while the app is running, which during a ride is when the thread is worth a badge.
/// </para>
/// <para>
/// <strong>The count survives a restart</strong>, through <see cref="IDeviceSettings"/> like every
/// other device-local value. A badge that reset to nothing every launch would quietly lose the two
/// posts a rider was about to read — and a phone the OS reclaimed mid-ride is exactly the case the
/// rail exists for (see <see cref="CurrentRideState"/>).
/// </para>
/// <para>
/// A rider's own post is not unread, and neither is one that lands while the thread is on screen.
/// Those are the only two questions asked; everything else that arrives is counted.
/// </para>
/// </summary>
public sealed class UnreadThreadState : IDisposable
{
	/// <summary>
	/// The <see cref="IDeviceSettings"/> key. Multi-field, so the value carries the leading
	/// <c>1|</c> version marker that <see cref="CurrentRideState.StorageKey"/>'s bare guid does not.
	/// </summary>
	public const string StorageKey = "dlr.unread-threads";

	/// <summary>
	/// How many adventures are counted at once. A rider is live in one ride and occasionally two
	/// (§5.7); the cap is what stops a year of them accumulating in a device store nothing sweeps.
	/// The adventure nobody has posted to for longest falls off the end.
	/// </summary>
	public const int MaxTracked = 8;

	/// <summary>
	/// Where counting stops. The badge says "99+" long before this, so the rest only keeps the
	/// stored string to a fixed width.
	/// </summary>
	public const int MaxCount = 999;

	private readonly IRideHubClient _hub;
	private readonly AuthState _auth;
	private readonly IDeviceSettings _settings;

	/// <summary>Most recently posted-to first, so the cap drops the quietest adventure.</summary>
	private readonly List<Entry> _entries = [];

	private Guid? _open;
	private bool _loaded;
	private bool _disposed;

	/// <summary>Starts counting posts as they arrive.</summary>
	/// <param name="hub">Where posts arrive (§5.3).</param>
	/// <param name="auth">Who is reading — which is how a rider's own post is recognised.</param>
	/// <param name="settings">Where the counts are persisted, so they outlive the process.</param>
	public UnreadThreadState(IRideHubClient hub, AuthState auth, IDeviceSettings settings)
	{
		_hub = hub;
		_auth = auth;
		_settings = settings;

		_hub.CommentPosted += OnCommentPosted;
	}

	/// <summary>Fired whenever a count moves, so the rail can redraw its badge.</summary>
	public event Action? Changed;

	/// <summary>Whether the device store has been read yet.</summary>
	public bool IsLoaded => _loaded;

	/// <summary>
	/// How many unread posts one adventure is holding — zero for a ride nothing has arrived for,
	/// and zero for <c>null</c>, which is a device that has not opened a ride at all.
	/// </summary>
	/// <param name="rideId">Which adventure, or null.</param>
	public int CountFor(Guid? rideId) =>
		rideId is { } id && Find(id) is { } entry ? entry.Count : 0;

	/// <summary>
	/// Reads the persisted counts. Idempotent — the rail calls it on first render and nobody else
	/// has to coordinate with that.
	/// <para>
	/// Callers must run this <em>after</em> first render on the web: the browser store is reached
	/// through JS interop, which does not exist during the prerender pass.
	/// </para>
	/// </summary>
	/// <param name="cancellationToken">Cancels the read.</param>
	public async Task LoadAsync(CancellationToken cancellationToken = default)
	{
		if (_loaded)
			return;

		// Set before the read so a rail that renders twice does not start two round trips.
		_loaded = true;

		// Posts that landed before the rail got round to reading the store. Their own write was
		// held back — see OnCommentPosted — because writing what is in memory before knowing what
		// is on the device is how the count stored yesterday gets overwritten by the one post that
		// arrived while the app was starting.
		bool pending = _entries.Count > 0;

		List<Entry> stored = Decode(await _settings.GetAsync(StorageKey, cancellationToken));

		// Backwards, and added to rather than replacing: Bump moves what it touches to the front,
		// and a post that landed in the first second of a launch is as unread as yesterday's.
		for (int i = stored.Count - 1; i >= 0; i--)
			Bump(stored[i].RideId, stored[i].Count);

		if (stored.Count > 0)
			Changed?.Invoke();

		if (pending)
			await SaveAsync(cancellationToken);
	}

	/// <summary>
	/// The rider is now looking at this adventure's thread: the count goes to nothing, and posts
	/// landing while it is on screen do not start it again.
	/// </summary>
	/// <param name="rideId">The thread now on screen.</param>
	/// <param name="cancellationToken">Cancels the read and the write.</param>
	public async Task OpenedAsync(Guid rideId, CancellationToken cancellationToken = default)
	{
		// Before the clear, not after: a thread opened straight from a tapped notification runs
		// ahead of the rail's own load, and reading afterwards would put the stored count back.
		await LoadAsync(cancellationToken);

		_open = rideId;

		if (Find(rideId) is not { Count: > 0 } entry)
			return;

		entry.Count = 0;

		Changed?.Invoke();
		await SaveAsync(cancellationToken);
	}

	/// <summary>
	/// The thread has gone off screen, so its posts count again. Does nothing for a thread that was
	/// not the open one — the ride page and the thread page tear down in either order.
	/// </summary>
	/// <param name="rideId">The thread that was on screen.</param>
	public void Closed(Guid rideId)
	{
		if (_open == rideId)
			_open = null;
	}

	private void OnCommentPosted(CommentDto comment)
	{
		// A shared route's thread (§6.2) is not on the rail, so nothing counts it.
		if (_disposed || comment.GroupRideId is not { } rideId)
			return;

		// Every post a rider makes comes straight back down the hub they published it on, so
		// without this the badge would count the rider's own conversation with themselves.
		if (_auth.UserId is { } me && comment.AuthorId == me)
			return;

		if (_open == rideId)
			return;

		Bump(rideId, 1);
		Changed?.Invoke();

		// Only once the device has been read: a write before that would replace counts this
		// instance has not seen yet. LoadAsync persists the merge instead.
		if (_loaded)
			Forget(SaveAsync(CancellationToken.None).AsTask());
	}

	private Entry? Find(Guid rideId) => _entries.Find(entry => entry.RideId == rideId);

	/// <summary>Adds to an adventure's count and moves it to the front, dropping the oldest when full.</summary>
	private void Bump(Guid rideId, int by)
	{
		if (Find(rideId) is { } existing)
		{
			existing.Count = Math.Min(existing.Count + by, MaxCount);
			_entries.Remove(existing);
			_entries.Insert(0, existing);
			return;
		}

		_entries.Insert(0, new Entry(rideId) { Count = Math.Min(by, MaxCount) });

		if (_entries.Count > MaxTracked)
			_entries.RemoveRange(MaxTracked, _entries.Count - MaxTracked);
	}

	/// <summary>
	/// Writes the counts, or removes the key when there are none — the same thing
	/// <see cref="CurrentRideState.ClearAsync"/> does, and for the same reason.
	/// </summary>
	private ValueTask SaveAsync(CancellationToken cancellationToken) =>
		_entries.Exists(entry => entry.Count > 0)
			? _settings.SetAsync(StorageKey, Encode(), cancellationToken)
			: _settings.RemoveAsync(StorageKey, cancellationToken);

	private string Encode() =>
		"1|" + string.Join(',', _entries
			.Where(entry => entry.Count > 0)
			.Select(entry => string.Create(
				CultureInfo.InvariantCulture,
				$"{entry.RideId:N}:{entry.Count}")));

	/// <summary>
	/// Reads back what <see cref="Encode"/> wrote. Anything else — a value from a format this
	/// version does not know, a half-written string — reads as "nothing unread", which is what a
	/// device with nothing stored answers and is never worse than wrong.
	/// </summary>
	private static List<Entry> Decode(string? stored)
	{
		List<Entry> entries = [];

		if (stored is null || !stored.StartsWith("1|", StringComparison.Ordinal))
			return entries;

		foreach (string part in stored[2..].Split(',', StringSplitOptions.RemoveEmptyEntries))
		{
			string[] fields = part.Split(':');

			if (fields.Length == 2
				&& Guid.TryParseExact(fields[0], "N", out Guid rideId)
				&& int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int count)
				&& rideId != Guid.Empty
				&& count > 0)
			{
				entries.Add(new Entry(rideId) { Count = Math.Min(count, MaxCount) });
			}
		}

		return entries;
	}

	/// <summary>
	/// Abandons a device write nobody is waiting for, without leaving an unobserved exception
	/// behind. The caller is a hub callback, which has nowhere to report one — and a count that
	/// failed to persist must never be a reason a post fails to arrive in the thread.
	/// </summary>
	private static void Forget(Task task) =>
		task.ContinueWith(
			static faulted => _ = faulted.Exception,
			CancellationToken.None,
			TaskContinuationOptions.OnlyOnFaulted,
			TaskScheduler.Default);

	/// <inheritdoc />
	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;
		_hub.CommentPosted -= OnCommentPosted;
	}

	private sealed class Entry(Guid rideId)
	{
		public Guid RideId { get; } = rideId;

		public int Count { get; set; }
	}
}
