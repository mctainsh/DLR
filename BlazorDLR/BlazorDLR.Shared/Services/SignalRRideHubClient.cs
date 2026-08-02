using DLR.Core.Contracts.Comments;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Rides;
using Microsoft.AspNetCore.SignalR.Client;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// The <see cref="IRideHubClient"/> both hosts use, wrapping <see cref="HubConnection"/>.
/// <para>
/// <strong>Reconnect strategy (§5.3).</strong> Automatic reconnect with jitter — the built-in
/// <c>WithAutomaticReconnect</c> handles the loop, and this class supplies the delay set with
/// a jittered exponential curve. On reconnect the client re-joins any ride groups it was in
/// before the drop; §5.3's rule is <em>fetch snapshot state</em> rather than replay history,
/// so the ride pages call <c>IApiClient.GetRideAsync</c> after they see the reconnected event.
/// </para>
/// <para>
/// <strong>Access-token expiration is deliberately not enforced on the connection.</strong>
/// SignalR's <c>CloseOnAuthenticationExpiration</c> would kill a two-hour ride's connection
/// every fifteen minutes for no reason (§7.6). The token provider below supplies a fresh
/// token on reconnect, which is where rotation belongs.
/// </para>
/// </summary>
public sealed class SignalRRideHubClient : IRideHubClient
{
	private static readonly TimeSpan[] ReconnectDelays =
	[
		TimeSpan.FromSeconds(0),
		TimeSpan.FromSeconds(2),
		TimeSpan.FromSeconds(5),
		TimeSpan.FromSeconds(10),
		TimeSpan.FromSeconds(30),
	];

	private readonly Uri _hubUri;
	private readonly Func<CancellationToken, Task<string?>> _tokenProvider;
	private readonly HashSet<Guid> _joinedRides = new();

	private HubConnection? _connection;

	/// <param name="hubUri">The absolute or origin-relative URL of the ride hub (§5.3).</param>
	/// <param name="tokenProvider">
	/// Returns the current access token. Called on connect, on reconnect, and on
	/// negotiation — mobile reads it out of <c>SecureStorage</c>, web returns null and lets
	/// the cookie do the work (§18.5).
	/// </param>
	public SignalRRideHubClient(Uri hubUri, Func<CancellationToken, Task<string?>> tokenProvider)
	{
		_hubUri = hubUri;
		_tokenProvider = tokenProvider;
	}

	/// <inheritdoc />
	public bool IsConnected => _connection?.State == HubConnectionState.Connected;

	/// <inheritdoc />
	public event Action<PositionBatch>? PositionsUpdated;
	/// <inheritdoc />
	public event Action<Guid, RideMemberSummary>? MemberJoined;
	/// <inheritdoc />
	public event Action<Guid, Guid>? MemberLeft;
	/// <inheritdoc />
	public event Action<Guid, RideStateDto>? RideStateChanged;
	/// <inheritdoc />
	public event Action<Guid, Guid>? RouteUpdated;
	/// <inheritdoc />
	public event Action<Guid, JoinRequestSummary>? JoinRequestReceived;
	/// <inheritdoc />
	public event Action<Guid, JoinResult>? JoinRequestDecided;
	/// <inheritdoc />
	public event Action<Guid, MarkerDto>? MarkerAdded;
	/// <inheritdoc />
	public event Action<Guid, MarkerDto>? MarkerUpdated;
	/// <inheritdoc />
	public event Action<Guid, Guid>? MarkerRemoved;
	/// <inheritdoc />
	public event Action<Guid, CommentDto>? CommentPosted;
	/// <inheritdoc />
	public event Action<Guid, CommentDto>? CommentEdited;
	/// <inheritdoc />
	public event Action<Guid, Guid>? CommentRemoved;
	/// <inheritdoc />
	public event Action<Guid, Guid, bool>? CommentPinChanged;
	/// <inheritdoc />
	public event Action<Guid, ReactionCounts>? ReactionsUpdated;
	/// <inheritdoc />
	public event Action<Guid, PollResults>? PollUpdated;
	/// <inheritdoc />
	public event Action<Guid, RidePermissions>? PermissionsChanged;
	/// <inheritdoc />
	public event Action<Guid, DateTimeOffset>? SharingWindDownStarted;
	/// <inheritdoc />
	public event Action<Guid, Guid, bool>? MemberSharingChanged;

	/// <inheritdoc />
	public async Task ConnectAsync(CancellationToken cancellationToken = default)
	{
		if (_connection is not null)
		{
			return;
		}

		HubConnection connection = new HubConnectionBuilder()
			.WithUrl(_hubUri, options =>
			{
				options.AccessTokenProvider = async () => await _tokenProvider(CancellationToken.None);
			})
			.WithAutomaticReconnect(new JitteredReconnect(ReconnectDelays))
			.Build();

		// Every event name here matches the corresponding member on IRideClient on the server side
		// of the hub (§5.3). A misspelling would produce silence rather than an error at connect,
		// which is why they live in one place close to the connection setup.
		connection.On<PositionBatch>("PositionsUpdated", batch => PositionsUpdated?.Invoke(batch));
		connection.On<Guid, RideMemberSummary>("MemberJoined", (r, m) => MemberJoined?.Invoke(r, m));
		connection.On<Guid, Guid>("MemberLeft", (r, u) => MemberLeft?.Invoke(r, u));
		connection.On<Guid, RideStateDto>("RideStateChanged", (r, s) => RideStateChanged?.Invoke(r, s));
		connection.On<Guid, Guid>("RouteUpdated", (r, t) => RouteUpdated?.Invoke(r, t));
		connection.On<Guid, JoinRequestSummary>("JoinRequestReceived", (r, req) => JoinRequestReceived?.Invoke(r, req));
		connection.On<Guid, JoinResult>("JoinRequestDecided", (r, res) => JoinRequestDecided?.Invoke(r, res));
		connection.On<Guid, MarkerDto>("MarkerAdded", (r, m) => MarkerAdded?.Invoke(r, m));
		connection.On<Guid, MarkerDto>("MarkerUpdated", (r, m) => MarkerUpdated?.Invoke(r, m));
		connection.On<Guid, Guid>("MarkerRemoved", (r, m) => MarkerRemoved?.Invoke(r, m));
		connection.On<Guid, CommentDto>("CommentPosted", (r, c) => CommentPosted?.Invoke(r, c));
		connection.On<Guid, CommentDto>("CommentEdited", (r, c) => CommentEdited?.Invoke(r, c));
		connection.On<Guid, Guid>("CommentRemoved", (r, c) => CommentRemoved?.Invoke(r, c));
		connection.On<Guid, Guid, bool>("CommentPinChanged", (r, c, p) => CommentPinChanged?.Invoke(r, c, p));
		connection.On<Guid, ReactionCounts>("ReactionsUpdated", (c, counts) => ReactionsUpdated?.Invoke(c, counts));
		connection.On<Guid, PollResults>("PollUpdated", (c, results) => PollUpdated?.Invoke(c, results));
		connection.On<Guid, RidePermissions>("RidePermissionsChanged", (r, p) => PermissionsChanged?.Invoke(r, p));
		connection.On<Guid, DateTimeOffset>("SharingWindDownStarted", (r, ends) => SharingWindDownStarted?.Invoke(r, ends));
		connection.On<Guid, Guid, bool>("MemberSharingChanged", (r, u, s) => MemberSharingChanged?.Invoke(r, u, s));

		connection.Reconnected += async _ =>
		{
			// Re-join every group this client held before the drop. The server's group
			// tracking lives in the connection that just died; nothing on the new one knows
			// about it until we ask.
			foreach (Guid rideId in _joinedRides.ToList())
			{
				try { await connection.InvokeAsync("JoinRide", rideId); }
				catch { /* the ride page's OnConnected handler will retry on next tick */ }
			}
		};

		_connection = connection;
		await connection.StartAsync(cancellationToken);
	}

	/// <inheritdoc />
	public async Task JoinRideAsync(Guid rideId, CancellationToken cancellationToken = default)
	{
		HubConnection connection = Require();
		await connection.InvokeAsync("JoinRide", rideId, cancellationToken);
		_joinedRides.Add(rideId);
	}

	/// <inheritdoc />
	public async Task LeaveRideAsync(Guid rideId, CancellationToken cancellationToken = default)
	{
		HubConnection connection = Require();
		await connection.InvokeAsync("LeaveRide", rideId, cancellationToken);
		_joinedRides.Remove(rideId);
	}

	/// <inheritdoc />
	public async Task PublishPositionAsync(PositionUpdate update, CancellationToken cancellationToken = default)
	{
		HubConnection connection = Require();
		await connection.InvokeAsync("PublishPosition", update, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		if (_connection is not null)
		{
			try { await _connection.DisposeAsync(); } catch { /* already gone */ }
			_connection = null;
		}
		_joinedRides.Clear();
	}

	private HubConnection Require() =>
		_connection ?? throw new InvalidOperationException("Hub is not connected. Call ConnectAsync first.");

	private sealed class JitteredReconnect : IRetryPolicy
	{
		private readonly TimeSpan[] _delays;

		public JitteredReconnect(TimeSpan[] delays) => _delays = delays;

		public TimeSpan? NextRetryDelay(RetryContext retryContext)
		{
			int index = (int)Math.Min(retryContext.PreviousRetryCount, _delays.Length - 1);
			TimeSpan @base = _delays[index];
			// ± 25 % jitter so many clients disconnected by the same server bounce do not all
			// return in the same millisecond (§5.3, "reconnect with jitter").
			double jitter = 1.0 + (Random.Shared.NextDouble() * 0.5 - 0.25);
			return TimeSpan.FromMilliseconds(@base.TotalMilliseconds * jitter);
		}
	}
}
