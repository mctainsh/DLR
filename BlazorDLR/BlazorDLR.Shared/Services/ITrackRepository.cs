using DLR.Core.Contracts.Tracks;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// Every track read a shared component makes goes through this interface (§18.6).
/// <para>
/// <strong>Phase 1:</strong> both hosts bind this to <c>HttpTrackRepository</c>, which
/// delegates to <see cref="IApiClient"/>. <strong>Phase 2+:</strong> the mobile host will
/// swap to a SQLite-backed implementation that reads from local storage first and drains
/// an outbox in the background (§4.4). Components resolve the interface, not the concrete
/// class, so the swap is one line of DI and no screen changes.
/// </para>
/// </summary>
public interface ITrackRepository
{
	/// <summary>The tracks the caller owns.</summary>
	Task<IReadOnlyList<TrackSummary>> ListTracksAsync(CancellationToken cancellationToken = default);

	/// <summary>One track with its simplified polyline for display.</summary>
	Task<TrackDetail?> GetTrackAsync(Guid trackId, CancellationToken cancellationToken = default);
}
