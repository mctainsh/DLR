using BlazorDLR.Shared.Components;
using BlazorDLR.Shared.Services;
using Bunit;
using DLR.Core.Contracts.Rides;
using DLR.Core.Tracks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Components;

/// <summary>
/// The rider list behind "Ride members live" (§5.3, §5.4, §5.6).
/// <para>
/// The arithmetic and all four orders belong to <c>MemberRoster</c> and are tested there. What
/// is left for a renderer is what only a renderer can answer: that every rider gets a row, that
/// the row says which of §5.6's three states they are in <em>in words</em> rather than by colour
/// alone, that the swatch is the colour the map draws them in, and that the order control
/// actually reorders the rows a rider is looking at.
/// </para>
/// </summary>
public sealed class LiveMemberListTests : BunitContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

	private const double BaseLat = -33.852;
	private const double BaseLon = 151.211;

	public LiveMemberListTests() =>
		// Ages are measured against the clock the component resolves, so a test states the
		// instant rather than racing one (ClockRules, §10.4).
		Services.AddSingleton<TimeProvider>(new FakeTimeProvider(FixedInstant));

	private static RideMemberSummary Member(
		Guid id,
		string name,
		bool sharing = true,
		bool hasPosition = true,
		string role = "Rider",
		string? colour = null) =>
		new(id, name, role, FixedInstant, sharing, hasPosition, colour);

	private static RiderPositionDto Fix(Guid id, string name, double lat, double lon, DateTimeOffset? recordedUtc = null) =>
		new(id, name, PositionScale.FromDegrees(lat), PositionScale.FromDegrees(lon), null, null,
			recordedUtc ?? FixedInstant);

	private static IReadOnlyList<TrackPoint> EastwardRoute() =>
	[
		new TrackPoint(BaseLat, BaseLon),
		new TrackPoint(BaseLat, BaseLon + 0.05),
	];

	[Fact]
	public void EveryMemberGetsARow_AndTheCountIsStated()
	{
		IRenderedComponent<LiveMemberList> component = Render<LiveMemberList>(parameters => parameters
			.Add(p => p.Members, [Member(Guid.NewGuid(), "Adam"), Member(Guid.NewGuid(), "Bea")]));

		component.FindAll(".live-members li").Count.ShouldBe(2);
		component.Find(".live-members h3").TextContent.ShouldBe("Members (2)");
	}

	[Fact]
	public void TheThreeStates_AreSaidInWords_NotByColourAlone()
	{
		// §5.6 leans on these staying distinguishable. A rider reading a phone through a visor in
		// daylight loses the colour first, so the chip has to carry the word.
		Guid sharing = Guid.NewGuid();
		Guid quiet = Guid.NewGuid();
		Guid off = Guid.NewGuid();

		IRenderedComponent<LiveMemberList> component = Render<LiveMemberList>(parameters => parameters
			.Add(p => p.Members,
			[
				Member(sharing, "Live"),
				Member(quiet, "Quiet"),
				Member(off, "Off", sharing: false, hasPosition: false),
			])
			.Add(p => p.Positions, new Dictionary<Guid, RiderPositionDto>
			{
				[sharing] = Fix(sharing, "Live", BaseLat, BaseLon),
			}));

		string[] states = [.. component.FindAll(".live-members .state").Select(chip => chip.TextContent.Trim())];

		states.ShouldContain("sharing");
		states.ShouldContain("no signal");
		states.ShouldContain("not sharing");
	}

	[Fact]
	public void EachRowCarriesTheColourTheMapDrawsThatRiderIn()
	{
		IRenderedComponent<LiveMemberList> component = Render<LiveMemberList>(parameters => parameters
			.Add(p => p.Members, [Member(Guid.NewGuid(), "Picky", colour: "#dc2626")]));

		// §16.3: the swatch and the pin have to agree, or the swatch is worse than no swatch.
		(component.Find(".live-members .swatch").GetAttribute("style") ?? string.Empty)
			.ShouldContain("#dc2626");
	}

	[Fact]
	public void TheReadersOwnRow_SaysSo()
	{
		Guid me = Guid.NewGuid();

		IRenderedComponent<LiveMemberList> component = Render<LiveMemberList>(parameters => parameters
			.Add(p => p.Members, [Member(me, "Me"), Member(Guid.NewGuid(), "Someone")])
			.Add(p => p.SelfUserId, me));

		component.FindAll(".live-members li.self").Count.ShouldBe(1);
		component.Find(".live-members li.self").TextContent.ShouldContain("you");
	}

	[Fact]
	public void TheFigures_AreLabelled_SoTwoDistancesCannotBeReadAsOneAnother()
	{
		// "340 m" and "4.2 km" measure different things, and which is which cannot be inferred
		// from the order they happen to be laid out in.
		Guid rider = Guid.NewGuid();

		IRenderedComponent<LiveMemberList> component = Render<LiveMemberList>(parameters => parameters
			.Add(p => p.Members, [Member(rider, "Rider")])
			.Add(p => p.Positions, new Dictionary<Guid, RiderPositionDto>
			{
				[rider] = Fix(rider, "Rider", BaseLat, BaseLon + 0.02, FixedInstant - TimeSpan.FromSeconds(20)),
			})
			.Add(p => p.Route, EastwardRoute())
			// The reader sits a kilometre north of the rider, who is two along the route from its
			// start — so range and distance-along are deliberately different numbers here. Two
			// columns that always agreed would prove nothing about which is which.
			.Add(p => p.From, (BaseLat + 0.01, BaseLon + 0.02)));

		component.Find(".live-members .age dd").TextContent.Trim().ShouldBe("20 s");
		component.Find(".live-members .range dd").TextContent.Trim().ShouldBe("1.1 km");
		component.Find(".live-members .along dd").TextContent.Trim().ShouldBe("1.8 km");
		component.Find(".live-members .gap dd").TextContent.Trim().ShouldBe("leader",
			"the only rider on the ride is the one at the front of it (§5.4).");
	}

	[Fact]
	public void WithNothingToMeasure_TheFiguresReadAsUnknown_RatherThanAsZero()
	{
		IRenderedComponent<LiveMemberList> component = Render<LiveMemberList>(parameters => parameters
			.Add(p => p.Members, [Member(Guid.NewGuid(), "Off", sharing: false, hasPosition: false)])
			.Add(p => p.Positions, new Dictionary<Guid, RiderPositionDto>()));

		component.Find(".live-members .age dd").TextContent.Trim().ShouldBe("—",
			"'0 s' would claim a fix arrived this instant from a rider who is not sharing at all.");
		component.Find(".live-members .range dd").TextContent.Trim().ShouldBe("—");
	}

	[Fact]
	public void ChangingTheOrder_ReordersTheRows()
	{
		Guid near = Guid.NewGuid();
		Guid far = Guid.NewGuid();

		IRenderedComponent<LiveMemberList> component = Render<LiveMemberList>(parameters => parameters
			.Add(p => p.Members, [Member(far, "Adam"), Member(near, "Zoe")])
			.Add(p => p.Positions, new Dictionary<Guid, RiderPositionDto>
			{
				// Adam is the fresher fix and the further away; Zoe the older and the nearer. The
				// two orders therefore disagree, which is the whole point of offering both.
				[far] = Fix(far, "Adam", BaseLat, BaseLon + 0.1, FixedInstant),
				[near] = Fix(near, "Zoe", BaseLat, BaseLon + 0.001, FixedInstant - TimeSpan.FromSeconds(20)),
			})
			.Add(p => p.From, (BaseLat, BaseLon))
			.Add(p => p.Sort, MemberSort.LastActive));

		component.FindAll(".live-members li strong")[0].TextContent.ShouldBe("Adam");

		component.Find(".live-members .sort select").Change(nameof(MemberSort.Range));

		component.FindAll(".live-members li strong")[0].TextContent.ShouldBe("Zoe",
			"nearest first is a different question from most recent first, and the control has to answer it.");
	}

	[Fact]
	public void OrderingByPositionAlongTheRoute_IsOfferedButDisabled_OnARideWithNoRoute()
	{
		// Present rather than absent: the four orders are what this screen is, and an option that
		// silently vanishes reads as the app having lost a feature. Disabled says why.
		IRenderedComponent<LiveMemberList> withoutRoute = Render<LiveMemberList>(parameters => parameters
			.Add(p => p.Members, [Member(Guid.NewGuid(), "Adam")]));

		withoutRoute.FindAll(".live-members .sort option")
			.Single(option => option.TextContent.Trim() == MemberRoster.Label(MemberSort.AlongRoute))
			.HasAttribute("disabled").ShouldBeTrue();

		IRenderedComponent<LiveMemberList> withRoute = Render<LiveMemberList>(parameters => parameters
			.Add(p => p.Members, [Member(Guid.NewGuid(), "Adam")])
			.Add(p => p.Route, EastwardRoute()));

		withRoute.FindAll(".live-members .sort option")
			.Single(option => option.TextContent.Trim() == MemberRoster.Label(MemberSort.AlongRoute))
			.HasAttribute("disabled").ShouldBeFalse();
	}

	[Fact]
	public void AllFourOrders_AreOffered()
	{
		IRenderedComponent<LiveMemberList> component = Render<LiveMemberList>(parameters => parameters
			.Add(p => p.Members, [Member(Guid.NewGuid(), "Adam")]));

		string[] offered = [.. component.FindAll(".live-members .sort option").Select(option => option.TextContent.Trim())];

		offered.ShouldBe(
		[
			MemberRoster.Label(MemberSort.LastActive),
			MemberRoster.Label(MemberSort.Name),
			MemberRoster.Label(MemberSort.AlongRoute),
			MemberRoster.Label(MemberSort.Range),
		]);
	}

	[Fact]
	public void ARiderPastTheOffRouteThreshold_IsCalledOut()
	{
		Guid lost = Guid.NewGuid();

		IRenderedComponent<LiveMemberList> component = Render<LiveMemberList>(parameters => parameters
			.Add(p => p.Members, [Member(lost, "Lost")])
			.Add(p => p.Positions, new Dictionary<Guid, RiderPositionDto>
			{
				[lost] = Fix(lost, "Lost", BaseLat + 0.01, BaseLon + 0.02),
			})
			.Add(p => p.Route, EastwardRoute()));

		// §5.4: being off the line is not a fifth measurement, it is a thing that has gone wrong,
		// so it gets a line of its own rather than a column.
		component.Find(".live-members .off").TextContent.ShouldContain("Off route");
	}

	[Fact]
	public void AnEmptyRide_SaysSo_RatherThanRenderingAnEmptyBox()
	{
		IRenderedComponent<LiveMemberList> component = Render<LiveMemberList>(parameters => parameters
			.Add(p => p.Members, []));

		component.Find(".live-members .empty").TextContent.ShouldContain("Nobody");
	}
}
