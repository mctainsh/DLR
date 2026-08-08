using DLR.Core.Contracts.Tracks;
using DLR.Core.Tracks;

namespace BlazorDLR.Shared.Tracks;

/// <summary>Why a trim did nothing (§15.5). The page turns each into a sentence.</summary>
public enum TrimRefusal
{
	/// <summary>The trim was applied.</summary>
	None = 0,

	/// <summary>No cursor is placed, so there is nothing to trim from.</summary>
	NoCursor = 1,

	/// <summary>
	/// Fewer than <see cref="TrackEditor.MinimumSurvivingPoints"/> points would be left. Refused
	/// here rather than at the server so the button says no instead of the network doing.
	/// </summary>
	TooFewPointsRemain = 2,
}

/// <summary>
/// The editor's uncommitted working copy: which raw points the rider has struck out, in what
/// order, and where the cursor sits (§15.5).
/// <para>
/// <strong>Nothing here touches the network.</strong> Trims accumulate against a local mask and
/// each one is undoable a step at a time, right back to the track as loaded. Only
/// <see cref="Removals"/> ever leaves — handed to <c>POST /tracks/{id}/edit</c> when the rider
/// hits Apply, at which point it is permanent and this session is thrown away.
/// </para>
/// <para>
/// <strong>A trim eats the cursor point and the cursor follows the cut.</strong> The cursor is
/// the leading edge of the bite, not a fixed anchor beside it — press back twice and the second
/// bite continues where the first stopped. Leaving the cursor standing and moving it to the far
/// side of the gap instead was tried and is worse than either: the point it vacates ends up
/// stranded between two gaps, so a rider chewing back to the start leaves a spike behind every
/// press. Leaving it standing and <em>not</em> moving it is what the cursor is being asked to
/// stop doing.
/// </para>
/// <para>
/// <strong>Indices are raw throughout</strong> — positions in the full-resolution point list from
/// <c>GET /tracks/{id}/points</c>, never into the simplified line the map draws. That is §15.5's
/// central constraint and the reason this class exists as a separate, testable thing rather than
/// as fields on a Razor page: "the tenth point along from here" has to mean the tenth
/// <em>surviving</em> point, and getting that wrong deletes a different span than the one under
/// the finger.
/// </para>
/// </summary>
public sealed class TrackTrimSession
{
	private readonly TrackPoint[] _points;

	/// <summary>Struck-out flags, one per raw index. The whole edit state, minus the ordering.</summary>
	private readonly bool[] _struck;

	/// <summary>
	/// One entry per trim, holding exactly what that trim struck out and where the cursor stood
	/// before it. Undo pops one and puts both back — recomputing either is not possible after a
	/// later trim, because by then "the ten behind the cursor" is a different ten and the cursor
	/// itself has moved on.
	/// </summary>
	private readonly Stack<Step> _steps = new();

	private int[] _survivingIndices = [];
	private TrackPoint[] _survivingPoints = [];
	private int? _cursor;

	/// <summary>Opens a session over the full-resolution points, with nothing struck out.</summary>
	/// <param name="points">Every raw point, in order.</param>
	public TrackTrimSession(IReadOnlyList<TrackPoint> points)
	{
		_points = [.. points];
		_struck = new bool[_points.Length];
		Rebuild();
	}

	/// <summary>Every raw point the track started with.</summary>
	public IReadOnlyList<TrackPoint> Points => _points;

	/// <summary>Raw indices of the points still standing, ascending.</summary>
	public IReadOnlyList<int> SurvivingIndices => _survivingIndices;

	/// <summary>The points still standing, parallel to <see cref="SurvivingIndices"/>.</summary>
	public IReadOnlyList<TrackPoint> SurvivingPoints => _survivingPoints;

	/// <summary>How many points would be removed if this were applied now.</summary>
	public int StruckCount => _points.Length - _survivingPoints.Length;

	/// <summary>How many undo steps stand between here and the track as loaded.</summary>
	public int StepCount => _steps.Count;

	/// <summary>Whether anything has been struck out.</summary>
	public bool HasEdits => _steps.Count > 0;

	/// <summary>The raw index the cursor sits on, or null before the rider has tapped the line.</summary>
	public int? Cursor => _cursor;

	/// <summary>The point under the cursor, or null when there is no cursor.</summary>
	public TrackPoint? CursorPoint => _cursor is { } index ? _points[index] : null;

	/// <summary>
	/// Puts the cursor on a raw index. Refuses an index that is out of range or already struck
	/// out — the cursor is where the next bite starts, and a bite cannot start on a point that
	/// is already gone.
	/// </summary>
	/// <param name="rawIndex">A raw point index, normally from <see cref="TrackHitTest"/>.</param>
	/// <returns>Whether the cursor moved there.</returns>
	public bool PlaceCursor(int rawIndex)
	{
		if (rawIndex < 0 || rawIndex >= _points.Length || _struck[rawIndex])
		{
			return false;
		}

		_cursor = rawIndex;
		return true;
	}

	/// <summary>
	/// Strikes out the cursor point and the surviving points behind it, then leaves the cursor on
	/// the point the bite stopped at — one step back along the track.
	/// </summary>
	/// <param name="count">
	/// How many points to take, the cursor included. Fewer are taken if fewer are there, so a
	/// −10 that meets the start of the track trims to the start rather than refusing.
	/// </param>
	public TrimRefusal TrimBack(int count) => Strike(count, back: true);

	/// <summary>
	/// Strikes out the cursor point and the surviving points ahead of it, then leaves the cursor
	/// on the point the bite stopped at — one step forward along the track.
	/// </summary>
	/// <param name="count">How many points to take, the cursor included.</param>
	public TrimRefusal TrimForward(int count) => Strike(count, back: false);

	/// <summary>
	/// Reverses the most recent trim, points and cursor together. Repeated calls walk back to the
	/// track as loaded, with the cursor retracing the path it took on the way out — an undo that
	/// restored the points but left the cursor where the bite ended would put the next trim
	/// somewhere the rider never chose.
	/// </summary>
	/// <returns>Whether there was anything to undo.</returns>
	public bool Undo()
	{
		if (!_steps.TryPop(out Step step))
		{
			return false;
		}

		foreach (int index in step.Struck)
		{
			_struck[index] = false;
		}

		_cursor = step.CursorBefore;

		Rebuild();
		return true;
	}

	/// <summary>
	/// The struck points as the wire wants them: ascending, disjoint, half-open ranges of raw
	/// indices (§15.5).
	/// <para>
	/// Built from the mask rather than accumulated from the trims, so two trims that happen to
	/// abut arrive as one range and the server sees the same edit however the rider got there.
	/// </para>
	/// </summary>
	public IReadOnlyList<IndexRange> Removals()
	{
		List<IndexRange> ranges = [];
		int runStart = -1;

		// Runs to _struck.Length inclusive: the extra step closes a run that reaches the end.
		for (int index = 0; index <= _struck.Length; index++)
		{
			bool struck = index < _struck.Length && _struck[index];

			if (struck && runStart < 0)
			{
				runStart = index;
			}
			else if (!struck && runStart >= 0)
			{
				ranges.Add(new IndexRange(runStart, index));
				runStart = -1;
			}
		}

		return ranges;
	}

	private TrimRefusal Strike(int count, bool back)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

		if (_cursor is not { } cursor)
		{
			return TrimRefusal.NoCursor;
		}

		// The cursor is always a surviving index, so this always finds it.
		int position = Array.BinarySearch(_survivingIndices, cursor);
		if (position < 0)
		{
			return TrimRefusal.NoCursor;
		}

		// Half-open window into the surviving list, running from the cursor in the chosen
		// direction and including it. Clamped rather than refused when the rider asks for ten
		// and seven are there: trimming to the end of the track is what they meant.
		int first = back ? Math.Max(0, position - count + 1) : position;
		int last = back ? position + 1 : Math.Min(_survivingIndices.Length, position + count);

		if (_survivingIndices.Length - (last - first) < TrackEditor.MinimumSurvivingPoints)
		{
			return TrimRefusal.TooFewPointsRemain;
		}

		// Where the cursor lands, read off the survivors *before* they are struck: the points
		// either side of the bite are outside it and therefore still standing afterwards. It
		// prefers the direction of travel and falls back to the other side, which is what
		// happens when the bite reached the end of the track — there the cursor turns round
		// onto the new first or last point rather than having nowhere to be.
		int? behind = first > 0 ? _survivingIndices[first - 1] : null;
		int? ahead = last < _survivingIndices.Length ? _survivingIndices[last] : null;

		int[] struck = _survivingIndices[first..last];

		foreach (int index in struck)
		{
			_struck[index] = true;
		}

		_steps.Push(new Step(struck, cursor));
		_cursor = back ? behind ?? ahead : ahead ?? behind;

		Rebuild();

		return TrimRefusal.None;
	}

	/// <summary>
	/// One trim, as undo needs it: exactly the raw indices that trim struck, and where the cursor
	/// was standing before the bite moved it.
	/// </summary>
	/// <param name="Struck">The raw indices this trim removed.</param>
	/// <param name="CursorBefore">The cursor position to restore on undo.</param>
	private readonly record struct Step(int[] Struck, int CursorBefore);

	/// <summary>
	/// Recomputes the surviving view. Held rather than computed on demand because the map
	/// redraws and the hit test runs against it, and both would otherwise walk the raw list —
	/// 43 000 points on a long tour (§15.5).
	/// </summary>
	private void Rebuild()
	{
		List<int> indices = new(_points.Length);

		for (int index = 0; index < _points.Length; index++)
		{
			if (!_struck[index])
			{
				indices.Add(index);
			}
		}

		_survivingIndices = [.. indices];
		_survivingPoints = [.. indices.Select(index => _points[index])];
	}
}
