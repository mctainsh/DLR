namespace DLR.Core.Contracts.Tracks;

/// <summary>
/// <c>PATCH /api/v1/tracks/{id}/details</c> — the description, the cover photograph and whether
/// the route is shared with everyone (§6.2, §6.3).
/// <para>
/// <strong>All three together, and every field is an assignment rather than a suggestion.</strong>
/// The screen behind this is one panel with one Save button, and a partial update would mean a
/// rider who cleared the description and pressed Save could not tell whether the empty box meant
/// "remove it" or "leave it alone". Sending the whole panel back makes that unambiguous — null
/// clears, a value sets.
/// </para>
/// <para>
/// Not versioned, on <see cref="RenameTrackRequest"/>'s reasoning: none of this moves a point, so
/// none of it can invalidate an editor open in another tab.
/// </para>
/// </summary>
/// <param name="Description">
/// What to say about the route, or null to remove what is there. Cleaned and length-checked
/// against <see cref="DLR.Core.Tracks.TrackDescription"/>.
/// </param>
/// <param name="PhotoId">
/// A photograph the caller uploaded to <c>POST /api/v1/photos</c>, or null to remove the cover.
/// Somebody else's photo identifier is refused rather than silently ignored — otherwise a guessed
/// identifier would republish their photograph under a route of the caller's choosing.
/// </param>
/// <param name="Visibility">
/// <see cref="TrackVisibilityDto.Public"/> shares the route with every signed-in rider;
/// <see cref="TrackVisibilityDto.Private"/> takes it back off the list.
/// </param>
public sealed record UpdateTrackDetailsRequest(
	string? Description,
	Guid? PhotoId,
	TrackVisibilityDto Visibility);

/// <summary>
/// One row of the shared-routes list (§6.2).
/// <para>
/// Deliberately not a <see cref="TrackSummary"/>. This list is somebody else's routes, and a
/// browse row needs the owner's name and how far away the route is, while it has no use at all
/// for a version, a point count or a segment count — every one of which is an edit concern on a
/// track the caller cannot edit.
/// </para>
/// </summary>
/// <param name="Id">The track. <c>GET /api/v1/tracks/{id}</c> will serve it to any signed-in rider while it stays public.</param>
/// <param name="Name">What it is called.</param>
/// <param name="Description">What the owner wrote, or null.</param>
/// <param name="PhotoId">The cover photograph, or null.</param>
/// <param name="OwnerName">Whose route it is — the username, never a self-chosen display name (§7.3).</param>
/// <param name="DistanceM">Metres along the ground.</param>
/// <param name="AscentM">Metres climbed, or null when the source carried no elevation.</param>
/// <param name="SharedUtc">When it was first shared. What the list sorts on when no area filter is set.</param>
/// <param name="CentreLat">Latitude of the route's bounding-box centre — enough to put a pin on it.</param>
/// <param name="CentreLon">Longitude of the same.</param>
/// <param name="AwayKm">
/// Great-circle kilometres from the point the caller filtered around, or null when they did not
/// filter around one. Null rather than zero: zero means "you are standing on it" (§8).
/// </param>
public sealed record SharedTrackSummary(
	Guid Id,
	string? Name,
	string? Description,
	Guid? PhotoId,
	string OwnerName,
	double DistanceM,
	double? AscentM,
	DateTimeOffset SharedUtc,
	double CentreLat,
	double CentreLon,
	double? AwayKm);

/// <summary>
/// One page of <c>GET /api/v1/tracks/shared</c> (§6.2).
/// <para>
/// <strong>Numbered pages with a total, not a cursor.</strong> §17.8's thread is a feed that only
/// ever grows at one end and is read once, so a cursor is right there. This is a catalogue somebody
/// filters and pages back and forth through, and "page 3 of 47" is information they are using to
/// decide whether to narrow the filter instead. A cursor cannot say 47.
/// </para>
/// </summary>
/// <param name="Items">The routes on this page, in the server's order.</param>
/// <param name="Page">Which page this is, one-based.</param>
/// <param name="PageSize">How many rows a full page holds.</param>
/// <param name="TotalCount">
/// How many routes match the filter across every page — counted after the filter, so narrowing
/// the search is visibly narrowing something.
/// </param>
public sealed record SharedTrackPage(
	IReadOnlyList<SharedTrackSummary> Items,
	int Page,
	int PageSize,
	int TotalCount)
{
	/// <summary>How many pages the filter produces. At least one, so an empty result is "page 1 of 1".</summary>
	public int PageCount => TotalCount <= 0 ? 1 : (TotalCount + PageSize - 1) / PageSize;
}

/// <summary>
/// What the browse list is asking for (§6.2).
/// <para>
/// A record rather than five loose arguments because the screen holds exactly this and hands it
/// straight over, and because the clamping rules below then have one place to live instead of
/// being restated by every caller.
/// </para>
/// </summary>
/// <param name="Name">
/// Matches any route whose name contains this, case-insensitively. Null or blank matches
/// everything.
/// </param>
/// <param name="Latitude">The point to measure from, paired with <paramref name="Longitude"/>.</param>
/// <param name="Longitude">The other half of the point.</param>
/// <param name="WithinKm">
/// How far from that point a route's centre may be. Null — or a point that is not supplied —
/// means no area filter at all.
/// </param>
/// <param name="Page">One-based page number.</param>
public sealed record SharedTrackQuery(
	string? Name = null,
	double? Latitude = null,
	double? Longitude = null,
	double? WithinKm = null,
	int Page = 1)
{
	/// <summary>Rows a page holds. Stated here so the client can size its pager before it asks.</summary>
	public const int PageSize = 20;

	/// <summary>
	/// The furthest an area filter will reach. Beyond a few thousand kilometres the filter has
	/// stopped narrowing anything, and a caller asking for 40 000 km is asking for the whole
	/// table sorted by a distance nobody needs computed.
	/// </summary>
	public const double MaxWithinKm = 5000;

	/// <summary>Whether both halves of a usable centre point are present and in range.</summary>
	public bool HasArea =>
		Latitude is >= -90 and <= 90
		&& Longitude is >= -180 and <= 180
		&& WithinKm is > 0;
}
