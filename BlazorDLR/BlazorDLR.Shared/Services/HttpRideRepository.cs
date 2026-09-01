using DLR.Core.Contracts.Rides;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// The Phase 1 <see cref="IRideRepository"/>: passes through to <see cref="IApiClient"/>.
/// <para>
/// The ride-list endpoint does not exist in Phase 1's server surface yet - the server tracks
/// rides by explicit id from either a join code or a membership check. Until it does, the
/// list reads as empty rather than throwing, so the ride screens render "no rides yet" and
/// the app is usable. Phase 2 adds <c>GET /api/v1/me/rides</c>.
/// </para>
/// </summary>
public sealed class HttpRideRepository : IRideRepository
{
	private readonly IApiClient _api;

	public HttpRideRepository(IApiClient api)
	{
		_api = api;
	}

	/// <inheritdoc />
	public Task<IReadOnlyList<RideDetail>> ListRidesAsync(CancellationToken cancellationToken = default) =>
		Task.FromResult<IReadOnlyList<RideDetail>>(Array.Empty<RideDetail>());

	/// <inheritdoc />
	public async Task<RideDetail?> GetRideAsync(Guid rideId, CancellationToken cancellationToken = default)
	{
		try
		{
			return await _api.GetRideAsync(rideId, cancellationToken);
		}
		catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
		{
			return null;
		}
	}
}
