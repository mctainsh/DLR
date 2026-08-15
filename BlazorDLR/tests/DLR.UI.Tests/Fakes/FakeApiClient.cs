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
		new("AGPL-3.0-only", "https://github.com/mctainsh/dlr", "abcd1234", "1.0.0+abcd1234", null);

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

	/// <summary>Every <see cref="UploadTrackAsync"/> request, in order — what the recorder's tests read.</summary>
	public List<UploadTrackRequest> UploadedTracks { get; } = new();

	/// <summary>Set to make <see cref="UploadTrackAsync"/> throw, for the "save failed" path.</summary>
	public Exception? UploadTrackException { get; set; }

	/// <summary>
	/// Set to make Token / Register throw.
	/// <para>
	/// Typed as the base <see cref="HttpRequestException"/> rather than <see cref="ApiException"/>
	/// so §7.9's distinction can be tested at the token endpoint: an <see cref="ApiException"/> is
	/// the server refusing and carries the status it refused with, while a bare
	/// <see cref="HttpRequestException"/> has no status because there was no response — a rider in
	/// a tunnel, which must never end a session. <see cref="ApiException"/> derives from it, so a
	/// test that was already assigning one is unaffected.
	/// </para>
	/// </summary>
	public HttpRequestException? TokenException { get; set; }

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

	/// <summary>The last address the UI asked the server to store (§7.7).</summary>
	public SetEmailRequest? LastSetEmailRequest { get; private set; }

	/// <summary>Set to make <see cref="SetEmailAsync"/> throw.</summary>
	public ApiException? SetEmailException { get; set; }

	public Task SetEmailAsync(SetEmailRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(SetEmailAsync));
		LastSetEmailRequest = request;
		if (SetEmailException is not null) throw SetEmailException;
		return Task.CompletedTask;
	}

	/// <summary>The last link the confirm page followed (§7.14).</summary>
	public ConfirmEmailRequest? LastConfirmEmailRequest { get; private set; }

	/// <summary>Set to make <see cref="ConfirmEmailAsync"/> throw — a stale or spent link.</summary>
	public ApiException? ConfirmEmailException { get; set; }

	public Task<TokenResponse> ConfirmEmailAsync(ConfirmEmailRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(ConfirmEmailAsync));
		LastConfirmEmailRequest = request;
		if (ConfirmEmailException is not null) throw ConfirmEmailException;
		return Task.FromResult(TokenResult
			?? new TokenResponse("access", 900, "refresh", new AuthenticatedUser(request.UserId, "test", true, true)));
	}

	public Task ResendConfirmationAsync(CancellationToken cancellationToken = default) { Record(nameof(ResendConfirmationAsync)); return Task.CompletedTask; }

	/// <summary>The last address a reset link was asked for (§7.7).</summary>
	public ForgotPasswordRequest? LastForgotPasswordRequest { get; private set; }

	/// <summary>Set to make <see cref="ForgotPasswordAsync"/> throw — a transport failure, never "no such address" (§7.8).</summary>
	public ApiException? ForgotPasswordException { get; set; }

	public Task ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(ForgotPasswordAsync));
		LastForgotPasswordRequest = request;
		if (ForgotPasswordException is not null) throw ForgotPasswordException;
		return Task.CompletedTask;
	}

	/// <summary>The last reset the UI submitted, for §7.7 assertions.</summary>
	public ResetPasswordRequest? LastResetPasswordRequest { get; private set; }

	/// <summary>Set to make <see cref="ResetPasswordAsync"/> throw — a stale link, or a refused password.</summary>
	public ApiException? ResetPasswordException { get; set; }

	public Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(ResetPasswordAsync));
		LastResetPasswordRequest = request;
		if (ResetPasswordException is not null) throw ResetPasswordException;
		return Task.CompletedTask;
	}

	/// <summary>The last password change request.</summary>
	public ChangePasswordRequest? LastChangePasswordRequest { get; private set; }

	/// <summary>Set to throw the given exception from <see cref="ChangePasswordAsync"/>.</summary>
	public ApiException? ChangePasswordException { get; set; }

	public Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(ChangePasswordAsync));
		LastChangePasswordRequest = request;
		if (ChangePasswordException is not null) throw ChangePasswordException;
		return Task.CompletedTask;
	}

	public Task<IReadOnlyList<DeviceSession>> ListSessionsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Recorded(nameof(ListSessionsAsync), SessionsResult));

	/// <summary>Every device id passed to <see cref="RevokeSessionAsync"/>, in order.</summary>
	public List<Guid> RevokedSessions { get; } = new();

	public Task RevokeSessionAsync(Guid deviceId, CancellationToken cancellationToken = default)
	{
		Record(nameof(RevokeSessionAsync));
		RevokedSessions.Add(deviceId);
		return Task.CompletedTask;
	}

	public Task<OwnProfile> GetProfileAsync(CancellationToken cancellationToken = default) =>
		Task.FromResult(Recorded(nameof(GetProfileAsync), ProfileResult ?? new OwnProfile(null, null, null, false, false, false, false)));
	public UpdateProfileRequest? LastUpdateProfileRequest { get; private set; }

	public Task UpdateProfileAsync(UpdateProfileRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(UpdateProfileAsync));
		LastUpdateProfileRequest = request;
		return Task.CompletedTask;
	}

	public Task<IReadOnlyList<TrackSummary>> ListTracksAsync(CancellationToken cancellationToken = default) => Task.FromResult(Recorded(nameof(ListTracksAsync), TracksResult));
	private static readonly DateTimeOffset SampleInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	public Task<TrackSummary> UploadTrackAsync(UploadTrackRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(UploadTrackAsync));
		UploadedTracks.Add(request);

		if (UploadTrackException is not null)
		{
			return Task.FromException<TrackSummary>(UploadTrackException);
		}

		// Enough of a summary for the screen that shows it back: the counts the caller sent, so a
		// test can tell an upload that carried the whole track from one that carried a filtered one.
		return Task.FromResult(new TrackSummary(
			Guid.NewGuid(),
			request.Name,
			SampleInstant,
			request.Points.Count > 0 ? request.Points[0].TimeUtc : null,
			request.Points.Count > 0 ? request.Points[^1].TimeUtc : null,
			0,
			null,
			null,
			null,
			request.Points.Count,
			Math.Max(1, request.SegmentStarts?.Count ?? 1),
			request.Source,
			1));
	}

	public Task<TrackDetail> GetTrackAsync(Guid trackId, CancellationToken cancellationToken = default) =>
		Task.FromResult(Recorded(nameof(GetTrackAsync), TrackDetailResult
			?? new TrackDetail(new TrackSummary(trackId, "Test", SampleInstant, null, null, 0, null, null, null, 0, 1, TrackSourceDto.Recorded, 1), null, Array.Empty<DLR.Core.Tracks.TrackPoint>())));
	public Task<HttpResponseMessage> ExportTrackGpxAsync(Guid trackId, CancellationToken cancellationToken = default) => throw new NotImplementedException();

	/// <summary>Every rename the UI sent, in order (§15.1).</summary>
	public List<(Guid TrackId, string Name)> RenamedTracks { get; } = new();

	/// <summary>Every track the UI asked the server to delete.</summary>
	public List<Guid> DeletedTracks { get; } = new();

	/// <summary>Set to make <see cref="RenameTrackAsync"/> throw.</summary>
	public Exception? RenameTrackException { get; set; }

	/// <summary>Set to make <see cref="DeleteTrackAsync"/> throw — the §15.4 live-route conflict.</summary>
	public Exception? DeleteTrackException { get; set; }

	public Task<TrackSummary> RenameTrackAsync(Guid trackId, RenameTrackRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(RenameTrackAsync));
		RenamedTracks.Add((trackId, request.Name));

		if (RenameTrackException is not null)
		{
			return Task.FromException<TrackSummary>(RenameTrackException);
		}

		// The stored summary, not what was typed — the real endpoint trims on the way in, and a
		// screen that echoed the raw string would disagree with the list it goes back to.
		TrackSummary current = TrackDetailResult?.Track
			?? TracksResult.FirstOrDefault(track => track.Id == trackId)
			?? new TrackSummary(trackId, null, SampleInstant, null, null, 0, null, null, null, 0, 1, TrackSourceDto.Recorded, 1);

		return Task.FromResult(current with { Name = request.Name.Trim() });
	}

	public Task DeleteTrackAsync(Guid trackId, CancellationToken cancellationToken = default)
	{
		Record(nameof(DeleteTrackAsync));
		DeletedTracks.Add(trackId);

		return DeleteTrackException is not null
			? Task.FromException(DeleteTrackException)
			: Task.CompletedTask;
	}
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

	/// <summary>Overrideable MyRides response.</summary>
	public MyRides MyRidesResult { get; set; } = new(Array.Empty<RideSummary>(), Array.Empty<RideSummary>());

	public Task<MyRides> ListMyRidesAsync(CancellationToken cancellationToken = default) =>
		Task.FromResult(Recorded(nameof(ListMyRidesAsync), MyRidesResult));

	/// <summary>
	/// Set to make <see cref="GetRideAsync"/> throw.
	/// <para>
	/// Typed as the base <see cref="HttpRequestException"/> rather than <see cref="ApiException"/>
	/// so both halves of §7.9's distinction can be tested: an <see cref="ApiException"/> is the
	/// server answering — a ride that 404s, or one this rider is not on — and a bare
	/// <see cref="HttpRequestException"/> with no status is a phone in a tunnel, which is the case
	/// the offline cache exists for (§4.4). <see cref="ApiException"/> derives from it, so a test
	/// that was already assigning one is unaffected.
	/// </para>
	/// </summary>
	public HttpRequestException? RideException { get; set; }

	public Task<RideDetail> GetRideAsync(Guid rideId, CancellationToken cancellationToken = default)
	{
		Record(nameof(GetRideAsync));

		return RideException is not null
			? Task.FromException<RideDetail>(RideException)
			: Task.FromResult(RideResult
				?? new RideDetail(rideId, "Test ride", null, SampleInstant, RideStateDto.Open, JoinPolicyDto.Approval, 50, 0, false, null, new RidePermissions(), Array.Empty<RideMemberSummary>()));
	}
	/// <summary>The last <see cref="CreateRideAsync"/> request the UI sent.</summary>
	public CreateRideRequest? LastCreateRideRequest { get; private set; }

	/// <summary>The last <see cref="JoinRideByCodeAsync"/> request the UI sent.</summary>
	public JoinByCodeRequest? LastJoinRideByCodeRequest { get; private set; }

	/// <summary>Every <see cref="DecideJoinRequestAsync"/> call, in order.</summary>
	public List<(Guid RideId, Guid RequestId, DecideJoinRequest Request)> DecideJoinRequests { get; } = new();

	/// <summary>Overrideable JoinResult for the join-by-code path.</summary>
	public JoinResult? JoinResult { get; set; }

	public Task<RideDetail> CreateRideAsync(CreateRideRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(CreateRideAsync));
		LastCreateRideRequest = request;
		Guid newId = Guid.NewGuid();
		return Task.FromResult(new RideDetail(
			Id: newId,
			Name: request.Name,
			Description: request.Description,
			StartUtc: request.StartUtc,
			State: RideStateDto.Open,
			JoinPolicy: request.JoinPolicy,
			MemberCap: 50,
			MemberCount: 1,
			IsOrganiser: true,
			JoinCode: "TEST-CODE",
			Permissions: new RidePermissions(),
			Members: Array.Empty<RideMemberSummary>()));
	}

	public Task<JoinResult> JoinRideByCodeAsync(JoinByCodeRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(JoinRideByCodeAsync));
		LastJoinRideByCodeRequest = request;
		return Task.FromResult(JoinResult ?? new JoinResult(Guid.NewGuid(), Joined: true, RequestId: null));
	}

	public Task<IReadOnlyList<JoinRequestSummary>> ListJoinRequestsAsync(Guid rideId, CancellationToken cancellationToken = default) => Task.FromResult(Recorded(nameof(ListJoinRequestsAsync), JoinRequestsResult));
	public Task DecideJoinRequestAsync(Guid rideId, Guid requestId, DecideJoinRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(DecideJoinRequestAsync));
		DecideJoinRequests.Add((rideId, requestId, request));
		return Task.CompletedTask;
	}
	public List<Guid> StartedRides { get; } = new();

	public Task StartRideAsync(Guid rideId, CancellationToken cancellationToken = default)
	{
		Record(nameof(StartRideAsync));
		StartedRides.Add(rideId);
		return Task.CompletedTask;
	}

	public (Guid RideId, EndRideRequest Request)? LastEndRide { get; private set; }

	public Task EndRideAsync(Guid rideId, EndRideRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(EndRideAsync));
		LastEndRide = (rideId, request);
		return Task.CompletedTask;
	}
	/// <summary>The last permissions payload the UI sent.</summary>
	public RidePermissions? LastUpdatedPermissions { get; private set; }

	public Task UpdatePermissionsAsync(Guid rideId, RidePermissions permissions, CancellationToken cancellationToken = default)
	{
		Record(nameof(UpdatePermissionsAsync));
		LastUpdatedPermissions = permissions;
		return Task.CompletedTask;
	}
	public List<(Guid RideId, SetSharingRequest Request)> SetSharingRequests { get; } = new();

	public Task SetSharingAsync(Guid rideId, SetSharingRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(SetSharingAsync));
		SetSharingRequests.Add((rideId, request));
		return Task.CompletedTask;
	}
	/// <summary>Set to make <see cref="LeaveRideAsync"/> throw — the organiser's 409, most obviously.</summary>
	public ApiException? LeaveRideException { get; set; }

	public Task LeaveRideAsync(Guid rideId, CancellationToken cancellationToken = default)
	{
		if (LeaveRideException is not null)
		{
			return Task.FromException(LeaveRideException);
		}

		Record(nameof(LeaveRideAsync));
		return Task.CompletedTask;
	}

	public Task RemoveMemberAsync(Guid rideId, Guid userId, CancellationToken cancellationToken = default) { Record(nameof(RemoveMemberAsync)); return Task.CompletedTask; }

	/// <summary>
	/// What <see cref="ListRideRoutesAsync"/> hands back (§5.4). Mutable rather than a fixed list,
	/// because attaching and detaching are meant to be visible in a later call — a test that adds
	/// a route asserts on what the panel shows afterwards.
	/// </summary>
	public List<RideRoute> RoutesResult { get; } = new();

	/// <summary>Every track id passed to <see cref="AddRideRouteAsync"/>, in order.</summary>
	public List<(Guid RideId, Guid TrackId)> AddedRoutes { get; } = new();

	/// <summary>Every track id passed to <see cref="RemoveRideRouteAsync"/>, in order.</summary>
	public List<(Guid RideId, Guid TrackId)> RemovedRoutes { get; } = new();

	public Task<IReadOnlyList<RideRoute>> ListRideRoutesAsync(Guid rideId, CancellationToken cancellationToken = default) =>
		Task.FromResult(Recorded(nameof(ListRideRoutesAsync), (IReadOnlyList<RideRoute>)[.. RoutesResult]));

	public Task<RideRoute> AddRideRouteAsync(Guid rideId, AddRideRouteRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(AddRideRouteAsync));
		AddedRoutes.Add((rideId, request.TrackId));

		// The server answers with the whole set refreshed; the fake keeps that true by adding to
		// its own list, so a component that refetches after attaching sees what it just added.
		TrackSummary? track = TracksResult.FirstOrDefault(row => row.Id == request.TrackId);

		RideRoute added = new(
			request.TrackId,
			track?.Name,
			track?.DistanceM ?? 0,
			track?.PointCount ?? 0,
			EncodedPolyline: string.Empty,
			Bounds: null,
			AddedUtc: SampleInstant,
			AddedByUserId: Guid.Empty,
			AddedByUserName: "test");

		RoutesResult.Add(added);

		return Task.FromResult(added);
	}

	public Task RemoveRideRouteAsync(Guid rideId, Guid trackId, CancellationToken cancellationToken = default)
	{
		Record(nameof(RemoveRideRouteAsync));
		RemovedRoutes.Add((rideId, trackId));
		RoutesResult.RemoveAll(route => route.TrackId == trackId);
		return Task.CompletedTask;
	}

	public Task<IReadOnlyList<RiderPositionDto>> GetPositionsSnapshotAsync(Guid rideId, CancellationToken cancellationToken = default) => Task.FromResult(Recorded(nameof(GetPositionsSnapshotAsync), PositionsResult));
	/// <summary>Fixes that came in over REST — the fallback path when the hub could not carry one (§5.7).</summary>
	public List<PositionUpdate> PublishedPositions { get; } = [];

	/// <summary>Set to make the REST publish fail too, which is the case the UI has to state.</summary>
	public ApiException? PublishPositionException { get; set; }

	public Task<PublishResult> PublishPositionAsync(PositionUpdate update, CancellationToken cancellationToken = default)
	{
		if (PublishPositionException is not null)
		{
			return Task.FromException<PublishResult>(PublishPositionException);
		}

		PublishedPositions.Add(update);
		return Task.FromResult(new PublishResult(Array.Empty<Guid>()));
	}

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

	/// <summary>Every marker id passed to <see cref="DeleteMarkerAsync"/>, in order.</summary>
	public List<Guid> DeletedMarkers { get; } = new();

	public Task DeleteMarkerAsync(Guid markerId, CancellationToken cancellationToken = default) { Record(nameof(DeleteMarkerAsync)); DeletedMarkers.Add(markerId); return Task.CompletedTask; }
	public Task AttachMarkerPhotoAsync(Guid markerId, AttachPhotoRequest request, CancellationToken cancellationToken = default) { Record(nameof(AttachMarkerPhotoAsync)); return Task.CompletedTask; }

	public Task<PhotoUploaded> UploadPhotoAsync(Stream content, string contentType, string fileName, CancellationToken cancellationToken = default) => throw new NotImplementedException();

	public Task<CommentPage> GetThreadAsync(Guid rideId, string? cursor, CancellationToken cancellationToken = default) =>
		Task.FromResult(Recorded(nameof(GetThreadAsync), ThreadResult ?? new CommentPage(Array.Empty<CommentDto>(), Array.Empty<CommentDto>(), null)));
	/// <summary>Every PostCommentAsync request, in order.</summary>
	public List<PostCommentRequest> PostCommentRequests { get; } = new();

	public Task<CommentDto> PostCommentAsync(Guid rideId, PostCommentRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(PostCommentAsync));
		PostCommentRequests.Add(request);
		return Task.FromResult(new CommentDto(
			Id: Guid.NewGuid(),
			GroupRideId: rideId,
			AuthorId: Guid.NewGuid(),
			AuthorUserName: "test",
			Kind: request.Poll is null ? CommentKindDto.Text : CommentKindDto.Poll,
			Body: request.Body,
			PhotoId: request.PhotoId,
			IsPinned: false,
			CreatedUtc: request.CreatedUtc ?? SampleInstant,
			PostedUtc: SampleInstant,
			EditedUtc: null,
			AuthoredEarlier: false));
	}

	public Task<CommentDto> EditCommentAsync(Guid commentId, EditCommentRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
	public Task DeleteCommentAsync(Guid commentId, CancellationToken cancellationToken = default) { Record(nameof(DeleteCommentAsync)); return Task.CompletedTask; }

	/// <summary>The last PinComment request and its target id.</summary>
	public (Guid CommentId, PinCommentRequest Request)? LastPin { get; private set; }

	public Task PinCommentAsync(Guid commentId, PinCommentRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(PinCommentAsync));
		LastPin = (commentId, request);
		return Task.CompletedTask;
	}

	/// <summary>The last SetReaction request and its target id.</summary>
	public (Guid CommentId, SetReactionRequest Request)? LastReaction { get; private set; }

	public Task SetReactionAsync(Guid commentId, SetReactionRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(SetReactionAsync));
		LastReaction = (commentId, request);
		return Task.CompletedTask;
	}

	public (Guid CommentId, CastVoteRequest Request)? LastCastVote { get; private set; }

	public Task CastVoteAsync(Guid commentId, CastVoteRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(CastVoteAsync));
		LastCastVote = (commentId, request);
		return Task.CompletedTask;
	}

	public List<Guid> ClosedPolls { get; } = new();

	public Task ClosePollAsync(Guid commentId, CancellationToken cancellationToken = default)
	{
		Record(nameof(ClosePollAsync));
		ClosedPolls.Add(commentId);
		return Task.CompletedTask;
	}

	public Task<ContentReported> ReportCommentAsync(Guid commentId, ReportContentRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
	public Task<ContentReported> ReportMarkerAsync(Guid markerId, ReportContentRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
	public Task BlockUserAsync(BlockUserRequest request, CancellationToken cancellationToken = default) { Record(nameof(BlockUserAsync)); return Task.CompletedTask; }
	public List<Guid> UnblockedUsers { get; } = new();

	public Task UnblockUserAsync(Guid userId, CancellationToken cancellationToken = default)
	{
		Record(nameof(UnblockUserAsync));
		UnblockedUsers.Add(userId);
		return Task.CompletedTask;
	}
	public Task<IReadOnlyList<BlockedRider>> ListBlocksAsync(CancellationToken cancellationToken = default) => Task.FromResult(Recorded(nameof(ListBlocksAsync), BlocksResult));

	/// <summary>The last DeleteAccount request, for §6.3 assertions.</summary>
	public DeleteAccountRequest? LastDeleteAccountRequest { get; private set; }

	public Task<HttpResponseMessage> ExportAccountAsync(CancellationToken cancellationToken = default)
	{
		Record(nameof(ExportAccountAsync));
		// Return a tiny in-memory ZIP-shaped byte array — the composer only cares about
		// IsSuccessStatusCode and length for the download-link path.
		HttpResponseMessage response = new(System.Net.HttpStatusCode.OK)
		{
			Content = new ByteArrayContent(new byte[] { 0x50, 0x4B, 0x03, 0x04 }), // "PK" ZIP magic
		};
		response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
		return Task.FromResult(response);
	}

	public Task DeleteAccountAsync(DeleteAccountRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(DeleteAccountAsync));
		LastDeleteAccountRequest = request;
		return Task.CompletedTask;
	}
}
