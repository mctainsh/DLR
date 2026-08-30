using DLR.Core.Contracts.Account;
using DLR.Core.Contracts.Admin;
using DLR.Core.Contracts.Comments;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Moderation;
using DLR.Core.Contracts.Photos;
using DLR.Core.Contracts.Rides;
using DLR.Core.Contracts.Tracks;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// The one seam every shared component crosses to reach the server (§18.2, §18.6).
/// <para>
/// <strong>Returns the DTOs from <c>DLR.Core.Contracts</c> unchanged.</strong> A shared
/// component never invents a parallel model — one break of the wire contract is one build
/// failure, in the same assembly the server references (§3).
/// </para>
/// <para>
/// The mobile host binds this to an <c>HttpClient</c> with a bearer-token auth handler; the
/// web host binds it to an <c>HttpClient</c> with <c>credentials: 'include'</c> so its
/// <c>HttpOnly</c> cookie travels automatically (§18.5). Neither implementation is a shared
/// concern — this interface is.
/// </para>
/// </summary>
public interface IApiClient
{
	// -- About / licence (§14.6.2) --------------------------------------------------------

	/// <summary><c>GET /api/v1/about</c> — the AGPL §13 source offer.</summary>
	Task<AboutInfo> GetAboutAsync(CancellationToken cancellationToken = default);

	// -- Auth (§7.2, §7.4, §7.7, §7.10, §7.14) --------------------------------------------

	Task<TokenResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);
	Task<TokenResponse> TokenAsync(TokenRequest request, CancellationToken cancellationToken = default);
	Task<bool> IsUserNameAvailableAsync(string userName, CancellationToken cancellationToken = default);
	Task SetEmailAsync(SetEmailRequest request, CancellationToken cancellationToken = default);
	Task<TokenResponse> ConfirmEmailAsync(ConfirmEmailRequest request, CancellationToken cancellationToken = default);
	Task ResendConfirmationAsync(CancellationToken cancellationToken = default);
	Task ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken = default);
	Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default);
	Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default);
	Task<IReadOnlyList<DeviceSession>> ListSessionsAsync(CancellationToken cancellationToken = default);
	Task RevokeSessionAsync(Guid deviceId, CancellationToken cancellationToken = default);

	// -- Profile (§7.3, §7.14) ------------------------------------------------------------

	Task<OwnProfile> GetProfileAsync(CancellationToken cancellationToken = default);
	Task UpdateProfileAsync(UpdateProfileRequest request, CancellationToken cancellationToken = default);

	// -- Home private area (§10.1) ---------------------------------------------------------

	/// <summary>
	/// <c>GET /api/v1/me/private-area</c> — the circle inside which this account publishes
	/// nothing, or the fact that it has none (§10.1).
	/// <para>
	/// Its own three endpoints rather than three more fields on <c>PUT /me/profile</c>, and the
	/// separation is load-bearing rather than tidy: the profile screen sends a whole
	/// <see cref="UpdateProfileRequest"/> every time somebody edits a display name, so a private
	/// area riding along inside that request would be cleared by any client that had not been
	/// taught about it. A privacy control must not be deletable as a side effect of an unrelated
	/// save.
	/// </para>
	/// </summary>
	Task<PrivateAreaResponse> GetPrivateAreaAsync(CancellationToken cancellationToken = default);

	/// <summary><c>PUT /api/v1/me/private-area</c> — places or moves it. The radius is clamped server-side.</summary>
	Task SetPrivateAreaAsync(PrivateAreaSettings request, CancellationToken cancellationToken = default);

	/// <summary><c>DELETE /api/v1/me/private-area</c> — forgets it, so the account shares from everywhere again.</summary>
	Task ClearPrivateAreaAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// <c>PUT /api/v1/me/avatar</c> — the photograph shown beside the caller's username (§7.3).
	/// <para>
	/// Its own call rather than a field on <see cref="UpdateProfileAsync"/>, for the reason the
	/// private area above has its own: that request replaces the whole profile, so an avatar
	/// travelling inside it would be cleared by any client not taught about it.
	/// </para>
	/// </summary>
	Task<OwnProfile> SetAvatarAsync(SetAvatarRequest request, CancellationToken cancellationToken = default);

	/// <summary><c>DELETE /api/v1/me/avatar</c> — removes it, so the name is drawn on its own again.</summary>
	Task<OwnProfile> ClearAvatarAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// <c>GET /api/v1/users/avatars</c> — the photographs for a screenful of usernames, in one
	/// request (§7.3).
	/// <para>
	/// Callers should go through <c>RiderAvatars</c> rather than here: it is the thing that
	/// batches a render pass into one call and remembers the answers, including the negative ones.
	/// </para>
	/// </summary>
	Task<IReadOnlyList<RiderAvatarDto>> GetRiderAvatarsAsync(IReadOnlyCollection<string> userNames, CancellationToken cancellationToken = default);

	// -- Tracks (§6.3, §15) ---------------------------------------------------------------

	/// <summary>
	/// <c>POST /api/v1/tracks</c> — stores a recorded or imported track (§6.3).
	/// <para>
	/// Idempotent on <see cref="UploadTrackRequest.ClientGuid"/>, which is what lets the recorder
	/// press "save" again after a failure it could not tell from a success (§4.4).
	/// </para>
	/// </summary>
	Task<TrackSummary> UploadTrackAsync(UploadTrackRequest request, CancellationToken cancellationToken = default);

	Task<IReadOnlyList<TrackSummary>> ListTracksAsync(CancellationToken cancellationToken = default);
	Task<TrackDetail> GetTrackAsync(Guid trackId, CancellationToken cancellationToken = default);
	Task<HttpResponseMessage> ExportTrackGpxAsync(Guid trackId, CancellationToken cancellationToken = default);

	/// <summary>
	/// <c>PATCH /api/v1/tracks/{id}</c> — renames a stored track, recorded or imported (§15.1).
	/// <para>
	/// Carries no version: a rename moves no point, so it cannot conflict with an edit the way one
	/// edit conflicts with another (§15.5).
	/// </para>
	/// </summary>
	Task<TrackSummary> RenameTrackAsync(Guid trackId, RenameTrackRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	/// <c>DELETE /api/v1/tracks/{id}</c> — deletes the track, its markers and its points.
	/// Irreversible, and refused while a live ride is using it as a planned route (§15.4).
	/// </summary>
	Task DeleteTrackAsync(Guid trackId, CancellationToken cancellationToken = default);

	/// <summary><c>GET /api/v1/tracks/{id}/points</c> — full-resolution points for the editor (§15.5).</summary>
	Task<TrackPointsResponse> GetTrackPointsAsync(Guid trackId, CancellationToken cancellationToken = default);

	/// <summary><c>POST /api/v1/tracks/{id}/edit</c> (§15.5).</summary>
	Task<TrackEditResponse> EditTrackAsync(Guid trackId, EditTrackRequest request, CancellationToken cancellationToken = default);

	/// <summary><c>POST /api/v1/tracks/{id}/edit/undo</c> (§15.6).</summary>
	Task<TrackEditResponse> UndoTrackEditAsync(Guid trackId, CancellationToken cancellationToken = default);

	/// <summary><c>DELETE /api/v1/tracks/{id}/previous-version</c> — remove the retained original now (§15.6).</summary>
	Task PurgeTrackPreviousVersionAsync(Guid trackId, CancellationToken cancellationToken = default);

	/// <summary>
	/// <c>PATCH /api/v1/tracks/{id}/details</c> — the description, the cover photograph and
	/// whether the route is shared with everyone (§6.2).
	/// <para>
	/// All three go together because the screen behind it is one panel with one Save. Carries no
	/// version, on <see cref="RenameTrackAsync"/>'s reasoning: none of it moves a point.
	/// </para>
	/// </summary>
	Task<TrackSummary> UpdateTrackDetailsAsync(Guid trackId, UpdateTrackDetailsRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	/// <c>GET /api/v1/tracks/shared</c> — one page of the routes other riders have shared (§6.2).
	/// <para>
	/// Not on <see cref="ITrackRepository"/>, deliberately. That interface is the offline seam —
	/// Phase 2 backs it with SQLite so a rider's own tracks are there in a tunnel (§4.4, §18.6) —
	/// and browsing what strangers published today is the one track read that has no offline
	/// answer at all. A repository method that could only ever throw on the phone would be worse
	/// than not having one.
	/// </para>
	/// </summary>
	Task<SharedTrackPage> ListSharedTracksAsync(SharedTrackQuery query, CancellationToken cancellationToken = default);

	/// <summary>
	/// <c>GET /api/v1/tracks/{id}/rating</c> — the average, the count and the caller's own star
	/// rating for one shared route (§6.2).
	/// </summary>
	Task<TrackRatingSummary> GetTrackRatingAsync(Guid trackId, CancellationToken cancellationToken = default);

	/// <summary>
	/// <c>PUT /api/v1/tracks/{id}/rating</c> — rates a shared route, replacing whatever the caller
	/// gave it before.
	/// </summary>
	Task<TrackRatingSummary> RateTrackAsync(Guid trackId, RateTrackRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	/// <c>DELETE /api/v1/tracks/{id}/rating</c> — withdraws the caller's rating. Never a zero:
	/// a stored nought would count as the worst possible score rather than as no opinion.
	/// </summary>
	Task<TrackRatingSummary> ClearTrackRatingAsync(Guid trackId, CancellationToken cancellationToken = default);

	// -- Group rides (§5.2, §5.6, §5.8) ---------------------------------------------------

	/// <summary><c>GET /api/v1/group-rides</c> — the caller's rides, split by role.</summary>
	Task<MyRides> ListMyRidesAsync(CancellationToken cancellationToken = default);

	/// <summary><c>POST /api/v1/group-rides</c>.</summary>
	Task<RideDetail> CreateRideAsync(CreateRideRequest request, CancellationToken cancellationToken = default);

	/// <summary><c>POST /api/v1/group-rides/join</c> — the join-code path (§5.2 path 1).</summary>
	Task<JoinResult> JoinRideByCodeAsync(JoinByCodeRequest request, CancellationToken cancellationToken = default);

	/// <summary><c>GET /api/v1/group-rides/{id}</c>.</summary>
	Task<RideDetail> GetRideAsync(Guid rideId, CancellationToken cancellationToken = default);

	/// <summary><c>GET /api/v1/group-rides/{id}/join-requests</c> — organiser only.</summary>
	Task<IReadOnlyList<JoinRequestSummary>> ListJoinRequestsAsync(Guid rideId, CancellationToken cancellationToken = default);

	/// <summary><c>POST /api/v1/group-rides/{id}/join-requests/{requestId}</c> — admit or decline.</summary>
	Task DecideJoinRequestAsync(Guid rideId, Guid requestId, DecideJoinRequest request, CancellationToken cancellationToken = default);

	/// <summary><c>PUT /api/v1/group-rides/{id}/permissions</c> — the organiser's three content switches (§5.8).</summary>
	Task UpdatePermissionsAsync(Guid rideId, RidePermissions permissions, CancellationToken cancellationToken = default);

	/// <summary><c>PUT /api/v1/group-rides/{id}/sharing/me</c> — the rider's own sharing decision (§5.6).</summary>
	Task SetSharingAsync(Guid rideId, SetSharingRequest request, CancellationToken cancellationToken = default);

	/// <summary><c>DELETE /api/v1/group-rides/{id}/members/me</c> — leave the ride.</summary>
	Task LeaveRideAsync(Guid rideId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Takes back a join request the caller made and nobody has answered (§5.2).
	/// <para>
	/// The counterpart to <see cref="LeaveRideAsync"/> for somebody who never got in. Idempotent:
	/// a request that has already been answered or already withdrawn succeeds quietly, because the
	/// caller asked for it to be gone and it is.
	/// </para>
	/// </summary>
	/// <param name="rideId">Which adventure they asked about.</param>
	/// <param name="requestId">Their pending request.</param>
	/// <param name="cancellationToken">Cancels the call.</param>
	Task WithdrawJoinRequestAsync(Guid rideId, Guid requestId, CancellationToken cancellationToken = default);

	/// <summary><c>DELETE /api/v1/group-rides/{id}/members/{userId}</c> — organiser removes a member.</summary>
	Task RemoveMemberAsync(Guid rideId, Guid userId, CancellationToken cancellationToken = default);

	/// <summary>
	/// <c>DELETE /api/v1/group-rides/{id}</c> — the organiser deletes the whole adventure.
	/// <para>
	/// Irreversible, and the only way to finish one. It takes the thread, the markers and every
	/// stored position with it, and blanks the map for anyone still on the road.
	/// </para>
	/// </summary>
	Task DeleteRideAsync(Guid rideId, CancellationToken cancellationToken = default);

	// -- Planned routes (§5.4) ------------------------------------------------------------

	/// <summary>
	/// <c>GET /api/v1/group-rides/{id}/routes</c> — the ride's planned routes, oldest first.
	/// <para>
	/// The only way a member reads a route somebody else owns: <c>GET /tracks/{id}</c> is
	/// owner-scoped and answers 404 to everybody else (§15.4).
	/// </para>
	/// </summary>
	Task<IReadOnlyList<RideRoute>> ListRideRoutesAsync(Guid rideId, CancellationToken cancellationToken = default);

	/// <summary><c>POST /api/v1/group-rides/{id}/routes</c> — attach one of the caller's tracks.</summary>
	Task<RideRoute> AddRideRouteAsync(Guid rideId, AddRideRouteRequest request, CancellationToken cancellationToken = default);

	/// <summary><c>DELETE /api/v1/group-rides/{id}/routes/{trackId}</c> — detach; the track is untouched.</summary>
	Task RemoveRideRouteAsync(Guid rideId, Guid trackId, CancellationToken cancellationToken = default);

	// -- Positions (§5.3, §5.7) -----------------------------------------------------------

	/// <summary><c>GET /api/v1/group-rides/{id}/positions</c> — snapshot after reconnect (§5.3).</summary>
	Task<IReadOnlyList<RiderPositionDto>> GetPositionsSnapshotAsync(Guid rideId, CancellationToken cancellationToken = default);

	/// <summary>
	/// <c>POST /api/v1/positions</c> — one publish, fanned out server-side to every ride
	/// the rider is live in (§5.7). Deliberately not the SignalR hub — Phase 2 tolerates
	/// the extra round trip in exchange for retryable HTTP semantics.
	/// </summary>
	Task<PublishResult> PublishPositionAsync(PositionUpdate update, CancellationToken cancellationToken = default);

	/// <summary>
	/// <c>POST /api/v1/positions/privacy</c> — the device saying this rider has entered or left
	/// their own private area (§10.1).
	/// <para>
	/// <strong>No coordinate travels, in either direction.</strong> A fix from inside the circle is
	/// dropped where it was read; this is the one bit that goes instead, and it is what turns a pin
	/// frozen outside somebody's house into a member row that says "private". Going private deletes
	/// the rider's stored position on the server — suppression, not obfuscation.
	/// </para>
	/// <para>
	/// The hub carries this too and is the ordinary path. This exists because losing it is expensive
	/// in a way that losing one fix is not: it is sent once, at the edge of the circle, rather than
	/// every tick.
	/// </para>
	/// </summary>
	/// <param name="update">Which way the rider crossed the edge of their circle.</param>
	/// <param name="cancellationToken">Abandons the call.</param>
	/// <returns>The rides that were told, empty when the server already believed it.</returns>
	Task<PublishResult> SetPositionPrivacyAsync(PositionPrivacyUpdate update, CancellationToken cancellationToken = default);

	// -- Markers (§16) --------------------------------------------------------------------

	/// <summary><c>POST /api/v1/markers</c> — attaches to exactly one parent (§16.1).</summary>
	Task<MarkerDto> CreateMarkerAsync(CreateMarkerRequest request, CancellationToken cancellationToken = default);

	/// <summary><c>GET /api/v1/group-rides/{id}/markers</c>.</summary>
	Task<IReadOnlyList<MarkerDto>> ListRideMarkersAsync(Guid rideId, CancellationToken cancellationToken = default);

	/// <summary><c>PUT /api/v1/markers/{id}</c>.</summary>
	Task<MarkerDto> UpdateMarkerAsync(Guid markerId, UpdateMarkerRequest request, CancellationToken cancellationToken = default);

	/// <summary><c>DELETE /api/v1/markers/{id}</c>.</summary>
	Task DeleteMarkerAsync(Guid markerId, CancellationToken cancellationToken = default);

	/// <summary><c>PATCH /api/v1/markers/{id}/photo</c> — attach or detach (§16.4).</summary>
	Task AttachMarkerPhotoAsync(Guid markerId, AttachPhotoRequest request, CancellationToken cancellationToken = default);

	// -- Photos (§16.4) -------------------------------------------------------------------

	/// <summary>
	/// <c>POST /api/v1/photos</c> — one multipart upload. Server re-encodes and strips
	/// metadata (§16.4). The client passes any bytes it can pick; the server sniffs.
	/// </summary>
	Task<PhotoUploaded> UploadPhotoAsync(Stream content, string contentType, string fileName, CancellationToken cancellationToken = default);

	// -- Comments (§17, §6.2) -------------------------------------------------------------

	/// <summary><c>GET /api/v1/group-rides/{id}/comments</c> — thread page, pinned first (§17.8).</summary>
	Task<CommentPage> GetThreadAsync(Guid rideId, string? cursor, CancellationToken cancellationToken = default);

	/// <summary><c>POST /api/v1/group-rides/{id}/comments</c> — post text, photo or poll.</summary>
	Task<CommentDto> PostCommentAsync(Guid rideId, PostCommentRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	/// <c>GET /api/v1/tracks/{id}/comments</c> — a shared route's thread, pinned first (§6.2).
	/// <para>
	/// A separate pair of methods rather than one that takes a discriminated union, because the
	/// two paths are the only thing that differs and a union would be a type invented so that two
	/// string literals could share a method. Everything downstream — editing, deleting, pinning,
	/// reacting, voting, reporting — already keys on the comment's own id and is shared as it
	/// stands.
	/// </para>
	/// </summary>
	Task<CommentPage> GetTrackThreadAsync(Guid trackId, string? cursor, CancellationToken cancellationToken = default);

	/// <summary><c>POST /api/v1/tracks/{id}/comments</c> — post to a shared route's thread.</summary>
	Task<CommentDto> PostTrackCommentAsync(Guid trackId, PostCommentRequest request, CancellationToken cancellationToken = default);

	/// <summary><c>PATCH /api/v1/comments/{id}</c> — author edit within window.</summary>
	Task<CommentDto> EditCommentAsync(Guid commentId, EditCommentRequest request, CancellationToken cancellationToken = default);

	/// <summary><c>DELETE /api/v1/comments/{id}</c> — author or organiser.</summary>
	Task DeleteCommentAsync(Guid commentId, CancellationToken cancellationToken = default);

	/// <summary><c>POST /api/v1/comments/{id}/pin</c> — organiser or leader.</summary>
	Task PinCommentAsync(Guid commentId, PinCommentRequest request, CancellationToken cancellationToken = default);

	/// <summary><c>PUT /api/v1/comments/{id}/reaction</c> — null clears.</summary>
	Task SetReactionAsync(Guid commentId, SetReactionRequest request, CancellationToken cancellationToken = default);

	/// <summary><c>POST /api/v1/comments/{id}/votes</c> — full set of choices (empty clears).</summary>
	Task CastVoteAsync(Guid commentId, CastVoteRequest request, CancellationToken cancellationToken = default);

	/// <summary><c>POST /api/v1/comments/{id}/close-poll</c> — author or organiser.</summary>
	Task ClosePollAsync(Guid commentId, CancellationToken cancellationToken = default);

	// -- Moderation (§17.7) ---------------------------------------------------------------

	/// <summary><c>POST /api/v1/comments/{id}/report</c>.</summary>
	Task<ContentReported> ReportCommentAsync(Guid commentId, ReportContentRequest request, CancellationToken cancellationToken = default);

	/// <summary><c>POST /api/v1/markers/{id}/report</c>.</summary>
	Task<ContentReported> ReportMarkerAsync(Guid markerId, ReportContentRequest request, CancellationToken cancellationToken = default);

	/// <summary><c>POST /api/v1/blocks</c>.</summary>
	Task BlockUserAsync(BlockUserRequest request, CancellationToken cancellationToken = default);

	/// <summary><c>DELETE /api/v1/blocks/{userId}</c>.</summary>
	Task UnblockUserAsync(Guid userId, CancellationToken cancellationToken = default);

	/// <summary><c>GET /api/v1/blocks</c> — the caller's own list.</summary>
	Task<IReadOnlyList<BlockedRider>> ListBlocksAsync(CancellationToken cancellationToken = default);

	// -- Account (§6.3, §10.1) ------------------------------------------------------------

	/// <summary>
	/// <c>GET /api/v1/me/export</c> — a ZIP containing the caller's data (§6.3). Streamed
	/// rather than materialised: for a rider with a large ride library this can be tens of
	/// megabytes, and the browser downloads it directly rather than loading it into memory
	/// through Blazor.
	/// </summary>
	Task<HttpResponseMessage> ExportAccountAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// <c>DELETE /api/v1/me</c> — irreversible, and it takes the current password in the
	/// body (§6.3). A fifteen-minute access token lifted off a shared machine is not enough
	/// to end an account.
	/// </summary>
	Task DeleteAccountAsync(DeleteAccountRequest request, CancellationToken cancellationToken = default);

	// -- Administration (§14.6) -----------------------------------------------------------
	//
	// All three are 403 for everybody not named in the server's Admins roster, and the screens
	// behind them are only reachable from a Settings entry that OwnProfile.IsAdmin unlocks. The
	// flag is a convenience for the menu; these are still checked server-side on every call.

	/// <summary>
	/// <c>GET /api/v1/admin/users</c> — every account, with what it has put into the service.
	/// </summary>
	/// <param name="search">Filters by username, or null for everybody.</param>
	/// <param name="skip">How many rows to step over.</param>
	/// <param name="take">How many rows to return.</param>
	/// <param name="cancellationToken">Abandons the call.</param>
	Task<IReadOnlyList<AdminUserRow>> AdminUsersAsync(
		string? search = null,
		int skip = 0,
		int take = 50,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// <c>GET /api/v1/admin/logs</c> — the newest entries in the server's log file.
	/// </summary>
	/// <param name="day">Which day, or null for the newest the server holds.</param>
	/// <param name="level">Lowest level to include, or null for everything.</param>
	/// <param name="take">How many lines.</param>
	/// <param name="databaseCommands">
	/// Whether EF Core's statement lines count against <paramref name="take"/>. False makes the
	/// server step over them while reading, so the cap is spent on the lines the caller came for.
	/// </param>
	/// <param name="cancellationToken">Abandons the call.</param>
	Task<AdminLogPage> AdminLogsAsync(
		DateOnly? day = null,
		string? level = null,
		int take = 200,
		bool databaseCommands = true,
		CancellationToken cancellationToken = default);

	/// <summary><c>GET /api/v1/admin/stats</c> — activity, live rides and fixes per minute.</summary>
	/// <param name="cancellationToken">Abandons the call.</param>
	Task<AdminStats> AdminStatsAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// <c>DELETE /api/v1/admin/users/{id}</c> — erases an account and everything it owns.
	/// </summary>
	/// <param name="userId">Which account.</param>
	/// <param name="request">The handle the caller last saw against that id.</param>
	/// <param name="cancellationToken">Abandons the call.</param>
	/// <remarks>
	/// Irreversible, and refused for the caller's own account and for anyone on the server's
	/// roster — see the endpoint for why the second of those is a security guard rather than
	/// politeness.
	/// </remarks>
	Task AdminDeleteUserAsync(
		Guid userId,
		AdminDeleteUserRequest request,
		CancellationToken cancellationToken = default);
}

/// <summary>The AGPL §13 offer, minted server-side (§14.6.2).</summary>
public sealed record AboutInfo(
	string Licence,
	string SourceUrl,
	string Commit,
	string Version,
	DateTimeOffset? BuiltUtc);
