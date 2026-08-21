using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using DLR.Core.Contracts.Tracks;
using DLR.UI.Tests.Fakes;

namespace DLR.UI.Tests.State;

/// <summary>
/// The rider's own track, from the first fix to the upload (§4.4, §15.1, §18.6).
/// <para>
/// Three things are worth more than the rest of this file put together: that nothing is sent
/// anywhere until <c>SaveAsync</c> is called, that the private-area filter runs on the way out
/// rather than being trusted to have run on the way in (§10.1), and that a track survives the app
/// being reclaimed mid-tour.
/// </para>
/// </summary>
public sealed class TrackRecordingStateTests
{
	private static readonly DateTimeOffset Start = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

	private const double Latitude = -33.868;
	private const double Longitude = 151.209;

	private static double NorthOf(double metres) => Latitude + (metres / 111_320d);

	private static LocationFix Fix(double latitude = Latitude, int secondsIn = 0) =>
		new(latitude, Longitude, 5, 12.5, 90, Start.AddSeconds(secondsIn));

	private sealed class Harness
	{
		public InMemoryDeviceSettings Settings { get; } = new();

		public FakeApiClient Api { get; } = new();

		public PrivateAreaState PrivateAreas { get; }

		public TrackRecordingState Recording { get; }

		public Harness()
		{
			PrivateAreas = new PrivateAreaState(Settings, Api);
			Recording = new TrackRecordingState(Settings, Api, PrivateAreas);
		}

		/// <summary>A fresh state over the <em>same</em> device store — a relaunch, in other words.</summary>
		public TrackRecordingState Relaunch() => new(Settings, Api, PrivateAreas);

		/// <summary>Rides north, one fix every <paramref name="stepM"/> metres.</summary>
		public async Task RideAsync(int fixes, double stepM = 20, int secondsPerFix = 2)
		{
			for (int index = 0; index < fixes; index++)
			{
				await Recording.OfferAsync(Fix(NorthOf(stepM * index), index * secondsPerFix));
			}
		}
	}

	[Fact]
	public async Task ADeviceThatHasNeverChosen_RecordsAtTenMetres()
	{
		// §15.1's own defaults, and the ones that must not need a settings visit to get right.
		Harness harness = new();

		await harness.Recording.LoadAsync();

		harness.Recording.IsEnabled.ShouldBeTrue();
		harness.Recording.IntervalM.ShouldBe(TrackRecording.DefaultIntervalM);
	}

	[Fact]
	public async Task WithRecordingOff_NothingIsKept()
	{
		Harness harness = new();
		await harness.Recording.LoadAsync();
		await harness.Recording.SetEnabledAsync(false);

		await harness.RideAsync(20);

		harness.Recording.HasTrack.ShouldBeFalse();
		harness.Recording.Stats.ShouldBeNull();
	}

	[Fact]
	public async Task TurningRecordingOff_KeepsWhatWasAlreadyRecorded()
	{
		// The switch stops the recorder; it is not a delete button. A rider who turns it off part
		// way through a tour has not asked to lose the morning.
		Harness harness = new();
		await harness.Recording.LoadAsync();
		await harness.RideAsync(5);

		int before = harness.Recording.PointCount;
		before.ShouldBe(5);

		await harness.Recording.SetEnabledAsync(false);

		harness.Recording.PointCount.ShouldBe(before);
	}

	[Fact]
	public async Task TheIntervalDecidesHowMuchIsKept()
	{
		Harness harness = new();
		await harness.Recording.LoadAsync();
		await harness.Recording.SetIntervalAsync(100);

		// Fifty-five metres a fix, so one step is inside a 100 m interval and two are past it.
		// Deliberately not a step that lands on the boundary — the fixture's metres-per-degree is
		// an approximation and a test that turned on it would be testing the approximation.
		await harness.RideAsync(11, stepM: 55);

		harness.Recording.PointCount.ShouldBe(6,
			"a 100 m interval over 550 m of travelling keeps the first fix and one every other step.");
	}

	[Fact]
	public async Task AnIntervalNothingOffersIsIgnored()
	{
		// The screen only ever sends one of the five, so this is a guard against a stored value
		// from a future build rather than against the UI.
		Harness harness = new();
		await harness.Recording.LoadAsync();

		await harness.Recording.SetIntervalAsync(37);

		harness.Recording.IntervalM.ShouldBe(TrackRecording.DefaultIntervalM);
	}

	[Fact]
	public async Task NothingIsSentAnywhereUntilSaveIsCalled()
	{
		// The claim the whole feature rests on. Recording is device-local (§18.6); the track
		// reaches the server on exactly one path, and this is not it.
		Harness harness = new();
		await harness.Recording.LoadAsync();

		await harness.RideAsync(50);

		harness.Api.UploadedTracks.ShouldBeEmpty();
		harness.Api.Calls.ShouldNotContain(nameof(IApiClient.UploadTrackAsync));
	}

	[Fact]
	public async Task ATrackSurvivesTheAppBeingReclaimedMidRide()
	{
		// Android reclaiming the app in a pocket is the failure this is written against, and it
		// is not exotic — it is what happens on a long tour with the screen off.
		Harness harness = new();
		await harness.Recording.LoadAsync();
		await harness.RideAsync(40);
		await harness.Recording.FlushAsync();

		TrackRecordingState relaunched = harness.Relaunch();
		await relaunched.LoadAsync();

		relaunched.PointCount.ShouldBe(40);
		relaunched.Stats.ShouldNotBeNull();
		relaunched.Stats!.DistanceM.ShouldBeGreaterThan(0);
	}

	[Fact]
	public async Task ARelaunchDoesNotDrawALineAcrossTheHoleItLeft()
	{
		// The app was gone for an unknown length of time, so the ride does not resume in the
		// segment it left off in (§15.3).
		Harness harness = new();
		await harness.Recording.LoadAsync();
		await harness.RideAsync(5);
		await harness.Recording.FlushAsync();

		TrackRecordingState relaunched = harness.Relaunch();
		await relaunched.LoadAsync();
		await relaunched.OfferAsync(Fix(NorthOf(200), 60));

		relaunched.Stats!.SegmentCount.ShouldBe(2);
	}

	[Fact]
	public async Task Saving_UploadsTheTrack_AndClearsTheDevice()
	{
		Harness harness = new();
		await harness.Recording.LoadAsync();
		await harness.RideAsync(10);

		TrackSummary saved = await harness.Recording.SaveAsync("Coast run", excludePrivateArea: true);

		saved.PointCount.ShouldBe(10);

		harness.Api.UploadedTracks.Count.ShouldBe(1);
		harness.Api.UploadedTracks[0].Name.ShouldBe("Coast run");
		harness.Api.UploadedTracks[0].Source.ShouldBe(TrackSourceDto.Recorded);

		harness.Recording.HasTrack.ShouldBeFalse("a saved track is no longer in progress.");

		// And it is gone from the device too, not merely from memory.
		TrackRecordingState relaunched = harness.Relaunch();
		await relaunched.LoadAsync();
		relaunched.HasTrack.ShouldBeFalse();
	}

	/// <summary>
	/// §15.1: a track is named before it is saved. The rule lives in the state and not only on the
	/// Location screen's disabled button, because this is the one path that takes a recorded ride
	/// off the device — and a list of rides called "Untitled" is a list nobody can use.
	/// </summary>
	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public async Task Saving_WithoutAName_IsRefused_AndKeepsTheTrack(string? name)
	{
		Harness harness = new();
		await harness.Recording.LoadAsync();
		await harness.RideAsync(10);

		await Should.ThrowAsync<InvalidOperationException>(
			() => harness.Recording.SaveAsync(name, excludePrivateArea: false));

		harness.Api.UploadedTracks.ShouldBeEmpty("nothing left the device");

		harness.Recording.HasTrack.ShouldBeTrue(
			"a save refused for want of a name must leave the adventure exactly where it was.");
	}

	[Fact]
	public async Task Saving_TrimsTheName()
	{
		Harness harness = new();
		await harness.Recording.LoadAsync();
		await harness.RideAsync(10);

		await harness.Recording.SaveAsync("  Saturday coast run  ", excludePrivateArea: false);

		harness.Api.UploadedTracks.ShouldHaveSingleItem().Name.ShouldBe("Saturday coast run");
	}

	[Fact]
	public async Task Saving_LeavesOutThePrivateArea_WhenAskedTo()
	{
		PrivateArea area = new(Latitude, Longitude, PrivateArea.MinRadiusM);

		Harness harness = new();
		await harness.PrivateAreas.SetAsync(area);
		await harness.Recording.LoadAsync();

		// Thirty metres a fix, riding out from the centre: the first four are inside the 100 m
		// circle and the other eleven are not.
		await harness.RideAsync(15, stepM: 30);

		await harness.Recording.SaveAsync("Coast run", excludePrivateArea: true);

		UploadTrackRequest uploaded = harness.Api.UploadedTracks.ShouldHaveSingleItem();

		uploaded.Points.Count.ShouldBe(11,
			"§10.1: every point inside the circle is cut out before the track leaves the device.");
		uploaded.Points.ShouldAllBe(point => !area.Contains(point.Latitude, point.Longitude));
	}

	[Fact]
	public async Task Saving_KeepsThePrivateArea_WhenTheRiderTurnsTheFilterOff()
	{
		// It is a choice, not a policy — and a rider who wants their whole commute is entitled to
		// it. What matters is that it is theirs to make, and that it defaults the other way.
		Harness harness = new();
		await harness.PrivateAreas.SetAsync(new PrivateArea(Latitude, Longitude, PrivateArea.MinRadiusM));
		await harness.Recording.LoadAsync();
		await harness.RideAsync(15, stepM: 30);

		await harness.Recording.SaveAsync("Coast run", excludePrivateArea: false);

		harness.Api.UploadedTracks.ShouldHaveSingleItem().Points.Count.ShouldBe(15);
	}

	[Fact]
	public async Task Saving_ATrackWhollyInsideThePrivateArea_RefusesAndKeepsIt()
	{
		Harness harness = new();
		await harness.PrivateAreas.SetAsync(new PrivateArea(Latitude, Longitude, PrivateArea.DefaultRadiusM));
		await harness.Recording.LoadAsync();
		await harness.RideAsync(10);

		await Should.ThrowAsync<InvalidOperationException>(
			() => harness.Recording.SaveAsync("Coast run", excludePrivateArea: true));

		harness.Api.UploadedTracks.ShouldBeEmpty();
		harness.Recording.HasTrack.ShouldBeTrue(
			"a save that could not happen must not take the track with it.");
	}

	[Fact]
	public async Task AFailedSave_KeepsTheTrack_AndTheNextTrySendsTheSameIdentifier()
	{
		// The upload is idempotent on the client identifier (§4.4), which is what makes "press it
		// again" safe when the failure could have been either side of the server storing it.
		Harness harness = new();
		await harness.Recording.LoadAsync();
		await harness.RideAsync(10);

		harness.Api.UploadTrackException = new HttpRequestException("no signal");

		await Should.ThrowAsync<HttpRequestException>(
			() => harness.Recording.SaveAsync("Coast run", excludePrivateArea: false));

		harness.Recording.HasTrack.ShouldBeTrue();

		harness.Api.UploadTrackException = null;
		await harness.Recording.SaveAsync("Coast run", excludePrivateArea: false);

		harness.Api.UploadedTracks.Count.ShouldBe(2);
		harness.Api.UploadedTracks[1].ClientGuid.ShouldBe(harness.Api.UploadedTracks[0].ClientGuid);
	}

	[Fact]
	public async Task ANewTrackAfterASave_CarriesADifferentIdentifier()
	{
		// Or the second upload would collide with the first and the server, being idempotent,
		// would hand back the ride from last week.
		Harness harness = new();
		await harness.Recording.LoadAsync();
		await harness.RideAsync(10);
		await harness.Recording.SaveAsync("Coast run", excludePrivateArea: false);

		await harness.RideAsync(10);
		await harness.Recording.SaveAsync("Coast run", excludePrivateArea: false);

		harness.Api.UploadedTracks[1].ClientGuid.ShouldNotBe(harness.Api.UploadedTracks[0].ClientGuid);
	}

	[Fact]
	public async Task Deleting_ForgetsTheTrackOnTheDeviceToo()
	{
		Harness harness = new();
		await harness.Recording.LoadAsync();
		await harness.RideAsync(10);
		await harness.Recording.FlushAsync();

		await harness.Recording.DiscardAsync();

		harness.Recording.HasTrack.ShouldBeFalse();
		harness.Api.UploadedTracks.ShouldBeEmpty("delete is a device operation; the server was never told.");

		TrackRecordingState relaunched = harness.Relaunch();
		await relaunched.LoadAsync();
		relaunched.HasTrack.ShouldBeFalse();
	}

	[Fact]
	public async Task TheSettingsSurviveARelaunch()
	{
		Harness harness = new();
		await harness.Recording.LoadAsync();
		await harness.Recording.SetEnabledAsync(false);
		await harness.Recording.SetIntervalAsync(500);

		TrackRecordingState relaunched = harness.Relaunch();
		await relaunched.LoadAsync();

		relaunched.IsEnabled.ShouldBeFalse();
		relaunched.IntervalM.ShouldBe(500);
	}
}
