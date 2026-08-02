using BlazorDLR.Shared.Services;
using DLR.Core.Contracts.Tracks;

namespace DLR.UI.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="ITrackRepository"/> for the shared UI's Phase 1 tests. Phase 2+
/// swaps the mobile binding for a SQLite-backed implementation (§4.4) — components resolve
/// the interface and never the concrete class, so a test can hand any implementation over.
/// </summary>
public sealed class FakeTrackRepository : ITrackRepository
{
	public List<TrackSummary> Tracks { get; } = new();
	public TrackDetail? DetailResult { get; set; }

	/// <summary>Set to make <see cref="ListTracksAsync"/> throw for the error-path test.</summary>
	public Exception? ListException { get; set; }

	public Task<IReadOnlyList<TrackSummary>> ListTracksAsync(CancellationToken cancellationToken = default)
	{
		if (ListException is not null) throw ListException;
		return Task.FromResult<IReadOnlyList<TrackSummary>>(Tracks);
	}

	public Task<TrackDetail?> GetTrackAsync(Guid trackId, CancellationToken cancellationToken = default) =>
		Task.FromResult(DetailResult);
}
