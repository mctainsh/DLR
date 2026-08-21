using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.State;
using DLR.Core.Contracts.Identity;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.State;

/// <summary>
/// §7.4's single-flight refresh rule: when several callers hit a 401 at once — three
/// screens plus a hub reconnect — one shared task serves them all, so the token
/// endpoint sees one call rather than n. If this regresses, a mid-ride reconnect on a
/// spotty connection would produce a burst of refreshes and revoke the family (§7.4
/// treats concurrent replays outside the grace window as theft).
/// </summary>
public sealed class AuthStateTests
{
	// A fixed instant — architecture rule ClockRules forbids ambient clock reads in test
	// source (§10.4). The value itself is arbitrary; only the fact that it isn't now matters.
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private static AuthState BuildAuth(FakeApiClient api, FakeTokenStore tokens, FakeTimeProvider clock) =>
		new(api, tokens, clock);

	[Fact]
	public async Task ConcurrentRefresh_HitsTheTokenEndpointOnce()
	{
		FakeApiClient api = new();
		FakeTokenStore tokens = new();
		FakeTimeProvider clock = new(FixedInstant);

		// Seed a refresh token in the store; no access token — every caller misses the
		// cache and races the refresh.
		await tokens.WriteRefreshTokenAsync("stored-refresh");

		// Slow the token response so 20 concurrent callers pile up on the semaphore.
		int inFlight = 0;
		int peakInFlight = 0;
		object gate = new();
		TaskCompletionSource release = new();

		// A hand-rolled fake token endpoint. Counts hits, waits for the release, and
		// answers with a fresh session. If two calls got through, peakInFlight > 1.
		int tokenHits = 0;
		Func<TokenRequest, Task<TokenResponse>> fakeToken = async request =>
		{
			int hits = Interlocked.Increment(ref tokenHits);
			int currentInFlight;
			lock (gate)
			{
				inFlight++;
				currentInFlight = inFlight;
				if (currentInFlight > peakInFlight) peakInFlight = currentInFlight;
			}
			await release.Task;
			lock (gate) { inFlight--; }
			return new TokenResponse("access-" + hits, 900, "successor-" + hits,
				new AuthenticatedUser(Guid.NewGuid(), "test", false, false));
		};

		// Wire the fake token method into the FakeApiClient by subclassing inline.
		DelegatingApiClient wired = new(api, fakeToken);

		AuthState auth = new(wired, tokens, clock);

		// Twenty concurrent callers, all asking for a fresh token.
		Task<string?>[] callers = Enumerable.Range(0, 20)
			.Select(_ => Task.Run(() => auth.GetOrRefreshAccessTokenAsync()))
			.ToArray();

		// Give them a moment to pile up on the gate.
		await Task.Delay(50);

		// Let the token endpoint answer.
		release.SetResult();

		string?[] results = await Task.WhenAll(callers);

		tokenHits.ShouldBe(1,
			"§7.4: twenty concurrent refreshes must produce one token call, not twenty. " +
			"Any more and the server's family-reuse detector would revoke the family on the next real refresh.");
		peakInFlight.ShouldBeLessThanOrEqualTo(1,
			"a second caller reaching the token endpoint before the first returns is the exact regression this test exists to catch.");
		results.ShouldAllBe(r => r == "access-1", "every caller gets the same result — that is what single-flight means.");
	}

	[Fact]
	public async Task GetOrRefreshAccessToken_WithNoStoredToken_SignsOut()
	{
		FakeApiClient api = new();
		FakeTokenStore tokens = new(); // empty
		FakeTimeProvider clock = new(FixedInstant);
		AuthState auth = new(api, tokens, clock);

		string? result = await auth.GetOrRefreshAccessTokenAsync();

		result.ShouldBeNull(
			"with nothing to refresh, the auth state must fail rather than block on a phantom refresh.");
	}

	[Fact]
	public async Task ApplySession_SetsUserIdAndUserName()
	{
		FakeApiClient api = new();
		FakeTokenStore tokens = new();
		FakeTimeProvider clock = new(FixedInstant);
		AuthState auth = new(api, tokens, clock);

		Guid userId = Guid.NewGuid();
		await auth.ApplySessionAsync(new TokenResponse(
			"access", 900, "refresh",
			new AuthenticatedUser(userId, "DaveSmith", true, true)));

		auth.UserId.ShouldBe(userId);
		auth.UserName.ShouldBe("DaveSmith");
		auth.AccessToken.ShouldBe("access");
	}

	[Fact]
	public async Task SignOut_ClearsMemoryAndTokenStore()
	{
		FakeApiClient api = new();
		FakeTokenStore tokens = new();
		FakeTimeProvider clock = new(FixedInstant);
		AuthState auth = new(api, tokens, clock);

		await auth.ApplySessionAsync(new TokenResponse(
			"access", 900, "refresh",
			new AuthenticatedUser(Guid.NewGuid(), "test", false, false)));

		await auth.SignOutAsync();

		auth.AccessToken.ShouldBeNull();
		auth.UserId.ShouldBeNull();
		tokens.StoredToken.ShouldBeNull("§7.4: sign-out clears the refresh token from storage.");
		tokens.ClearCount.ShouldBe(1);
	}

	/// <summary>
	/// A wrapper that lets the test hijack TokenAsync without subclassing FakeApiClient.
	/// Every other method forwards to the inner fake, unchanged.
	/// </summary>
	private sealed class DelegatingApiClient(FakeApiClient inner, Func<TokenRequest, Task<TokenResponse>> token) : IApiClient
	{
		public Task<TokenResponse> TokenAsync(TokenRequest request, CancellationToken cancellationToken = default) => token(request);

		// Delegated methods below are the entire IApiClient surface; only TokenAsync is intercepted.
		public Task<AboutInfo> GetAboutAsync(CancellationToken ct = default) => inner.GetAboutAsync(ct);
		public Task<TokenResponse> RegisterAsync(RegisterRequest r, CancellationToken ct = default) => inner.RegisterAsync(r, ct);
		public Task<bool> IsUserNameAvailableAsync(string u, CancellationToken ct = default) => inner.IsUserNameAvailableAsync(u, ct);
		public Task SetEmailAsync(SetEmailRequest r, CancellationToken ct = default) => inner.SetEmailAsync(r, ct);
		public Task<TokenResponse> ConfirmEmailAsync(ConfirmEmailRequest r, CancellationToken ct = default) => inner.ConfirmEmailAsync(r, ct);
		public Task ResendConfirmationAsync(CancellationToken ct = default) => inner.ResendConfirmationAsync(ct);
		public Task ForgotPasswordAsync(ForgotPasswordRequest r, CancellationToken ct = default) => inner.ForgotPasswordAsync(r, ct);
		public Task ResetPasswordAsync(ResetPasswordRequest r, CancellationToken ct = default) => inner.ResetPasswordAsync(r, ct);
		public Task ChangePasswordAsync(ChangePasswordRequest r, CancellationToken ct = default) => inner.ChangePasswordAsync(r, ct);
		public Task<IReadOnlyList<DeviceSession>> ListSessionsAsync(CancellationToken ct = default) => inner.ListSessionsAsync(ct);
		public Task RevokeSessionAsync(Guid d, CancellationToken ct = default) => inner.RevokeSessionAsync(d, ct);
		public Task<OwnProfile> GetProfileAsync(CancellationToken ct = default) => inner.GetProfileAsync(ct);
		public Task UpdateProfileAsync(UpdateProfileRequest r, CancellationToken ct = default) => inner.UpdateProfileAsync(r, ct);
		public Task<PrivateAreaResponse> GetPrivateAreaAsync(CancellationToken ct = default) => inner.GetPrivateAreaAsync(ct);
		public Task SetPrivateAreaAsync(PrivateAreaSettings r, CancellationToken ct = default) => inner.SetPrivateAreaAsync(r, ct);
		public Task ClearPrivateAreaAsync(CancellationToken ct = default) => inner.ClearPrivateAreaAsync(ct);
		public Task<DLR.Core.Contracts.Tracks.TrackSummary> UploadTrackAsync(DLR.Core.Contracts.Tracks.UploadTrackRequest r, CancellationToken ct = default) => inner.UploadTrackAsync(r, ct);
		public Task<IReadOnlyList<DLR.Core.Contracts.Tracks.TrackSummary>> ListTracksAsync(CancellationToken ct = default) => inner.ListTracksAsync(ct);
		public Task<DLR.Core.Contracts.Tracks.TrackDetail> GetTrackAsync(Guid t, CancellationToken ct = default) => inner.GetTrackAsync(t, ct);
		public Task<HttpResponseMessage> ExportTrackGpxAsync(Guid t, CancellationToken ct = default) => inner.ExportTrackGpxAsync(t, ct);
		public Task<DLR.Core.Contracts.Tracks.TrackSummary> RenameTrackAsync(Guid t, DLR.Core.Contracts.Tracks.RenameTrackRequest r, CancellationToken ct = default) => inner.RenameTrackAsync(t, r, ct);
		public Task DeleteTrackAsync(Guid t, CancellationToken ct = default) => inner.DeleteTrackAsync(t, ct);
		public Task<DLR.Core.Contracts.Tracks.TrackPointsResponse> GetTrackPointsAsync(Guid t, CancellationToken ct = default) => inner.GetTrackPointsAsync(t, ct);
		public Task<DLR.Core.Contracts.Tracks.TrackEditResponse> EditTrackAsync(Guid t, DLR.Core.Contracts.Tracks.EditTrackRequest r, CancellationToken ct = default) => inner.EditTrackAsync(t, r, ct);
		public Task<DLR.Core.Contracts.Tracks.TrackEditResponse> UndoTrackEditAsync(Guid t, CancellationToken ct = default) => inner.UndoTrackEditAsync(t, ct);
		public Task PurgeTrackPreviousVersionAsync(Guid t, CancellationToken ct = default) => inner.PurgeTrackPreviousVersionAsync(t, ct);
		public Task<DLR.Core.Contracts.Rides.MyRides> ListMyRidesAsync(CancellationToken ct = default) => inner.ListMyRidesAsync(ct);
		public Task<DLR.Core.Contracts.Rides.RideDetail> GetRideAsync(Guid r, CancellationToken ct = default) => inner.GetRideAsync(r, ct);
		public Task<DLR.Core.Contracts.Rides.RideDetail> CreateRideAsync(DLR.Core.Contracts.Rides.CreateRideRequest r, CancellationToken ct = default) => inner.CreateRideAsync(r, ct);
		public Task<DLR.Core.Contracts.Rides.JoinResult> JoinRideByCodeAsync(DLR.Core.Contracts.Rides.JoinByCodeRequest r, CancellationToken ct = default) => inner.JoinRideByCodeAsync(r, ct);
		public Task<IReadOnlyList<DLR.Core.Contracts.Rides.JoinRequestSummary>> ListJoinRequestsAsync(Guid r, CancellationToken ct = default) => inner.ListJoinRequestsAsync(r, ct);
		public Task DecideJoinRequestAsync(Guid r, Guid q, DLR.Core.Contracts.Rides.DecideJoinRequest req, CancellationToken ct = default) => inner.DecideJoinRequestAsync(r, q, req, ct);
		public Task StartRideAsync(Guid r, CancellationToken ct = default) => inner.StartRideAsync(r, ct);
		public Task EndRideAsync(Guid r, DLR.Core.Contracts.Rides.EndRideRequest req, CancellationToken ct = default) => inner.EndRideAsync(r, req, ct);
		public Task UpdatePermissionsAsync(Guid r, DLR.Core.Contracts.Rides.RidePermissions p, CancellationToken ct = default) => inner.UpdatePermissionsAsync(r, p, ct);
		public Task SetSharingAsync(Guid r, DLR.Core.Contracts.Rides.SetSharingRequest req, CancellationToken ct = default) => inner.SetSharingAsync(r, req, ct);
		public Task LeaveRideAsync(Guid r, CancellationToken ct = default) => inner.LeaveRideAsync(r, ct);
		public Task RemoveMemberAsync(Guid r, Guid u, CancellationToken ct = default) => inner.RemoveMemberAsync(r, u, ct);
		public Task<IReadOnlyList<DLR.Core.Contracts.Rides.RideRoute>> ListRideRoutesAsync(Guid r, CancellationToken ct = default) => inner.ListRideRoutesAsync(r, ct);
		public Task<DLR.Core.Contracts.Rides.RideRoute> AddRideRouteAsync(Guid r, DLR.Core.Contracts.Rides.AddRideRouteRequest req, CancellationToken ct = default) => inner.AddRideRouteAsync(r, req, ct);
		public Task RemoveRideRouteAsync(Guid r, Guid t, CancellationToken ct = default) => inner.RemoveRideRouteAsync(r, t, ct);
		public Task<IReadOnlyList<DLR.Core.Contracts.Rides.RiderPositionDto>> GetPositionsSnapshotAsync(Guid r, CancellationToken ct = default) => inner.GetPositionsSnapshotAsync(r, ct);
		public Task<DLR.Core.Contracts.Rides.PublishResult> PublishPositionAsync(DLR.Core.Contracts.Rides.PositionUpdate u, CancellationToken ct = default) => inner.PublishPositionAsync(u, ct);
		public Task<DLR.Core.Contracts.Markers.MarkerDto> CreateMarkerAsync(DLR.Core.Contracts.Markers.CreateMarkerRequest r, CancellationToken ct = default) => inner.CreateMarkerAsync(r, ct);
		public Task<IReadOnlyList<DLR.Core.Contracts.Markers.MarkerDto>> ListRideMarkersAsync(Guid r, CancellationToken ct = default) => inner.ListRideMarkersAsync(r, ct);
		public Task<DLR.Core.Contracts.Markers.MarkerDto> UpdateMarkerAsync(Guid m, DLR.Core.Contracts.Markers.UpdateMarkerRequest r, CancellationToken ct = default) => inner.UpdateMarkerAsync(m, r, ct);
		public Task DeleteMarkerAsync(Guid m, CancellationToken ct = default) => inner.DeleteMarkerAsync(m, ct);
		public Task AttachMarkerPhotoAsync(Guid m, DLR.Core.Contracts.Photos.AttachPhotoRequest r, CancellationToken ct = default) => inner.AttachMarkerPhotoAsync(m, r, ct);
		public Task<DLR.Core.Contracts.Photos.PhotoUploaded> UploadPhotoAsync(Stream s, string ct2, string n, CancellationToken ct = default) => inner.UploadPhotoAsync(s, ct2, n, ct);
		public Task<DLR.Core.Contracts.Comments.CommentPage> GetThreadAsync(Guid r, string? c, CancellationToken ct = default) => inner.GetThreadAsync(r, c, ct);
		public Task<DLR.Core.Contracts.Comments.CommentDto> PostCommentAsync(Guid r, DLR.Core.Contracts.Comments.PostCommentRequest req, CancellationToken ct = default) => inner.PostCommentAsync(r, req, ct);
		public Task<DLR.Core.Contracts.Comments.CommentDto> EditCommentAsync(Guid c, DLR.Core.Contracts.Comments.EditCommentRequest r, CancellationToken ct = default) => inner.EditCommentAsync(c, r, ct);
		public Task DeleteCommentAsync(Guid c, CancellationToken ct = default) => inner.DeleteCommentAsync(c, ct);
		public Task PinCommentAsync(Guid c, DLR.Core.Contracts.Comments.PinCommentRequest r, CancellationToken ct = default) => inner.PinCommentAsync(c, r, ct);
		public Task SetReactionAsync(Guid c, DLR.Core.Contracts.Comments.SetReactionRequest r, CancellationToken ct = default) => inner.SetReactionAsync(c, r, ct);
		public Task CastVoteAsync(Guid c, DLR.Core.Contracts.Comments.CastVoteRequest r, CancellationToken ct = default) => inner.CastVoteAsync(c, r, ct);
		public Task ClosePollAsync(Guid c, CancellationToken ct = default) => inner.ClosePollAsync(c, ct);
		public Task<DLR.Core.Contracts.Moderation.ContentReported> ReportCommentAsync(Guid c, DLR.Core.Contracts.Moderation.ReportContentRequest r, CancellationToken ct = default) => inner.ReportCommentAsync(c, r, ct);
		public Task<DLR.Core.Contracts.Moderation.ContentReported> ReportMarkerAsync(Guid m, DLR.Core.Contracts.Moderation.ReportContentRequest r, CancellationToken ct = default) => inner.ReportMarkerAsync(m, r, ct);
		public Task BlockUserAsync(DLR.Core.Contracts.Moderation.BlockUserRequest r, CancellationToken ct = default) => inner.BlockUserAsync(r, ct);
		public Task UnblockUserAsync(Guid u, CancellationToken ct = default) => inner.UnblockUserAsync(u, ct);
		public Task<IReadOnlyList<DLR.Core.Contracts.Moderation.BlockedRider>> ListBlocksAsync(CancellationToken ct = default) => inner.ListBlocksAsync(ct);
		public Task<HttpResponseMessage> ExportAccountAsync(CancellationToken ct = default) => inner.ExportAccountAsync(ct);
		public Task DeleteAccountAsync(DLR.Core.Contracts.Account.DeleteAccountRequest r, CancellationToken ct = default) => inner.DeleteAccountAsync(r, ct);
	}
}
