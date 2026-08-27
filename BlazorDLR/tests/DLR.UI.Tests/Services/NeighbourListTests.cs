using BlazorDLR.Shared.Services;
using DLR.Core.Contracts.Rides;
using DLR.Core.Tracks;

namespace DLR.UI.Tests.Services;

/// <summary>
/// Who the live map's neighbours panel names, and in what order (§5.4).
/// <para>
/// The rules are a short list and every one of them is a claim about where somebody is on a road,
/// so they are pinned here rather than by reading markup back: which four riders are "nearest",
/// which way up the panel goes, and which of the many ways a ride can have nothing to say leave it
/// empty rather than misleading.
/// </para>
/// </summary>
public sealed class NeighbourListTests
{
	private static readonly DateTimeOffset Now = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

	private static readonly Guid Me = Guid.Parse("11111111-1111-1111-1111-111111111111");

	/// <summary>Sydney Harbour Bridge, give or take — the anchor every fixture below hangs off.</summary>
	private const double BaseLat = -33.852;
	private const double BaseLon = 151.211;

	/// <summary>
	/// A route running due east from the anchor. Straight, so "distance along" reads as a number in
	/// the assertions rather than as whatever a curve happened to integrate to.
	/// </summary>
	private static IReadOnlyList<TrackPoint> EastwardRoute() =>
	[
		new TrackPoint(BaseLat, BaseLon),
		new TrackPoint(BaseLat, BaseLon + 0.5),
	];

	private static RideMemberSummary Member(Guid id, string name, bool sharing = true, string? colour = null) =>
		new(id, name, "Rider", Now, sharing, true, colour);

	private static RiderPositionDto Fix(Guid id, string name, double metresEast, DateTimeOffset? recordedUtc = null) =>
		new(
			id,
			name,
			PositionScale.FromDegrees(BaseLat),
			// Metres east of the anchor, at this latitude. The route is due east, so this is also
			// the rider's distance-along.
			PositionScale.FromDegrees(BaseLon + (metresEast / (111_320.0 * Math.Cos(BaseLat * Math.PI / 180.0)))),
			null,
			null,
			recordedUtc ?? Now);

	/// <summary>
	/// Builds the roster the panel narrows, so each test states a ride rather than a list of rows.
	/// </summary>
	/// <param name="travellers">Each rider's name and how many metres along the route they are.</param>
	private static IReadOnlyList<MemberRow> Roster(params (Guid Id, string Name, double? AlongMetres)[] riders)
	{
		List<RideMemberSummary> members = [];
		Dictionary<Guid, RiderPositionDto> positions = [];

		foreach ((Guid id, string name, double? along) in riders)
		{
			// No distance-along means no fix, which on this ride means they are not sharing.
			members.Add(Member(id, name, sharing: along is not null));

			if (along is { } metres)
			{
				positions[id] = Fix(id, name, metres);
			}
		}

		return MemberRoster.Build(members, positions, EastwardRoute(), null, Me, Now, MemberSort.AlongRoute);
	}

	private static Guid Rider(int index) => new($"{index:D8}-0000-0000-0000-000000000000");

	// -- Which four ---------------------------------------------------------------------------

	/// <summary>
	/// The panel's whole reason to exist: on a fifty-rider ride the four riders either side are the
	/// answer, and the other forty-five are noise at 40 km/h.
	/// </summary>
	[Fact]
	public void OnlyTheNearestFourOthers_AreCarried_AndTheyAreTheNearestAlongTheRoad()
	{
		IReadOnlyList<MemberRow> rows = Roster(
			(Rider(1), "FarAhead", 5_000),
			(Rider(2), "Ahead2", 400),
			(Rider(3), "Ahead1", 100),
			(Me, "Me", 1_000),
			(Rider(4), "Behind1", 900),
			(Rider(5), "Behind2", 700),
			(Rider(6), "FarBehind", 20));

		IReadOnlyList<NeighbourRow> panel = NeighbourList.Nearest(rows, Me, selfAlongMetres: 1_000);

		panel.Count.ShouldBe(5, "four neighbours plus the traveller reading them.");
		panel.Select(row => row.UserName).ShouldBe(
			["Ahead2", "Me", "Behind1", "Behind2", "Ahead1"],
			ignoreOrder: true,
			"nearest is measured along the road in both directions — the traveller 600 m ahead beats the "
			+ "one 4 km ahead, and being behind is no reason to be dropped.");
	}

	/// <summary>
	/// Nearest is measured <em>along the route</em>, never through the air. Two riders either side of
	/// a river are a hundred metres apart and twenty minutes apart, and the second number is the one
	/// that decides whether anybody waits.
	/// </summary>
	[Fact]
	public void ARiderJustBehind_BeatsOneMuchFurtherAhead()
	{
		IReadOnlyList<MemberRow> rows = Roster(
			(Rider(1), "MilesUpTheRoad", 3_000),
			(Me, "Me", 1_000),
			(Rider(2), "JustBack", 950));

		IReadOnlyList<NeighbourRow> panel = NeighbourList.Nearest(rows, Me, selfAlongMetres: 1_000, count: 1);

		panel.Select(row => row.UserName).ShouldBe(["Me", "JustBack"]);
	}

	// -- Which way up -------------------------------------------------------------------------

	/// <summary>
	/// The shape of the panel is the answer before any of the numbers are read: leader at the top,
	/// tail at the bottom, and the reader in their real place among them.
	/// </summary>
	[Fact]
	public void TheRowsAreInRoadOrder_LeaderFirst()
	{
		IReadOnlyList<MemberRow> rows = Roster(
			(Rider(1), "Second", 1_200),
			(Me, "Me", 1_000),
			(Rider(2), "First", 1_400),
			(Rider(3), "Last", 600));

		IReadOnlyList<NeighbourRow> panel = NeighbourList.Nearest(rows, Me, selfAlongMetres: 1_000);

		panel.Select(row => row.UserName).ShouldBe(["First", "Second", "Me", "Last"]);
	}

	[Fact]
	public void TheLeadingRider_FindsThemselvesAtTheTop()
	{
		IReadOnlyList<MemberRow> rows = Roster(
			(Me, "Me", 2_000),
			(Rider(1), "Chasing", 1_800),
			(Rider(2), "Behind", 1_500));

		IReadOnlyList<NeighbourRow> panel = NeighbourList.Nearest(rows, Me, selfAlongMetres: 2_000);

		panel[0].IsSelf.ShouldBeTrue("off the front is the top of the list, because that is where they are.");
	}

	[Fact]
	public void TheLastRider_FindsThemselvesAtTheBottom()
	{
		IReadOnlyList<MemberRow> rows = Roster(
			(Me, "Me", 500),
			(Rider(1), "Ahead", 900),
			(Rider(2), "WayAhead", 1_500));

		IReadOnlyList<NeighbourRow> panel = NeighbourList.Nearest(rows, Me, selfAlongMetres: 500);

		panel[^1].IsSelf.ShouldBeTrue("off the back is the bottom of the list, for the same reason.");
	}

	// -- The gaps -----------------------------------------------------------------------------

	[Fact]
	public void AGapIsSignedFromTheReader_AheadPositiveAndBehindNegative()
	{
		IReadOnlyList<MemberRow> rows = Roster(
			(Me, "Me", 1_000),
			(Rider(1), "Ahead", 1_400),
			(Rider(2), "Behind", 700));

		IReadOnlyList<NeighbourRow> panel = NeighbourList.Nearest(rows, Me, selfAlongMetres: 1_000);

		panel.Single(row => row.UserName == "Ahead").RelativeMetres.ShouldBe(400, tolerance: 5);
		panel.Single(row => row.UserName == "Behind").RelativeMetres.ShouldBe(-300, tolerance: 5);
		panel.Single(row => row.IsSelf).RelativeMetres.ShouldBe(0);
	}

	/// <summary>
	/// The reader's own place is handed in rather than read off their row, and this is what that
	/// buys: on a phone the live map measures from the device's own GPS, not from the ride's
	/// round-tripped copy of it, so a rider holding a steady wheel does not watch the whole group
	/// drift past them once every fan-out tick.
	/// </summary>
	[Fact]
	public void TheReadersOwnPlace_IsTheOneHandedIn_NotTheRidesCopyOfThem()
	{
		// The ride still has this rider back at 1 000 m; the device knows they are at 1 400 m.
		IReadOnlyList<MemberRow> rows = Roster(
			(Me, "Me", 1_000),
			(Rider(1), "Other", 1_500));

		IReadOnlyList<NeighbourRow> panel = NeighbourList.Nearest(rows, Me, selfAlongMetres: 1_400);

		panel.Single(row => row.UserName == "Other").RelativeMetres.ShouldBe(100, tolerance: 5,
			"measured from where this device says it is, not from the copy that went to the server "
			+ "and came back.");
	}

	[Theory]
	[InlineData(0, "level")]
	[InlineData(12, "level")]
	[InlineData(-12, "level")]
	[InlineData(340, "+ 340 m")]
	[InlineData(-340, "- 340 m")]
	[InlineData(1_250, "+ 1.3 km")]
	[InlineData(-1_250, "- 1.3 km")]
	public void AGapIsReadAsWords_NotAsASign(double metres, string expected) =>
		NeighbourList.FormatRelative(metres).ShouldBe(expected,
			"the row order already says which way; a minus sign at 0.8 rem through a visor does not.");

	// -- Travellers nobody has heard from -----------------------------------------------------

	/// <summary>
	/// Builds a roster where each traveller's fix has an age, so the tests below can state "nobody
	/// has heard from her in twenty minutes" as a fact rather than by sleeping.
	/// </summary>
	/// <param name="riders">Each traveller's name, distance along the route, and how old their fix is.</param>
	private static IReadOnlyList<MemberRow> AgedRoster(
		params (Guid Id, string Name, double AlongMetres, TimeSpan Age)[] riders)
	{
		List<RideMemberSummary> members = [];
		Dictionary<Guid, RiderPositionDto> positions = [];

		foreach ((Guid id, string name, double along, TimeSpan age) in riders)
		{
			members.Add(Member(id, name));
			positions[id] = Fix(id, name, along, Now - age);
		}

		return MemberRoster.Build(members, positions, EastwardRoute(), null, Me, Now, MemberSort.AlongRoute);
	}

	/// <summary>
	/// The point of <see cref="PinExpiry"/> applied to a gap rather than a pin, and it bites harder
	/// here: a pin at least sits where the traveller was, and "300 m ahead" is a claim about now.
	/// A flat phone at the last stop would otherwise drift backwards down the panel all afternoon.
	/// </summary>
	[Fact]
	public void ATravellerNobodyHasHeardFromInTooLong_IsNotNamed()
	{
		IReadOnlyList<MemberRow> rows = AgedRoster(
			(Me, "Me", 1_000, TimeSpan.Zero),
			(Rider(1), "FlatPhone", 1_100, TimeSpan.FromMinutes(20)),
			(Rider(2), "Riding", 1_400, TimeSpan.FromSeconds(4)));

		IReadOnlyList<NeighbourRow> panel =
			NeighbourList.Nearest(rows, Me, selfAlongMetres: 1_000, keepFor: TimeSpan.FromMinutes(10));

		panel.Select(row => row.UserName).ShouldBe(["Riding", "Me"],
			"the nearer of the two has not been heard from in twenty minutes, and a gap to somebody "
			+ "who is not there is worse than one line fewer.");
	}

	/// <summary>
	/// Dropping one is not the same as leaving a hole where they were: the panel carries four
	/// travellers, and they should be the four nearest ones who are actually out there.
	/// </summary>
	[Fact]
	public void TheLineAQuietTravellerWouldHaveTaken_GoesToTheNextOneAlong()
	{
		IReadOnlyList<MemberRow> rows = AgedRoster(
			(Me, "Me", 1_000, TimeSpan.Zero),
			(Rider(1), "Silent", 1_050, TimeSpan.FromHours(1)),
			(Rider(2), "Near", 1_200, TimeSpan.Zero),
			(Rider(3), "Far", 4_000, TimeSpan.Zero));

		IReadOnlyList<NeighbourRow> panel =
			NeighbourList.Nearest(rows, Me, selfAlongMetres: 1_000, count: 1, keepFor: TimeSpan.FromMinutes(10));

		panel.Select(row => row.UserName).ShouldBe(["Near", "Me"]);
	}

	/// <summary>
	/// The reader is the thing every other number is measured from, and on a phone their place comes
	/// from this device's own receiver rather than from the ride's round-tripped copy of it. Ageing
	/// them out would empty the panel rather than trim it.
	/// </summary>
	[Fact]
	public void TheReaderIsNeverAgedOutOfTheirOwnPanel()
	{
		IReadOnlyList<MemberRow> rows = AgedRoster(
			(Me, "Me", 1_000, TimeSpan.FromHours(2)),
			(Rider(1), "Riding", 1_200, TimeSpan.Zero));

		IReadOnlyList<NeighbourRow> panel =
			NeighbourList.Nearest(rows, Me, selfAlongMetres: 1_000, keepFor: TimeSpan.FromMinutes(10));

		panel.Select(row => row.UserName).ShouldBe(["Riding", "Me"]);
	}

	/// <summary>
	/// The rule is opt-in, so a caller that has no answer for how long a fix is worth reading gets
	/// the behaviour the panel has always had rather than a silent cut-off of somebody's choosing.
	/// </summary>
	[Fact]
	public void WithNoLimitAsked_AgeIsNotAReasonToDropAnybody()
	{
		IReadOnlyList<MemberRow> rows = AgedRoster(
			(Me, "Me", 1_000, TimeSpan.Zero),
			(Rider(1), "Ancient", 1_100, TimeSpan.FromDays(1)));

		NeighbourList.Nearest(rows, Me, selfAlongMetres: 1_000)
			.Select(row => row.UserName).ShouldBe(["Ancient", "Me"]);
	}

	/// <summary>
	/// The other half of the same decision, and the reason dropping them from the panel is honest
	/// rather than a lie by omission: the members screen still has them, and it writes the age of
	/// that fix beside every figure it draws from one.
	/// </summary>
	[Fact]
	public void TheMembersScreenKeepsTheTravellerThePanelDropped()
	{
		IReadOnlyList<MemberRow> rows = AgedRoster(
			(Me, "Me", 1_000, TimeSpan.Zero),
			(Rider(1), "FlatPhone", 1_100, TimeSpan.FromMinutes(20)));

		NeighbourList.Nearest(rows, Me, selfAlongMetres: 1_000, keepFor: TimeSpan.FromMinutes(10))
			.Select(row => row.UserName).ShouldNotContain("FlatPhone");

		MemberRow dropped = rows.Single(row => row.UserName == "FlatPhone");

		dropped.AlongMetres!.Value.ShouldBe(1_100, tolerance: 5,
			"nothing was deleted — the roster the members screen draws is untouched by any of this.");
		dropped.FixAge.ShouldBe(TimeSpan.FromMinutes(20),
			"and it carries the age that says what happened to them.");
	}

	// -- When there is nothing to say ---------------------------------------------------------

	/// <summary>
	/// The panel covers a strip of a map that is the page, so it has to disappear rather than sit
	/// there full of em dashes whenever it cannot answer.
	/// </summary>
	[Fact]
	public void WithNoPlaceOnTheRouteForTheReader_ThereIsNothingToMeasureFrom()
	{
		IReadOnlyList<MemberRow> rows = Roster((Me, "Me", 1_000), (Rider(1), "Other", 1_200));

		NeighbourList.Nearest(rows, Me, selfAlongMetres: null).ShouldBeEmpty(
			"an adventure with no route, or a device with no fix yet — either way there is no 'ahead'.");
	}

	[Fact]
	public void WithNobodyElseOnTheRoute_ThePanelIsEmptyRatherThanAListOfOne()
	{
		IReadOnlyList<MemberRow> rows = Roster(
			(Me, "Me", 1_000),
			// Not sharing, so no fix and no place on the route.
			(Rider(1), "Alone", null));

		NeighbourList.Nearest(rows, Me, selfAlongMetres: 1_000).ShouldBeEmpty(
			"a panel listing one traveller, and it is you, says nothing the map was not already saying.");
	}

	[Fact]
	public void ARiderWhoIsNotSharing_IsNotAName_TheyAreARowThatCannotAnswer()
	{
		IReadOnlyList<MemberRow> rows = Roster(
			(Me, "Me", 1_000),
			(Rider(1), "Quiet", null),
			(Rider(2), "Sharing", 1_100));

		IReadOnlyList<NeighbourRow> panel = NeighbourList.Nearest(rows, Me, selfAlongMetres: 1_000);

		panel.Select(row => row.UserName).ShouldNotContain("Quiet",
			"one number per traveller is the whole content of this panel; a traveller with no number costs "
			+ "a line and answers nothing.");
	}

	/// <summary>
	/// A fix that stopped arriving still names a real place — it is where they were — and the panel
	/// keeps it, marked. Dropping it would take the rider in the tunnel off the one screen the group
	/// is using to decide whether to wait.
	/// </summary>
	[Fact]
	public void ARiderWhoHasGoneQuiet_KeepsTheirRow_AndItSaysSo()
	{
		Guid stale = Rider(1);

		IReadOnlyList<MemberRow> rows = MemberRoster.Build(
			[Member(Me, "Me"), Member(stale, "Tunnel")],
			new Dictionary<Guid, RiderPositionDto>
			{
				[Me] = Fix(Me, "Me", 1_000),
				[stale] = Fix(stale, "Tunnel", 1_300, Now - MemberRoster.StaleAfter - TimeSpan.FromSeconds(1)),
			},
			EastwardRoute(),
			from: null,
			selfUserId: Me,
			now: Now,
			sort: MemberSort.AlongRoute);

		IReadOnlyList<NeighbourRow> panel = NeighbourList.Nearest(rows, Me, selfAlongMetres: 1_000);

		panel.Single(row => row.UserName == "Tunnel").Presence.ShouldBe(MemberPresence.NoSignal,
			"a gap that has quietly frozen must not be read as a gap that is holding steady (§5.6).");
	}

	[Fact]
	public void WithNobodyReading_ThereIsNoPanel() =>
		NeighbourList.Nearest(Roster((Me, "Me", 1_000)), selfUserId: null, selfAlongMetres: 1_000)
			.ShouldBeEmpty();

	/// <summary>
	/// The swatch is the panel's only wordless thing, and its whole job is to be the colour the map
	/// has just drawn that rider in (§16.3). A panel naming them in one colour while the pin a
	/// hundred metres up the road is another is worse than no swatch at all.
	/// </summary>
	[Fact]
	public void EachRider_CarriesTheColourTheirPinIsDrawnIn()
	{
		Guid other = Rider(1);

		IReadOnlyList<MemberRow> rows = MemberRoster.Build(
			[Member(Me, "Me"), Member(other, "Other", colour: "#ff8800")],
			new Dictionary<Guid, RiderPositionDto>
			{
				[Me] = Fix(Me, "Me", 1_000),
				[other] = Fix(other, "Other", 1_200),
			},
			EastwardRoute(),
			from: null,
			selfUserId: Me,
			now: Now,
			sort: MemberSort.AlongRoute);

		IReadOnlyList<NeighbourRow> panel = NeighbourList.Nearest(rows, Me, selfAlongMetres: 1_000);

		panel.Single(row => row.UserName == "Other").Colour.ShouldBe("#ff8800");
		panel.Single(row => row.IsSelf).Colour.ShouldNotBeNullOrWhiteSpace(
			"a traveller who has never picked a colour still has one the map draws them in.");
	}
}
