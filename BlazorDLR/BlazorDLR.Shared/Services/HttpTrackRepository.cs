using DLR.Core.Contracts.Tracks;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// The Phase 1 <see cref="ITrackRepository"/>: passes straight through to
/// <see cref="IApiClient"/>. Both hosts bind this today. Phase 2+ replaces the mobile
/// binding with a SQLite-backed repository that reads local storage first (§4.4).
/// </summary>
public sealed class HttpTrackRepository : ITrackRepository
{
	private readonly IApiClient _api;

	public HttpTrackRepository(IApiClient api)
	{
		_api = api;
	}

	/// <inheritdoc />
	public Task<IReadOnlyList<TrackSummary>> ListTracksAsync(CancellationToken cancellationToken = default) =>
		_api.ListTracksAsync(cancellationToken);

	/// <inheritdoc />
	public async Task<TrackDetail?> GetTrackAsync(Guid trackId, CancellationToken cancellationToken = default)
	{
		try
		{
			return await _api.GetTrackAsync(trackId, cancellationToken);
		}
		catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
		{
			return null;
		}
	}
}
