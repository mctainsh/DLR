using DLR.Core.Contracts.Identity;

namespace DLR.Core.Contracts.Account;

/// <summary>
/// Everything the server holds about one account, as <c>export.json</c> inside the archive
/// (§6.3, §10.1).
/// <para>
/// <strong>An export is a promise about completeness, so the shape is deliberately flat and
/// exhaustive rather than pretty.</strong> Anything omitted here is data the account holder has
/// been told they were given and were not - which is a different and worse kind of bug from an
/// ugly file. Where something is a file rather than a field (a track's points, a photograph) the
/// record names its path inside the archive, so nothing is silently reduced to an identifier.
/// </para>
/// </summary>
/// <param name="GeneratedUtc">When the archive was produced.</param>
/// <param name="UserId">The account.</param>
/// <param name="UserName">The name, which cannot have changed since registration (§7.2).</param>
/// <param name="CreatedUtc">When the account was created.</param>
/// <param name="LastActiveUtc">When the server last heard from it (§7.10).</param>
/// <param name="Profile">The three optional fields, their three switches, and the home private area (§7.3, §10.1).</param>
/// <param name="Tracks">Recorded and imported rides, with their points as GPX in the archive.</param>
/// <param name="Markers">Markers this account placed, wherever they hang (§16).</param>
/// <param name="Photos">Photographs it uploaded, with the image itself in the archive.</param>
/// <param name="Rides">Rides it organised or was admitted to.</param>
/// <param name="Comments">Posts in ride threads, polls included (§17).</param>
/// <param name="Reactions">Reactions it left.</param>
/// <param name="Votes">Poll options it holds a vote on.</param>
/// <param name="Devices">Signed-in installations (§7.10).</param>
public sealed record AccountExport(
	DateTimeOffset GeneratedUtc,
	Guid UserId,
	string UserName,
	DateTimeOffset CreatedUtc,
	DateTimeOffset LastActiveUtc,
	ExportedProfile Profile,
	IReadOnlyList<ExportedTrack> Tracks,
	IReadOnlyList<ExportedMarker> Markers,
	IReadOnlyList<ExportedPhoto> Photos,
	IReadOnlyList<ExportedRide> Rides,
	IReadOnlyList<ExportedComment> Comments,
	IReadOnlyList<ExportedReaction> Reactions,
	IReadOnlyList<ExportedVote> Votes,
	IReadOnlyList<ExportedDevice> Devices);

/// <summary>
/// The §7.3 fields and switches, both halves.
/// <para>
/// The <em>switches</em> are exported alongside the values, not just the values. What a rider chose
/// to share is a decision they made about their own privacy, and an export that showed a phone
/// number without saying whether anybody could see it would be answering a different question.
/// </para>
/// </summary>
/// <param name="DisplayName">Optional, and never the map label.</param>
/// <param name="PhoneNumber">Optional, never verified (§7.3).</param>
/// <param name="Email">Optional, and the only recovery path when present (§7.7).</param>
/// <param name="EmailConfirmed">Whether the address has been confirmed.</param>
/// <param name="ShareDisplayName">Whether co-members see the display name.</param>
/// <param name="SharePhoneNumber">Whether co-members see the phone number.</param>
/// <param name="ShareEmail">Whether co-members see the address.</param>
/// <param name="PrivateArea">
/// The home private area the account holds, or null when it has none (§10.1).
/// <para>
/// Exported because the server holds it and it is the rider's data - the same reason the
/// switches above are. It is also the one field in this record that is a location, so an export
/// containing it is an export that names where somebody lives: the archive is handed to the
/// account holder over an authenticated request and nowhere else, and that is worth remembering
/// before anything else is ever allowed to read one.
/// </para>
/// </param>
public sealed record ExportedProfile(
	string? DisplayName,
	string? PhoneNumber,
	string? Email,
	bool EmailConfirmed,
	bool ShareDisplayName,
	bool SharePhoneNumber,
	bool ShareEmail,
	PrivateAreaSettings? PrivateArea = null);

/// <summary>One track, with its points written out as GPX beside this file (§15.1).</summary>
/// <param name="Id">The track.</param>
/// <param name="Name">What the rider called it.</param>
/// <param name="Source">Recorded or imported.</param>
/// <param name="CreatedUtc">When it was stored.</param>
/// <param name="StartedUtc">When the ride began, when the points carry times.</param>
/// <param name="DistanceM">Metres.</param>
/// <param name="AscentM">Metres climbed, or null when the file had no elevation.</param>
/// <param name="Version">How many times it has been edited (§15.4).</param>
/// <param name="GpxPath">Where the points are in the archive.</param>
/// <param name="PreviousVersionGpxPath">
/// The retained pre-edit original, while it exists (§15.6). Present only inside the undo window -
/// it is the rider's data for as long as this server holds it, so it is exported with the track
/// rather than quietly left out of the one file that claims to be everything.
/// </param>
public sealed record ExportedTrack(
	Guid Id,
	string? Name,
	string Source,
	DateTimeOffset CreatedUtc,
	DateTimeOffset? StartedUtc,
	double DistanceM,
	double? AscentM,
	int Version,
	string GpxPath,
	string? PreviousVersionGpxPath);

/// <summary>One marker this account placed (§16.1).</summary>
/// <param name="Id">The marker.</param>
/// <param name="TrackId">Its parent, when it hangs off a track.</param>
/// <param name="GroupRideId">Its parent, when it hangs off a ride.</param>
/// <param name="Lat">Degrees.</param>
/// <param name="Lon">Degrees.</param>
/// <param name="DirectionDeg">Bearing, or null for no bearing - never zero for absent (§16.2).</param>
/// <param name="Icon">The icon key as stored, unknown ones included.</param>
/// <param name="Title">Its title.</param>
/// <param name="Note">Its note.</param>
/// <param name="PhotoId">The attached photograph, if any.</param>
/// <param name="CreatedUtc">When it was placed.</param>
public sealed record ExportedMarker(
	Guid Id,
	Guid? TrackId,
	Guid? GroupRideId,
	double Lat,
	double Lon,
	short? DirectionDeg,
	string Icon,
	string Title,
	string? Note,
	Guid? PhotoId,
	DateTimeOffset CreatedUtc);

/// <summary>One photograph, with the image itself in the archive (§16.4, §16.6).</summary>
/// <param name="Id">The photo.</param>
/// <param name="WidthPx">Stored width, after orientation was applied.</param>
/// <param name="HeightPx">Stored height.</param>
/// <param name="ByteSize">Bytes on disk.</param>
/// <param name="CreatedUtc">When it was ingested.</param>
/// <param name="ImagePath">
/// Where the image is in the archive. §16.6 requires the export to include the photographs
/// themselves; a list of identifiers would not be an export of anybody's photographs.
/// </param>
public sealed record ExportedPhoto(
	Guid Id,
	int WidthPx,
	int HeightPx,
	int ByteSize,
	DateTimeOffset CreatedUtc,
	string ImagePath);

/// <summary>
/// One ride this account organised or rode in (§5.2).
/// <para>
/// <strong>The join code is not here, and that is not an oversight.</strong> It is the ride's entire
/// access control and it goes only to the organiser (§5.2) - an export handed to a member that
/// carried it would let any member re-share the group the organiser curated, through a file nobody
/// thinks of as a sharing surface.
/// </para>
/// </summary>
/// <param name="Id">The ride.</param>
/// <param name="Name">What it is called.</param>
/// <param name="Role">Owner, leader, rider or spectator.</param>
/// <param name="StartUtc">When it was planned to start.</param>
/// <param name="JoinedUtc">When this account joined.</param>
/// <param name="ShareLocation">Whether this account consented to broadcast to it (§5.6).</param>
public sealed record ExportedRide(
	Guid Id,
	string Name,
	string Role,
	DateTimeOffset StartUtc,
	DateTimeOffset JoinedUtc,
	bool ShareLocation);

/// <summary>One post in a ride thread - polls included, since a poll is a comment (§17.5).</summary>
/// <param name="Id">The comment.</param>
/// <param name="GroupRideId">Which ride's thread.</param>
/// <param name="Kind">Text or poll.</param>
/// <param name="Body">What it said; a poll's body is its question.</param>
/// <param name="PhotoId">An attached photograph, if any.</param>
/// <param name="PostedUtc">When the server received it - the order the thread reads in (§17.2).</param>
/// <param name="CreatedUtc">When it was authored, which differs for a post composed offline.</param>
/// <param name="EditedUtc">When it was last edited, if it was.</param>
/// <param name="PollOptions">The options, when this is a poll.</param>
public sealed record ExportedComment(
	Guid Id,
	Guid? GroupRideId,
	Guid? TrackId,
	string Kind,
	string? Body,
	Guid? PhotoId,
	DateTimeOffset PostedUtc,
	DateTimeOffset CreatedUtc,
	DateTimeOffset? EditedUtc,
	IReadOnlyList<string> PollOptions);

/// <summary>One reaction this account left (§17.4).</summary>
/// <param name="CommentId">What it reacted to.</param>
/// <param name="Reaction">The key, unknown ones included (§17.4's forward compatibility).</param>
public sealed record ExportedReaction(Guid CommentId, string Reaction);

/// <summary>One poll option this account holds a vote on (§17.5).</summary>
/// <param name="CommentId">The poll's comment.</param>
/// <param name="OptionText">What the option said.</param>
/// <param name="CreatedUtc">When the vote was cast.</param>
public sealed record ExportedVote(Guid CommentId, string OptionText, DateTimeOffset CreatedUtc);

/// <summary>One signed-in installation (§7.10).</summary>
/// <param name="Id">The device id, which the server assigned.</param>
/// <param name="Name">What the client called it; never verified.</param>
/// <param name="CreatedUtc">First seen.</param>
/// <param name="LastSeenUtc">Last heard from, throttled to one write an hour.</param>
public sealed record ExportedDevice(
	Guid Id,
	string? Name,
	DateTimeOffset CreatedUtc,
	DateTimeOffset LastSeenUtc);

/// <summary>
/// What <c>DELETE /api/v1/me</c> takes (§6.3).
/// <para>
/// The current password, because this is the one irreversible action in the API and a stolen
/// fifteen-minute access token should not be enough to end somebody's account. Every account has a
/// password - §7.2 makes username and password <em>the</em> account - so requiring it excludes
/// nobody.
/// </para>
/// </summary>
/// <param name="CurrentPassword">The caller's password, re-entered.</param>
public sealed record DeleteAccountRequest(string CurrentPassword);
