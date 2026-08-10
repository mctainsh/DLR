using DLR.Core.Contracts.Account;
using DLR.Core.Contracts.Comments;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Moderation;
using DLR.Core.Contracts.Photos;
using DLR.Core.Contracts.Rides;
using DLR.Core.Contracts.Tracks;
using Microsoft.AspNetCore.Components;

namespace BlazorDLR.Shared.Services.Stubs;

/// <summary>
/// Phase 0 stubs for every seam in §4 of <c>SharedFrontend.md</c>. Each method throws
/// <see cref="NotImplementedException"/> with the phase it is scheduled to arrive in, so a
/// screen that tries to use it before its dependency has been built fails in a way that names
/// the reason rather than as a null-reference or an empty string.
/// <para>
/// One shared set rather than per-host copies: the throwing shape is the same on either side,
/// and duplicating it would be four stub files that drift.
/// </para>
/// </summary>
internal static class Phase0
{
	public const string Message = "Phase 0 stub — real implementation arrives in Phase 1 of SharedFrontend.md.";
}

/// <summary>
/// A last-resort <see cref="IApiClient"/> that throws on every method. Only used by the
/// architecture-test harness and by a host whose real implementation has not been wired.
/// A screen reaching for this before its dependency is built fails with a message naming
/// the phase rather than as a null-reference.
/// </summary>
public sealed class ThrowingApiClient : IApiClient
{
	private static T Throw<T>() => throw new NotImplementedException(Phase0.Message);
	private static Task<T> AsyncThrow<T>() => Task.FromException<T>(new NotImplementedException(Phase0.Message));
	private static Task AsyncVoidThrow() => Task.FromException(new NotImplementedException(Phase0.Message));

	public Task<AboutInfo> GetAboutAsync(CancellationToken cancellationToken = default) => AsyncThrow<AboutInfo>();
	public Task<TokenResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default) => AsyncThrow<TokenResponse>();
	public Task<TokenResponse> TokenAsync(TokenRequest request, CancellationToken cancellationToken = default) => AsyncThrow<TokenResponse>();
	public Task<bool> IsUserNameAvailableAsync(string userName, CancellationToken cancellationToken = default) => AsyncThrow<bool>();
	public Task SetEmailAsync(SetEmailRequest request, CancellationToken cancellationToken = default) => AsyncVoidThrow();
	public Task<TokenResponse> ConfirmEmailAsync(ConfirmEmailRequest request, CancellationToken cancellationToken = default) => AsyncThrow<TokenResponse>();
	public Task ResendConfirmationAsync(CancellationToken cancellationToken = default) => AsyncVoidThrow();
	public Task ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken = default) => AsyncVoidThrow();
	public Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default) => AsyncVoidThrow();
	public Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default) => AsyncVoidThrow();
	public Task<IReadOnlyList<DeviceSession>> ListSessionsAsync(CancellationToken cancellationToken = default) => AsyncThrow<IReadOnlyList<DeviceSession>>();
	public Task RevokeSessionAsync(Guid deviceId, CancellationToken cancellationToken = default) => AsyncVoidThrow();
	public Task<OwnProfile> GetProfileAsync(CancellationToken cancellationToken = default) => AsyncThrow<OwnProfile>();
	public Task UpdateProfileAsync(UpdateProfileRequest request, CancellationToken cancellationToken = default) => AsyncVoidThrow();
	public Task<IReadOnlyList<TrackSummary>> ListTracksAsync(CancellationToken cancellationToken = default) => AsyncThrow<IReadOnlyList<TrackSummary>>();
	public Task<TrackDetail> GetTrackAsync(Guid trackId, CancellationToken cancellationToken = default) => AsyncThrow<TrackDetail>();
	public Task<HttpResponseMessage> ExportTrackGpxAsync(Guid trackId, CancellationToken cancellationToken = default) => AsyncThrow<HttpResponseMessage>();
	public Task<TrackPointsResponse> GetTrackPointsAsync(Guid trackId, CancellationToken cancellationToken = default) => AsyncThrow<TrackPointsResponse>();
	public Task<TrackEditResponse> EditTrackAsync(Guid trackId, EditTrackRequest request, CancellationToken cancellationToken = default) => AsyncThrow<TrackEditResponse>();
	public Task<TrackEditResponse> UndoTrackEditAsync(Guid trackId, CancellationToken cancellationToken = default) => AsyncThrow<TrackEditResponse>();
	public Task PurgeTrackPreviousVersionAsync(Guid trackId, CancellationToken cancellationToken = default) => AsyncVoidThrow();
	public Task<MyRides> ListMyRidesAsync(CancellationToken cancellationToken = default) => AsyncThrow<MyRides>();
	public Task<RideDetail> GetRideAsync(Guid rideId, CancellationToken cancellationToken = default) => AsyncThrow<RideDetail>();
	public Task<RideDetail> CreateRideAsync(CreateRideRequest request, CancellationToken cancellationToken = default) => AsyncThrow<RideDetail>();
	public Task<JoinResult> JoinRideByCodeAsync(JoinByCodeRequest request, CancellationToken cancellationToken = default) => AsyncThrow<JoinResult>();
	public Task<IReadOnlyList<JoinRequestSummary>> ListJoinRequestsAsync(Guid rideId, CancellationToken cancellationToken = default) => AsyncThrow<IReadOnlyList<JoinRequestSummary>>();
	public Task DecideJoinRequestAsync(Guid rideId, Guid requestId, DecideJoinRequest request, CancellationToken cancellationToken = default) => AsyncVoidThrow();
	public Task StartRideAsync(Guid rideId, CancellationToken cancellationToken = default) => AsyncVoidThrow();
	public Task EndRideAsync(Guid rideId, EndRideRequest request, CancellationToken cancellationToken = default) => AsyncVoidThrow();
	public Task UpdatePermissionsAsync(Guid rideId, RidePermissions permissions, CancellationToken cancellationToken = default) => AsyncVoidThrow();
	public Task SetSharingAsync(Guid rideId, SetSharingRequest request, CancellationToken cancellationToken = default) => AsyncVoidThrow();
	public Task LeaveRideAsync(Guid rideId, CancellationToken cancellationToken = default) => AsyncVoidThrow();
	public Task RemoveMemberAsync(Guid rideId, Guid userId, CancellationToken cancellationToken = default) => AsyncVoidThrow();
	public Task<IReadOnlyList<RideRoute>> ListRideRoutesAsync(Guid rideId, CancellationToken cancellationToken = default) => AsyncThrow<IReadOnlyList<RideRoute>>();
	public Task<RideRoute> AddRideRouteAsync(Guid rideId, AddRideRouteRequest request, CancellationToken cancellationToken = default) => AsyncThrow<RideRoute>();
	public Task RemoveRideRouteAsync(Guid rideId, Guid trackId, CancellationToken cancellationToken = default) => AsyncVoidThrow();
	public Task<IReadOnlyList<RiderPositionDto>> GetPositionsSnapshotAsync(Guid rideId, CancellationToken cancellationToken = default) => AsyncThrow<IReadOnlyList<RiderPositionDto>>();
	public Task<PublishResult> PublishPositionAsync(PositionUpdate update, CancellationToken cancellationToken = default) => AsyncThrow<PublishResult>();
	public Task<MarkerDto> CreateMarkerAsync(CreateMarkerRequest request, CancellationToken cancellationToken = default) => AsyncThrow<MarkerDto>();
	public Task<IReadOnlyList<MarkerDto>> ListRideMarkersAsync(Guid rideId, CancellationToken cancellationToken = default) => AsyncThrow<IReadOnlyList<MarkerDto>>();
	public Task<MarkerDto> UpdateMarkerAsync(Guid markerId, UpdateMarkerRequest request, CancellationToken cancellationToken = default) => AsyncThrow<MarkerDto>();
	public Task DeleteMarkerAsync(Guid markerId, CancellationToken cancellationToken = default) => AsyncVoidThrow();
	public Task AttachMarkerPhotoAsync(Guid markerId, AttachPhotoRequest request, CancellationToken cancellationToken = default) => AsyncVoidThrow();
	public Task<PhotoUploaded> UploadPhotoAsync(Stream content, string contentType, string fileName, CancellationToken cancellationToken = default) => AsyncThrow<PhotoUploaded>();
	public Task<CommentPage> GetThreadAsync(Guid rideId, string? cursor, CancellationToken cancellationToken = default) => AsyncThrow<CommentPage>();
	public Task<CommentDto> PostCommentAsync(Guid rideId, PostCommentRequest request, CancellationToken cancellationToken = default) => AsyncThrow<CommentDto>();
	public Task<CommentDto> EditCommentAsync(Guid commentId, EditCommentRequest request, CancellationToken cancellationToken = default) => AsyncThrow<CommentDto>();
	public Task DeleteCommentAsync(Guid commentId, CancellationToken cancellationToken = default) => AsyncVoidThrow();
	public Task PinCommentAsync(Guid commentId, PinCommentRequest request, CancellationToken cancellationToken = default) => AsyncVoidThrow();
	public Task SetReactionAsync(Guid commentId, SetReactionRequest request, CancellationToken cancellationToken = default) => AsyncVoidThrow();
	public Task CastVoteAsync(Guid commentId, CastVoteRequest request, CancellationToken cancellationToken = default) => AsyncVoidThrow();
	public Task ClosePollAsync(Guid commentId, CancellationToken cancellationToken = default) => AsyncVoidThrow();
	public Task<ContentReported> ReportCommentAsync(Guid commentId, ReportContentRequest request, CancellationToken cancellationToken = default) => AsyncThrow<ContentReported>();
	public Task<ContentReported> ReportMarkerAsync(Guid markerId, ReportContentRequest request, CancellationToken cancellationToken = default) => AsyncThrow<ContentReported>();
	public Task BlockUserAsync(BlockUserRequest request, CancellationToken cancellationToken = default) => AsyncVoidThrow();
	public Task UnblockUserAsync(Guid userId, CancellationToken cancellationToken = default) => AsyncVoidThrow();
	public Task<IReadOnlyList<BlockedRider>> ListBlocksAsync(CancellationToken cancellationToken = default) => AsyncThrow<IReadOnlyList<BlockedRider>>();
	public Task<HttpResponseMessage> ExportAccountAsync(CancellationToken cancellationToken = default) => AsyncThrow<HttpResponseMessage>();
	public Task DeleteAccountAsync(DeleteAccountRequest request, CancellationToken cancellationToken = default) => AsyncVoidThrow();
}

/// <inheritdoc />
public sealed class ThrowingRideHubClient : IRideHubClient
{
	/// <inheritdoc />
	public bool IsConnected => false;

	// Every event on IRideHubClient is declared here. None ever fire — the throwing stub is used
	// by the SSR shell (which does not do realtime) and by any host that has not been wired to
	// the real SignalRRideHubClient. Declaring them makes the interface satisfiable; suppressing
	// the "unused" warning happens in DisposeAsync by nulling every one.
#pragma warning disable CS0067 // Event never used
	public event Action<PositionBatch>? PositionsUpdated;
	public event Action<Guid, RideMemberSummary>? MemberJoined;
	public event Action<Guid, Guid>? MemberLeft;
	public event Action<Guid, RideStateDto>? RideStateChanged;
	public event Action<Guid>? RoutesChanged;
	public event Action<Guid, JoinRequestSummary>? JoinRequestReceived;
	public event Action<Guid, JoinResult>? JoinRequestDecided;
	public event Action<Guid, MarkerDto>? MarkerAdded;
	public event Action<Guid, MarkerDto>? MarkerUpdated;
	public event Action<Guid, Guid>? MarkerRemoved;
	public event Action<CommentDto>? CommentPosted;
	public event Action<CommentDto>? CommentEdited;
	public event Action<Guid>? CommentRemoved;
	public event Action<Guid, bool>? CommentPinChanged;
	public event Action<Guid, ReactionCounts>? ReactionsUpdated;
	public event Action<Guid, PollResults>? PollUpdated;
	public event Action<Guid, RidePermissions>? PermissionsChanged;
	public event Action<Guid, DateTimeOffset>? SharingWindDownStarted;
	public event Action<Guid, Guid, bool>? MemberSharingChanged;
#pragma warning restore CS0067

	/// <inheritdoc />
	public Task ConnectAsync(CancellationToken cancellationToken = default) =>
		throw new NotImplementedException(Phase0.Message);

	/// <inheritdoc />
	public Task JoinRideAsync(Guid rideId, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException(Phase0.Message);

	/// <inheritdoc />
	public Task LeaveRideAsync(Guid rideId, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException(Phase0.Message);

	/// <inheritdoc />
	public Task PublishPositionAsync(PositionUpdate update, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException(Phase0.Message);

	/// <inheritdoc />
	public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <inheritdoc />
public sealed class ThrowingRideRepository : IRideRepository
{
	/// <inheritdoc />
	public Task<IReadOnlyList<RideDetail>> ListRidesAsync(CancellationToken cancellationToken = default) =>
		throw new NotImplementedException(Phase0.Message);

	/// <inheritdoc />
	public Task<RideDetail?> GetRideAsync(Guid rideId, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException(Phase0.Message);
}

/// <summary>
/// The web host binds <see cref="ITokenStore"/> to this because the refresh token lives in an
/// <c>HttpOnly</c> cookie the JS heap cannot touch (§18.5). Writing is deliberately silent —
/// the cookie is authoritative and this method has nothing meaningful to persist. Reading
/// returns <c>null</c> because there is no readable value.
/// </summary>
public sealed class CookieBackedTokenStore : ITokenStore
{
	/// <inheritdoc />
	public ValueTask<string?> ReadRefreshTokenAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromResult<string?>(null);

	/// <inheritdoc />
	public ValueTask WriteRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default) =>
		ValueTask.CompletedTask;

	/// <inheritdoc />
	public ValueTask ClearAsync(CancellationToken cancellationToken = default) =>
		ValueTask.CompletedTask;
}

/// <inheritdoc />
public sealed class NoopLocationProvider : ILocationProvider
{
	/// <inheritdoc />
	public bool IsSupported => false;

	/// <inheritdoc />
	public bool IsRecording => false;

	/// <inheritdoc />
	public Task<LocationPermissionState> EnsurePermissionsAsync(CancellationToken cancellationToken = default) =>
		Task.FromResult(LocationPermissionState.NotSupported);

	/// <inheritdoc />
	public async IAsyncEnumerable<LocationFix> WatchAsync(
		AccuracyProfile profile,
		[System.Runtime.CompilerServices.EnumeratorCancellation]
		CancellationToken cancellationToken = default)
	{
		// The browser has no continuous GPS the app can trust (§18.6). Yield nothing rather
		// than throw — a component that resolves this and immediately awaits the first fix
		// hangs forever unless somebody wires the cancellation token, which is the right
		// posture: "no fixes will ever come" is the accurate answer.
		await Task.CompletedTask;
		yield break;
	}
}

/// <summary>
/// A no-op media picker for hosts that cannot pick or capture yet. The real mobile picker uses
/// MAUI's <c>MediaPicker</c> (Phase 1); the real web picker uses <c>&lt;InputFile&gt;</c> plumbed
/// through a callback.
/// </summary>
public sealed class NoopMediaPicker : IMediaPicker
{
	/// <inheritdoc />
	public bool CanCapture => false;

	/// <inheritdoc />
	public Task<PickedMedia?> PickPhotoAsync(CancellationToken cancellationToken = default) =>
		Task.FromResult<PickedMedia?>(null);

	/// <inheritdoc />
	public Task<PickedMedia?> CapturePhotoAsync(CancellationToken cancellationToken = default) =>
		Task.FromResult<PickedMedia?>(null);
}

/// <summary>
/// A social sign-in provider that reports "not available" — the shape every host binds
/// today. Real Apple / Google bindings are additive work at Phase 3 store submission
/// (§7.16), and the Welcome page calls <see cref="IExternalSignInProvider.IsAvailable"/>
/// before it offers the button.
/// </summary>
public sealed class UnavailableExternalSignInProvider : IExternalSignInProvider
{
	public UnavailableExternalSignInProvider(ExternalProvider provider) => Provider = provider;

	/// <inheritdoc />
	public ExternalProvider Provider { get; }

	/// <inheritdoc />
	public bool IsAvailable => false;

	/// <inheritdoc />
	public Task<DLR.Core.Contracts.Identity.TokenResponse?> StartAsync(CancellationToken cancellationToken = default) =>
		Task.FromResult<DLR.Core.Contracts.Identity.TokenResponse?>(null);
}

/// <summary>
/// A push service that reports "not supported" — the web binding in v1 (§18.2), and the
/// mobile fallback while FCM/APNs are still being wired up in Phase 3.
/// </summary>
public sealed class NoopNotificationService : INotificationService
{
	/// <inheritdoc />
	public bool IsSupported => false;

	/// <inheritdoc />
	public Task RegisterAsync(string deviceToken, CancellationToken cancellationToken = default) =>
		Task.CompletedTask;

	/// <inheritdoc />
	public Task UnregisterAsync(CancellationToken cancellationToken = default) =>
		Task.CompletedTask;
}

/// <summary>
/// An in-memory <see cref="IThemeService"/> for tests and for the SSR shell (which
/// has no persistent storage of its own). Dark is the default on read — the design
/// default (§18.6).
/// </summary>
public sealed class InMemoryThemeService : IThemeService
{
	private AppTheme _theme = AppTheme.Dark;

	public Task<AppTheme> GetAsync(CancellationToken cancellationToken = default) =>
		Task.FromResult(_theme);

	public Task SetAsync(AppTheme theme, CancellationToken cancellationToken = default)
	{
		_theme = theme;
		return Task.CompletedTask;
	}
}

/// <summary>
/// An <see cref="IDeviceSettings"/> that forgets everything when the process does.
/// <para>
/// Bound by the SSR host, which has no device to store anything on — the prerender renders
/// with the shipped defaults and the WASM client re-resolves against browser
/// <c>localStorage</c> the moment it boots. Also what bUnit tests get, so a test that sets a
/// preference can read it back without a browser.
/// </para>
/// </summary>
public sealed class InMemoryDeviceSettings : IDeviceSettings
{
	private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

	/// <inheritdoc />
	public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(_values.TryGetValue(key, out string? value) ? value : null);

	/// <inheritdoc />
	public ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default)
	{
		_values[key] = value;
		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
	{
		_values.Remove(key);
		return ValueTask.CompletedTask;
	}
}

/// <summary>
/// A base-map interop that answers every question with "not initialised".
/// <para>
/// Since v0.24 the real implementation is <see cref="MapLibreInterop"/> and every
/// interactive host registers it, so this survives for one caller: the SSR pass in
/// <c>BlazorDLR.Web</c>, which has no JS runtime to import a module into. A prerender that
/// tried would fail mid-render rather than hand the client a shell to hydrate.
/// </para>
/// </summary>
public sealed class UninitialisedMapInterop : IMapInterop
{
	/// <inheritdoc />
	public MapProvider Provider => MapProvider.MapLibreOsm;

	/// <inheritdoc />
	public event Action<MapViewport>? ViewportChanged
	{
		add { /* no viewport ever emitted from a stub. */ }
		remove { /* symmetric no-op. */ }
	}

	/// <inheritdoc />
	public event Action<MapClick>? Clicked
	{
		add { /* nothing to click during a prerender. */ }
		remove { /* symmetric no-op. */ }
	}

	/// <inheritdoc />
	public ValueTask InitAsync(ElementReference host, MapOptions options, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException(Phase0.Message);

	/// <inheritdoc />
	public ValueTask SetCameraAsync(MapCamera camera, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException(Phase0.Message);

	/// <inheritdoc />
	public ValueTask DisposeAsync(CancellationToken cancellationToken = default) =>
		ValueTask.CompletedTask;
}

