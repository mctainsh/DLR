using DLR.Core.Tracks;

namespace DLR.Core.Contracts.Maps;

/// <summary>
/// One offline map pack the catalogue offers (§4.2).
/// <para>
/// The wire shape of a <c>catalogue.json</c> entry, and the same record the planned
/// <c>GET /api/v1/map-packs</c> will answer with - so the day the server grows that endpoint the
/// client changes where it reads from and not what it reads. It is published today by
/// <c>Documentation/Build-AuMapPacks.ps1</c>, which writes this shape after building each extract.
/// </para>
/// <para>
/// <strong>Everything here is the publisher's claim, not a fact about this device.</strong> Nothing
/// is trusted enough to act on unchecked: <c>MapPackCatalogue</c> refuses an id the pack store would
/// not accept as a directory name, resolves <see cref="Url"/> against the catalogue's own address,
/// and the downloader still checks the PMTiles magic on what actually arrives.
/// </para>
/// </summary>
/// <param name="Id">The pack's slug - <c>au-nsw</c> - and what it is called on the device.</param>
/// <param name="Name">What a rider reads: <c>New South Wales</c>.</param>
/// <param name="Region">
/// The country the pack is part of - <c>Australia</c>, <c>France</c> - or the smallest natural
/// grouping when no one country owns it (<c>The Caribbean</c>, <c>The Sahel</c>). The settings
/// screen asks for this first and for the area within it second, because two hundred regions in one
/// dropdown is not a choice anybody makes on a phone.
/// <para>
/// Nullable, and every reader has to cope: a catalogue published before this field existed carries
/// no region at all, and that is the catalogue on the host riders are fetching from today. What the
/// client does about it is <c>MapPackCatalogue.RegionFor</c> - one fallback, stated once.
/// </para>
/// <para>
/// Published rather than worked out on the device. The alternative was a table on the phone saying
/// which slug belongs to which country, which is a copy of the publisher's table that ships a
/// release behind it and disagrees the day a pack is re-cut.
/// </para>
/// </param>
/// <param name="Bounds">
/// The ground the extract covers. Nullable here though §4.2 writes it plain: it is framing
/// information, and a catalogue that omitted it should cost a rider the map preview rather than the
/// map.
/// </param>
/// <param name="MinZoom">The shallowest zoom in the archive, normally 0.</param>
/// <param name="MaxZoom">
/// The deepest zoom with real data, normally 14. Above it MapLibre overzooms the vector tiles, which
/// stays sharp - §4.1's whole size argument.
/// </param>
/// <param name="SizeBytes">How much of the phone it will take. The number the settings screen shows before anybody commits to it.</param>
/// <param name="Sha256">The archive's checksum, lowercase hex.</param>
/// <param name="Version">Bumped when the extract is rebuilt.</param>
/// <param name="Url">
/// Where the archive is. Absolute in what the build script publishes; a relative value is resolved
/// against the catalogue's own address so a host can move without republishing every entry.
/// </param>
public sealed record MapPackSummary(
	string Id,
	string Name,
	string? Region,
	TrackBounds? Bounds,
	int MinZoom,
	int MaxZoom,
	long SizeBytes,
	string Sha256,
	int Version,
	string Url);
