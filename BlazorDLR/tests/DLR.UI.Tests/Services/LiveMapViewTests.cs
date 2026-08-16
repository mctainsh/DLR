using BlazorDLR.Shared.Services;

namespace DLR.UI.Tests.Services;

/// <summary>
/// The live map's remembered view — the one string that decides where the ride map opens.
/// <para>
/// The value comes off a device we do not control and can have been written by another
/// version, so what is pinned here is the round trip and the refusals: a view that cannot be
/// read whole must answer "nothing stored" rather than a camera pointing somewhere nobody
/// asked for.
/// </para>
/// </summary>
public sealed class LiveMapViewTests
{
	private static readonly Guid RideId = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");

	[Fact]
	public void ARoundTrip_KeepsEveryField()
	{
		LiveMapView view = new(RideId, -33.86785, 151.20732, 14.5, FollowMe: true, HeadingUp: true);

		LiveMapView? read = LiveMapView.Decode(view.Encode());

		read.ShouldNotBeNull();
		read.RideId.ShouldBe(RideId, "the camera means nothing without the ride it belongs to.");
		read.Latitude.ShouldBe(-33.86785, tolerance: 1e-5);
		read.Longitude.ShouldBe(151.20732, tolerance: 1e-5);
		read.ZoomLevel.ShouldBe(14.5, tolerance: 1e-2);
		read.FollowMe.ShouldBeTrue();
		read.HeadingUp.ShouldBeTrue();
	}

	/// <summary>
	/// The two modes are independent switches, and the encoding has to keep them that way — a
	/// rider who wants a turning map they pan around themselves is asking for exactly this pair.
	/// </summary>
	[Theory]
	[InlineData(true, false)]
	[InlineData(false, true)]
	[InlineData(false, false)]
	public void TheTwoModes_SurviveTheRoundTripSeparately(bool followMe, bool headingUp)
	{
		LiveMapView? read = LiveMapView.Decode(
			new LiveMapView(RideId, -33.8, 151.2, 12, followMe, headingUp).Encode());

		read.ShouldNotBeNull();
		read.FollowMe.ShouldBe(followMe);
		read.HeadingUp.ShouldBe(headingUp);
	}

	/// <summary>
	/// The compatibility this format's field-count floor is for. <c>HeadingUp</c> was appended to
	/// the tail without a version bump, so the value on a device that last ran the build before it
	/// still opens the map where the rider left it — losing at most the one preference that build
	/// never had.
	/// </summary>
	[Fact]
	public void AViewWrittenBeforeHeadingUpExisted_StillReadsWhole()
	{
		LiveMapView? read = LiveMapView.Decode($"1|{RideId:N}|-33.8|151.2|12|1");

		read.ShouldNotBeNull("a field appended to the tail must not cost a rider their camera.");
		read.Latitude.ShouldBe(-33.8, tolerance: 1e-5);
		read.FollowMe.ShouldBeTrue();
		read.HeadingUp.ShouldBeFalse("north-up is what that build always drew.");
	}

	[Fact]
	public void FollowingOff_SurvivesTheRoundTripAsOff()
	{
		LiveMapView? read = LiveMapView.Decode(new LiveMapView(RideId, 0, 0, 3, FollowMe: false).Encode());

		read.ShouldNotBeNull();
		read.FollowMe.ShouldBeFalse(
			"a rider who turned following off must not find it back on at the next launch.");
	}

	[Fact]
	public void ADeviceThatHasStoredNothing_ReadsAsNothing()
	{
		LiveMapView.Decode(null).ShouldBeNull();
		LiveMapView.Decode("").ShouldBeNull();
		LiveMapView.Decode("   ").ShouldBeNull();
	}

	[Theory]
	[InlineData("2|6f9619ff8b86d011b42d00c04fc964ff|-33.8|151.2|12|1")]  // a version we cannot place
	[InlineData("1|not-a-guid|-33.8|151.2|12|1")]
	[InlineData("1|6f9619ff8b86d011b42d00c04fc964ff|south|151.2|12|1")]
	[InlineData("1|6f9619ff8b86d011b42d00c04fc964ff|-33.8|151.2|12")]     // truncated
	[InlineData("1|6f9619ff8b86d011b42d00c04fc964ff|-91|151.2|12|1")]     // off the earth
	[InlineData("1|6f9619ff8b86d011b42d00c04fc964ff|-33.8|181|12|1")]
	public void AValueThatCannotBeReadWhole_ReadsAsNothing(string encoded) =>
		LiveMapView.Decode(encoded).ShouldBeNull(
			"half a camera is a camera pointing somewhere the rider never looked; the map is " +
			"better off opening where it always did.");

	[Fact]
	public void AZoomOutsideWhatAnyBaseMapOffers_IsClampedRatherThanRefused()
	{
		LiveMapView? read = LiveMapView.Decode(
			$"1|{RideId:N}|-33.8|151.2|99|0");

		read.ShouldNotBeNull("an out-of-range zoom is repairable — the ground it names is not in doubt.");
		read.ZoomLevel.ShouldBe(LiveMapView.MaxZoomLevel);
	}

	[Fact]
	public void ANonFiniteCentre_CannotBeStored() =>
		Should.Throw<InvalidOperationException>(
			() => new LiveMapView(RideId, double.NaN, 151.2, 12, FollowMe: false).Encode());

	[Fact]
	public void IsFor_AnswersOnlyForTheRideTheViewWasStoredAgainst()
	{
		LiveMapView view = new(RideId, -33.8, 151.2, 12, FollowMe: false);

		view.IsFor(RideId).ShouldBeTrue();
		view.IsFor(Guid.NewGuid()).ShouldBeFalse(
			"applying one ride's camera to another opens a Melbourne ride over Sydney.");
	}

	[Fact]
	public void ToCamera_IsTheStoredCentreAndZoom()
	{
		MapCamera camera = new LiveMapView(RideId, -33.8, 151.2, 12, FollowMe: true).ToCamera();

		camera.Latitude.ShouldBe(-33.8);
		camera.Longitude.ShouldBe(151.2);
		camera.ZoomLevel.ShouldBe(12);
		camera.HeadingDeg.ShouldBe(0, "a remembered view is north-up; the map does not store a rotation.");
	}
}
