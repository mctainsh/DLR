namespace DLR.Core.Tracks;

/// <summary>Why an edit was refused (§15.5). Every one is a <c>400</c> naming the range.</summary>
public enum TrackEditError
{
	/// <summary>The edit is fine.</summary>
	None = 0,

	/// <summary>A range covers no points.</summary>
	EmptyRange = 1,

	/// <summary>A range starts before zero or ends past the last point.</summary>
	OutOfBounds = 2,

	/// <summary>Ranges are not ascending, or two of them overlap.</summary>
	OverlappingOrDescending = 3,

	/// <summary>
	/// Fewer than two points would survive. Below two it is not a line — and deleting the whole
	/// thing is <c>DELETE /tracks/{id}</c>, which already exists.
	/// </summary>
	TooFewPointsRemain = 4,
}

/// <summary>
/// The result of an edit: the new shape, or why it was refused.
/// <para>
/// A result rather than an exception, unlike <see cref="GpxReader"/>. The difference is real:
/// a malformed GPX file is discovered mid-stream by a parser that throws, while an edit is
/// validated up front against indices the caller chose. Refusing one is an answer, not an
/// accident.
/// </para>
/// </summary>
/// <param name="Error">What was wrong, or <see cref="TrackEditError.None"/>.</param>
/// <param name="Message">The problem in words, naming the offending range.</param>
/// <param name="Geometry">The surviving points, when the edit was applied.</param>
public readonly record struct TrackEditResult(
	TrackEditError Error,
	string? Message,
	TrackGeometry? Geometry)
{
	/// <summary>Whether the edit was applied.</summary>
	public bool IsValid => Error == TrackEditError.None;

	/// <summary>The edited shape, or a throw if the edit was refused.</summary>
	public TrackGeometry Result =>
		Geometry ?? throw new InvalidOperationException($"The edit was refused: {Message}");

	internal static TrackEditResult Applied(TrackGeometry geometry) =>
		new(TrackEditError.None, null, geometry);

	internal static TrackEditResult Refused(TrackEditError error, string message) =>
		new(error, message, null);
}

/// <summary>
/// One primitive, three gestures (§15.5).
/// <para>
/// Trim the start, trim the end, cut something out of the middle — all of them are
/// <em>remove a half-open range of raw point indices</em>. Trim-start is a range anchored at 0,
/// trim-end is a range ending at the point count, an interior cut is neither. Three separate
/// operations would be three places for the index arithmetic to be subtly different.
/// </para>
/// </summary>
public static class TrackEditor
{
	/// <summary>Below this a track is not a line (§15.5).</summary>
	public const int MinimumSurvivingPoints = 2;

	/// <summary>Removes the given ranges, or explains why it will not.</summary>
	/// <param name="geometry">The full-resolution track.</param>
	/// <param name="removals">Ascending, disjoint, non-empty ranges of raw indices.</param>
	public static TrackEditResult Remove(TrackGeometry geometry, IReadOnlyList<PointRange> removals)
	{
		if (Validate(geometry, removals) is { IsValid: false } refusal)
		{
			return refusal;
		}

		List<TrackPoint> kept = [];
		List<int> segmentStarts = [];

		HashSet<int> originalStarts = [.. geometry.SegmentStarts];

		int nextRemoval = 0;
		bool breakPending = false;

		for (int index = 0; index < geometry.Points.Count; index++)
		{
			while (nextRemoval < removals.Count && removals[nextRemoval].To <= index)
			{
				nextRemoval++;
			}

			if (nextRemoval < removals.Count && removals[nextRemoval].Contains(index))
			{
				// Only a gap with something on both sides is a discontinuity. Trimming the
				// start or the end creates no break — there is nothing on the outside to
				// disconnect from.
				breakPending = kept.Count > 0;

				continue;
			}

			if (kept.Count > 0 && (breakPending || originalStarts.Contains(index)))
			{
				segmentStarts.Add(kept.Count);
			}

			breakPending = false;

			kept.Add(geometry.Points[index]);
		}

		return TrackEditResult.Applied(new TrackGeometry(kept, segmentStarts));
	}

	/// <summary>
	/// Rebuilds every derived figure from the surviving points (§15.5).
	/// <para>
	/// Recomputed, never adjusted. Subtracting the removed span's distance from the stored
	/// total is how an edited track ends up with numbers that no longer describe it after the
	/// third edit — and it is the same calculator that ran at record and import time, so an
	/// untouched half cannot report different climbing (§15.7).
	/// </para>
	/// </summary>
	/// <param name="geometry">The edited track.</param>
	public static TrackStats Restat(TrackGeometry geometry) => TrackStats.From(geometry);

	private static TrackEditResult Validate(TrackGeometry geometry, IReadOnlyList<PointRange> removals)
	{
		int count = geometry.Points.Count;
		int removed = 0;

		for (int index = 0; index < removals.Count; index++)
		{
			PointRange range = removals[index];

			if (range.IsEmpty)
			{
				return TrackEditResult.Refused(
					TrackEditError.EmptyRange,
					$"The range {range} removes no points.");
			}

			if (range.From < 0 || range.To > count)
			{
				return TrackEditResult.Refused(
					TrackEditError.OutOfBounds,
					$"The range {range} is outside the track, which has {count} points.");
			}

			// Ascending and disjoint are one check: a range that starts before the previous one
			// ended is either out of order or overlapping, and the caller does not need the two
			// told apart to fix it.
			if (index > 0 && range.From < removals[index - 1].To)
			{
				return TrackEditResult.Refused(
					TrackEditError.OverlappingOrDescending,
					$"The range {range} overlaps or precedes {removals[index - 1]}. " +
					"Ranges must be ascending and disjoint.");
			}

			removed += range.Length;
		}

		int surviving = count - removed;

		return surviving < MinimumSurvivingPoints
			? TrackEditResult.Refused(
				TrackEditError.TooFewPointsRemain,
				$"That edit would leave {surviving} point(s). A track needs at least " +
				$"{MinimumSurvivingPoints}; to remove it entirely, delete the track.")
			: TrackEditResult.Applied(geometry);
	}
}
