using DLR.Core.Contracts.Rides;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// Every ride read a shared component makes goes through this interface (§18.6).
/// <para>
/// <strong>Mobile:</strong> SQLite is the source of truth; the outbox drains uploads on a
/// background loop (§4.4). A component reads what is on the device and does not care whether
/// the server has caught up.
/// <strong>Web:</strong> passes straight through to <see cref="IApiClient"/>. There is no
/// browser-side persistence in v1 (§18.6).
/// </para>
/// <para>
/// A page that calls <see cref="IApiClient"/> directly bypasses SQLite on mobile, which turns
/// the offline promise (§4.4) into fiction on exactly the screens that need it most. So the
/// rule is: pages resolve this, not <c>IApiClient</c>.
/// </para>
/// </summary>
public interface IRideRepository
{
	/// <summary>The rides the caller is a member of.</summary>
	Task<IReadOnlyList<RideDetail>> ListRidesAsync(CancellationToken cancellationToken = default);

	/// <summary>One ride, by id.</summary>
	Task<RideDetail?> GetRideAsync(Guid rideId, CancellationToken cancellationToken = default);
}
