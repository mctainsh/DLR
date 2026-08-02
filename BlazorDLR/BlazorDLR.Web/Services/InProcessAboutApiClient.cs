using BlazorDLR.Shared.Services;
using DLR.Core.Contracts.Account;
using DLR.Core.Contracts.Comments;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Moderation;
using DLR.Core.Contracts.Photos;
using DLR.Core.Contracts.Rides;
using DLR.Core.Contracts.Tracks;
using DLR.Server.Api;
using Microsoft.Extensions.Options;

namespace BlazorDLR.Web.Services;

/// <summary>
/// The server-side SSR pass renders shared components in the same process that serves the API,
/// so <see cref="IApiClient"/> reaching the About endpoint over HTTP would be a roundtrip to
/// itself. This shim answers <see cref="GetAboutAsync"/> directly from the services already
/// registered for <c>AboutController</c>; every other method throws, because the WASM client
/// that boots after SSR re-resolves the shared services against its own DI and answers those
/// calls there.
/// </summary>
public sealed class InProcessAboutApiClient : IApiClient
{
	private readonly BuildInformation _build;
	private readonly IOptions<AboutOptions> _about;

	public InProcessAboutApiClient(BuildInformation build, IOptions<AboutOptions> about)
	{
		_build = build;
		_about = about;
	}

	/// <inheritdoc />
	public Task<AboutInfo> GetAboutAsync(CancellationToken cancellationToken = default) =>
		Task.FromResult(new AboutInfo(
			Licence: AboutOptions.Licence,
			SourceUrl: _about.Value.SourceUrl,
			Commit: _build.Commit,
			Version: _build.Version,
			BuiltUtc: _build.BuiltUtc));

	private static readonly string SsrGuard =
		"The SSR shell renders the shared components but has no signed-in session — the WASM " +
		"client that boots after it re-resolves this interface and handles authed calls. If a " +
		"component is calling this during a static render, it is a wiring bug.";

	public Task<TokenResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<TokenResponse> TokenAsync(TokenRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<bool> IsUserNameAvailableAsync(string userName, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task SetEmailAsync(SetEmailRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<TokenResponse> ConfirmEmailAsync(ConfirmEmailRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task ResendConfirmationAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<IReadOnlyList<DeviceSession>> ListSessionsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task RevokeSessionAsync(Guid deviceId, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<OwnProfile> GetProfileAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task UpdateProfileAsync(UpdateProfileRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<IReadOnlyList<TrackSummary>> ListTracksAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<TrackDetail> GetTrackAsync(Guid trackId, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<TrackImportResult> ImportTracksAsync(Stream file, string fileName, bool dryRun, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<HttpResponseMessage> ExportTrackGpxAsync(Guid trackId, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<TrackPointsResponse> GetTrackPointsAsync(Guid trackId, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<TrackEditResponse> EditTrackAsync(Guid trackId, EditTrackRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<TrackEditResponse> UndoTrackEditAsync(Guid trackId, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task PurgeTrackPreviousVersionAsync(Guid trackId, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<RideDetail> GetRideAsync(Guid rideId, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<RideDetail> CreateRideAsync(CreateRideRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<JoinResult> JoinRideByCodeAsync(JoinByCodeRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<IReadOnlyList<JoinRequestSummary>> ListJoinRequestsAsync(Guid rideId, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task DecideJoinRequestAsync(Guid rideId, Guid requestId, DecideJoinRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task StartRideAsync(Guid rideId, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task EndRideAsync(Guid rideId, EndRideRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task UpdatePermissionsAsync(Guid rideId, RidePermissions permissions, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task SetSharingAsync(Guid rideId, SetSharingRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task LeaveRideAsync(Guid rideId, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task RemoveMemberAsync(Guid rideId, Guid userId, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<IReadOnlyList<RiderPositionDto>> GetPositionsSnapshotAsync(Guid rideId, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<PublishResult> PublishPositionAsync(PositionUpdate update, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<MarkerDto> CreateMarkerAsync(CreateMarkerRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<IReadOnlyList<MarkerDto>> ListRideMarkersAsync(Guid rideId, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<MarkerDto> UpdateMarkerAsync(Guid markerId, UpdateMarkerRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task DeleteMarkerAsync(Guid markerId, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task AttachMarkerPhotoAsync(Guid markerId, AttachPhotoRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<PhotoUploaded> UploadPhotoAsync(Stream content, string contentType, string fileName, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<CommentPage> GetThreadAsync(Guid rideId, string? cursor, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<CommentDto> PostCommentAsync(Guid rideId, PostCommentRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<CommentDto> EditCommentAsync(Guid commentId, EditCommentRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task DeleteCommentAsync(Guid commentId, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task PinCommentAsync(Guid commentId, PinCommentRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task SetReactionAsync(Guid commentId, SetReactionRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task CastVoteAsync(Guid commentId, CastVoteRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task ClosePollAsync(Guid commentId, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<ContentReported> ReportCommentAsync(Guid commentId, ReportContentRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<ContentReported> ReportMarkerAsync(Guid markerId, ReportContentRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task BlockUserAsync(BlockUserRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task UnblockUserAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<IReadOnlyList<BlockedRider>> ListBlocksAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task<HttpResponseMessage> ExportAccountAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
	public Task DeleteAccountAsync(DeleteAccountRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException(SsrGuard);
}
