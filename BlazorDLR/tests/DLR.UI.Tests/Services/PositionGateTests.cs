using BlazorDLR.Shared.Services;

namespace DLR.UI.Tests.Services;

/// <summary>
/// §4.2's filter — the four rules between the receiver and the wire.
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

	/// <summary>Moves a point north by roughly <paramref name="metres"/>.</summary>
	private static double NorthOf(double metres) => Latitude + (metres / 111_320d);

	[Fact]
	public void TheFirstUsableFix_AlwaysGoes()
	{
		// A rider who has just turned sharing on wants to be on the map now. Waiting out the
		// profile's first interval would leave them absent for a full minute on Eco, which reads
		// as the feature not working.
		PositionGate gate = new(AccuracyProfile.Balanced);

		gate.Evaluate(Fix()).Publish.ShouldBeTrue();
	}

	[Fact]
	public void AFixTheReceiverIsNotSureAbout_IsRefused()
	{
		PositionGate gate = new(AccuracyProfile.Balanced);

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
		PositionGate gate = new(AccuracyProfile.Precise);

		gate.Evaluate(Fix(accuracy: null)).Publish.ShouldBeTrue();
	}

	[Fact]
	public void AStationaryRider_IsNotRepublishedEverySecond()
	{
		// The parked-bike case, and the reason a min-distance rule exists at all: a receiver keeps
		// producing fixes whether or not anything moved.
		PositionGate gate = new(AccuracyProfile.Balanced);

		gate.Evaluate(Fix()).Publish.ShouldBeTrue();

		gate.Evaluate(Fix(secondsIn: 1)).ShouldBe(
			new PositionGateDecision(false, PositionGateReason.TooSoonAndTooClose));
	}

	[Fact]
	public void AStationaryRider_IsStillPublishedOncePerInterval()
	{
		// And the reason min-distance alone is not enough: somebody waiting at a junction must not
		// go stale on the map of the rider coming to find them.
		PositionGate gate = new(AccuracyProfile.Balanced);

		gate.Evaluate(Fix()).Publish.ShouldBeTrue();
		gate.Evaluate(Fix(secondsIn: 30)).Publish.ShouldBeTrue();
	}

	[Fact]
	public void MovingFarEnough_PublishesBeforeTheIntervalIsUp()
	{
		// The two rules are an *or*. A rider at speed crosses the profile's distance well inside
		// its interval, and that is exactly when their pin most needs to move.
		PositionGate gate = new(AccuracyProfile.Balanced);

		gate.Evaluate(Fix()).Publish.ShouldBeTrue();
		gate.Evaluate(Fix(latitude: NorthOf(30), secondsIn: 1)).Publish.ShouldBeTrue();
	}

	[Fact]
	public void AJumpNobodyCouldHaveRidden_IsRefused()
	{
		// 5 km in one second. A receiver correcting itself after a cold start, not a rider — and
		// published, it would drag the whole group's map somewhere nobody is.
		PositionGate gate = new(AccuracyProfile.Balanced);

		gate.Evaluate(Fix()).Publish.ShouldBeTrue();

		gate.Evaluate(Fix(latitude: NorthOf(5_000), secondsIn: 1)).ShouldBe(
			new PositionGateDecision(false, PositionGateReason.ImplausibleSpeed));
	}

	[Fact]
	public void RealMotorwaySpeed_IsNotMistakenForAJump()
	{
		// 40 m/s is 144 km/h. The sanity rule has to sit well above anything a road produces or it
		// would quietly delete the fastest part of a ride.
		PositionGate gate = new(AccuracyProfile.Precise);

		gate.Evaluate(Fix()).Publish.ShouldBeTrue();
		gate.Evaluate(Fix(latitude: NorthOf(40), secondsIn: 1)).Publish.ShouldBeTrue();
	}

	[Fact]
	public void AFixStampedBeforeOneAlreadySent_IsRefused()
	{
		// A replayed cached point, or a clock correction landing mid-ride. Publishing it moves
		// every other rider's map backwards.
		PositionGate gate = new(AccuracyProfile.Balanced);

		gate.Evaluate(Fix(secondsIn: 10)).Publish.ShouldBeTrue();

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
		PositionGate gate = new(AccuracyProfile.Balanced);

		gate.Evaluate(Fix(latitude, longitude)).ShouldBe(
			new PositionGateDecision(false, PositionGateReason.NotACoordinate));
	}

	[Fact]
	public void Reset_LetsARideResumeSomewhereElse()
	{
		// Stopped sharing in one city, started again in another. Without the reset the first fix of
		// the new ride is refused as an implausible speed — and the gap between the two is exactly
		// the time the app was not watching.
		PositionGate gate = new(AccuracyProfile.Balanced);

		gate.Evaluate(Fix()).Publish.ShouldBeTrue();
		gate.Reset();

		gate.LastAccepted.ShouldBeNull();
		gate.Evaluate(Fix(latitude: -37.814, longitude: 144.963, secondsIn: 2)).Publish.ShouldBeTrue();
	}

	[Fact]
	public void TheProfiles_AreTheOnesTheDesignStates()
	{
		// §4.2 fixes these three pairs. They are what the settings screen prints, what the
		// platform receivers are tuned against, and what a rider chooses between — so a change
		// here is a change to the design rather than a tweak.
		PositionGate.MinInterval(AccuracyProfile.Eco).ShouldBe(TimeSpan.FromSeconds(60));
		PositionGate.MinDistanceM(AccuracyProfile.Eco).ShouldBe(50);

		PositionGate.MinInterval(AccuracyProfile.Balanced).ShouldBe(TimeSpan.FromSeconds(30));
		PositionGate.MinDistanceM(AccuracyProfile.Balanced).ShouldBe(10);

		PositionGate.MinInterval(AccuracyProfile.Precise).ShouldBe(TimeSpan.FromSeconds(10));
		PositionGate.MinDistanceM(AccuracyProfile.Precise).ShouldBe(5);
	}

	[Fact]
	public void TheAccuracyGate_NeverRefusesAGoodConsumerFix()
	{
		// The one way to get this floor wrong is to set it below what a phone produces on a good
		// day, which would leave a receiver that never publishes anything at all.
		foreach (AccuracyProfile profile in Enum.GetValues<AccuracyProfile>())
		{
			PositionGate.MaxAccuracyM(profile).ShouldBeGreaterThanOrEqualTo(30,
				$"a {profile} gate tighter than a consumer GPS's own error would publish nothing.");

			// The other end of the same clamp. Eco's 50 m step would derive a 200 m gate, which
			// is a cell-tower fix — published, it puts a rider two suburbs from where they are.
			PositionGate.MaxAccuracyM(profile).ShouldBeLessThanOrEqualTo(100,
				$"a {profile} gate this loose would publish fixes nobody could ride to.");
		}
	}
}
