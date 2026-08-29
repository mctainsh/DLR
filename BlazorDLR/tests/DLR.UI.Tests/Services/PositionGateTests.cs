using BlazorDLR.Shared.Services;

namespace DLR.UI.Tests.Services;

/// <summary>
/// §4.2's filter — the five rules between the receiver and the wire.
/// <para>
/// Worth testing properly because every one of these rules exists to stop a specific observed
/// failure, and none of them can be checked on a phone without going and riding: a parked bike
/// publishing once a second, a cold fix that puts somebody three streets away, and the jump to
/// nowhere that a receiver produces while it is deciding where it is.
/// </para>
/// </summary>
public sealed class PositionGateTests
{
	private static readonly DateTimeOffset Start = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

	/// <summary>Sydney, and a point due east of it — a metre of longitude is about 92 m here.</summary>
	private const double Latitude = -33.868;
	private const double Longitude = 151.209;

	private static LocationFix Fix(
		double latitude = Latitude,
		double longitude = Longitude,
		double? accuracy = 5,
		int secondsIn = 0) =>
		new(latitude, longitude, accuracy, null, null, Start.AddSeconds(secondsIn));

	/// <summary>A rate in the units the rider sets it in.</summary>
	private static LocationUpdateRate Rate(double distanceM, int maximumS, int minimumS) =>
		new(distanceM, TimeSpan.FromSeconds(maximumS), TimeSpan.FromSeconds(minimumS));

	/// <summary>Moves a point north by roughly <paramref name="metres"/>.</summary>
	private static double NorthOf(double metres) => Latitude + (metres / 111_320d);

	[Fact]
	public void TheFirstUsableFix_AlwaysGoes()
	{
		// A rider who has just turned sharing on wants to be on the map now. Waiting out the first
		// maximum would leave them absent for a minute, which reads as the feature not working.
		PositionGate gate = new(LocationUpdateRate.Default);

		gate.Evaluate(Fix()).Publish.ShouldBeTrue();
	}

	[Fact]
	public void AFixTheReceiverIsNotSureAbout_IsRefused()
	{
		PositionGate gate = new(LocationUpdateRate.Default);

		// 200 m of claimed error is a cell-tower fix, not a GPS one: it says "somewhere in this
		// suburb", and drawing it puts a rider on a road they are not on.
		gate.Evaluate(Fix(accuracy: 200)).ShouldBe(
			new PositionGateDecision(false, PositionGateReason.TooInaccurate));
	}

	[Fact]
	public void AFixWithNoStatedAccuracy_IsTrusted()
	{
		// Null is "the platform did not say", which is not the same as "badly". Refusing these
		// would silently exclude every device that does not report accuracy (§8).
		PositionGate gate = new(LocationUpdateRate.Default);

		gate.Evaluate(Fix(accuracy: null)).Publish.ShouldBeTrue();
	}

	[Fact]
	public void InsideTheMinimum_NothingGoes_HoweverFarTheRiderHasTravelled()
	{
		// The rider's floor, and the one refusal that says nothing about the fix: this one is
		// carrying 30 m of new road and is still held, because the last send is too recent.
		PositionGate gate = new(Rate(25, 60, 5));

		gate.Evaluate(Fix()).Publish.ShouldBeTrue();
		gate.Confirm(Fix());

		gate.Evaluate(Fix(latitude: NorthOf(30), secondsIn: 2)).ShouldBe(
			new PositionGateDecision(false, PositionGateReason.HeldByMinimum));
	}

	[Fact]
	public void WhatGoesWhenTheMinimumLifts_IsWhereTheRiderIsThen()
	{
		// The point of holding rather than queueing. The fix that rang the bell at 2 s is gone by
		// the time the floor lifts, and the one that goes is the newer one — a rider at speed has
		// travelled another 60 m in the meantime and the ride wants that, not the old point.
		PositionGate gate = new(Rate(25, 60, 5));

		gate.Evaluate(Fix()).Publish.ShouldBeTrue();
		gate.Confirm(Fix());

		gate.Evaluate(Fix(latitude: NorthOf(30), secondsIn: 2)).Publish.ShouldBeFalse();

		LocationFix later = Fix(latitude: NorthOf(90), secondsIn: 5);

		gate.Evaluate(later).Publish.ShouldBeTrue();
		gate.Confirm(later);
		gate.LastAccepted!.Latitude.ShouldBe(NorthOf(90));
	}

	[Fact]
	public void AStationaryRider_IsNotRepublishedEveryFix()
	{
		// The parked-bike case, and the reason an update distance exists at all: a receiver keeps
		// producing fixes whether or not anything moved. Past the floor, and still nothing to say.
		PositionGate gate = new(Rate(25, 60, 5));

		gate.Evaluate(Fix()).Publish.ShouldBeTrue();
		gate.Confirm(Fix());

		gate.Evaluate(Fix(secondsIn: 10)).ShouldBe(
			new PositionGateDecision(false, PositionGateReason.NothingNewToSay));
	}

	[Fact]
	public void AStationaryRider_IsStillPublishedOncePerMaximum()
	{
		// And the reason the distance alone is not enough: somebody waiting at a junction must not
		// go stale on the map of the rider coming to find them.
		PositionGate gate = new(Rate(25, 60, 5));

		gate.Evaluate(Fix()).Publish.ShouldBeTrue();
		gate.Confirm(Fix());

		gate.Evaluate(Fix(secondsIn: 60)).Publish.ShouldBeTrue();
	}

	[Fact]
	public void MovingFarEnough_PublishesBeforeTheMaximumIsUp()
	{
		// The distance and the maximum are an *or*. A rider at speed crosses the update distance
		// well inside the maximum, and that is exactly when their pin most needs to move.
		PositionGate gate = new(Rate(25, 60, 5));

		gate.Evaluate(Fix()).Publish.ShouldBeTrue();
		gate.Confirm(Fix());

		gate.Evaluate(Fix(latitude: NorthOf(30), secondsIn: 6)).Publish.ShouldBeTrue();
	}

	[Fact]
	public void AJumpNobodyCouldHaveRidden_IsRefused()
	{
		// 5 km in one second. A receiver correcting itself after a cold start, not a rider — and
		// published, it would drag the whole group's map somewhere nobody is.
		PositionGate gate = new(Rate(25, 60, 2));

		gate.Evaluate(Fix()).Publish.ShouldBeTrue();
		gate.Confirm(Fix());

		gate.Evaluate(Fix(latitude: NorthOf(5_000), secondsIn: 1)).ShouldBe(
			new PositionGateDecision(false, PositionGateReason.ImplausibleSpeed));
	}

	[Fact]
	public void RealMotorwaySpeed_IsNotMistakenForAJump()
	{
		// 40 m/s is 144 km/h. The sanity rule has to sit well above anything a road produces or it
		// would quietly delete the fastest part of a ride.
		PositionGate gate = new(Rate(5, 30, 2));

		gate.Evaluate(Fix()).Publish.ShouldBeTrue();
		gate.Confirm(Fix());

		gate.Evaluate(Fix(latitude: NorthOf(80), secondsIn: 2)).Publish.ShouldBeTrue();
	}

	[Fact]
	public void AFixStampedBeforeOneAlreadySent_IsRefused()
	{
		// A replayed cached point, or a clock correction landing mid-ride. Publishing it moves
		// every other rider's map backwards — and it is asked before the floor, because a fix from
		// the past is wrong rather than early.
		PositionGate gate = new(LocationUpdateRate.Default);

		gate.Evaluate(Fix(secondsIn: 10)).Publish.ShouldBeTrue();
		gate.Confirm(Fix(secondsIn: 10));

		gate.Evaluate(Fix(secondsIn: 4)).ShouldBe(
			new PositionGateDecision(false, PositionGateReason.OutOfOrder));
	}

	[Theory]
	[InlineData(double.NaN, Longitude)]
	[InlineData(Latitude, double.PositiveInfinity)]
	[InlineData(91, Longitude)]
	[InlineData(Latitude, -181)]
	public void SomethingThatIsNotAPointOnTheEarth_IsRefused(double latitude, double longitude)
	{
		PositionGate gate = new(LocationUpdateRate.Default);

		gate.Evaluate(Fix(latitude, longitude)).ShouldBe(
			new PositionGateDecision(false, PositionGateReason.NotACoordinate));
	}

	[Fact]
	public void ApprovingAFix_DoesNotMoveTheCadenceOn_UntilItLands()
	{
		// The reported symptom was a pin that stopped for a minute or more after a link came back.
		// Half of it was here: the gate advanced the moment it said yes, so a fix that then failed
		// to send still spent the whole maximum — the rider's link recovered and the app sat on its
		// hands for another minute before it would even try again.
		PositionGate gate = new(Rate(25, 60, 5));

		gate.Evaluate(Fix()).Publish.ShouldBeTrue();

		// Nothing confirmed, so nothing to measure against: the very next fix is offered again
		// rather than being held by the floor.
		gate.LastAccepted.ShouldBeNull();
		gate.Evaluate(Fix(secondsIn: 1)).Publish.ShouldBeTrue();

		// And once one lands, the ordinary rules resume.
		gate.Confirm(Fix(secondsIn: 1));
		gate.Evaluate(Fix(secondsIn: 2)).ShouldBe(
			new PositionGateDecision(false, PositionGateReason.HeldByMinimum));
	}

	[Fact]
	public void ConfirmingAnOlderFix_DoesNotWalkTheReferenceBackwards()
	{
		// Two sends can overlap when one is retried across a slow link. The later one confirming
		// first must not be undone by the earlier one arriving behind it.
		PositionGate gate = new(LocationUpdateRate.Default);

		gate.Confirm(Fix(secondsIn: 10));
		gate.Confirm(Fix(secondsIn: 4));

		gate.LastAccepted!.RecordedUtc.ShouldBe(Start.AddSeconds(10));
	}

	[Fact]
	public void AReferencePointThatIsItselfWrong_DoesNotSilenceARiderForever()
	{
		// The other half of the reported symptom, and the nastier half. A refused fix does not
		// become the new reference — that is what makes the speed rule work — so a reference that
		// is *wrong* refuses everything measured against it. The only way out used to be the rider
		// travelling far enough for the arithmetic to fall back under 90 m/s, which for a 20 km
		// error is 222 seconds of a pin that has stopped moving on every other rider's map.
		PositionGate gate = new(Rate(25, 60, 2));

		gate.Evaluate(Fix()).Publish.ShouldBeTrue();
		gate.Confirm(Fix());

		// Three fixes 20 km away, one second apart, all agreeing with each other. Each is refused:
		// one bad fix is exactly the case the rule exists for, and caution is the right answer
		// until they have had a chance to disagree.
		for (int second = 1; second <= PositionGate.MaxConsecutiveImplausible; second++)
		{
			gate.Evaluate(Fix(latitude: NorthOf(20_000), secondsIn: second)).ShouldBe(
				new PositionGateDecision(false, PositionGateReason.ImplausibleSpeed),
				$"fix {second} of {PositionGate.MaxConsecutiveImplausible} is still one the receiver may be wrong about.");
		}

		// The next one is the gate concluding that the fault is its own reference point.
		gate.Evaluate(Fix(latitude: NorthOf(20_000), secondsIn: PositionGate.MaxConsecutiveImplausible + 1))
			.Publish.ShouldBeTrue("three fixes that agree with each other and not with the reference are the reference being stale.");
	}

	[Fact]
	public void AFixThatAgreesWithTheReference_ClearsTheRunBehindIt()
	{
		// The escape is for a *consecutive* run. A single receiver glitch between two good fixes
		// must not accumulate towards it, or a long ride would eventually let a real jump through.
		PositionGate gate = new(Rate(10, 60, 2));

		gate.Evaluate(Fix()).Publish.ShouldBeTrue();
		gate.Confirm(Fix());

		gate.Evaluate(Fix(latitude: NorthOf(20_000), secondsIn: 2)).Reason
			.ShouldBe(PositionGateReason.ImplausibleSpeed);

		// A fix back where the rider actually is, which the reference agrees with.
		gate.Evaluate(Fix(latitude: NorthOf(20), secondsIn: 4)).Publish.ShouldBeTrue();
		gate.Confirm(Fix(latitude: NorthOf(20), secondsIn: 4));

		// So the run starts again from nothing rather than from one.
		for (int second = 6; second < 6 + (2 * PositionGate.MaxConsecutiveImplausible); second += 2)
		{
			gate.Evaluate(Fix(latitude: NorthOf(20_000), secondsIn: second)).Reason
				.ShouldBe(PositionGateReason.ImplausibleSpeed);
		}
	}

	[Fact]
	public void Reset_LetsARideResumeSomewhereElse()
	{
		// Stopped sharing in one city, started again in another. Without the reset the first fix of
		// the new ride is refused as an implausible speed — and the gap between the two is exactly
		// the time the app was not watching.
		PositionGate gate = new(LocationUpdateRate.Default);

		gate.Evaluate(Fix()).Publish.ShouldBeTrue();
		gate.Confirm(Fix());
		gate.Reset();

		gate.LastAccepted.ShouldBeNull();
		gate.Evaluate(Fix(latitude: -37.814, longitude: 144.963, secondsIn: 2)).Publish.ShouldBeTrue();
	}

	[Fact]
	public void TheAccuracyGate_NeverRefusesAGoodConsumerFix()
	{
		// The one way to get this floor wrong is to set it below what a phone produces on a good
		// day, which would leave a receiver that never publishes anything at all.
		foreach (double distance in LocationUpdateRate.Distances)
		{
			PositionGate.MaxAccuracyFor(distance).ShouldBeGreaterThanOrEqualTo(30,
				$"a {distance:0} m gate tighter than a consumer GPS's own error would publish nothing.");

			// The other end of the same clamp. A 500 m step would derive a 2 km gate, which is a
			// cell-tower fix — published, it puts a rider two suburbs from where they are.
			PositionGate.MaxAccuracyFor(distance).ShouldBeLessThanOrEqualTo(50,
				$"a {distance:0} m gate this loose would publish fixes nobody could adventure to.");
		}
	}
}
