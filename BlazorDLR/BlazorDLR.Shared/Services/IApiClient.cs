using DLR.Core.Contracts.Account;
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

	/// <summary><c>GET /api/v1/tracks/{id}/points</c> — full-resolution points for the editor (§15.5).</summary>
	Task<TrackPointsResponse> GetTrackPointsAsync(Guid trackId, CancellationToken cancellationToken = default);

	/// <summary><c>POST /api/v1/tracks/{id}/edit</c> (§15.5).</summary>
	Task<TrackEditResponse> EditTrackAsync(Guid trackId, EditTrackRequest request, CancellationToken cancellationToken = default);

	/// <summary><c>POST /api/v1/tracks/{id}/edit/undo</c> (§15.6).</summary>
	Task<TrackEditResponse> UndoTrackEditAsync(Guid trackId, CancellationToken cancellationToken = default);

	/// <summary><c>DELETE /api/v1/tracks/{id}/previous-version</c> — remove the retained original now (§15.6).</summary>
	Task PurgeTrackPreviousVersionAsync(Guid trackId, CancellationToken cancellationToken = default);

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

	/// <summary><c>POST /api/v1/group-rides/{id}/start</c>.</summary>
	Task StartRideAsync(Guid rideId, CancellationToken cancellationToken = default);

	/// <summary><c>POST /api/v1/group-rides/{id}/ending</c> — end with immediate or wind-down (§5.6).</summary>
	Task EndRideAsync(Guid rideId, EndRideRequest request, CancellationToken cancellationToken = default);

	/// <summary><c>PUT /api/v1/group-rides/{id}/permissions</c> — the organiser's three content switches (§5.8).</summary>
	Task UpdatePermissionsAsync(Guid rideId, RidePermissions permissions, CancellationToken cancellationToken = default);

	/// <summary><c>PUT /api/v1/group-rides/{id}/sharing/me</c> — the rider's own sharing decision (§5.6).</summary>
	Task SetSharingAsync(Guid rideId, SetSharingRequest request, CancellationToken cancellationToken = default);

	/// <summary><c>DELETE /api/v1/group-rides/{id}/members/me</c> — leave the ride.</summary>
	Task LeaveRideAsync(Guid rideId, CancellationToken cancellationToken = default);

	/// <summary><c>DELETE /api/v1/group-rides/{id}/members/{userId}</c> — organiser removes a member.</summary>
	Task RemoveMemberAsync(Guid rideId, Guid userId, CancellationToken cancellationToken = default);

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

	// -- Comments (§17) -------------------------------------------------------------------

	/// <summary><c>GET /api/v1/group-rides/{id}/comments</c> — thread page, pinned first (§17.8).</summary>
	Task<CommentPage> GetThreadAsync(Guid rideId, string? cursor, CancellationToken cancellationToken = default);

	/// <summary><c>POST /api/v1/group-rides/{id}/comments</c> — post text, photo or poll.</summary>
	Task<CommentDto> PostCommentAsync(Guid rideId, PostCommentRequest request, CancellationToken cancellationToken = default);

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
}

/// <summary>The AGPL §13 offer, minted server-side (§14.6.2).</summary>
public sealed record AboutInfo(
	string Licence,
	string SourceUrl,
	string Commit,
	string Version,
	DateTimeOffset? BuiltUtc);
