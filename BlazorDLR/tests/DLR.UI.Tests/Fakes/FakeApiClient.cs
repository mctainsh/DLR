using System.Collections.Concurrent;
using BlazorDLR.Shared.Services;
using DLR.Core.Contracts.Account;
using DLR.Core.Contracts.Comments;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Moderation;
using DLR.Core.Contracts.Photos;
using DLR.Core.Contracts.Rides;
using DLR.Core.Contracts.Tracks;

namespace DLR.UI.Tests.Fakes;

/// <summary>
/// A hand-written <see cref="IApiClient"/> that records what the UI asked for and lets
/// tests hand back canned responses. Deliberately not a mocking framework — the interface
/// is 30-odd methods and one bespoke fake reads more clearly than 30 lambdas set up per
/// test.
/// <para>
/// The pattern: a test wires up one or two <c>Result</c> fields, renders the component,
/// and inspects <c>Calls</c>. Every method throws by default so a component making an
/// unexpected call fails loudly rather than silently.
/// </para>
/// </summary>
public sealed class FakeApiClient : IApiClient
{
	/// <summary>Every method name the UI called, in order.</summary>
	public ConcurrentQueue<string> Calls { get; } = new();

	// Result fields — set from a test, read from the interface method.
	public AboutInfo AboutResult { get; set; } =
		new("AGPL-3.0-only", "https://github.com/dumbluckrides/dlr", "abcd1234", "1.0.0+abcd1234", null);

	public TokenResponse? TokenResult { get; set; }
	public bool UserNameAvailableResult { get; set; } = true;
	public OwnProfile? ProfileResult { get; set; }
	public IReadOnlyList<TrackSummary> TracksResult { get; set; } = Array.Empty<TrackSummary>();
	public TrackDetail? TrackDetailResult { get; set; }
	public IReadOnlyList<DeviceSession> SessionsResult { get; set; } = Array.Empty<DeviceSession>();
	public IReadOnlyList<BlockedRider> BlocksResult { get; set; } = Array.Empty<BlockedRider>();
	public RideDetail? RideResult { get; set; }
	public IReadOnlyList<RiderPositionDto> PositionsResult { get; set; } = Array.Empty<RiderPositionDto>();
	public IReadOnlyList<MarkerDto> MarkersResult { get; set; } = Array.Empty<MarkerDto>();
	public CommentPage? ThreadResult { get; set; }
	public IReadOnlyList<JoinRequestSummary> JoinRequestsResult { get; set; } = Array.Empty<JoinRequestSummary>();
	public TrackPointsResponse? TrackPointsResult { get; set; }
	public TrackEditResponse? EditTrackResult { get; set; }

	/// <summary>The last <see cref="EditTrackAsync"/> request the UI sent, for §15.5 assertions.</summary>
	public EditTrackRequest? LastEditTrackRequest { get; private set; }

	/// <summary>Set to make Token / Register throw the given ApiException.</summary>
	public ApiException? TokenException { get; set; }

	private T Recorded<T>(string method, T result)
	{
		Calls.Enqueue(method);
		return result;
	}

	private void Record(string method) => Calls.Enqueue(method);

	public Task<AboutInfo> GetAboutAsync(CancellationToken cancellationToken = default) =>
		Task.FromResult(Recorded(nameof(GetAboutAsync), AboutResult));

	public Task<TokenResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(RegisterAsync));
		if (TokenException is not null) throw TokenException;
		return Task.FromResult(TokenResult
			?? new TokenResponse("access", 900, "refresh", new AuthenticatedUser(Guid.NewGuid(), request.UserName, request.Email is not null, false)));
	}

	public Task<TokenResponse> TokenAsync(TokenRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(TokenAsync));
		if (TokenException is not null) throw TokenException;
		return Task.FromResult(TokenResult
			?? new TokenResponse("access", 900, "refresh", new AuthenticatedUser(Guid.NewGuid(), request.UserName ?? "test", false, false)));
	}

	public Task<bool> IsUserNameAvailableAsync(string userName, CancellationToken cancellationToken = default) =>
		Task.FromResult(Recorded(nameof(IsUserNameAvailableAsync), UserNameAvailableResult));

	public Task SetEmailAsync(SetEmailRequest request, CancellationToken cancellationToken = default) { Record(nameof(SetEmailAsync)); return Task.CompletedTask; }
	public Task<TokenResponse> ConfirmEmailAsync(ConfirmEmailRequest request, CancellationToken cancellationToken = default) => Task.FromResult(Recorded(nameof(ConfirmEmailAsync), TokenResult ?? new TokenResponse("access", 900, "refresh", new AuthenticatedUser(request.UserId, "test", true, true))));
	public Task ResendConfirmationAsync(CancellationToken cancellationToken = default) { Record(nameof(ResendConfirmationAsync)); return Task.CompletedTask; }
	public Task ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken = default) { Record(nameof(ForgotPasswordAsync)); return Task.CompletedTask; }
	public Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default) { Record(nameof(ResetPasswordAsync)); return Task.CompletedTask; }
	public Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default) { Record(nameof(ChangePasswordAsync)); return Task.CompletedTask; }
	public Task<IReadOnlyList<DeviceSession>> ListSessionsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Recorded(nameof(ListSessionsAsync), SessionsResult));
	public Task RevokeSessionAsync(Guid deviceId, CancellationToken cancellationToken = default) { Record(nameof(RevokeSessionAsync)); return Task.CompletedTask; }

	public Task<OwnProfile> GetProfileAsync(CancellationToken cancellationToken = default) =>
		Task.FromResult(Recorded(nameof(GetProfileAsync), ProfileResult ?? new OwnProfile(null, null, null, false, false, false, false)));
	public Task UpdateProfileAsync(UpdateProfileRequest request, CancellationToken cancellationToken = default) { Record(nameof(UpdateProfileAsync)); return Task.CompletedTask; }

	public Task<IReadOnlyList<TrackSummary>> ListTracksAsync(CancellationToken cancellationToken = default) => Task.FromResult(Recorded(nameof(ListTracksAsync), TracksResult));
	private static readonly DateTimeOffset SampleInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	public Task<TrackDetail> GetTrackAsync(Guid trackId, CancellationToken cancellationToken = default) =>
		Task.FromResult(Recorded(nameof(GetTrackAsync), TrackDetailResult
			?? new TrackDetail(new TrackSummary(trackId, "Test", SampleInstant, null, null, 0, null, null, null, 0, 1, TrackSourceDto.Recorded, 1), null, Array.Empty<DLR.Core.Tracks.TrackPoint>())));
	public Task<TrackImportResult> ImportTracksAsync(Stream file, string fileName, bool dryRun, CancellationToken cancellationToken = default) => throw new NotImplementedException();
	public Task<HttpResponseMessage> ExportTrackGpxAsync(Guid trackId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
	public Task<TrackPointsResponse> GetTrackPointsAsync(Guid trackId, CancellationToken cancellationToken = default) =>
		Task.FromResult(Recorded(nameof(GetTrackPointsAsync), TrackPointsResult
			?? new TrackPointsResponse(1, 100, "", null, null, new[] { 0 })));
	public Task<TrackEditResponse> EditTrackAsync(Guid trackId, EditTrackRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(EditTrackAsync));
		LastEditTrackRequest = request;
		return Task.FromResult(EditTrackResult
			?? new TrackEditResponse(new TrackSummary(trackId, "Test", SampleInstant, null, null, 0, null, null, null, 0, 2, TrackSourceDto.Recorded, request.Version + 1), null));
	}
	public Task<TrackEditResponse> UndoTrackEditAsync(Guid trackId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
	public Task PurgeTrackPreviousVersionAsync(Guid trackId, CancellationToken cancellationToken = default) => throw new NotImplementedException();

	public Task<RideDetail> GetRideAsync(Guid rideId, CancellationToken cancellationToken = default) =>
		Task.FromResult(Recorded(nameof(GetRideAsync), RideResult
			?? new RideDetail(rideId, "Test ride", null, SampleInstant, RideStateDto.Open, JoinPolicyDto.Approval, 50, 0, false, null, new RidePermissions(), Array.Empty<RideMemberSummary>())));
	public Task<RideDetail> CreateRideAsync(CreateRideRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
	public Task<JoinResult> JoinRideByCodeAsync(JoinByCodeRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
	public Task<IReadOnlyList<JoinRequestSummary>> ListJoinRequestsAsync(Guid rideId, CancellationToken cancellationToken = default) => Task.FromResult(Recorded(nameof(ListJoinRequestsAsync), JoinRequestsResult));
	public Task DecideJoinRequestAsync(Guid rideId, Guid requestId, DecideJoinRequest request, CancellationToken cancellationToken = default) { Record(nameof(DecideJoinRequestAsync)); return Task.CompletedTask; }
	public Task StartRideAsync(Guid rideId, CancellationToken cancellationToken = default) { Record(nameof(StartRideAsync)); return Task.CompletedTask; }
	public Task EndRideAsync(Guid rideId, EndRideRequest request, CancellationToken cancellationToken = default) { Record(nameof(EndRideAsync)); return Task.CompletedTask; }
	public Task UpdatePermissionsAsync(Guid rideId, RidePermissions permissions, CancellationToken cancellationToken = default) { Record(nameof(UpdatePermissionsAsync)); return Task.CompletedTask; }
	public Task SetSharingAsync(Guid rideId, SetSharingRequest request, CancellationToken cancellationToken = default) { Record(nameof(SetSharingAsync)); return Task.CompletedTask; }
	public Task LeaveRideAsync(Guid rideId, CancellationToken cancellationToken = default) { Record(nameof(LeaveRideAsync)); return Task.CompletedTask; }
	public Task RemoveMemberAsync(Guid rideId, Guid userId, CancellationToken cancellationToken = default) { Record(nameof(RemoveMemberAsync)); return Task.CompletedTask; }

	public Task<IReadOnlyList<RiderPositionDto>> GetPositionsSnapshotAsync(Guid rideId, CancellationToken cancellationToken = default) => Task.FromResult(Recorded(nameof(GetPositionsSnapshotAsync), PositionsResult));
	public Task<PublishResult> PublishPositionAsync(PositionUpdate update, CancellationToken cancellationToken = default) => throw new NotImplementedException();

	/// <summary>The last <see cref="CreateMarkerAsync"/> request the UI sent, for §16.2 assertions.</summary>
	public CreateMarkerRequest? LastCreateMarkerRequest { get; private set; }

	public Task<MarkerDto> CreateMarkerAsync(CreateMarkerRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(CreateMarkerAsync));
		LastCreateMarkerRequest = request;
		return Task.FromResult(new MarkerDto(
			Id: Guid.NewGuid(),
			TrackId: request.TrackId,
			GroupRideId: request.GroupRideId,
			Lat: request.Lat,
			Lon: request.Lon,
			Icon: request.Icon,
			Title: request.Title,
			Note: request.Note,
			DirectionDeg: request.DirectionDeg,
			PhotoId: null,
			CreatedByUserId: Guid.NewGuid(),
			CreatedByUserName: "test",
			CreatedUtc: SampleInstant,
			UpdatedUtc: SampleInstant));
	}
	public Task<IReadOnlyList<MarkerDto>> ListRideMarkersAsync(Guid rideId, CancellationToken cancellationToken = default) => Task.FromResult(Recorded(nameof(ListRideMarkersAsync), MarkersResult));
	public Task<MarkerDto> UpdateMarkerAsync(Guid markerId, UpdateMarkerRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
	public Task DeleteMarkerAsync(Guid markerId, CancellationToken cancellationToken = default) { Record(nameof(DeleteMarkerAsync)); return Task.CompletedTask; }
	public Task AttachMarkerPhotoAsync(Guid markerId, AttachPhotoRequest request, CancellationToken cancellationToken = default) { Record(nameof(AttachMarkerPhotoAsync)); return Task.CompletedTask; }

	public Task<PhotoUploaded> UploadPhotoAsync(Stream content, string contentType, string fileName, CancellationToken cancellationToken = default) => throw new NotImplementedException();

	public Task<CommentPage> GetThreadAsync(Guid rideId, string? cursor, CancellationToken cancellationToken = default) =>
		Task.FromResult(Recorded(nameof(GetThreadAsync), ThreadResult ?? new CommentPage(Array.Empty<CommentDto>(), Array.Empty<CommentDto>(), null)));
	public Task<CommentDto> PostCommentAsync(Guid rideId, PostCommentRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
	public Task<CommentDto> EditCommentAsync(Guid commentId, EditCommentRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
	public Task DeleteCommentAsync(Guid commentId, CancellationToken cancellationToken = default) { Record(nameof(DeleteCommentAsync)); return Task.CompletedTask; }
	public Task PinCommentAsync(Guid commentId, PinCommentRequest request, CancellationToken cancellationToken = default) { Record(nameof(PinCommentAsync)); return Task.CompletedTask; }
	public Task SetReactionAsync(Guid commentId, SetReactionRequest request, CancellationToken cancellationToken = default) { Record(nameof(SetReactionAsync)); return Task.CompletedTask; }
	public Task CastVoteAsync(Guid commentId, CastVoteRequest request, CancellationToken cancellationToken = default) { Record(nameof(CastVoteAsync)); return Task.CompletedTask; }
	public Task ClosePollAsync(Guid commentId, CancellationToken cancellationToken = default) { Record(nameof(ClosePollAsync)); return Task.CompletedTask; }

	public Task<ContentReported> ReportCommentAsync(Guid commentId, ReportContentRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
	public Task<ContentReported> ReportMarkerAsync(Guid markerId, ReportContentRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
	public Task BlockUserAsync(BlockUserRequest request, CancellationToken cancellationToken = default) { Record(nameof(BlockUserAsync)); return Task.CompletedTask; }
	public Task UnblockUserAsync(Guid userId, CancellationToken cancellationToken = default) { Record(nameof(UnblockUserAsync)); return Task.CompletedTask; }
	public Task<IReadOnlyList<BlockedRider>> ListBlocksAsync(CancellationToken cancellationToken = default) => Task.FromResult(Recorded(nameof(ListBlocksAsync), BlocksResult));

	public Task<HttpResponseMessage> ExportAccountAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
	public Task DeleteAccountAsync(DeleteAccountRequest request, CancellationToken cancellationToken = default) { Record(nameof(DeleteAccountAsync)); return Task.CompletedTask; }
}
