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

	/// <summary>
	/// What <see cref="GetAboutAsync"/> answers, or <c>null</c> — the default — for a host whose
	/// client cannot answer About at all.
	/// <para>
	/// <strong>Null by default, and that is load-bearing.</strong> <c>SourceOfferFooter</c> sits at
	/// the foot of every signed-out page (§14.6.2) and keeps its answer in a private <em>static</em>
	/// so navigations do not refetch it — so any suite whose page happens to carry the footer can
	/// write a value that the footer's own tests then read instead of the one they wired. Worse, it
	/// can write it <em>late</em>: a footer mounted by a render the test never waited for lands
	/// after that test has finished, inside somebody else's.
	/// </para>
	/// <para>
	/// Answering null makes that impossible rather than unlikely: the footer catches
	/// <see cref="NotImplementedException"/>, renders its placeholder — which is what a rider on a
	/// host with no About endpoint sees anyway — and never touches the cache. A suite that is
	/// actually testing the footer wires a value here, and clears the cache next to its render
	/// (<c>SourceOfferFooterCache</c>).
	/// </para>
	/// </summary>
	public AboutInfo? AboutResult { get; set; }

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

	/// <summary>
	/// Answers <see cref="AboutResult"/>, or throws the way a host that cannot answer About does —
	/// see that property for why the throwing case is the default.
	/// </summary>
	public Task<AboutInfo> GetAboutAsync(CancellationToken cancellationToken = default) =>
		AboutResult is { } about
			? Task.FromResult(Recorded(nameof(GetAboutAsync), about))
			: throw new NotImplementedException("This fake has no About wired — set AboutResult if the test needs one.");

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

	// -- Home private area (§10.1) --
	//
	// Stateful rather than a canned result, unlike most of this fake: the account is now the
	// source of truth for the private area, so a test that saves one and expects to read it back
	// is testing the real arrangement. PrivateAreaStateTests builds a second state over the same
	// fake to stand for the rider's other phone.

	/// <summary>What the account holds, or null for an account that has never set one.</summary>
	public PrivateAreaSettings? PrivateAreaResult { get; set; }

	/// <summary>
	/// Set to make all three private-area calls throw — the phone in a tunnel, which is the case
	/// the gate has to keep answering through.
	/// </summary>
	public Exception? PrivateAreaException { get; set; }

	/// <summary>How many times the account was asked for the area, for the "reads it once" assertions.</summary>
	public int PrivateAreaReads { get; private set; }

	public Task<PrivateAreaResponse> GetPrivateAreaAsync(CancellationToken cancellationToken = default)
	{
		Record(nameof(GetPrivateAreaAsync));
		PrivateAreaReads++;

		return PrivateAreaException is not null
			? Task.FromException<PrivateAreaResponse>(PrivateAreaException)
			: Task.FromResult(new PrivateAreaResponse(PrivateAreaResult));
	}

	public Task SetPrivateAreaAsync(PrivateAreaSettings request, CancellationToken cancellationToken = default)
	{
		Record(nameof(SetPrivateAreaAsync));

		if (PrivateAreaException is not null)
		{
			return Task.FromException(PrivateAreaException);
		}

		// Normalised on the way in, like the endpoint: a test that writes an out-of-range radius
		// and reads it back should see what the server would have kept.
		PrivateAreaResult = request.Normalised();
		return Task.CompletedTask;
	}

	public Task ClearPrivateAreaAsync(CancellationToken cancellationToken = default)
	{
		Record(nameof(ClearPrivateAreaAsync));

		if (PrivateAreaException is not null)
		{
			return Task.FromException(PrivateAreaException);
		}

		PrivateAreaResult = null;
		return Task.CompletedTask;
	}

	// -- Profile photograph (§7.3) --
	//
	// Like the private area above, this fake is the source of truth rather than a recorder: a test
	// that sets an avatar and then reads a profile back is testing the real arrangement.

	/// <summary>What every rider's avatar lookup answers, keyed on username. Absent means "no photograph".</summary>
	public Dictionary<string, Guid?> AvatarsByUserName { get; } = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Every batch of names the UI looked up, in order — what a test asserts the batching actually did.</summary>
	public List<IReadOnlyCollection<string>> AvatarLookups { get; } = new();

	/// <summary>Set to make <see cref="SetAvatarAsync"/> and <see cref="ClearAvatarAsync"/> throw.</summary>
	public Exception? AvatarException { get; set; }

	/// <summary>Set to make <see cref="GetRiderAvatarsAsync"/> throw — the phone in a tunnel.</summary>
	public Exception? GetRiderAvatarsException { get; set; }

	public Task<OwnProfile> SetAvatarAsync(SetAvatarRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(SetAvatarAsync));

		if (AvatarException is not null)
		{
			return Task.FromException<OwnProfile>(AvatarException);
		}

		ProfileResult = CurrentProfile() with { AvatarPhotoId = request.PhotoId };

		return Task.FromResult(ProfileResult);
	}

	public Task<OwnProfile> ClearAvatarAsync(CancellationToken cancellationToken = default)
	{
		Record(nameof(ClearAvatarAsync));

		if (AvatarException is not null)
		{
			return Task.FromException<OwnProfile>(AvatarException);
		}

		ProfileResult = CurrentProfile() with { AvatarPhotoId = null };

		return Task.FromResult(ProfileResult);
	}

	/// <summary>
	/// Answers for every name asked about, exactly as the endpoint does — a name with no entry
	/// gets a row saying "no photograph" rather than no row at all.
	/// </summary>
	public Task<IReadOnlyList<RiderAvatarDto>> GetRiderAvatarsAsync(
		IReadOnlyCollection<string> userNames,
		CancellationToken cancellationToken = default)
	{
		Record(nameof(GetRiderAvatarsAsync));
		AvatarLookups.Add(userNames);

		if (GetRiderAvatarsException is not null)
		{
			return Task.FromException<IReadOnlyList<RiderAvatarDto>>(GetRiderAvatarsException);
		}

		return Task.FromResult<IReadOnlyList<RiderAvatarDto>>(
		[
			.. userNames.Select(name => new RiderAvatarDto(
				name,
				AvatarsByUserName.TryGetValue(name, out Guid? photoId) ? photoId : null)),
		]);
	}

	private OwnProfile CurrentProfile() =>
		ProfileResult ?? new OwnProfile(null, null, null, false, false, false, false);

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

	/// <summary>Every details-and-sharing save the UI sent, in order (§6.2).</summary>
	public List<(Guid TrackId, UpdateTrackDetailsRequest Request)> UpdatedTrackDetails { get; } = new();

	/// <summary>Set to make <see cref="UpdateTrackDetailsAsync"/> throw.</summary>
	public Exception? UpdateTrackDetailsException { get; set; }

	/// <summary>What <see cref="ListSharedTracksAsync"/> pages through. The fake filters and pages it the way the server does.</summary>
	public List<SharedTrackSummary> SharedTracks { get; } = new();

	/// <summary>Every browse query the UI sent, in order — what a test asserts the filter controls actually did.</summary>
	public List<SharedTrackQuery> SharedTrackQueries { get; } = new();

	/// <summary>Set to make <see cref="ListSharedTracksAsync"/> throw.</summary>
	public Exception? ListSharedTracksException { get; set; }

	/// <summary>What <see cref="GetTrackRatingAsync"/> answers, per route (§6.2).</summary>
	public Dictionary<Guid, TrackRatingSummary> TrackRatings { get; } = new();

	/// <summary>Every rating the UI set, in order — null stars means it was withdrawn.</summary>
	public List<(Guid TrackId, int? Stars)> RatingsSet { get; } = new();

	/// <summary>Set to make any of the three rating calls throw.</summary>
	public Exception? TrackRatingException { get; set; }

	public Task<TrackRatingSummary> GetTrackRatingAsync(Guid trackId, CancellationToken cancellationToken = default)
	{
		Record(nameof(GetTrackRatingAsync));

		return TrackRatingException is not null
			? Task.FromException<TrackRatingSummary>(TrackRatingException)
			: Task.FromResult(RatingFor(trackId));
	}

	public Task<TrackRatingSummary> RateTrackAsync(Guid trackId, RateTrackRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(RateTrackAsync));
		RatingsSet.Add((trackId, request.Stars));

		if (TrackRatingException is not null)
		{
			return Task.FromException<TrackRatingSummary>(TrackRatingException);
		}

		// The tally the real endpoint returns, recomputed the way it would be: the caller's own
		// star replaces whatever they gave before, so a test that taps twice sees one rating and
		// not two. Nobody else's ratings are modelled — the average moves to what this rider
		// chose only when they are the only one.
		TrackRatingSummary before = RatingFor(trackId);
		int count = before.Mine is null ? before.Count + 1 : before.Count;
		double total = ((before.Average ?? 0) * before.Count) - (before.Mine ?? 0) + request.Stars;

		TrackRatings[trackId] = new TrackRatingSummary(count == 0 ? null : total / count, count, request.Stars);

		return Task.FromResult(TrackRatings[trackId]);
	}

	public Task<TrackRatingSummary> ClearTrackRatingAsync(Guid trackId, CancellationToken cancellationToken = default)
	{
		Record(nameof(ClearTrackRatingAsync));
		RatingsSet.Add((trackId, null));

		if (TrackRatingException is not null)
		{
			return Task.FromException<TrackRatingSummary>(TrackRatingException);
		}

		TrackRatingSummary before = RatingFor(trackId);
		int count = before.Mine is null ? before.Count : before.Count - 1;
		double total = ((before.Average ?? 0) * before.Count) - (before.Mine ?? 0);

		TrackRatings[trackId] = new TrackRatingSummary(count == 0 ? null : total / count, count, null);

		return Task.FromResult(TrackRatings[trackId]);
	}

	/// <summary>What is on file for a route, or "nobody has rated it".</summary>
	private TrackRatingSummary RatingFor(Guid trackId) =>
		TrackRatings.TryGetValue(trackId, out TrackRatingSummary? summary) ? summary : TrackRatingSummary.None;

	public Task<TrackSummary> UpdateTrackDetailsAsync(Guid trackId, UpdateTrackDetailsRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(UpdateTrackDetailsAsync));
		UpdatedTrackDetails.Add((trackId, request));

		if (UpdateTrackDetailsException is not null)
		{
			return Task.FromException<TrackSummary>(UpdateTrackDetailsException);
		}

		// The stored summary rather than an echo of the request, for RenameTrackAsync's reason:
		// the real endpoint cleans on the way in and the screen prints back what was stored.
		TrackSummary current = TrackDetailResult?.Track
			?? TracksResult.FirstOrDefault(track => track.Id == trackId)
			?? new TrackSummary(trackId, null, SampleInstant, null, null, 0, null, null, null, 0, 1, TrackSourceDto.Recorded, 1);

		return Task.FromResult(current with
		{
			Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
			PhotoId = request.PhotoId,
			Visibility = request.Visibility,
		});
	}

	/// <summary>
	/// Filters and pages <see cref="SharedTracks"/> the way the endpoint does, so a test can drive
	/// the pager over more rows than a page holds without standing up a server.
	/// </summary>
	public Task<SharedTrackPage> ListSharedTracksAsync(SharedTrackQuery query, CancellationToken cancellationToken = default)
	{
		Record(nameof(ListSharedTracksAsync));
		SharedTrackQueries.Add(query);

		if (ListSharedTracksException is not null)
		{
			return Task.FromException<SharedTrackPage>(ListSharedTracksException);
		}

		IEnumerable<SharedTrackSummary> matches = SharedTracks;

		if (!string.IsNullOrWhiteSpace(query.Name))
		{
			matches = matches.Where(track =>
				track.Name is not null
				&& track.Name.Contains(query.Name.Trim(), StringComparison.OrdinalIgnoreCase));
		}

		if (query.HasArea)
		{
			matches = matches.Where(track => track.AwayKm is null || track.AwayKm <= query.WithinKm);
		}

		List<SharedTrackSummary> all = matches.ToList();

		List<SharedTrackSummary> page = all
			.Skip((Math.Max(1, query.Page) - 1) * SharedTrackQuery.PageSize)
			.Take(SharedTrackQuery.PageSize)
			.ToList();

		return Task.FromResult(new SharedTrackPage(page, Math.Max(1, query.Page), SharedTrackQuery.PageSize, all.Count));
	}

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
				?? new RideDetail(rideId, "Test adventure", null, SampleInstant, RideStateDto.Open, JoinPolicyDto.Approval, 50, 0, false, null, new RidePermissions(), Array.Empty<RideMemberSummary>()));
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

	/// <summary>Every ride id passed to <see cref="DeleteRideAsync"/>, in order.</summary>
	public List<Guid> DeletedRides { get; } = new();

	/// <summary>Set to make the delete fail — the §5.6 refusal on a ride in progress is the real one.</summary>
	public ApiException? DeleteRideException { get; set; }

	public Task DeleteRideAsync(Guid rideId, CancellationToken cancellationToken = default)
	{
		Record(nameof(DeleteRideAsync));

		if (DeleteRideException is not null)
		{
			return Task.FromException(DeleteRideException);
		}

		DeletedRides.Add(rideId);

		// The list the server hands back afterwards no longer has it — the fake keeps that true so
		// a page that refetches after deleting sees what it did.
		MyRidesResult = new MyRides(
			[.. MyRidesResult.Organised.Where(row => row.Id != rideId)],
			[.. MyRidesResult.Joined.Where(row => row.Id != rideId)]);

		return Task.CompletedTask;
	}

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

	/// <summary>Set to make attaching a route fail, which is a case the composer has to state.</summary>
	public ApiException? AddRideRouteException { get; set; }

	public Task<RideRoute> AddRideRouteAsync(Guid rideId, AddRideRouteRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(AddRideRouteAsync));
		AddedRoutes.Add((rideId, request.TrackId));

		if (AddRideRouteException is not null)
		{
			return Task.FromException<RideRoute>(AddRideRouteException);
		}

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

	/// <summary>Private-area crossings that came in over REST — the fallback path (§10.1).</summary>
	public List<PositionPrivacyUpdate> PublishedPrivacy { get; } = [];

	/// <summary>Set to make the REST privacy call fail too.</summary>
	public ApiException? SetPositionPrivacyException { get; set; }

	public Task<PublishResult> SetPositionPrivacyAsync(PositionPrivacyUpdate update, CancellationToken cancellationToken = default)
	{
		if (SetPositionPrivacyException is not null)
		{
			return Task.FromException<PublishResult>(SetPositionPrivacyException);
		}

		PublishedPrivacy.Add(update);
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

	/// <summary>What <see cref="GetTrackThreadAsync"/> answers, or an empty thread (§6.2).</summary>
	public CommentPage? TrackThreadResult { get; set; }

	public Task<CommentPage> GetTrackThreadAsync(Guid trackId, string? cursor, CancellationToken cancellationToken = default) =>
		Task.FromResult(Recorded(
			nameof(GetTrackThreadAsync),
			TrackThreadResult ?? new CommentPage(Array.Empty<CommentDto>(), Array.Empty<CommentDto>(), null)));

	/// <summary>Every route the UI posted to, with what it sent, in order.</summary>
	public List<(Guid TrackId, PostCommentRequest Request)> PostTrackCommentRequests { get; } = new();

	public Task<CommentDto> PostTrackCommentAsync(Guid trackId, PostCommentRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(PostTrackCommentAsync));
		PostTrackCommentRequests.Add((trackId, request));

		return Task.FromResult(new CommentDto(
			Id: Guid.NewGuid(),
			GroupRideId: null,
			TrackId: trackId,
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
	/// <summary>Every PostCommentAsync request, in order.</summary>
	public List<PostCommentRequest> PostCommentRequests { get; } = new();

	public Task<CommentDto> PostCommentAsync(Guid rideId, PostCommentRequest request, CancellationToken cancellationToken = default)
	{
		Record(nameof(PostCommentAsync));
		PostCommentRequests.Add(request);
		return Task.FromResult(new CommentDto(
			Id: Guid.NewGuid(),
			GroupRideId: rideId,
			TrackId: null,
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
