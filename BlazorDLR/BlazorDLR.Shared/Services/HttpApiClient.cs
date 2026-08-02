using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DLR.Core.Contracts.Account;
using DLR.Core.Contracts.Comments;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Maps;
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

	// -- Tracks --

	/// <inheritdoc />
	public async Task<IReadOnlyList<TrackSummary>> ListTracksAsync(CancellationToken cancellationToken = default) =>
		await GetAsync<List<TrackSummary>>("/api/v1/tracks", cancellationToken);

	/// <inheritdoc />
	public Task<TrackDetail> GetTrackAsync(Guid trackId, CancellationToken cancellationToken = default) =>
		GetAsync<TrackDetail>($"/api/v1/tracks/{trackId}", cancellationToken);

	/// <inheritdoc />
	public async Task<TrackImportResult> ImportTracksAsync(
		Stream file,
		string fileName,
		bool dryRun,
		CancellationToken cancellationToken = default)
	{
		// The server sniffs the content-type of the file (§15.3), so this hint is just a hint.
		// Content-Disposition supplies the file name because the server logs it and, on the
		// success path, records it on the resulting Track.
		using MultipartFormDataContent form = new();
		StreamContent fileContent = new(file);
		fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/gpx+xml");
		form.Add(fileContent, "file", fileName);

		string url = dryRun ? "/api/v1/tracks/import?dryRun=true" : "/api/v1/tracks/import";
		using HttpResponseMessage response = await _http.PostAsync(url, form, cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);

		TrackImportResult? body = await response.Content.ReadFromJsonAsync<TrackImportResult>(Json, cancellationToken);
		return body ?? throw new InvalidOperationException("Empty import response body.");
	}

	/// <inheritdoc />
	public Task<HttpResponseMessage> ExportTrackGpxAsync(Guid trackId, CancellationToken cancellationToken = default) =>
		_http.GetAsync($"/api/v1/tracks/{trackId}/gpx", HttpCompletionOption.ResponseHeadersRead, cancellationToken);

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

	// -- Rides --

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

	// -- Comments --

	/// <inheritdoc />
	public Task<CommentPage> GetThreadAsync(Guid rideId, string? cursor, CancellationToken cancellationToken = default)
	{
		string path = cursor is null
			? $"/api/v1/group-rides/{rideId}/comments"
			: $"/api/v1/group-rides/{rideId}/comments?cursor={Uri.EscapeDataString(cursor)}";
		return GetAsync<CommentPage>(path, cancellationToken);
	}

	/// <inheritdoc />
	public Task<CommentDto> PostCommentAsync(Guid rideId, PostCommentRequest request, CancellationToken cancellationToken = default) =>
		PostAsync<PostCommentRequest, CommentDto>($"/api/v1/group-rides/{rideId}/comments", request, cancellationToken);

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

	// -- MapKit token — not on IApiClient because only the map interop calls it --

	/// <summary>
	/// <c>GET /api/v1/maps/token</c> — the MapKit JS credential (§4.5). Not on
	/// <see cref="IApiClient"/> because only the map interop calls it and the interface
	/// stays small on purpose.
	/// </summary>
	public Task<MapToken> GetMapTokenAsync(CancellationToken cancellationToken = default) =>
		GetAsync<MapToken>("/api/v1/maps/token", cancellationToken);

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
		throw new ApiException(error);
	}

	private sealed record UserNameAvailability(bool Available);
}
