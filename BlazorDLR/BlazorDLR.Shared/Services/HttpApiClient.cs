using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlazorDLR.Shared.Diagnostics;
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
/// The concrete <see cref="IApiClient"/> both hosts use. One JSON pipeline, one URL surface,
/// two DI registrations — the mobile host binds it to a bearer-token <c>HttpClient</c>,
/// the web host binds it to an <c>HttpClient</c> with <c>credentials: include</c> so its
/// cookie travels (§18.5).
/// </summary>
public sealed class HttpApiClient : IApiClient
{
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	private readonly HttpClient _http;

	/// <param name="http">
	/// The host wires up the base address, the auth handler (mobile) and the credential mode (web).
	/// This class does not care which — it uses the client it was handed.
	/// </param>
	public HttpApiClient(HttpClient http)
	{
		_http = http;
	}

	// -- About --

	/// <inheritdoc />
	public Task<AboutInfo> GetAboutAsync(CancellationToken cancellationToken = default) =>
		GetAsync<AboutInfo>("/api/v1/about", cancellationToken);

	// -- Auth --

	/// <inheritdoc />
	public Task<TokenResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default) =>
		PostAsync<RegisterRequest, TokenResponse>("/api/v1/auth/register", request, cancellationToken);

	/// <inheritdoc />
	public Task<TokenResponse> TokenAsync(TokenRequest request, CancellationToken cancellationToken = default) =>
		PostAsync<TokenRequest, TokenResponse>("/api/v1/auth/token", request, cancellationToken);

	/// <inheritdoc />
	public async Task<bool> IsUserNameAvailableAsync(string userName, CancellationToken cancellationToken = default)
	{
		UserNameAvailability response = await GetAsync<UserNameAvailability>(
			$"/api/v1/auth/username-available?u={Uri.EscapeDataString(userName)}",
			cancellationToken);
		return response.Available;
	}

	/// <inheritdoc />
	public Task SetEmailAsync(SetEmailRequest request, CancellationToken cancellationToken = default) =>
		PostVoidAsync("/api/v1/auth/email", request, cancellationToken);

	/// <inheritdoc />
	public Task<TokenResponse> ConfirmEmailAsync(ConfirmEmailRequest request, CancellationToken cancellationToken = default) =>
		PostAsync<ConfirmEmailRequest, TokenResponse>("/api/v1/auth/confirm-email", request, cancellationToken);

	/// <inheritdoc />
	public Task ResendConfirmationAsync(CancellationToken cancellationToken = default) =>
		PostVoidAsync<object>("/api/v1/auth/resend-confirmation", new { }, cancellationToken);

	/// <inheritdoc />
	public Task ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken = default) =>
		PostVoidAsync("/api/v1/auth/forgot-password", request, cancellationToken);

	/// <inheritdoc />
	public Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default) =>
		PostVoidAsync("/api/v1/auth/reset-password", request, cancellationToken);

	/// <inheritdoc />
	public Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default) =>
		PostVoidAsync("/api/v1/auth/change-password", request, cancellationToken);

	/// <inheritdoc />
	public async Task<IReadOnlyList<DeviceSession>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
		await GetAsync<List<DeviceSession>>("/api/v1/auth/sessions", cancellationToken);

	/// <inheritdoc />
	public async Task RevokeSessionAsync(Guid deviceId, CancellationToken cancellationToken = default)
	{
		using HttpResponseMessage response = await _http.DeleteAsync($"/api/v1/auth/sessions/{deviceId}", cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
	}

	// -- Profile --

	/// <inheritdoc />
	public Task<OwnProfile> GetProfileAsync(CancellationToken cancellationToken = default) =>
		GetAsync<OwnProfile>("/api/v1/me/profile", cancellationToken);

	/// <inheritdoc />
	public async Task UpdateProfileAsync(UpdateProfileRequest request, CancellationToken cancellationToken = default)
	{
		using HttpResponseMessage response = await _http.PutAsJsonAsync("/api/v1/me/profile", request, Json, cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
	}

	// -- Home private area (§10.1) --

	/// <inheritdoc />
	public Task<PrivateAreaResponse> GetPrivateAreaAsync(CancellationToken cancellationToken = default) =>
		GetAsync<PrivateAreaResponse>("/api/v1/me/private-area", cancellationToken);

	/// <inheritdoc />
	public async Task SetPrivateAreaAsync(PrivateAreaSettings request, CancellationToken cancellationToken = default)
	{
		using HttpResponseMessage response = await _http.PutAsJsonAsync("/api/v1/me/private-area", request, Json, cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
	}

	/// <inheritdoc />
	public async Task ClearPrivateAreaAsync(CancellationToken cancellationToken = default)
	{
		using HttpResponseMessage response = await _http.DeleteAsync("/api/v1/me/private-area", cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
	}

	/// <inheritdoc />
	public async Task<OwnProfile> SetAvatarAsync(SetAvatarRequest request, CancellationToken cancellationToken = default)
	{
		using HttpResponseMessage response = await _http.PutAsJsonAsync("/api/v1/me/avatar", request, Json, cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
		OwnProfile? body = await response.Content.ReadFromJsonAsync<OwnProfile>(Json, cancellationToken);
		return body ?? throw new InvalidOperationException("Empty avatar response body.");
	}

	/// <inheritdoc />
	public async Task<OwnProfile> ClearAvatarAsync(CancellationToken cancellationToken = default)
	{
		using HttpResponseMessage response = await _http.DeleteAsync("/api/v1/me/avatar", cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
		OwnProfile? body = await response.Content.ReadFromJsonAsync<OwnProfile>(Json, cancellationToken);
		return body ?? throw new InvalidOperationException("Empty avatar response body.");
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<RiderAvatarDto>> GetRiderAvatarsAsync(
		IReadOnlyCollection<string> userNames,
		CancellationToken cancellationToken = default)
	{
		if (userNames.Count == 0)
		{
			return [];
		}

		// No escaping around the separator: §7.2's allowed set is letters, digits and -._, so a
		// username cannot contain a comma and cannot smuggle one in. The names are still escaped
		// individually, because a client holding a name off a cached row is untrusted input like
		// any other.
		string names = string.Join(
			AvatarLookup.Separator,
			userNames.Take(AvatarLookup.MaxNames).Select(Uri.EscapeDataString));

		return await GetAsync<List<RiderAvatarDto>>($"/api/v1/users/avatars?names={names}", cancellationToken);
	}

	// -- Tracks --

	/// <inheritdoc />
	public Task<TrackSummary> UploadTrackAsync(UploadTrackRequest request, CancellationToken cancellationToken = default) =>
		PostAsync<UploadTrackRequest, TrackSummary>("/api/v1/tracks", request, cancellationToken);

	/// <inheritdoc />
	public async Task<IReadOnlyList<TrackSummary>> ListTracksAsync(CancellationToken cancellationToken = default) =>
		await GetAsync<List<TrackSummary>>("/api/v1/tracks", cancellationToken);

	/// <inheritdoc />
	public Task<TrackDetail> GetTrackAsync(Guid trackId, CancellationToken cancellationToken = default) =>
		GetAsync<TrackDetail>($"/api/v1/tracks/{trackId}", cancellationToken);

	/// <inheritdoc />
	public Task<HttpResponseMessage> ExportTrackGpxAsync(Guid trackId, CancellationToken cancellationToken = default) =>
		_http.GetAsync($"/api/v1/tracks/{trackId}/gpx", HttpCompletionOption.ResponseHeadersRead, cancellationToken);

	/// <inheritdoc />
	public async Task<TrackSummary> RenameTrackAsync(Guid trackId, RenameTrackRequest request, CancellationToken cancellationToken = default)
	{
		using HttpRequestMessage message = new(HttpMethod.Patch, $"/api/v1/tracks/{trackId}")
		{
			Content = JsonContent.Create(request, options: Json),
		};
		using HttpResponseMessage response = await _http.SendAsync(message, cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
		TrackSummary? body = await response.Content.ReadFromJsonAsync<TrackSummary>(Json, cancellationToken);
		return body ?? throw new InvalidOperationException("Empty rename response body.");
	}

	/// <inheritdoc />
	public async Task DeleteTrackAsync(Guid trackId, CancellationToken cancellationToken = default)
	{
		using HttpResponseMessage response = await _http.DeleteAsync($"/api/v1/tracks/{trackId}", cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
	}

	/// <inheritdoc />
	public Task<TrackPointsResponse> GetTrackPointsAsync(Guid trackId, CancellationToken cancellationToken = default) =>
		GetAsync<TrackPointsResponse>($"/api/v1/tracks/{trackId}/points", cancellationToken);

	/// <inheritdoc />
	public Task<TrackEditResponse> EditTrackAsync(Guid trackId, EditTrackRequest request, CancellationToken cancellationToken = default) =>
		PostAsync<EditTrackRequest, TrackEditResponse>($"/api/v1/tracks/{trackId}/edit", request, cancellationToken);

	/// <inheritdoc />
	public async Task<TrackEditResponse> UndoTrackEditAsync(Guid trackId, CancellationToken cancellationToken = default)
	{
		using HttpResponseMessage response = await _http.PostAsync(
			$"/api/v1/tracks/{trackId}/edit/undo",
			content: null,
			cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
		TrackEditResponse? body = await response.Content.ReadFromJsonAsync<TrackEditResponse>(Json, cancellationToken);
		return body ?? throw new InvalidOperationException("Empty undo response body.");
	}

	/// <inheritdoc />
	public async Task PurgeTrackPreviousVersionAsync(Guid trackId, CancellationToken cancellationToken = default)
	{
		using HttpResponseMessage response = await _http.DeleteAsync(
			$"/api/v1/tracks/{trackId}/previous-version", cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
	}

	/// <inheritdoc />
	public async Task<TrackSummary> UpdateTrackDetailsAsync(Guid trackId, UpdateTrackDetailsRequest request, CancellationToken cancellationToken = default)
	{
		using HttpRequestMessage message = new(HttpMethod.Patch, $"/api/v1/tracks/{trackId}/details")
		{
			Content = JsonContent.Create(request, options: Json),
		};
		using HttpResponseMessage response = await _http.SendAsync(message, cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
		TrackSummary? body = await response.Content.ReadFromJsonAsync<TrackSummary>(Json, cancellationToken);
		return body ?? throw new InvalidOperationException("Empty details response body.");
	}

	/// <inheritdoc />
	public Task<SharedTrackPage> ListSharedTracksAsync(SharedTrackQuery query, CancellationToken cancellationToken = default)
	{
		// Built as a list rather than one interpolated string so that an omitted filter is an
		// absent parameter rather than an empty one — "name=" and no name at all are the same
		// thing to the server today, and relying on that is how they stop being the same thing.
		List<string> parts = [];

		if (!string.IsNullOrWhiteSpace(query.Name))
			parts.Add($"name={Uri.EscapeDataString(query.Name)}");

		if (query.HasArea)
		{
			// Invariant, always. InvariantGlobalization is on for the whole solution, but a
			// query string carrying "-34,92" because somebody's culture leaked in is the kind of
			// bug that only appears on the one device it appears on.
			parts.Add($"lat={query.Latitude!.Value.ToString(CultureInfo.InvariantCulture)}");
			parts.Add($"lon={query.Longitude!.Value.ToString(CultureInfo.InvariantCulture)}");
			parts.Add($"withinKm={query.WithinKm!.Value.ToString(CultureInfo.InvariantCulture)}");
		}

		parts.Add($"page={(query.Page < 1 ? 1 : query.Page).ToString(CultureInfo.InvariantCulture)}");

		return GetAsync<SharedTrackPage>($"/api/v1/tracks/shared?{string.Join('&', parts)}", cancellationToken);
	}

	// -- Rides --

	/// <inheritdoc />
	public Task<MyRides> ListMyRidesAsync(CancellationToken cancellationToken = default) =>
		GetAsync<MyRides>("/api/v1/group-rides", cancellationToken);

	/// <inheritdoc />
	public Task<RideDetail> GetRideAsync(Guid rideId, CancellationToken cancellationToken = default) =>
		GetAsync<RideDetail>($"/api/v1/group-rides/{rideId}", cancellationToken);

	/// <inheritdoc />
	public Task<RideDetail> CreateRideAsync(CreateRideRequest request, CancellationToken cancellationToken = default) =>
		PostAsync<CreateRideRequest, RideDetail>("/api/v1/group-rides", request, cancellationToken);

	/// <inheritdoc />
	public Task<JoinResult> JoinRideByCodeAsync(JoinByCodeRequest request, CancellationToken cancellationToken = default) =>
		PostAsync<JoinByCodeRequest, JoinResult>("/api/v1/group-rides/join", request, cancellationToken);

	/// <inheritdoc />
	public async Task<IReadOnlyList<JoinRequestSummary>> ListJoinRequestsAsync(Guid rideId, CancellationToken cancellationToken = default) =>
		await GetAsync<List<JoinRequestSummary>>($"/api/v1/group-rides/{rideId}/join-requests", cancellationToken);

	/// <inheritdoc />
	public Task DecideJoinRequestAsync(Guid rideId, Guid requestId, DecideJoinRequest request, CancellationToken cancellationToken = default) =>
		PostVoidAsync($"/api/v1/group-rides/{rideId}/join-requests/{requestId}", request, cancellationToken);

	/// <inheritdoc />
	public async Task StartRideAsync(Guid rideId, CancellationToken cancellationToken = default)
	{
		using HttpResponseMessage response = await _http.PostAsync(
			$"/api/v1/group-rides/{rideId}/start", content: null, cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
	}

	/// <inheritdoc />
	public Task EndRideAsync(Guid rideId, EndRideRequest request, CancellationToken cancellationToken = default) =>
		PostVoidAsync($"/api/v1/group-rides/{rideId}/ending", request, cancellationToken);

	/// <inheritdoc />
	public async Task UpdatePermissionsAsync(Guid rideId, RidePermissions permissions, CancellationToken cancellationToken = default)
	{
		using HttpResponseMessage response = await _http.PutAsJsonAsync(
			$"/api/v1/group-rides/{rideId}/permissions", permissions, Json, cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
	}

	/// <inheritdoc />
	public async Task SetSharingAsync(Guid rideId, SetSharingRequest request, CancellationToken cancellationToken = default)
	{
		using HttpResponseMessage response = await _http.PutAsJsonAsync(
			$"/api/v1/group-rides/{rideId}/sharing/me", request, Json, cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
	}

	/// <inheritdoc />
	public async Task LeaveRideAsync(Guid rideId, CancellationToken cancellationToken = default)
	{
		using HttpResponseMessage response = await _http.DeleteAsync(
			$"/api/v1/group-rides/{rideId}/members/me", cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
	}

	/// <inheritdoc />
	public async Task RemoveMemberAsync(Guid rideId, Guid userId, CancellationToken cancellationToken = default)
	{
		using HttpResponseMessage response = await _http.DeleteAsync(
			$"/api/v1/group-rides/{rideId}/members/{userId}", cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
	}

	// -- Planned routes --

	/// <inheritdoc />
	public async Task<IReadOnlyList<RideRoute>> ListRideRoutesAsync(Guid rideId, CancellationToken cancellationToken = default) =>
		await GetAsync<List<RideRoute>>($"/api/v1/group-rides/{rideId}/routes", cancellationToken);

	/// <inheritdoc />
	public Task<RideRoute> AddRideRouteAsync(Guid rideId, AddRideRouteRequest request, CancellationToken cancellationToken = default) =>
		PostAsync<AddRideRouteRequest, RideRoute>($"/api/v1/group-rides/{rideId}/routes", request, cancellationToken);

	/// <inheritdoc />
	public async Task RemoveRideRouteAsync(Guid rideId, Guid trackId, CancellationToken cancellationToken = default)
	{
		using HttpResponseMessage response = await _http.DeleteAsync(
			$"/api/v1/group-rides/{rideId}/routes/{trackId}", cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
	}

	// -- Positions --

	/// <inheritdoc />
	public async Task<IReadOnlyList<RiderPositionDto>> GetPositionsSnapshotAsync(Guid rideId, CancellationToken cancellationToken = default) =>
		await GetAsync<List<RiderPositionDto>>($"/api/v1/group-rides/{rideId}/positions", cancellationToken);

	/// <inheritdoc />
	public Task<PublishResult> PublishPositionAsync(PositionUpdate update, CancellationToken cancellationToken = default) =>
		PostAsync<PositionUpdate, PublishResult>("/api/v1/positions", update, cancellationToken);

	// -- Markers --

	/// <inheritdoc />
	public Task<MarkerDto> CreateMarkerAsync(CreateMarkerRequest request, CancellationToken cancellationToken = default) =>
		PostAsync<CreateMarkerRequest, MarkerDto>("/api/v1/markers", request, cancellationToken);

	/// <inheritdoc />
	public async Task<IReadOnlyList<MarkerDto>> ListRideMarkersAsync(Guid rideId, CancellationToken cancellationToken = default) =>
		await GetAsync<List<MarkerDto>>($"/api/v1/group-rides/{rideId}/markers", cancellationToken);

	/// <inheritdoc />
	public async Task<MarkerDto> UpdateMarkerAsync(Guid markerId, UpdateMarkerRequest request, CancellationToken cancellationToken = default)
	{
		using HttpResponseMessage response = await _http.PutAsJsonAsync(
			$"/api/v1/markers/{markerId}", request, Json, cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
		MarkerDto? body = await response.Content.ReadFromJsonAsync<MarkerDto>(Json, cancellationToken);
		return body ?? throw new InvalidOperationException("Empty marker response.");
	}

	/// <inheritdoc />
	public async Task DeleteMarkerAsync(Guid markerId, CancellationToken cancellationToken = default)
	{
		using HttpResponseMessage response = await _http.DeleteAsync($"/api/v1/markers/{markerId}", cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
	}

	/// <inheritdoc />
	public async Task AttachMarkerPhotoAsync(Guid markerId, AttachPhotoRequest request, CancellationToken cancellationToken = default)
	{
		using HttpRequestMessage message = new(HttpMethod.Patch, $"/api/v1/markers/{markerId}/photo")
		{
			Content = JsonContent.Create(request, options: Json),
		};
		using HttpResponseMessage response = await _http.SendAsync(message, cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
	}

	// -- Photos --

	/// <inheritdoc />
	public async Task<PhotoUploaded> UploadPhotoAsync(Stream content, string contentType, string fileName, CancellationToken cancellationToken = default)
	{
		using MultipartFormDataContent form = new();
		StreamContent bytes = new(content);
		bytes.Headers.ContentType = new MediaTypeHeaderValue(contentType);
		form.Add(bytes, "file", fileName);

		using HttpResponseMessage response = await _http.PostAsync("/api/v1/photos", form, cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
		PhotoUploaded? body = await response.Content.ReadFromJsonAsync<PhotoUploaded>(Json, cancellationToken);
		return body ?? throw new InvalidOperationException("Empty photo response.");
	}

	/// <inheritdoc />
	public Task<TrackRatingSummary> GetTrackRatingAsync(Guid trackId, CancellationToken cancellationToken = default) =>
		GetAsync<TrackRatingSummary>($"/api/v1/tracks/{trackId}/rating", cancellationToken);

	/// <inheritdoc />
	public async Task<TrackRatingSummary> RateTrackAsync(Guid trackId, RateTrackRequest request, CancellationToken cancellationToken = default)
	{
		using HttpResponseMessage response = await _http.PutAsJsonAsync(
			$"/api/v1/tracks/{trackId}/rating", request, Json, cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
		TrackRatingSummary? body = await response.Content.ReadFromJsonAsync<TrackRatingSummary>(Json, cancellationToken);
		return body ?? throw new InvalidOperationException("Empty rating response.");
	}

	/// <inheritdoc />
	public async Task<TrackRatingSummary> ClearTrackRatingAsync(Guid trackId, CancellationToken cancellationToken = default)
	{
		using HttpResponseMessage response = await _http.DeleteAsync($"/api/v1/tracks/{trackId}/rating", cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
		TrackRatingSummary? body = await response.Content.ReadFromJsonAsync<TrackRatingSummary>(Json, cancellationToken);
		return body ?? throw new InvalidOperationException("Empty rating response.");
	}

	// -- Comments --

	/// <inheritdoc />
	public Task<CommentPage> GetThreadAsync(Guid rideId, string? cursor, CancellationToken cancellationToken = default) =>
		GetAsync<CommentPage>(ThreadPath($"/api/v1/group-rides/{rideId}/comments", cursor), cancellationToken);

	/// <inheritdoc />
	public Task<CommentDto> PostCommentAsync(Guid rideId, PostCommentRequest request, CancellationToken cancellationToken = default) =>
		PostAsync<PostCommentRequest, CommentDto>($"/api/v1/group-rides/{rideId}/comments", request, cancellationToken);

	/// <inheritdoc />
	public Task<CommentPage> GetTrackThreadAsync(Guid trackId, string? cursor, CancellationToken cancellationToken = default) =>
		GetAsync<CommentPage>(ThreadPath($"/api/v1/tracks/{trackId}/comments", cursor), cancellationToken);

	/// <inheritdoc />
	public Task<CommentDto> PostTrackCommentAsync(Guid trackId, PostCommentRequest request, CancellationToken cancellationToken = default) =>
		PostAsync<PostCommentRequest, CommentDto>($"/api/v1/tracks/{trackId}/comments", request, cancellationToken);

	/// <summary>
	/// Appends the cursor, escaped. The cursor is opaque to us — a position in a result set the
	/// server chose the encoding of — so it goes through <see cref="Uri.EscapeDataString"/> rather
	/// than being trusted to be URL-safe.
	/// </summary>
	private static string ThreadPath(string path, string? cursor) =>
		cursor is null ? path : $"{path}?cursor={Uri.EscapeDataString(cursor)}";

	/// <inheritdoc />
	public async Task<CommentDto> EditCommentAsync(Guid commentId, EditCommentRequest request, CancellationToken cancellationToken = default)
	{
		using HttpRequestMessage message = new(HttpMethod.Patch, $"/api/v1/comments/{commentId}")
		{
			Content = JsonContent.Create(request, options: Json),
		};
		using HttpResponseMessage response = await _http.SendAsync(message, cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
		CommentDto? body = await response.Content.ReadFromJsonAsync<CommentDto>(Json, cancellationToken);
		return body ?? throw new InvalidOperationException("Empty comment response.");
	}

	/// <inheritdoc />
	public async Task DeleteCommentAsync(Guid commentId, CancellationToken cancellationToken = default)
	{
		using HttpResponseMessage response = await _http.DeleteAsync($"/api/v1/comments/{commentId}", cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
	}

	/// <inheritdoc />
	public Task PinCommentAsync(Guid commentId, PinCommentRequest request, CancellationToken cancellationToken = default) =>
		PostVoidAsync($"/api/v1/comments/{commentId}/pin", request, cancellationToken);

	/// <inheritdoc />
	public async Task SetReactionAsync(Guid commentId, SetReactionRequest request, CancellationToken cancellationToken = default)
	{
		using HttpResponseMessage response = await _http.PutAsJsonAsync(
			$"/api/v1/comments/{commentId}/reaction", request, Json, cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
	}

	/// <inheritdoc />
	public Task CastVoteAsync(Guid commentId, CastVoteRequest request, CancellationToken cancellationToken = default) =>
		PostVoidAsync($"/api/v1/comments/{commentId}/votes", request, cancellationToken);

	/// <inheritdoc />
	public async Task ClosePollAsync(Guid commentId, CancellationToken cancellationToken = default)
	{
		using HttpResponseMessage response = await _http.PostAsync(
			$"/api/v1/comments/{commentId}/close-poll", content: null, cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
	}

	// -- Moderation --

	/// <inheritdoc />
	public Task<ContentReported> ReportCommentAsync(Guid commentId, ReportContentRequest request, CancellationToken cancellationToken = default) =>
		PostAsync<ReportContentRequest, ContentReported>($"/api/v1/comments/{commentId}/report", request, cancellationToken);

	/// <inheritdoc />
	public Task<ContentReported> ReportMarkerAsync(Guid markerId, ReportContentRequest request, CancellationToken cancellationToken = default) =>
		PostAsync<ReportContentRequest, ContentReported>($"/api/v1/markers/{markerId}/report", request, cancellationToken);

	/// <inheritdoc />
	public Task BlockUserAsync(BlockUserRequest request, CancellationToken cancellationToken = default) =>
		PostVoidAsync("/api/v1/blocks", request, cancellationToken);

	/// <inheritdoc />
	public async Task UnblockUserAsync(Guid userId, CancellationToken cancellationToken = default)
	{
		using HttpResponseMessage response = await _http.DeleteAsync($"/api/v1/blocks/{userId}", cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<BlockedRider>> ListBlocksAsync(CancellationToken cancellationToken = default) =>
		await GetAsync<List<BlockedRider>>("/api/v1/blocks", cancellationToken);

	// -- Account --

	/// <inheritdoc />
	public Task<HttpResponseMessage> ExportAccountAsync(CancellationToken cancellationToken = default) =>
		_http.GetAsync("/api/v1/me/export", HttpCompletionOption.ResponseHeadersRead, cancellationToken);

	/// <inheritdoc />
	public async Task DeleteAccountAsync(DeleteAccountRequest request, CancellationToken cancellationToken = default)
	{
		// DELETE with a body — HttpClient has no built-in helper for it, and the endpoint
		// takes the current password in the body deliberately (§6.3): putting it on the
		// query string would land the password in Caddy's access log.
		using HttpRequestMessage message = new(HttpMethod.Delete, "/api/v1/me")
		{
			Content = JsonContent.Create(request, options: Json),
		};
		using HttpResponseMessage response = await _http.SendAsync(message, cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
	}

	// -- Helpers --

	private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
	{
		using HttpResponseMessage response = await _http.GetAsync(path, cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
		T? body = await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
		return body ?? throw new InvalidOperationException($"Empty response body from {path}.");
	}

	private async Task<TResponse> PostAsync<TRequest, TResponse>(
		string path,
		TRequest request,
		CancellationToken cancellationToken)
	{
		using HttpResponseMessage response = await _http.PostAsJsonAsync(path, request, Json, cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
		TResponse? body = await response.Content.ReadFromJsonAsync<TResponse>(Json, cancellationToken);
		return body ?? throw new InvalidOperationException($"Empty response body from {path}.");
	}

	private async Task PostVoidAsync<TRequest>(string path, TRequest request, CancellationToken cancellationToken)
	{
		using HttpResponseMessage response = await _http.PostAsJsonAsync(path, request, Json, cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);
	}

	private Task PostVoidAsync(string path, object request, CancellationToken cancellationToken) =>
		PostVoidAsync<object>(path, request, cancellationToken);

	/// <summary>
	/// Non-success responses become <see cref="ApiException"/> with the parsed
	/// <c>ProblemDetails</c> body (§18.2). Screens catch this and render every message the
	/// server sent, rather than <c>EnsureSuccessStatusCode</c>'s "Response status code does
	/// not indicate success". The server returns useful reasons; the client should not throw
	/// them away.
	/// </summary>
	private static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		if (response.IsSuccessStatusCode)
		{
			return;
		}

		ApiError error = await ProblemDetailsReader.ReadAsync(response, cancellationToken);

		// Every failed call in the app funnels through here, so this one line covers the lot. The
		// method and path matter as much as the reason: a 401 on the token endpoint and a 401 on a
		// ride are the same message about two completely different problems.
		DiagnosticLog.Write(
			$"API {(int)response.StatusCode} {response.RequestMessage?.Method.Method} " +
			$"{response.RequestMessage?.RequestUri?.PathAndQuery}: {error.Title}");

		throw new ApiException(error);
	}

	private sealed record UserNameAvailability(bool Available);
}
