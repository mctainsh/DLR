namespace DLR.Core.Tracks;

/// <summary>
/// A track's shape: every point in file order, plus where the segments begin (§15.3).
/// <para>
/// Segments are held as indices into one flat list rather than as a list of lists, because the
/// editor works in <em>half-open raw index ranges</em> (§15.5) and a nested shape would mean
/// translating between two coordinate systems on every edit. A segment break is a pause or a
/// signal gap: distance and duration are never summed across one, and a straight line drawn
/// through a tunnel is a ride that never happened.
/// </para>
/// </summary>
public sealed class TrackGeometry
{
	private readonly int[] _segmentStarts;

	/// <summary>Builds a geometry from points and the indices segments start at.</summary>
	/// <param name="points">Every point, in file order.</param>
	/// <param name="segmentStarts">
	/// Ascending indices into <paramref name="points"/>. A leading zero is implied and added if
	/// absent, so a single-segment track can pass an empty list.
	/// </param>
	public TrackGeometry(IReadOnlyList<TrackPoint> points, IEnumerable<int>? segmentStarts = null)
	{
		Points = points;

		int[] starts = [.. (segmentStarts ?? []).Where(index => index > 0 && index < points.Count).Distinct().Order()];

		_segmentStarts = points.Count == 0 ? [] : [0, .. starts];
	}

	/// <summary>Every point, in the order the file gave them.</summary>
	public IReadOnlyList<TrackPoint> Points { get; }

	/// <summary>Where each segment starts. Empty only when there are no points at all.</summary>
	public IReadOnlyList<int> SegmentStarts => _segmentStarts;

	/// <summary>How many segments the track has.</summary>
	public int SegmentCount => _segmentStarts.Length;

	/// <summary>
	/// Consecutive pairs within a segment — every pair a distance or a duration may be measured
	/// across, and none that spans a break.
	/// </summary>
	public IEnumerable<(TrackPoint From, TrackPoint To)> Legs()
	{
		for (int segment = 0; segment < _segmentStarts.Length; segment++)
		{
			int start = _segmentStarts[segment];

			int end = segment + 1 < _segmentStarts.Length
				? _segmentStarts[segment + 1]
				: Points.Count;

			for (int index = start + 1; index < end; index++)
			{
				yield return (Points[index - 1], Points[index]);
			}
		}
	}
}
