using System.Globalization;
using BlazorDLR.Shared.Services;
using DLR.Core.Tracks;

namespace BlazorDLR.Shared.State;

/// <summary>A map pack covering ground the rider is looking at, and what taking it would cost.</summary>
/// <param name="Offer">The pack, as the catalogue published it.</param>
/// <param name="IsOnDevice">
/// Whether the archive is already here. A pack the phone holds but is not drawing with is the
/// better offer whenever both cover the ground - a tap rather than 300 MB - which is why this is
/// carried rather than filtered out.
/// </param>
public sealed record OfflineMapSuggestion(MapPackOffer Offer, bool IsOnDevice)
{
	/// <summary>What accepting this costs, in the words the button uses.</summary>
	public string ActionText => IsOnDevice
		? "Use this map"
		: $"Download {MapPackDownloader.Describe(Offer.SizeBytes)}";
}

/// <summary>
/// Whether to offer this device an offline map, which one, and what happens when it says yes
/// (§4.2, §4.5).
/// <para>
/// <strong>The dismissal set is the whole of the "do not pester" rule.</strong> Every surface that
/// makes the offer asks here rather than deciding for itself, so declining a pack silences all of
/// them at once - the only gate a rider can reason about.
/// </para>
/// <para>
/// Nothing is offered where nothing could be stored - both browser hosts (§18.6). That is the
/// first test in every method, so no catalogue is fetched there either.
/// </para>
/// </summary>
public sealed class OfflineMapOfferState
{
	/// <summary>
	/// The <see cref="IDeviceSettings"/> key, holding both what this device has declined and where
	/// its map last went blank. One key rather than one per pack, for the reason
	/// <see cref="ConsentAskedState.StorageKey"/> gives.
	/// </summary>
	public const string StorageKey = "dlr.map-offer";

	/// <summary>
	/// How many declined packs are remembered, most recent first. A rider who rides back into a
	/// region they turned down thirty-two regions ago being asked once more is the right way for
	/// this to fail.
	/// </summary>
	public const int MaxDeclined = 32;

	/// <summary>
	/// How far the map must move before a point counts as different ground, in degrees - roughly a
	/// kilometre, well inside any pack this would suggest. It bounds both the answer cache and the
	/// dead-zone recorder, so a rider panning about gets one answer and one write rather than one
	/// of each per frame.
	/// </summary>
	private const double SameGroundDeg = 0.01;

	/// <summary>
	/// How long to leave a catalogue that would not answer alone. Without it, a phone in the dead
	/// zone this feature exists for would re-issue the request every time the map moved. On the
	/// monotonic tick count, like <c>RideMap</c>'s own retry cooldown - this measures an interval
	/// rather than naming an instant, and no clock change may shorten or extend it.
	/// </summary>
	private const long CatalogueRetryAfterMs = 5 * 60 * 1000;

	private readonly IDeviceSettings _settings;
	private readonly MapPackState _packs;
	private readonly MapSourceState _sources;

	private readonly List<string> _declined = [];

	private (double Latitude, double Longitude)? _missed;
	private bool _loaded;
	private Task? _reading;

	/// <summary>The last point answered and what it answered, so a moving map re-asks only when it has to.</summary>
	private (double Latitude, double Longitude, OfflineMapSuggestion? Answer)? _cached;

	/// <summary>When the catalogue last refused, on the monotonic tick count, or zero for never.</summary>
	private long _catalogueFailedTicks;

	/// <summary>Creates the state over this device's store, its packs and its tile-source setting.</summary>
	/// <param name="settings">Where the declines and the last miss are remembered between launches.</param>
	/// <param name="packs">What this device holds, and what the catalogue offers.</param>
	/// <param name="sources">Which tiles the maps draw - what <see cref="TakeAsync"/> changes.</param>
	public OfflineMapOfferState(IDeviceSettings settings, MapPackState packs, MapSourceState sources)
	{
		_settings = settings;
		_packs = packs;
		_sources = sources;
	}

	/// <summary>Fired when a decline or a taken offer changes what the surfaces should draw.</summary>
	public event Action? Changed;

	/// <summary>Whether this host could hold a pack at all (§18.6). False silences everything here.</summary>
	public bool IsSupported => _sources.CanUseOffline;

	/// <summary>Where this device's map last drew nothing, or <c>null</c> when it never has.</summary>
	public (double Latitude, double Longitude)? MissedPoint => _missed;

	/// <summary>
	/// The deferred offer, once <see cref="PrimeMissedAsync"/> has looked for one: what would have
	/// helped where the map last went blank. Null on most launches.
	/// </summary>
	public OfflineMapSuggestion? Missed { get; private set; }

	/// <summary>
	/// Reads the device store, once per app. A second caller joins the first read rather than being
	/// told it has already happened - <see cref="ConsentAskedState.LoadAsync"/>'s reasoning: a
	/// surface must not decide nothing has been declined while the store's answer is in flight.
	/// </summary>
	/// <param name="cancellationToken">Abandons the read.</param>
	public Task LoadAsync(CancellationToken cancellationToken = default)
	{
		if (_loaded)
			return _reading ?? Task.CompletedTask;

		_loaded = true;

		return _reading = ReadAsync(cancellationToken);
	}

	/// <summary>Whether this device has already turned a pack down.</summary>
	/// <param name="packId">A catalogue id.</param>
	public bool IsDeclined(string packId) => _declined.Contains(packId, StringComparer.Ordinal);

	/// <summary>Records that the rider said no, which silences every surface that would have named it.</summary>
	/// <param name="packId">A catalogue id.</param>
	/// <param name="cancellationToken">Abandons the write.</param>
	public async ValueTask DeclineAsync(string packId, CancellationToken cancellationToken = default)
	{
		_declined.Remove(packId);
		_declined.Insert(0, packId);

		if (_declined.Count > MaxDeclined)
			_declined.RemoveRange(MaxDeclined, _declined.Count - MaxDeclined);

		await SaveAsync(cancellationToken);
		Invalidate();
	}

	/// <summary>
	/// The best pack for a point, or <c>null</c> when there is nothing worth saying.
	/// <para>
	/// Safe to call per viewport event: the answer is cached against the ground it was given for,
	/// so a map that is panning re-asks only once it leaves that ground.
	/// </para>
	/// </summary>
	/// <param name="latitudeDeg">Where the map is looking.</param>
	/// <param name="longitudeDeg">Where the map is looking.</param>
	/// <param name="cancellationToken">Abandons the catalogue read.</param>
	public async Task<OfflineMapSuggestion?> SuggestAsync(
		double latitudeDeg,
		double longitudeDeg,
		CancellationToken cancellationToken = default)
	{
		if (Answered(latitudeDeg, longitudeDeg) is { } cached)
			return cached.Answer;

		if (await CandidatesAsync(cancellationToken) is not { } offers)
			return null;

		OfflineMapSuggestion? answer = Best(offers.Covering(latitudeDeg, longitudeDeg));

		_cached = (latitudeDeg, longitudeDeg, answer);

		return answer;
	}

	/// <summary>
	/// The best pack for a whole box of ground - an adventure's planned routes (§5.4).
	/// <para>
	/// A pack holding all of it is preferred over one holding the middle, because the point of the
	/// offer is that the map does not go blank <em>on the way</em>. Falling back to the centre
	/// rather than answering nothing keeps a ride that crosses a border from being told there is no
	/// map for it at all.
	/// </para>
	/// </summary>
	/// <param name="bounds">The ground the ride covers.</param>
	/// <param name="cancellationToken">Abandons the catalogue read.</param>
	public async Task<OfflineMapSuggestion?> SuggestAsync(
		TrackBounds bounds,
		CancellationToken cancellationToken = default)
	{
		if (!bounds.IsWellFormed)
			return null;

		if (await CandidatesAsync(cancellationToken) is not { } offers)
			return null;

		return Best(offers.Covering(bounds))
			?? await SuggestAsync(
				MapGeometry.MercatorMidLatitude(bounds.MaxLatitude, bounds.MinLatitude),
				(bounds.MinLongitude + bounds.MaxLongitude) / 2,
				cancellationToken);
	}

	/// <summary>
	/// Remembers that the map drew nothing here, so the offer can be made where it can be acted on.
	/// <para>
	/// Stored rather than offered on the spot: a blank map is usually a phone with no signal, and a
	/// download button on that screen cannot work. One point, not a list - the last dead zone is
	/// the one the rider remembers.
	/// </para>
	/// </summary>
	/// <param name="latitudeDeg">Where the map had nothing to draw.</param>
	/// <param name="longitudeDeg">Where the map had nothing to draw.</param>
	/// <param name="cancellationToken">Abandons the write.</param>
	public async ValueTask RecordMissAsync(
		double latitudeDeg,
		double longitudeDeg,
		CancellationToken cancellationToken = default)
	{
		if (!IsSupported || !double.IsFinite(latitudeDeg) || !double.IsFinite(longitudeDeg))
			return;

		await LoadAsync(cancellationToken);

		if (IsNear(_missed, latitudeDeg, longitudeDeg))
			return;

		_missed = (latitudeDeg, longitudeDeg);

		// Deliberately silent: nothing on screen draws the recorded point, and raising Changed here
		// would have the map that just recorded it re-run its own suggest pass.
		await SaveAsync(cancellationToken);
	}

	/// <summary>
	/// Looks for a pack covering where the map last went blank, and holds it on <see cref="Missed"/>.
	/// <para>
	/// A rung of the launch ladder (<c>MainLayout</c>) rather than a screen's own lifecycle, so the
	/// surfaces that show it only render. A device that has never lost its map returns before any
	/// I/O, which is what keeps this off the cost of an ordinary launch.
	/// </para>
	/// </summary>
	/// <param name="cancellationToken">Abandons the catalogue read.</param>
	public async Task PrimeMissedAsync(CancellationToken cancellationToken = default)
	{
		if (!IsSupported)
			return;

		await LoadAsync(cancellationToken);

		if (_missed is not { } missed)
			return;

		Missed = await SuggestAsync(missed.Latitude, missed.Longitude, cancellationToken);

		if (Missed is not null)
			Changed?.Invoke();
	}

	/// <summary>Forgets the last blank map - after the offer is taken, or turned down.</summary>
	/// <param name="cancellationToken">Abandons the write.</param>
	public async ValueTask ForgetMissAsync(CancellationToken cancellationToken = default)
	{
		Missed = null;

		if (_missed is null)
			return;

		_missed = null;
		await SaveAsync(cancellationToken);
		Changed?.Invoke();
	}

	/// <summary>
	/// Takes a pack: fetches the archive if it is not here, and draws every map in the app with it
	/// either way.
	/// <para>
	/// The two halves are one operation because separating them is the failure §4.2 already had,
	/// where a several-hundred-megabyte download appeared to have done nothing. Settings → Maps
	/// adopts a pack through here too, so there is one statement of it.
	/// </para>
	/// </summary>
	/// <param name="suggestion">Which pack, and whether it needs fetching first.</param>
	/// <param name="theme">
	/// Which cartography to draw it with, or null to keep whatever the rider has chosen. The
	/// settings screen passes its own, because a theme picked before any pack was selected is
	/// remembered there rather than stored (§13 Q26).
	/// </param>
	/// <param name="cancellationToken">
	/// Abandons the write that selects it. The transfer is cancelled through
	/// <see cref="MapPackState.Cancel"/>, which is what the surfaces offer while one runs.
	/// </param>
	/// <returns>Whether the map is now drawing with it.</returns>
	public async Task<bool> TakeAsync(
		OfflineMapSuggestion suggestion,
		MapTheme? theme = null,
		CancellationToken cancellationToken = default)
	{
		if (!suggestion.IsOnDevice && !await _packs.DownloadAsync(suggestion.Offer))
			return false;

		return await SelectAsync(suggestion.Offer.Id, theme, cancellationToken);
	}

	/// <summary>
	/// Draws every map in the app with a pack this device already holds, and forgets any dead zone
	/// it answers. The second half of <see cref="TakeAsync"/>, and what Settings → Maps calls when a
	/// rider picks one off the list of what is already here.
	/// </summary>
	/// <param name="packId">A catalogue id, as the device filed it.</param>
	/// <param name="theme">Which cartography, or null to keep the rider's current choice.</param>
	/// <param name="cancellationToken">Abandons the write.</param>
	/// <returns>Whether the map is now drawing with it.</returns>
	public async Task<bool> SelectAsync(
		string packId,
		MapTheme? theme = null,
		CancellationToken cancellationToken = default)
	{
		if (!IsSupported)
			return false;

		await _sources.SetAsync(
			MapSource.OfflinePack(packId, theme ?? _sources.Chosen.Theme),
			cancellationToken);

		await ForgetMissAsync(cancellationToken);
		Invalidate();

		return true;
	}

	/// <summary>
	/// The cached answer for this ground, or null when there is none to reuse. Nothing is cached
	/// across a decline, a taken pack or a catalogue that has just been read - see
	/// <see cref="Invalidate"/>.
	/// </summary>
	private (double Latitude, double Longitude, OfflineMapSuggestion? Answer)? Answered(
		double latitudeDeg,
		double longitudeDeg)
	{
		if (_cached is not { } cached)
			return null;

		// An answer holds anywhere inside the pack it named - a rider panning within New South
		// Wales is being told the same thing - and only a short way when it named nothing, because
		// the next valley along may well be covered.
		bool holds = cached.Answer is { Offer.Bounds: { } box }
			? box.Contains(latitudeDeg, longitudeDeg)
			: IsNear((cached.Latitude, cached.Longitude), latitudeDeg, longitudeDeg);

		return holds ? cached : null;
	}

	private static bool IsNear((double Latitude, double Longitude)? point, double latitudeDeg, double longitudeDeg) =>
		point is { } held
		&& Math.Abs(held.Latitude - latitudeDeg) < SameGroundDeg
		&& Math.Abs(held.Longitude - longitudeDeg) < SameGroundDeg;

	/// <summary>Drops the cached answer and broadcasts, after something that changes what the answer would be.</summary>
	private void Invalidate()
	{
		_cached = null;
		Changed?.Invoke();
	}

	/// <summary>
	/// The packs to choose from, or <c>null</c> when nothing should be offered: a host with nowhere
	/// to put one, a transfer already running, or a catalogue that would not answer.
	/// </summary>
	private async Task<IReadOnlyList<MapPackOffer>?> CandidatesAsync(CancellationToken cancellationToken)
	{
		if (!IsSupported || _packs.IsDownloading)
			return null;

		// MapPackState only remembers a catalogue it could read, so that a rider deliberately
		// returning to the offline form gets a fresh attempt. This caller is a map, which re-asks
		// on its own as the rider moves, so the back-off has to live here.
		long now = Environment.TickCount64;

		if (_catalogueFailedTicks != 0 && now - _catalogueFailedTicks < CatalogueRetryAfterMs)
			return null;

		await LoadAsync(cancellationToken);
		await _packs.LoadAsync(cancellationToken);
		await _packs.LoadCatalogueAsync(cancellationToken);

		if (!_packs.IsCatalogueRead)
		{
			_catalogueFailedTicks = now;
			return null;
		}

		_catalogueFailedTicks = 0;

		return _packs.Offers;
	}

	/// <summary>
	/// Picks one of the packs covering some ground - already on the phone first, then the order
	/// <see cref="MapPackCoverage"/> hands them over in.
	/// <para>
	/// Already-here beats smaller because it is free and instant, and a device holding an archive
	/// it is not drawing with is the one case in this feature where the fix costs nothing.
	/// </para>
	/// </summary>
	private OfflineMapSuggestion? Best(IEnumerable<MapPackOffer> covering) =>
		covering
			.Where(offer => !IsDeclined(offer.Id) && !IsChosen(offer.Id))
			.Select(offer => new OfflineMapSuggestion(offer, _packs.Stored(offer.Id) is not null))
			.OrderBy(suggestion => suggestion.IsOnDevice ? 0 : 1)
			.FirstOrDefault();

	/// <summary>
	/// Whether this is the pack the rider already picked. Telling somebody to use the map they are
	/// using is how an app teaches people to stop reading its banners.
	/// </summary>
	private bool IsChosen(string packId) =>
		_sources.Chosen is { Kind: MapSourceKind.Offline, PackId: { } chosen }
		&& string.Equals(chosen, packId, StringComparison.Ordinal);

	private async Task ReadAsync(CancellationToken cancellationToken)
	{
		(List<string> declined, (double, double)? missed) =
			Decode(await _settings.GetAsync(StorageKey, cancellationToken));

		_declined.Clear();
		_declined.AddRange(declined);
		_missed = missed;
	}

	/// <summary>
	/// Writes the answer, or removes the key when there is nothing to say - the same thing
	/// <see cref="ConsentAskedState"/> does, and for the same reason.
	/// </summary>
	private ValueTask SaveAsync(CancellationToken cancellationToken)
	{
		if (_declined.Count == 0 && _missed is null)
			return _settings.RemoveAsync(StorageKey, cancellationToken);

		string missed = _missed is { } point
			? string.Create(CultureInfo.InvariantCulture, $"{point.Latitude:R},{point.Longitude:R}")
			: "";

		return _settings.SetAsync(
			StorageKey,
			$"1|{missed}|{string.Join(',', _declined)}",
			cancellationToken);
	}

	/// <summary>
	/// Reads back what <see cref="SaveAsync"/> wrote. Anything else reads as a device that has
	/// declined nothing and missed nothing, which errs towards offering rather than towards
	/// silence.
	/// </summary>
	private static (List<string> Declined, (double, double)? Missed) Decode(string? stored)
	{
		List<string> declined = [];

		if (stored is null || !stored.StartsWith("1|", StringComparison.Ordinal))
			return (declined, null);

		string[] parts = stored[2..].Split('|');

		if (parts.Length != 2)
			return (declined, null);

		foreach (string part in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries))
		{
			if (!declined.Contains(part, StringComparer.Ordinal))
				declined.Add(part);
		}

		if (declined.Count > MaxDeclined)
			declined.RemoveRange(MaxDeclined, declined.Count - MaxDeclined);

		return (declined, DecodePoint(parts[0]));
	}

	private static (double, double)? DecodePoint(string stored)
	{
		string[] pair = stored.Split(',');

		if (pair.Length != 2
			|| !double.TryParse(pair[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double latitude)
			|| !double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double longitude))
		{
			return null;
		}

		// It came from a store nothing validates: a point off the globe would select a pack by
		// accident, or none at all.
		return new TrackBounds(latitude, longitude, latitude, longitude).IsWellFormed
			? (latitude, longitude)
			: null;
	}
}
