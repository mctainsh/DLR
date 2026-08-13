using BlazorDLR.Shared.Services;

namespace BlazorDLR.Shared.State;

/// <summary>
/// What map packs are on this device and what a download in flight is doing (§4.4).
/// <para>
/// Scoped, and the same shape as the other device states: read once, held in memory, broadcast on
/// every change. The settings screen renders it; nothing else reads it, but it is a service rather
/// than page fields so a download survives the rider navigating away from the screen that started
/// it — which on a 300 MB archive they certainly will.
/// </para>
/// <para>
/// <strong>One download at a time.</strong> Two large transfers over one phone connection make
/// both slower and neither is what the rider is waiting for; the screen disables the button while
/// one is running rather than queueing.
/// </para>
/// </summary>
public sealed class MapPackState
{
	private readonly IMapPackStore _store;
	private readonly MapPackDownloader _downloader;

	private CancellationTokenSource? _cancelling;
	private bool _loaded;

	/// <summary>Creates the state over this device's pack store.</summary>
	/// <param name="store">Where packs live.</param>
	/// <param name="downloader">How they get there.</param>
	public MapPackState(IMapPackStore store, MapPackDownloader downloader)
	{
		_store = store;
		_downloader = downloader;
	}

	/// <summary>Fired after the first <see cref="LoadAsync"/>, on every progress report, and on every change.</summary>
	public event Action? Changed;

	/// <summary>Whether this host can hold packs at all (§18.6).</summary>
	public bool IsSupported => _store.IsSupported;

	/// <summary>Whether the device has been read yet.</summary>
	public bool IsLoaded => _loaded;

	/// <summary>The packs on this device, newest-listed-first order not guaranteed.</summary>
	public IReadOnlyList<StoredMapPack> Packs { get; private set; } = [];

	/// <summary>The download in flight, or <c>null</c> when none is.</summary>
	public MapPackProgress? Progress { get; private set; }

	/// <summary>Whether a download is running — the screen's cue to disable the button and offer cancel.</summary>
	public bool IsDownloading => _cancelling is not null;

	/// <summary>The last thing that happened, in the words to put on screen. Null before anything has.</summary>
	public string? Status { get; private set; }

	/// <summary>
	/// Reads what is on the device. Idempotent, so the settings screen calling it on first render
	/// costs nothing when something else already has.
	/// </summary>
	/// <param name="cancellationToken">Cancels the read.</param>
	public async Task LoadAsync(CancellationToken cancellationToken = default)
	{
		if (_loaded)
		{
			return;
		}

		_loaded = true;
		await RefreshAsync(cancellationToken);
	}

	/// <summary>Re-reads the device — after a download, a delete, or anything else that moved a file.</summary>
	/// <param name="cancellationToken">Cancels the read.</param>
	public async Task RefreshAsync(CancellationToken cancellationToken = default)
	{
		Packs = await _store.ListAsync(cancellationToken);
		Changed?.Invoke();
	}

	/// <summary>
	/// Downloads an archive from <paramref name="url"/> and calls it <paramref name="packId"/> on
	/// this device, resuming a previous attempt if there is one.
	/// <para>
	/// Does not throw. Everything a rider needs to know about a failure comes back through
	/// <see cref="Status"/>, because the alternative is a screen that has to catch exceptions to
	/// explain that a link was wrong.
	/// </para>
	/// </summary>
	/// <param name="packId">What to call it here.</param>
	/// <param name="url">Where to fetch it from.</param>
	public async Task DownloadAsync(string packId, Uri url)
	{
		if (IsDownloading)
		{
			return;
		}

		using CancellationTokenSource cancelling = new();
		_cancelling = cancelling;

		Progress = new MapPackProgress(packId, 0, null);
		Status = null;
		Changed?.Invoke();

		try
		{
			MapPackDownloadResult result = await _downloader.DownloadAsync(
				packId,
				url,
				new Progress<MapPackProgress>(reported =>
				{
					Progress = reported;
					Changed?.Invoke();
				}),
				cancelling.Token);

			Status = result.Message;
		}
		finally
		{
			_cancelling = null;
			Progress = null;

			// After the download either way: a cancelled one leaves a partial that changes nothing
			// in the list, and a failed one may still have removed a version.
			await RefreshAsync(CancellationToken.None);
		}
	}

	/// <summary>Stops a download in flight. The partial stays, so asking again carries on from there.</summary>
	public void Cancel() => _cancelling?.Cancel();

	/// <summary>
	/// Removes a pack from this device. Usually the largest single thing this app has put on the
	/// phone, so the settings screen offers it prominently.
	/// </summary>
	/// <param name="packId">Which pack.</param>
	public async Task DeleteAsync(string packId)
	{
		await _store.DeleteAsync(packId);
		Status = $"Removed {packId}.";
		await RefreshAsync();
	}
}
