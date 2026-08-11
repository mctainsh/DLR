using DLR.Core.Contracts.Comments;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Rides;

namespace BlazorDLR.Shared.Services.Platform;

/// <summary>
/// The <see cref="IRideHubClient"/> the SSR pass in <c>BlazorDLR.Web</c> binds.
/// <para>
/// A static render has no realtime connection and nothing to deliver events to — the WASM
/// client that boots after it re-resolves this interface against its own DI and connects
/// there. Reading <see cref="IsConnected"/> answers <c>false</c> honestly; opening a
/// connection throws, because a component doing that mid-prerender is a wiring bug and
/// should say so rather than hang.
/// </para>
/// </summary>
public sealed class ThrowingRideHubClient : IRideHubClient
{
	private const string SsrGuard =
		"The SSR pass has no realtime connection — the WASM client that boots after it " +
		"re-resolves IRideHubClient and connects there. A component opening a hub " +
		"connection during a static render is a wiring bug.";

	/// <inheritdoc />
	public bool IsConnected => false;

	// Every event on IRideHubClient is declared here so the interface is satisfiable. None
	// ever fire: this binding exists for a render pass that ends before any server push
	// could arrive.
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
		throw new NotImplementedException(SsrGuard);

	/// <inheritdoc />
	public Task JoinRideAsync(Guid rideId, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException(SsrGuard);

	/// <inheritdoc />
	public Task LeaveRideAsync(Guid rideId, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException(SsrGuard);

	/// <inheritdoc />
	public Task PublishPositionAsync(PositionUpdate update, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException(SsrGuard);

	/// <inheritdoc />
	public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
