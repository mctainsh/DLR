using BlazorDLR.Shared.Services;
using DLR.Core.Contracts.Rides;
using DLR.Core.Display;
using DLR.Core.Tracks;

namespace DLR.UI.Tests.Services;

/// <summary>
/// The rules behind the "Live members" screen (§5.3, §5.4, §5.6) - the three presence
/// states, the four figures on each row, and the four orders the list can be read in.
/// <para>
/// Pure logic, tested without a renderer: that is why it is a static class in
/// <c>Services/</c> rather than a block of <c>@@code</c> in the component. Six columns and four
/// orders is a pile of rules, and a rule that can only be checked by rendering it and reading
/// the markup back is a rule that gets checked once.
/// </para>
/// </summary>
public sealed class MemberRosterTests
{
	private static readonly DateTimeOffset Now = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

	/// <summary>Sydney Harbour Bridge, give or take - the anchor every fixture below hangs off.</summary>
	private const double BaseLat = -33.852;
	private const double BaseLon = 151.211;

	private static RideMemberSummary Member(
		Guid id,
		string name,
		bool sharing = true,
		bool hasPosition = true,
		string role = "Rider",
		string? colour = null,
		bool isPrivate = false) =>
		new(id, name, role, Now, sharing, hasPosition, colour, isPrivate);

	private static RiderPositionDto Fix(Guid id, string name, double lat, double lon, DateTimeOffset recordedUtc) =>
		new(id, name, PositionScale.FromDegrees(lat), PositionScale.FromDegrees(lon), null, null, recordedUtc);

	/// <summary>
	/// A route running due east from the anchor. Straight so "distance along" is readable as a
	/// number in the assertions rather than as whatever a curve happened to integrate to.
	/// </summary>
	private static IReadOnlyList<TrackPoint> EastwardRoute() =>
	[
		new TrackPoint(BaseLat, BaseLon),
		new TrackPoint(BaseLat, BaseLon + 0.05),
	];

	// -- Presence (§5.6) --------------------------------------------------------------------------

	[Fact]
	public void NotSharing_AndNoSignal_AreDifferentStates()
	{
		Guid quiet = Guid.NewGuid();
		Guid off = Guid.NewGuid();

		IReadOnlyList<MemberRow> rows = MemberRoster.Build(
			[Member(quiet, "Quiet"), Member(off, "Off", sharing: false, hasPosition: false)],
			// Neither has a fix in the live set: one because they are not sharing, one because
			// nothing is arriving.
			new Dictionary<Guid, RiderPositionDto>(),
			route: null,
			from: null,
			selfUserId: null,
			now: Now,
			sort: MemberSort.Name);

		rows.Single(row => row.UserId == quiet).Presence.ShouldBe(MemberPresence.NoSignal,
			"§5.6: sharing with nothing arriving is 'no signal' - their last point is still on the map.");
		rows.Single(row => row.UserId == off).Presence.ShouldBe(MemberPresence.NotSharing,
			"§5.6: not sharing is a decision, not a network condition, and collapsing the two is "
			+ "the ambiguity that leaves somebody behind at a junction.");
	}

	[Fact]
	public void AFixThatStoppedArriving_ReadsAsNoSignal_NotAsSharing()
	{
		// The case a plain "has a position" flag cannot see: the pin is there, and it stopped
		// moving two minutes ago.
		Guid rider = Guid.NewGuid();

		IReadOnlyList<MemberRow> rows = MemberRoster.Build(
			[Member(rider, "Stale")],
			new Dictionary<Guid, RiderPositionDto>
			{
				[rider] = Fix(rider, "Stale", BaseLat, BaseLon, Now - MemberRoster.StaleAfter - TimeSpan.FromSeconds(1)),
			},
			route: null,
			from: null,
			selfUserId: null,
			now: Now,
			sort: MemberSort.Name);

		rows.ShouldHaveSingleItem().Presence.ShouldBe(MemberPresence.NoSignal,
			"a fix nothing has replaced in over a minute is a traveller in a tunnel, not a live one.");
	}

	[Fact]
	public void AFreshFix_ReadsAsSharing()
	{
		Guid rider = Guid.NewGuid();

		IReadOnlyList<MemberRow> rows = MemberRoster.Build(
			[Member(rider, "Live")],
			new Dictionary<Guid, RiderPositionDto> { [rider] = Fix(rider, "Live", BaseLat, BaseLon, Now) },
			route: null,
			from: null,
			selfUserId: null,
			now: Now,
			sort: MemberSort.Name);

		MemberRow row = rows.ShouldHaveSingleItem();
		row.Presence.ShouldBe(MemberPresence.Sharing);
		row.FixAge.ShouldBe(TimeSpan.Zero);
	}

	[Fact]
	public void WithNoPositionSetAtAll_TheSnapshotsOwnFlagIsTheWholeOfWhatIsKnown()
	{
		// A caller that has no positions - the list rendered before the snapshot's fixes land.
		// Freshness is not a question that can be asked, so HasPosition is the answer.
		Guid rider = Guid.NewGuid();

		IReadOnlyList<MemberRow> rows = MemberRoster.Build(
			[Member(rider, "Someone", sharing: true, hasPosition: true)],
			positions: null,
			route: null,
			from: null,
			selfUserId: null,
			now: Now,
			sort: MemberSort.Name);

		MemberRow row = rows.ShouldHaveSingleItem();
		row.Presence.ShouldBe(MemberPresence.Sharing);
		row.FixAge.ShouldBeNull("no fix was supplied, so there is no age - and null is not zero.");
	}

	// -- The figures ------------------------------------------------------------------------------

	[Fact]
	public void Range_IsMeasuredFromTheReader()
	{
		Guid rider = Guid.NewGuid();

		// A tenth of a degree of longitude at this latitude is a little over 9 km.
		IReadOnlyList<MemberRow> rows = MemberRoster.Build(
			[Member(rider, "Away")],
			new Dictionary<Guid, RiderPositionDto>
			{
				[rider] = Fix(rider, "Away", BaseLat, BaseLon + 0.1, Now),
			},
			route: null,
			from: (BaseLat, BaseLon),
			selfUserId: null,
			now: Now,
			sort: MemberSort.Name);

		rows.ShouldHaveSingleItem().RangeMetres.ShouldNotBeNull().ShouldBe(9_250, tolerance: 250);
	}

	[Fact]
	public void Range_IsNull_WhenThisDeviceDoesNotKnowWhereTheReaderIs()
	{
		// A browser has no receiver (§18.6), and a phone's first fix has not landed yet. Null
		// rather than zero: zero metres means "right here", which is a different claim.
		Guid rider = Guid.NewGuid();

		IReadOnlyList<MemberRow> rows = MemberRoster.Build(
			[Member(rider, "Away")],
			new Dictionary<Guid, RiderPositionDto> { [rider] = Fix(rider, "Away", BaseLat, BaseLon, Now) },
			route: null,
			from: null,
			selfUserId: null,
			now: Now,
			sort: MemberSort.Name);

		rows.ShouldHaveSingleItem().RangeMetres.ShouldBeNull();
	}

	[Fact]
	public void AlongTheRoute_RanksTheLeaderFirst_AndMeasuresEveryoneBackFromThem()
	{
		Guid front = Guid.NewGuid();
		Guid back = Guid.NewGuid();

		IReadOnlyList<MemberRow> rows = MemberRoster.Build(
			[Member(back, "Zoe"), Member(front, "Adam")],
			new Dictionary<Guid, RiderPositionDto>
			{
				[back] = Fix(back, "Zoe", BaseLat, BaseLon + 0.01, Now),
				[front] = Fix(front, "Adam", BaseLat, BaseLon + 0.04, Now),
			},
			EastwardRoute(),
			from: null,
			selfUserId: null,
			now: Now,
			sort: MemberSort.AlongRoute);

		rows[0].UserId.ShouldBe(front, "§5.4: the leader is whoever has covered the most of the route.");
		rows[0].IsLeader.ShouldBeTrue();
		rows[0].GapMetres.ShouldBe(0);

		rows[1].UserId.ShouldBe(back);
		rows[1].IsLeader.ShouldBeFalse();

		// 0.03 degrees of longitude at this latitude - about 2.8 km back.
		rows[1].GapMetres.ShouldNotBeNull().ShouldBe(2_780, tolerance: 100);
	}

	[Fact]
	public void TheLeader_IsTheSamePerson_WhicheverOrderTheListIsRead()
	{
		// The gaps are worked out over the whole ride before it is ordered. A rider reading the
		// alphabetical list must not find a different person at the front from the one the route
		// order names.
		Guid front = Guid.NewGuid();
		Guid back = Guid.NewGuid();

		Dictionary<Guid, RiderPositionDto> positions = new()
		{
			[back] = Fix(back, "Adam", BaseLat, BaseLon + 0.01, Now),
			[front] = Fix(front, "Zoe", BaseLat, BaseLon + 0.04, Now),
		};

		IReadOnlyList<MemberRow> alphabetical = MemberRoster.Build(
			[Member(back, "Adam"), Member(front, "Zoe")],
			positions, EastwardRoute(), from: null, selfUserId: null, now: Now, sort: MemberSort.Name);

		alphabetical[0].UserName.ShouldBe("Adam", "alphabetical is alphabetical.");
		alphabetical.Single(row => row.IsLeader).UserId.ShouldBe(front,
			"§5.4's leader is a fact about the adventure, not about the order the rows happen to be in.");
	}

	[Fact]
	public void OffRoute_IsFlagged_PastTheThreshold()
	{
		Guid rider = Guid.NewGuid();

		IReadOnlyList<MemberRow> rows = MemberRoster.Build(
			[Member(rider, "Lost")],
			new Dictionary<Guid, RiderPositionDto>
			{
				// About 1.1 km north of a route that runs due east.
				[rider] = Fix(rider, "Lost", BaseLat + 0.01, BaseLon + 0.02, Now),
			},
			EastwardRoute(),
			from: null,
			selfUserId: null,
			now: Now,
			sort: MemberSort.Name);

		MemberRow row = rows.ShouldHaveSingleItem();
		row.OffRoute.ShouldBeTrue();
		row.OffMetres.ShouldNotBeNull().ShouldBeGreaterThan(MemberRoster.OffRouteThresholdMetres);
	}

	[Fact]
	public void WithNoRoute_TheTwoRouteColumnsAreEmpty_RatherThanZero()
	{
		Guid rider = Guid.NewGuid();

		IReadOnlyList<MemberRow> rows = MemberRoster.Build(
			[Member(rider, "Someone")],
			new Dictionary<Guid, RiderPositionDto> { [rider] = Fix(rider, "Someone", BaseLat, BaseLon, Now) },
			route: null,
			from: null,
			selfUserId: null,
			now: Now,
			sort: MemberSort.AlongRoute);

		MemberRow row = rows.ShouldHaveSingleItem();
		row.AlongMetres.ShouldBeNull("an adventure with no route has nothing to be along.");
		row.GapMetres.ShouldBeNull();
		row.IsLeader.ShouldBeFalse("nobody leads an adventure that has no route.");
	}

	[Fact]
	public void AClockAheadOfTheServer_DoesNotProduceAFixFromTheFuture()
	{
		// Fixes are stamped by the device that took them (§5.7), so a phone running a few seconds
		// fast is an ordinary thing rather than a fault. "-4 s old" is not.
		Guid rider = Guid.NewGuid();

		IReadOnlyList<MemberRow> rows = MemberRoster.Build(
			[Member(rider, "Fast")],
			new Dictionary<Guid, RiderPositionDto>
			{
				[rider] = Fix(rider, "Fast", BaseLat, BaseLon, Now + TimeSpan.FromSeconds(4)),
			},
			route: null,
			from: null,
			selfUserId: null,
			now: Now,
			sort: MemberSort.Name);

		rows.ShouldHaveSingleItem().FixAge.ShouldBe(TimeSpan.Zero);
	}

	[Fact]
	public void EveryRow_CarriesTheColourTheMapDrawsThatRiderIn()
	{
		Guid chosen = Guid.NewGuid();
		Guid never = Guid.NewGuid();

		IReadOnlyList<MemberRow> rows = MemberRoster.Build(
			[Member(chosen, "Picky", colour: "#dc2626"), Member(never, "Default")],
			positions: null,
			route: null,
			from: null,
			selfUserId: null,
			now: Now,
			sort: MemberSort.Name);

		// §16.3: the list and the map have to name the same rider the same way, and an account
		// that never chose gets the default rather than an empty swatch.
		rows.Single(row => row.UserId == chosen).Colour.ShouldBe("#dc2626", StringCompareShould.IgnoreCase);
		rows.Single(row => row.UserId == never).Colour.ShouldBe(MarkerColours.Default, StringCompareShould.IgnoreCase);
	}

	// -- The four orders --------------------------------------------------------------------------

	[Fact]
	public void LastActive_PutsTheFreshestFixFirst()
	{
		Guid recent = Guid.NewGuid();
		Guid older = Guid.NewGuid();

		IReadOnlyList<MemberRow> rows = MemberRoster.Build(
			[Member(older, "Adam"), Member(recent, "Zoe")],
			new Dictionary<Guid, RiderPositionDto>
			{
				[older] = Fix(older, "Adam", BaseLat, BaseLon, Now - TimeSpan.FromSeconds(30)),
				[recent] = Fix(recent, "Zoe", BaseLat, BaseLon, Now - TimeSpan.FromSeconds(2)),
			},
			route: null,
			from: null,
			selfUserId: null,
			now: Now,
			sort: MemberSort.LastActive);

		rows[0].UserId.ShouldBe(recent, "the adventure's most recent news belongs at the top, not its alphabet.");
		rows[1].UserId.ShouldBe(older);
	}

	[Fact]
	public void Range_PutsTheNearestFirst_AndTheReaderAtZero()
	{
		Guid me = Guid.NewGuid();
		Guid near = Guid.NewGuid();
		Guid far = Guid.NewGuid();

		IReadOnlyList<MemberRow> rows = MemberRoster.Build(
			[Member(far, "Adam"), Member(near, "Bea"), Member(me, "Me")],
			new Dictionary<Guid, RiderPositionDto>
			{
				[far] = Fix(far, "Adam", BaseLat, BaseLon + 0.1, Now),
				[near] = Fix(near, "Bea", BaseLat, BaseLon + 0.005, Now),
				[me] = Fix(me, "Me", BaseLat, BaseLon, Now),
			},
			route: null,
			from: (BaseLat, BaseLon),
			selfUserId: me,
			now: Now,
			sort: MemberSort.Range);

		rows[0].UserId.ShouldBe(me, "the reader is the point every other range is measured from.");
		rows[0].IsSelf.ShouldBeTrue();
		rows[1].UserId.ShouldBe(near);
		rows[2].UserId.ShouldBe(far);
	}

	[Fact]
	public void RowsWithNothingToSortOn_GoLast_InEveryOrder()
	{
		// A rider who is not sharing has no age, no range and no place on the route. Floating them
		// to the top - where a null sorts by default - fills the head of the list with the rows
		// carrying the least information.
		Guid quiet = Guid.NewGuid();
		Guid live = Guid.NewGuid();

		Dictionary<Guid, RiderPositionDto> positions = new()
		{
			[live] = Fix(live, "Zoe", BaseLat, BaseLon + 0.01, Now),
		};

		foreach (MemberSort sort in new[] { MemberSort.LastActive, MemberSort.AlongRoute, MemberSort.Range })
		{
			IReadOnlyList<MemberRow> rows = MemberRoster.Build(
				[Member(quiet, "Adam", sharing: false, hasPosition: false), Member(live, "Zoe")],
				positions,
				EastwardRoute(),
				from: (BaseLat, BaseLon),
				selfUserId: null,
				now: Now,
				sort: sort);

			rows[0].UserId.ShouldBe(live, $"{sort} must rank the traveller it can measure above the one it cannot.");
			rows[1].UserId.ShouldBe(quiet);
		}
	}

	[Fact]
	public void NamesBreakEveryTie_SoARepaintNeverShufflesTwoEqualRows()
	{
		Guid adam = Guid.NewGuid();
		Guid zoe = Guid.NewGuid();

		// Same point, same instant: everything numeric compares equal.
		Dictionary<Guid, RiderPositionDto> positions = new()
		{
			[zoe] = Fix(zoe, "Zoe", BaseLat, BaseLon, Now),
			[adam] = Fix(adam, "Adam", BaseLat, BaseLon, Now),
		};

		foreach (MemberSort sort in Enum.GetValues<MemberSort>())
		{
			IReadOnlyList<MemberRow> rows = MemberRoster.Build(
				[Member(zoe, "Zoe"), Member(adam, "Adam")],
				positions,
				EastwardRoute(),
				from: (BaseLat, BaseLon),
				selfUserId: null,
				now: Now,
				sort: sort);

			rows[0].UserName.ShouldBe("Adam", $"{sort} leaves equal rows in a stated order rather than an arbitrary one.");
		}
	}

	// -- The private area (§10.1) -----------------------------------------------------------------

	[Fact]
	public void Private_IsItsOwnState_NotNoSignal()
	{
		// The distinction the whole feature turns on. "No signal" is a reason to wait at the
		// junction; "private" is somebody in their kitchen who will be along.
		Guid home = Guid.NewGuid();
		Guid tunnel = Guid.NewGuid();

		IReadOnlyList<MemberRow> rows = MemberRoster.Build(
			[Member(home, "Home", hasPosition: false, isPrivate: true), Member(tunnel, "Tunnel", hasPosition: false)],
			new Dictionary<Guid, RiderPositionDto>(),
			route: null,
			from: null,
			selfUserId: null,
			now: Now,
			sort: MemberSort.Name);

		rows.Single(row => row.UserId == home).Presence.ShouldBe(MemberPresence.Private);
		rows.Single(row => row.UserId == tunnel).Presence.ShouldBe(MemberPresence.NoSignal,
			"§10.1: an empty row means something different when the rider chose it.");
	}

	[Fact]
	public void NotSharing_StillWins_OverPrivate()
	{
		// Both are true of the same rider, and only one of them is about *this* ride. A member who
		// switched sharing off is not coming back onto the map at the end of their street.
		Guid rider = Guid.NewGuid();

		IReadOnlyList<MemberRow> rows = MemberRoster.Build(
			[Member(rider, "Rider", sharing: false, hasPosition: false, isPrivate: true)],
			new Dictionary<Guid, RiderPositionDto>(),
			route: null,
			from: null,
			selfUserId: null,
			now: Now,
			sort: MemberSort.Name);

		rows.Single().Presence.ShouldBe(MemberPresence.NotSharing);
	}

	[Fact]
	public void APrivateRider_CarriesNoneOfTheFourFigures()
	{
		// Range, along, gap and off-route are all derived from a position the ride no longer holds -
		// and the leader is worked out over the same column, so a private rider cannot be one.
		Guid home = Guid.NewGuid();
		Guid moving = Guid.NewGuid();

		IReadOnlyList<MemberRow> rows = MemberRoster.Build(
			[Member(home, "Home", hasPosition: false, isPrivate: true), Member(moving, "Moving")],
			new Dictionary<Guid, RiderPositionDto> { [moving] = Fix(moving, "Moving", BaseLat, BaseLon + 0.01, Now) },
			EastwardRoute(),
			from: (BaseLat, BaseLon),
			selfUserId: null,
			now: Now,
			sort: MemberSort.Name);

		MemberRow row = rows.Single(candidate => candidate.UserId == home);

		row.RangeMetres.ShouldBeNull();
		row.AlongMetres.ShouldBeNull();
		row.GapMetres.ShouldBeNull();
		row.OffMetres.ShouldBeNull();
		row.OffRoute.ShouldBeFalse();
		row.FixAge.ShouldBeNull();
		row.IsLeader.ShouldBeFalse();

		rows.Single(candidate => candidate.UserId == moving).IsLeader.ShouldBeTrue(
			"the rider who is actually on the road leads it.");
	}

	[Fact]
	public void TheReadersOwnRow_KeepsItsFigures_FromTheirOwnDevice_WhileTheyArePrivate()
	{
		// §10.1 is a rule about what leaves the phone. On the phone there is nobody to hide the
		// rider's house from - they are standing in it - so their own row reads exactly as it did
		// before they got home.
		Guid me = Guid.NewGuid();

		IReadOnlyList<MemberRow> rows = MemberRoster.Build(
			[Member(me, "Me", hasPosition: false, isPrivate: true)],
			new Dictionary<Guid, RiderPositionDto>(),
			EastwardRoute(),
			from: (BaseLat, BaseLon + 0.01),
			selfUserId: me,
			now: Now,
			sort: MemberSort.Name,
			ownFix: new LocationFix(BaseLat, BaseLon + 0.01, null, null, null, Now));

		MemberRow row = rows.Single();

		row.Presence.ShouldBe(MemberPresence.Sharing,
			"the reader's own screen is the one place the circle changes nothing.");
		row.FixAge.ShouldBe(TimeSpan.Zero);
		row.AlongMetres.ShouldNotBeNull();
		row.RangeMetres!.Value.ShouldBeLessThan(1, "the range from the reader to themselves is zero.");
	}

	[Fact]
	public void SomebodyElsesRow_IsNeverFilledFromThisDevicesFix()
	{
		// The device's own reading answers for one row and one row only. Filling a private rider's
		// row from wherever this phone happens to be would put them on the map at the reader's
		// position, which is worse than leaving them off it.
		Guid me = Guid.NewGuid();
		Guid them = Guid.NewGuid();

		IReadOnlyList<MemberRow> rows = MemberRoster.Build(
			[Member(me, "Me"), Member(them, "Them", hasPosition: false, isPrivate: true)],
			new Dictionary<Guid, RiderPositionDto> { [me] = Fix(me, "Me", BaseLat, BaseLon, Now) },
			EastwardRoute(),
			from: (BaseLat, BaseLon),
			selfUserId: me,
			now: Now,
			sort: MemberSort.Name,
			ownFix: new LocationFix(BaseLat, BaseLon, null, null, null, Now));

		MemberRow row = rows.Single(candidate => candidate.UserId == them);

		row.Presence.ShouldBe(MemberPresence.Private);
		row.RangeMetres.ShouldBeNull();
		row.AlongMetres.ShouldBeNull();
	}

	// -- Formatting -------------------------------------------------------------------------------

	[Theory]
	[InlineData(null, "-")]
	[InlineData(0d, "0 m")]
	[InlineData(340d, "340 m")]
	[InlineData(999.6d, "1000 m")]
	[InlineData(4_200d, "4.2 km")]
	public void FormatDistance_IsCoarseBelowAKilometre_AndOneDecimalAbove(double? metres, string expected) =>
		MemberRoster.FormatDistance(metres).ShouldBe(expected);

	[Fact]
	public void FormatAge_SaysNothingKnown_RatherThanZero_WhenThereIsNoFix() =>
		MemberRoster.FormatAge(null).ShouldBe("-",
			"'0 s' would claim a fix arrived this instant, which is the opposite of what null means.");

	[Theory]
	[InlineData(2, "now")]
	[InlineData(42, "42 s")]
	[InlineData(150, "2 min")]
	[InlineData(7_200, "2 h")]
	public void FormatAge_GetsCoarserAsTheFixGetsOlder(int seconds, string expected) =>
		MemberRoster.FormatAge(TimeSpan.FromSeconds(seconds)).ShouldBe(expected);

	[Fact]
	public void EveryState_IsCalledSomethingOfItsOwn()
	{
		// §5.6's whole point: a group waits at the junction for "no signal" and does not wait for
		// somebody who is still in their kitchen. Two states sharing a word is that distinction
		// gone, and it is the kind of thing a rewording does by accident.
		string[] words = [.. AllStates.Select(MemberRoster.Label)];

		words.ShouldBeUnique();
		words.ShouldAllBe(word => !string.IsNullOrWhiteSpace(word));
	}

	[Fact]
	public void EveryState_ExplainsItself_ForTheKeyUnderTheList()
	{
		// The sentence is what a glyph is worth: the list draws four shapes, and a shape nobody can
		// name tells the four states apart no better than four shades of grey.
		string[] sentences = [.. AllStates.Select(MemberRoster.Describe)];

		sentences.ShouldBeUnique();
		sentences.ShouldAllBe(sentence => sentence.EndsWith('.'));
	}

	private static IEnumerable<MemberPresence> AllStates => Enum.GetValues<MemberPresence>();
}
