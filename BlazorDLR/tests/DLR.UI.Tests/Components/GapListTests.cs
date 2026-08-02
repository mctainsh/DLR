using BlazorDLR.Shared.Components;
using Bunit;
using DLR.Core.Contracts.Rides;
using DLR.Core.Tracks;

namespace DLR.UI.Tests.Components;

/// <summary>
/// §5.4's gap list. Three rendering properties that carry the specification:
/// the furthest-along rider is the leader; every other rider's gap is measured
/// back from them; a rider whose projection is off-route by more than the
/// threshold is called out explicitly rather than lumped in.
/// </summary>
public sealed class GapListTests : BunitContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private static IReadOnlyList<TrackPoint> StraightRoute()
	{
		// A ~1 km east-facing route: two points a little over one degree of longitude apart at
		// the equator would be far too long; we use a short latitude offset so distances stay
		// in the hundreds of metres and the assertions read naturally.
		return new[]
		{
			new TrackPoint(0.0, 0.0),
			new TrackPoint(0.0, 0.01), // ~1113 m east at the equator
		};
	}

	private static RiderPositionDto RiderAt(string userName, double lat, double lon) =>
		new(
			UserId: Guid.NewGuid(),
			UserName: userName,
			Lat: PositionScale.FromDegrees(lat),
			Lon: PositionScale.FromDegrees(lon),
			SpeedMps: null,
			HeadingDeg: null,
			RecordedUtc: FixedInstant);

	[Fact]
	public void FurthestAlong_IsMarkedAsLeader()
	{
		RiderPositionDto ahead = RiderAt("Alice", lat: 0.0, lon: 0.008);
		RiderPositionDto behind = RiderAt("Bob", lat: 0.0, lon: 0.003);

		Dictionary<Guid, RiderPositionDto> positions = new()
		{
			[ahead.UserId] = ahead,
			[behind.UserId] = behind,
		};

		IRenderedComponent<GapList> component = Render<GapList>(parameters => parameters
			.Add(p => p.Route, StraightRoute())
			.Add(p => p.Positions, positions));

		string markup = component.Markup;
		markup.Contains("leader", StringComparison.Ordinal).ShouldBeTrue(
			"§5.4: the furthest-along rider is the leader on the list.");

		// Alice's row must appear before Bob's — leader-first, everyone else back from them.
		int alicePos = markup.IndexOf("Alice", StringComparison.Ordinal);
		int bobPos = markup.IndexOf("Bob", StringComparison.Ordinal);
		alicePos.ShouldBeGreaterThan(-1);
		bobPos.ShouldBeGreaterThan(alicePos,
			"the list is sorted furthest-along first — a rider further down the route appears above one further back.");
	}

	[Fact]
	public void RiderFarFromRoute_IsMarkedOffRoute()
	{
		RiderPositionDto onRoute = RiderAt("Alice", lat: 0.0, lon: 0.005);
		// A latitude offset of 0.001° ~= 111 m — comfortably over the default 50 m threshold.
		RiderPositionDto strayed = RiderAt("Bob", lat: 0.001, lon: 0.005);

		Dictionary<Guid, RiderPositionDto> positions = new()
		{
			[onRoute.UserId] = onRoute,
			[strayed.UserId] = strayed,
		};

		IRenderedComponent<GapList> component = Render<GapList>(parameters => parameters
			.Add(p => p.Route, StraightRoute())
			.Add(p => p.Positions, positions));

		component.Markup.Contains("off route", StringComparison.Ordinal).ShouldBeTrue(
			"§5.4: a rider whose perpendicular distance exceeds the threshold shows an 'off route' badge.");
	}

	[Fact]
	public void NoRoute_RendersFriendlyHint()
	{
		IRenderedComponent<GapList> component = Render<GapList>(parameters => parameters
			.Add(p => p.Route, null)
			.Add(p => p.Positions, new Dictionary<Guid, RiderPositionDto>()));

		component.Markup.Contains("No planned route", StringComparison.Ordinal).ShouldBeTrue(
			"without a route the geometry cannot run — the list must say so rather than render an empty <ul>.");
	}

	[Fact]
	public void NoPositions_RendersFriendlyHint()
	{
		IRenderedComponent<GapList> component = Render<GapList>(parameters => parameters
			.Add(p => p.Route, StraightRoute())
			.Add(p => p.Positions, new Dictionary<Guid, RiderPositionDto>()));

		component.Markup.Contains("No positions", StringComparison.Ordinal).ShouldBeTrue(
			"a route with no rider fixes is a legitimate state (nobody is sharing yet), not an error.");
	}
}
