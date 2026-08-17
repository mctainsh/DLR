using BlazorDLR.Shared.Tracks;
using DLR.Core.Contracts.Tracks;
using DLR.Core.Tracks;

namespace DLR.UI.Tests.Tracks;

/// <summary>
/// The editor's uncommitted working copy (§15.5). Everything here is about indices, which is
/// the part of trimming that is easy to get subtly wrong and impossible to see on a screenshot:
/// <list type="bullet">
///   <item>a trim takes the cursor point with it and leaves the cursor on the new edge, so a
///     second press continues the bite instead of stranding the point it started from;</item>
///   <item>"ten" means ten <em>surviving</em> points, so a trim reaches past an earlier hole
///     rather than re-striking points that are already gone;</item>
///   <item>undo walks back one trim at a time to the track as loaded, cursor included;</item>
///   <item>what leaves for the wire is ascending, disjoint, half-open raw ranges — merged where
///     two trims abut, so the server sees the same edit however the rider got there.</item>
/// </list>
/// </summary>
public sealed class TrackTrimSessionTests
{
	/// <summary>A straight run of points, one ten-thousandth of a degree apart.</summary>
	private static TrackTrimSession Session(int count = 20) =>
		new([.. Enumerable.Range(0, count).Select(index => new TrackPoint(-33.8 + (index * 0.0001), 151.2))]);

	[Fact]
	public void TrimBack_TakesTheCursorPointWithIt_AndMovesTheCursorBack()
	{
		TrackTrimSession session = Session();
		session.PlaceCursor(10).ShouldBeTrue();

		session.TrimBack(3).ShouldBe(TrimRefusal.None);

		session.Removals().ShouldBe([new IndexRange(8, 11)],
			"the cursor point and the two behind it, as one half-open raw range.");
		session.Cursor.ShouldBe(7,
			"the cursor follows the cut and lands on the point the bite stopped at.");
		session.SurvivingIndices.ShouldNotContain(10);
	}

	[Fact]
	public void TrimForward_TakesTheCursorPointWithIt_AndMovesTheCursorForward()
	{
		TrackTrimSession session = Session();
		session.PlaceCursor(5);

		session.TrimForward(2).ShouldBe(TrimRefusal.None);

		session.Removals().ShouldBe([new IndexRange(5, 7)]);
		session.Cursor.ShouldBe(7);
	}

	[Fact]
	public void RepeatedTrims_ChewOneContiguousSpan_LeavingNoStrandedPoints()
	{
		TrackTrimSession session = Session();
		session.PlaceCursor(10);

		session.TrimBack(3);
		session.TrimBack(3);

		// The whole reason the cursor travels: the second bite starts where the first stopped.
		// A cursor that stayed put would re-measure from 10 and a cursor that jumped the gap
		// without being eaten would leave the point it vacated stranded between two holes.
		session.Removals().ShouldBe([new IndexRange(5, 11)],
			"two abutting bites merge into one range — the mask is the state, not the trim log.");
		session.Cursor.ShouldBe(4);
		session.StruckCount.ShouldBe(6);
	}

	[Fact]
	public void TrimBack_ThenTrimForward_ReversesDirectionFromWhereTheCursorLanded()
	{
		TrackTrimSession session = Session();
		session.PlaceCursor(10);

		session.TrimBack(1);          // strikes 10, cursor → 9
		session.TrimForward(1);       // strikes 9, cursor → 11

		session.Removals().ShouldBe([new IndexRange(9, 11)]);
		session.Cursor.ShouldBe(11,
			"forward from 9 has nothing left before 11 — the bite skips the hole it just made.");
	}

	[Fact]
	public void Undo_WalksBackOneTrimAtATime_ToTheTrackAsLoaded()
	{
		TrackTrimSession session = Session();
		session.PlaceCursor(10);
		session.TrimBack(3);          // strikes 8-10, cursor → 7
		session.TrimForward(2);       // strikes 7 and 11, cursor → 12

		session.StepCount.ShouldBe(2);

		session.Undo().ShouldBeTrue();
		session.Removals().ShouldBe([new IndexRange(8, 11)], "the most recent trim is the one that goes.");
		session.Cursor.ShouldBe(7, "undo puts the cursor back where that trim found it.");

		session.Undo().ShouldBeTrue();
		session.Removals().ShouldBeEmpty();
		session.HasEdits.ShouldBeFalse();
		session.SurvivingIndices.Count.ShouldBe(20);
		session.Cursor.ShouldBe(10, "all the way back, cursor included.");

		session.Undo().ShouldBeFalse("there is nothing before the track as loaded.");
	}

	[Fact]
	public void Undo_RestoresTheExactPointsThatTrimTook_NotThePositionsTheyHeld()
	{
		TrackTrimSession session = Session();
		session.PlaceCursor(10);
		session.TrimBack(3);          // strikes 8, 9, 10
		session.PlaceCursor(14);
		session.TrimForward(3);       // strikes 14, 15, 16

		session.Undo();               // must restore 14-16, leaving 8-10 struck

		session.Removals().ShouldBe([new IndexRange(8, 11)]);
		session.SurvivingIndices.ShouldContain(15);
		session.SurvivingIndices.ShouldNotContain(9);
	}

	[Fact]
	public void Trims_AskingForMoreThanIsThere_TakeWhatIsThere()
	{
		TrackTrimSession session = Session();
		session.PlaceCursor(3);

		session.TrimBack(10).ShouldBe(TrimRefusal.None);

		session.Removals().ShouldBe([new IndexRange(0, 4)],
			"the cursor and the three points behind it, so −10 trims to the start rather than refusing.");
		session.Cursor.ShouldBe(4,
			"with nothing left behind it, the cursor turns round onto the new first point.");
	}

	[Fact]
	public void Trim_IsRefused_WithoutACursor()
	{
		Session().TrimBack(1).ShouldBe(TrimRefusal.NoCursor,
			"back from WHERE — there is nothing to bite from.");
	}

	[Fact]
	public void Trim_IsRefused_RatherThanLeavingFewerThanTwoPoints()
	{
		TrackTrimSession session = Session(count: 4);
		session.PlaceCursor(3);

		session.TrimBack(3).ShouldBe(TrimRefusal.TooFewPointsRemain,
			"§15.5: below two points it is not a line, and the button should say so rather than the server.");
		session.HasEdits.ShouldBeFalse("a refused trim leaves no undo step behind.");
		session.Cursor.ShouldBe(3, "and leaves the cursor where it was.");

		session.TrimBack(2).ShouldBe(TrimRefusal.None, "two survivors is exactly the floor, not below it.");
		session.SurvivingIndices.Count.ShouldBe(2);
	}

	[Fact]
	public void Trim_RejectsANonPositiveCount_AsACallerMistake()
	{
		TrackTrimSession session = Session();
		session.PlaceCursor(5);

		Should.Throw<ArgumentOutOfRangeException>(() => session.TrimBack(0));
	}

	[Fact]
	public void PlaceCursor_RefusesAStruckPoint()
	{
		TrackTrimSession session = Session();
		session.PlaceCursor(10);
		session.TrimBack(3);          // strikes 8, 9, 10 — cursor → 7

		session.PlaceCursor(9).ShouldBeFalse("a deleted point is not somewhere a bite can start.");
		session.Cursor.ShouldBe(7, "a refused placement leaves the cursor where it was.");

		session.PlaceCursor(99).ShouldBeFalse();
		session.PlaceCursor(-1).ShouldBeFalse();
	}

	[Fact]
	public void Removals_AreAscendingAndDisjoint_AcrossSeparateHoles()
	{
		TrackTrimSession session = Session();

		session.PlaceCursor(15);
		session.TrimBack(2);          // strikes 14, 15
		session.PlaceCursor(6);
		session.TrimForward(2);       // strikes 6, 7

		IReadOnlyList<IndexRange> removals = session.Removals();

		removals.ShouldBe([new IndexRange(6, 8), new IndexRange(14, 16)],
			"the wire wants them in index order however the traveller made them (§15.5).");
	}

	[Fact]
	public void SurvivingPoints_StayParallelToSurvivingIndices()
	{
		TrackTrimSession session = Session();
		session.PlaceCursor(10);
		session.TrimBack(3);

		session.SurvivingPoints.Count.ShouldBe(session.SurvivingIndices.Count);

		// The hit test answers with a position in SurvivingPoints, and the page turns that into
		// a raw index through SurvivingIndices. If the two ever drift, the cursor lands on a
		// different point than the one tapped.
		for (int position = 0; position < session.SurvivingIndices.Count; position++)
		{
			session.SurvivingPoints[position]
				.ShouldBe(session.Points[session.SurvivingIndices[position]]);
		}
	}
}
