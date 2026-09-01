using BlazorDLR.Shared.Services;

namespace BlazorDLR.Shared.State;

/// <summary>
/// What map packs are on this device, what the catalogue offers, and what a download in flight is
/// doing (§4.2, §4.4).
/// <para>
/// Scoped, and the same shape as the other device states: read once, held in memory, broadcast on
/// every change. The settings screen renders it; nothing else reads it, but it is a service rather
/// than page fields so a download survives the rider navigating away from the screen that started
/// it - which on a 300 MB archive they certainly will.
/// </para>
/// <para>
/// <strong>One download at a time.</strong> Two large transfers over one phone connection make
/// both slower and neither is what the rider is waiting for; the screen disables the button while
/// one is running rather than queueing.
/// </para>
/// <para>
/// <strong>Two lists, and the difference is where they live.</strong> <see cref="Packs"/> is what
/// the phone holds; <see cref="Offers"/> is what the catalogue publishes. They are keyed the same
/// way - a pack's id is the catalogue's id - which is what lets the screen show one region once,
/// with either a size or a Download beside it.
/// </para>
/// </summary>
public sealed class MapPackState
{
	private readonly IMapPackStore _store;
	private readonly MapPackDownloader _downloader;
	private readonly MapPackCatalogue _catalogue;

	private CancellationTokenSource? _cancelling;
	private bool _loaded;
	private bool _catalogueRead;

	/// <summary>Creates the state over this device's pack store.</summary>
	/// <param name="store">Where packs live.</param>
	/// <param name="downloader">How they get there.</param>
	/// <param name="catalogue">What is on offer to download.</param>
	public MapPackState(IMapPackStore store, MapPackDownloader downloader, MapPackCatalogue catalogue)
	{
		_store = store;
		_downloader = downloader;
		_catalogue = catalogue;
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

	/// <summary>Whether a download is running - the screen's cue to disable the button and offer cancel.</summary>
	public bool IsDownloading => _cancelling is not null;

	/// <summary>The last thing that happened, in the words to put on screen. Null before anything has.</summary>
	public string? Status { get; private set; }

	/// <summary>
	/// What the catalogue offers, by country and then alphabetically within it - the order the
	/// settings screen's two dropdowns read in. Empty until <see cref="LoadCatalogueAsync"/> has
	/// answered, and empty again if it could not - in which case <see cref="CatalogueProblem"/> says
	/// why.
	/// </summary>
	public IReadOnlyList<MapPackOffer> Offers { get; private set; } = [];

	/// <summary>Why the catalogue could not be read, in the words to put on screen, or <c>null</c>.</summary>
	public string? CatalogueProblem { get; private set; }

	/// <summary>Whether a read of the catalogue is in flight - the screen's cue to say so rather than to show an empty list.</summary>
	public bool IsReadingCatalogue { get; private set; }

	/// <summary>Whether a copy of the catalogue has been read and is being held.</summary>
	public bool IsCatalogueRead => _catalogueRead;

	/// <summary>
	/// Reads the catalogue the first time something needs it, and holds that copy for the rest of the
	/// launch.
	/// <para>
	/// <strong>Not called on load.</strong> The list matters only to somebody adding a pack, and it is
	/// a request to a host that is not the API - asking for it when the screen opens would spend a
	/// rider's connection on a list most visits never look at.
	/// </para>
	/// <para>
	/// <strong>And not read twice.</strong> A catalogue is rebuilt when somebody publishes a new
	/// extract, which is weeks apart, so a second read inside one run of the app is a request that
	/// answers the same thing. This state is scoped, which on the phone means it lives as long as the
	/// app does - restarting is what gets a fresh copy, and that is the whole of the policy. There is
	/// no refresh control: one existed, and a button offering to re-fetch a file that changes monthly
	/// invited exactly the pulling-to-refresh it could never reward.
	/// </para>
	/// <para>
	/// <strong>A failed read is not a copy</strong>, so it is not held. Nothing here retries on its
	/// own - the screen decides that, and only asks again when the rider deliberately comes back to
	/// the offline source rather than on every render that happens to show the form.
	/// </para>
	/// </summary>
	/// <param name="cancellationToken">Cancels the read.</param>
	public async Task LoadCatalogueAsync(CancellationToken cancellationToken = default)
	{
		if (_catalogueRead)
			return;

		if (!IsSupported)
		{
			// Both browser hosts (§18.6). Nothing here could be downloaded to anywhere, so the request
			// would be spent to populate a list the screen does not render.
			return;
		}

		if (IsReadingCatalogue)
		{
			// A second call while the first is in flight - two paths into the offline form landing
			// together. The later one rides on the answer the first is already waiting for.
			return;
		}

		IsReadingCatalogue = true;
		CatalogueProblem = null;
		Changed?.Invoke();

		try
		{
			MapPackCatalogueResult result = await _catalogue.ReadAsync(cancellationToken);

			// Held only when there is something to hold. A failure leaves _catalogueRead false so the
			// next deliberate visit to the offline form asks again - a rider who opened this in a
			// tunnel would otherwise have no route to the list but restarting the app.
			if (result.Problem is null)
			{
				Offers = result.Packs;
				_catalogueRead = true;
			}

			CatalogueProblem = result.Problem;
		}
		finally
		{
			IsReadingCatalogue = false;
			Changed?.Invoke();
		}
	}

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

	/// <summary>Re-reads the device - after a download, a delete, or anything else that moved a file.</summary>
	/// <param name="cancellationToken">Cancels the read.</param>
	public async Task RefreshAsync(CancellationToken cancellationToken = default)
	{
		Packs = await _store.ListAsync(cancellationToken);
		Changed?.Invoke();
	}

	/// <summary>
	/// Downloads a pack the catalogue offers. It is filed under the catalogue's id rather than
	/// anything the rider chose - which is the point of having a catalogue: the id is what
	/// <c>MapSource</c> stores, so two devices holding New South Wales agree on what it is called.
	/// </summary>
	/// <param name="offer">Which pack from the catalogue.</param>
	public Task DownloadAsync(MapPackOffer offer) => DownloadAsync(offer.Id, offer.Url);

	/// <summary>Whether this device already holds a pack, and what it knows about it.</summary>
	/// <param name="packId">Which pack - a catalogue id.</param>
	public StoredMapPack? Stored(string packId) =>
		Packs.FirstOrDefault(pack => string.Equals(pack.PackId, packId, StringComparison.Ordinal));

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
			return;

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
