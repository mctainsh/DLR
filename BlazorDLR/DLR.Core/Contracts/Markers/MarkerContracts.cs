namespace DLR.Core.Contracts.Markers;

/// <summary>
/// Creating a marker (§16.1, §16.2).
/// <para>
/// Exactly one parent id is set. The database enforces it with a <c>CHECK</c> so the invariant
/// survives every path that ever writes the table, and the endpoint checks it too so the caller
/// gets a 400 rather than a 500.
/// </para>
/// </summary>
/// <param name="TrackId">The track it annotates, or null.</param>
/// <param name="GroupRideId">The ride it belongs to, or null.</param>
/// <param name="Lat">Latitude, scaled by 1e5 — the same encoding as a position (§5.5).</param>
/// <param name="Lon">Longitude, scaled by 1e5.</param>
/// <param name="Icon">A key from the curated set, or any storable key a newer client sends.</param>
/// <param name="Title">
/// Rendered beside the icon, so its limit is a rendering constraint. <strong>Empty is a marker
/// with no title</strong> — the icon already carries the meaning of most pins, and the overlay
/// draws the plate alone rather than an empty label. Whitespace-only cleans to the same thing.
/// </param>
/// <param name="Note">Shown on tap. Plain text; never rendered as HTML or Markdown.</param>
/// <param name="DirectionDeg">
/// Degrees from true north, 0–359, or null. <strong>Null is not zero</strong> — zero is due north,
/// a perfectly good bearing. A fuel stop has no direction; a blind crest does.
/// </param>
public sealed record CreateMarkerRequest(
	Guid? TrackId,
	Guid? GroupRideId,
	int Lat,
	int Lon,
	string Icon,
	string Title,
	string? Note = null,
	short? DirectionDeg = null);

/// <summary>Editing one (§16.5). The parent never moves.</summary>
/// <param name="Lat">Latitude, scaled.</param>
/// <param name="Lon">Longitude, scaled.</param>
/// <param name="Icon">The icon key.</param>
/// <param name="Title">The label, or empty to take one off.</param>
/// <param name="Note">The note, or null to clear it.</param>
/// <param name="DirectionDeg">The bearing, or null to clear it.</param>
public sealed record UpdateMarkerRequest(
	int Lat,
	int Lon,
	string Icon,
	string Title,
	string? Note = null,
	short? DirectionDeg = null);

/// <summary>A marker as the map sees it (§16.2).</summary>
/// <param name="Id">Which marker.</param>
/// <param name="TrackId">Its track parent, or null.</param>
/// <param name="GroupRideId">Its ride parent, or null.</param>
/// <param name="Lat">Latitude, scaled.</param>
/// <param name="Lon">Longitude, scaled.</param>
/// <param name="Icon">The icon key as stored — the client falls back if it cannot draw it.</param>
/// <param name="Title">The label, or empty for an untitled marker.</param>
/// <param name="Note">The note.</param>
/// <param name="DirectionDeg">The bearing, or null for none.</param>
/// <param name="PhotoId">The attached image, or null — fetched separately, so a marker draws first (§16.4).</param>
/// <param name="CreatedByUserId">Who placed it.</param>
/// <param name="CreatedByUserName">Their handle (§7.2).</param>
/// <param name="CreatedUtc">When.</param>
/// <param name="UpdatedUtc">When it was last edited.</param>
public sealed record MarkerDto(
	Guid Id,
	Guid? TrackId,
	Guid? GroupRideId,
	int Lat,
	int Lon,
	string Icon,
	string Title,
	string? Note,
	short? DirectionDeg,
	Guid? PhotoId,
	Guid CreatedByUserId,
	string CreatedByUserName,
	DateTimeOffset CreatedUtc,
	DateTimeOffset UpdatedUtc);
