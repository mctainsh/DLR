using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using DLR.Core.Contracts.Rides;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.State;

/// <summary>
/// The one path a fix takes from the receiver to the ride (§4.2, §4.3, §5.7, §10.1).
/// <para>
/// The platform receivers need a phone; everything above them does not, and this is where the
/// rules that matter live — that a fix from inside the rider's private area never leaves the
/// device, that the receiver runs exactly while a ride is asking for it, and that a hub which is
/// reconnecting does not silently take a rider off the map.
/// </para>
/// </summary>
public sealed class LocationBroadcastStateTests
{
	private static readonly DateTimeOffset Start = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

	private const double Latitude = -33.868;
	private const double Longitude = 151.209;

	private sealed class Harness
	{
		public FakeLocationProvider Provider { get; } = new();

		public FakeRideHubClient Hub { get; } = new();

		public FakeApiClient Api { get; } = new();

		public InMemoryDeviceSettings Settings { get; } = new();

		public FakeTimeProvider Clock { get; } = new(Start);

		public PrivateAreaState PrivateAreas { get; private set; } = default!;

		public GpsProfileState Profile { get; private set; } = default!;

		public TrackRecordingState Recording { get; private set; } = default!;

		public LocationBroadcastState Broadcast { get; private set; } = default!;

		public ConfirmService Confirm { get; } = new();

		/// <summary>
		/// Whether the device has already been shown Play's background-location disclosure. Most
		/// tests start from a device that has, because that is every ride after the first and it
		/// keeps the disclosure out of tests that are not about it.
		/// </summary>
		public Harness Build(bool disclosureAccepted = true)
		{
			if (disclosureAccepted)
			{
				Settings.SetAsync(LocationBroadcastState.DisclosureStorageKey, "1").AsTask().Wait();
			}

			PrivateAreas = new PrivateAreaState(Settings, Api);
			Profile = new GpsProfileState(Settings);
			Recording = new TrackRecordingState(Settings, Api, PrivateAreas);
			Broadcast = new LocationBroadcastState(
				Provider, Hub, Api, PrivateAreas, Profile, Recording, Settings, Confirm, Clock);
			return this;
		}

		/// <summary>
		/// Waits for a condition the background pump reaches, rather than sleeping on it.
		/// <para>
		/// A timeout reports the broadcaster's own state, because that is where the answer is: a
		/// pump that failed has already put the reason in Status and Detail, and a bare "timed
		/// out" would send the next person reading it to the wrong place.
		/// </para>
		/// </summary>
		public Task UntilAsync(Func<bool> condition, string because) =>
			BackgroundWait.UntilAsync(
				condition,
				because,
				() => $"Status={Broadcast.Status}, Detail={Broadcast.Detail ?? "<none>"}.");
	}

	private static LocationFix Fix(double latitude = Latitude, double longitude = Longitude, int secondsIn = 0) =>
		new(latitude, longitude, 5, 12.5, 90, Start.AddSeconds(secondsIn));

	[Fact]
	public void OnAHostWithNoReceiver_ItIsInertRatherThanBroken()
	{
		// Both browsers (§18.6). The ride screens ask about GPS without knowing which host they
		// are on, so the answer has to be a state rather than an exception.
		Harness harness = new Harness().Build();
		harness.Provider.IsSupported = false;

		harness.Broadcast.IsSupported.ShouldBeFalse();
		harness.Broadcast.Status.ShouldBe(LocationBroadcastStatus.Off);
	}

	[Fact]
	public async Task AskingForARide_StartsTheReceiver_AndPublishes()
	{
		Harness harness = new Harness().Build();

		await harness.Broadcast.ShareWithAsync(Guid.NewGuid());

		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");

		harness.Provider.Emit(Fix());

		await harness.UntilAsync(() => harness.Hub.Published.Count == 1, "the first fix to reach the hub");

		PositionUpdate published = harness.Hub.Published[0];
		published.Lat.ShouldBe(PositionScale.FromDegrees(Latitude));
		published.Lon.ShouldBe(PositionScale.FromDegrees(Longitude));
		published.RecordedUtc.ShouldBe(Start, "the receiver's stamp travels, not the moment it was sent.");
		published.SpeedMps.ShouldBe((short)13);
		published.HeadingDeg.ShouldBe((short)90);

		harness.Broadcast.Status.ShouldBe(LocationBroadcastStatus.Broadcasting);
		harness.Broadcast.LastPublishedUtc.ShouldBe(Start);
	}

	[Fact]
	public async Task AFixFromInsideThePrivateArea_NeverLeavesTheDevice()
	{
		// §10.1's whole point. Not filtered, not queued, not retried — dropped where it was read,
		// and the status says the setting is working rather than that something failed.
		Harness harness = new Harness().Build();
		await harness.PrivateAreas.SetAsync(new PrivateArea(Latitude, Longitude, 1_000));

		await harness.Broadcast.ShareWithAsync(Guid.NewGuid());
		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");

		harness.Provider.Emit(Fix());

		await harness.UntilAsync(
			() => harness.Broadcast.Status == LocationBroadcastStatus.Suppressed,
			"the private area to suppress the fix");

		harness.Hub.Published.ShouldBeEmpty();
		harness.Api.PublishedPositions.ShouldBeEmpty();

		// And a fix from outside the circle goes, so the gate is a circle rather than a switch.
		harness.Provider.Emit(Fix(latitude: -33.900, secondsIn: 6));

		await harness.UntilAsync(() => harness.Hub.Published.Count == 1, "a fix from outside the area to publish");
	}

	[Fact]
	public async Task EnteringThePrivateArea_TellsTheRide_WithoutTellingItWhere()
	{
		// The other half of §10.1, and the reason the rider stops being a pin frozen outside their
		// own house: the fix is dropped, and one bit goes in its place. Nothing on the wire is a
		// coordinate — several edge-snapped points would bound the centre, which is the one number
		// this protects.
		Harness harness = new Harness().Build();
		await harness.PrivateAreas.SetAsync(new PrivateArea(Latitude, Longitude, 1_000));

		await harness.Broadcast.ShareWithAsync(Guid.NewGuid());
		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");

		harness.Provider.Emit(Fix());

		await harness.UntilAsync(
			() => harness.Hub.PublishedPrivacy.Count == 1,
			"the ride to be told the rider is private");

		harness.Hub.PublishedPrivacy[0].Private.ShouldBeTrue();
		harness.Hub.Published.ShouldBeEmpty("no position may accompany it.");

		// Said once per crossing, not once per fix: fixes arrive about once a second and the answer
		// changes twice a ride.
		harness.Provider.Emit(Fix(secondsIn: 2));
		harness.Provider.Emit(Fix(secondsIn: 4));

		await harness.UntilAsync(() => harness.Broadcast.OwnFix?.RecordedUtc == Start.AddSeconds(4), "the later fixes");

		harness.Hub.PublishedPrivacy.Count.ShouldBe(1);

		// And leaving says so explicitly rather than leaving the published fix to imply it — the
		// §4.2 gate can refuse fixes for a while after a rider rolls out of their street.
		harness.Provider.Emit(Fix(latitude: -33.900, secondsIn: 10));

		await harness.UntilAsync(() => harness.Hub.PublishedPrivacy.Count == 2, "the ride to be told they are back");

		harness.Hub.PublishedPrivacy[1].Private.ShouldBeFalse();
	}

	[Fact]
	public async Task WithNoAnswerAboutThePrivateAreaYet_NothingIsAnnounced()
	{
		// The gate suppresses while this device has no answer, because a published fix costs the
		// whole feature and a suppressed one costs a moment (§10.1). Announcing on that would be a
		// different thing entirely: telling a rider's friends they are at home every time the app
		// starts up without a network.
		Harness harness = new Harness().Build();
		harness.Api.PrivateAreaException = new ApiException(
			new ApiError(System.Net.HttpStatusCode.ServiceUnavailable, "Offline", ["No network."]));

		await harness.Broadcast.ShareWithAsync(Guid.NewGuid());
		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");

		harness.Provider.Emit(Fix());

		await harness.UntilAsync(
			() => harness.Broadcast.Status == LocationBroadcastStatus.Suppressed,
			"the unanswered gate to suppress the fix");

		harness.Hub.PublishedPrivacy.ShouldBeEmpty();
		harness.Api.PublishedPrivacy.ShouldBeEmpty();
	}

	[Fact]
	public async Task WhenTheHubCannotCarryThePrivacyNotice_RestDoes()
	{
		// The message whose loss is expensive: a fix is replaced a second later, this is sent once,
		// at the kerb. A hub that happens to be reconnecting must not be the difference between a
		// rider being hidden and being parked outside their house.
		Harness harness = new Harness().Build();
		harness.Hub.PublishException = new InvalidOperationException("hub reconnecting");
		await harness.PrivateAreas.SetAsync(new PrivateArea(Latitude, Longitude, 1_000));

		await harness.Broadcast.ShareWithAsync(Guid.NewGuid());
		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");

		harness.Provider.Emit(Fix());

		await harness.UntilAsync(
			() => harness.Api.PublishedPrivacy.Count == 1,
			"the REST fallback to carry the notice");

		harness.Api.PublishedPrivacy[0].Private.ShouldBeTrue();
	}

	[Fact]
	public async Task TheDeviceKnowsWhereItIs_WhetherOrNotTheFixWentAnywhere()
	{
		// OwnFix is what this rider's own map draws (§4.3). It must not wait on the server: the
		// ride keeps nothing until it is Live (§5.1) and fans out on a 5 s tick (§5.3), and a phone
		// with a lock in hand that draws nothing reads as a broken app.
		Harness harness = new Harness().Build();
		harness.Hub.PublishException = new InvalidOperationException("hub reconnecting");
		harness.Api.PublishPositionException = new ApiException(new ApiError(
			System.Net.HttpStatusCode.ServiceUnavailable, "The server is not answering.", []));

		await harness.Broadcast.ShareWithAsync(Guid.NewGuid());
		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");

		harness.Provider.Emit(Fix());

		// Waited on the far side of both publish attempts, not on OwnFix: the pump records the fix
		// before it tries to send it, so waiting on OwnFix would be asserting the failure before
		// the failure had happened.
		await harness.UntilAsync(
			() => harness.Broadcast.Status == LocationBroadcastStatus.Failed,
			"both publish paths to refuse the fix");

		harness.Broadcast.OwnFix.ShouldNotBeNull(
			"nothing reached the adventure — which is a different fact from where the phone is.");
		harness.Broadcast.OwnFix!.Latitude.ShouldBe(Latitude);
		harness.Broadcast.OwnFix.Longitude.ShouldBe(Longitude);
	}

	[Fact]
	public async Task InsideThePrivateArea_TheRiderStillSeesThemselves()
	{
		// This asserted the opposite until riders reported the symptom: inside the circle the mark
		// stopped moving, the follow camera had nothing to follow and heading-up stopped turning —
		// the map went dead in the driveway and came back at the edge of the area.
		//
		// The rule §10.1 buys is that nobody else can see the fix, and that is enforced by not
		// publishing it. Blanking the rider's own screen protected nobody: they are standing in the
		// area, and this value never leaves the phone.
		Harness harness = new Harness().Build();
		await harness.PrivateAreas.SetAsync(new PrivateArea(Latitude, Longitude, 1_000));

		await harness.Broadcast.ShareWithAsync(Guid.NewGuid());
		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");

		harness.Provider.Emit(Fix());

		await harness.UntilAsync(
			() => harness.Broadcast.Status == LocationBroadcastStatus.Suppressed,
			"the private area to suppress the fix");

		harness.Broadcast.OwnFix.ShouldNotBeNull(
			"a rider's own map must keep working inside their own private area.");
		harness.Broadcast.OwnFix!.Latitude.ShouldBe(Latitude);
		harness.Broadcast.OwnFix.Longitude.ShouldBe(Longitude);

		// And the half that is the whole point of the feature: it went nowhere.
		harness.Hub.Published.ShouldBeEmpty();
		harness.Broadcast.LastPublishedUtc.ShouldBeNull();
	}

	[Fact]
	public async Task InsideThePrivateArea_TheOwnMarkKeepsMoving()
	{
		// The reported bug was not "no dot", it was "a dot that stopped updating". Two fixes, both
		// suppressed: the second has to land on OwnFix as well, or a rider riding across their own
		// area watches a stale mark all the way over it.
		Harness harness = new Harness().Build();
		await harness.PrivateAreas.SetAsync(new PrivateArea(Latitude, Longitude, 5_000));

		await harness.Broadcast.ShareWithAsync(Guid.NewGuid());
		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");

		harness.Provider.Emit(Fix());
		await harness.UntilAsync(
			() => harness.Broadcast.Status == LocationBroadcastStatus.Suppressed,
			"the first suppressed fix");

		const double MovedLatitude = Latitude + 0.01;
		harness.Provider.Emit(Fix(latitude: MovedLatitude, secondsIn: 10));

		await harness.UntilAsync(
			() => harness.Broadcast.OwnFix?.Latitude == MovedLatitude,
			"the rider's own mark to follow them across the area");

		harness.Hub.Published.ShouldBeEmpty("and neither fix left the phone.");
	}

	[Fact]
	public async Task EveryFix_TellsTheUi_EvenWhenNothingElseChanged()
	{
		// The rider's own mark moves off this event and nothing else. Status is a no-op between two
		// fixes of a steadily broadcasting phone — which is the whole steady state of a working
		// receiver — so raising only on a status change left the mark frozen on the first fix.
		Harness harness = new Harness().Build();

		await harness.Broadcast.ShareWithAsync(Guid.NewGuid());
		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");

		harness.Provider.Emit(Fix());
		await harness.UntilAsync(
			() => harness.Broadcast.Status == LocationBroadcastStatus.Broadcasting,
			"the first fix to publish");

		int raised = 0;
		harness.Broadcast.Changed += () => raised++;

		// A second fix a second later and a metre away: refused by the gate as too soon and too
		// close, and it leaves the status exactly where it was.
		harness.Provider.Emit(Fix(latitude: Latitude + 0.00001, secondsIn: 1));

		await harness.UntilAsync(() => raised > 0, "the UI to be told the device moved");

		harness.Broadcast.Status.ShouldBe(LocationBroadcastStatus.Broadcasting,
			"the status did not move — which is exactly the case that used to raise nothing.");
	}

	[Fact]
	public async Task StoppingTheReceiver_ForgetsWhereTheDeviceWas()
	{
		// A stopped GPS has no opinion about where the phone is. Left behind, the last fix is a dot
		// on a map the rider is still looking at, claiming a position nothing is updating.
		Harness harness = new Harness().Build();
		Guid rideId = Guid.NewGuid();

		await harness.Broadcast.ShareWithAsync(rideId);
		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");

		harness.Provider.Emit(Fix());
		await harness.UntilAsync(() => harness.Broadcast.OwnFix is not null, "the first fix");

		await harness.Broadcast.StopSharingAsync(rideId);

		harness.Broadcast.OwnFix.ShouldBeNull(
			"a stopped GPS has no opinion about where the phone is.");
	}

	[Fact]
	public async Task AFixTheGateRefuses_IsNotPublished()
	{
		// The parked-bike case end to end: the receiver keeps producing, and the wire does not.
		Harness harness = new Harness().Build();

		await harness.Broadcast.ShareWithAsync(Guid.NewGuid());
		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");

		harness.Provider.Emit(Fix());
		await harness.UntilAsync(() => harness.Hub.Published.Count == 1, "the first fix");

		harness.Provider.Emit(Fix(secondsIn: 1));

		await harness.UntilAsync(
			() => harness.Broadcast.LastGateReason == PositionGateReason.TooSoonAndTooClose,
			"the gate to refuse the second fix");

		harness.Hub.Published.Count.ShouldBe(1);
		harness.Broadcast.Status.ShouldBe(LocationBroadcastStatus.Broadcasting,
			"a traveller stopped at a junction is still on the map — the refusal is about bytes, not presence.");
	}

	[Fact]
	public async Task WhenTheHubIsDown_TheFixGoesOverRest()
	{
		// A reconnecting hub is routine on a motorcycle, not exceptional. Losing those fixes would
		// take a rider off the map for the length of every tunnel.
		Harness harness = new Harness().Build();
		harness.Hub.PublishException = new InvalidOperationException("hub reconnecting");

		await harness.Broadcast.ShareWithAsync(Guid.NewGuid());
		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");

		harness.Provider.Emit(Fix());

		// Waited on the status rather than on the count: the fix is recorded by the fake before the
		// broadcaster has finished reacting to it, so asserting the status straight after a count
		// would be racing the pump.
		await harness.UntilAsync(
			() => harness.Broadcast.Status == LocationBroadcastStatus.Broadcasting,
			"the REST fallback to carry the fix");

		harness.Api.PublishedPositions.Count.ShouldBe(1);
		harness.Hub.Published.ShouldBeEmpty();
		harness.Broadcast.Detail.ShouldBeNull(
			"one failed hub send with a successful fallback is not something to put in front of a traveller.");
	}

	[Fact]
	public async Task WhenNeitherPathWorks_ItSaysSo()
	{
		Harness harness = new Harness().Build();
		harness.Hub.PublishException = new InvalidOperationException("hub reconnecting");
		harness.Api.PublishPositionException = new ApiException(new ApiError(
			System.Net.HttpStatusCode.ServiceUnavailable, "The server is not answering.", []));

		await harness.Broadcast.ShareWithAsync(Guid.NewGuid());
		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");

		harness.Provider.Emit(Fix());

		await harness.UntilAsync(
			() => harness.Broadcast.Status == LocationBroadcastStatus.Failed,
			"the broadcaster to state that fixes are not landing");

		harness.Broadcast.Describe().ShouldContain("not reaching the adventure",
			customMessage: "a traveller who believes they are on the map when they are not is the failure this exists to prevent.");
	}

	[Fact]
	public async Task TheReceiverRunsWhileAnyRideWantsIt_AndStopsWithTheLast()
	{
		// A rider on two rides who leaves one is still on the other. Stopping there would take
		// them off a map they are on.
		Harness harness = new Harness().Build();
		Guid first = Guid.NewGuid();
		Guid second = Guid.NewGuid();

		await harness.Broadcast.ShareWithAsync(first);
		await harness.Broadcast.ShareWithAsync(second);

		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "one receiver for both adventures");

		await harness.Broadcast.StopSharingAsync(first);

		harness.Provider.Stopped.ShouldBeFalse("the second adventure is still asking.");
		harness.Broadcast.IsRequested.ShouldBeTrue();

		await harness.Broadcast.StopSharingAsync(second);

		harness.Provider.Stopped.ShouldBeTrue("the last reason went, so the receiver did.");
		harness.Broadcast.Status.ShouldBe(LocationBroadcastStatus.Off);
	}

	[Fact]
	public async Task AskingTwiceForTheSameRide_StartsOneReceiver()
	{
		// A ride's page is opened, left and opened again. A second watch would be a second
		// foreground service on Android.
		Harness harness = new Harness().Build();
		Guid rideId = Guid.NewGuid();

		await harness.Broadcast.ShareWithAsync(rideId);
		await harness.Broadcast.ShareWithAsync(rideId);

		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");
		harness.Provider.WatchCount.ShouldBe(1);
	}

	[Fact]
	public async Task WithoutPermission_ItStatesWhatTheRiderHasToDo()
	{
		Harness harness = new Harness().Build();
		harness.Provider.Permission = LocationPermissionState.DeniedPermanently;

		await harness.Broadcast.ShareWithAsync(Guid.NewGuid());

		await harness.UntilAsync(
			() => harness.Broadcast.Status == LocationBroadcastStatus.PermissionBlocked,
			"the refusal to be reported");

		harness.Broadcast.NeedsAttention.ShouldBeTrue();
		harness.Broadcast.Describe().ShouldContain("settings",
			customMessage: "a permission only the phone's settings can grant has to say so.");
	}

	[Fact]
	public async Task TheReceiverRunsAtTheProfileTheRiderChose()
	{
		Harness harness = new Harness().Build();
		await harness.Profile.SetAsync(AccuracyProfile.Precise);

		await harness.Broadcast.ShareWithAsync(Guid.NewGuid());

		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");
		harness.Provider.LastProfile.ShouldBe(AccuracyProfile.Precise);
	}

	[Fact]
	public async Task ChangingTheProfileMidRide_RestartsTheReceiverOnIt()
	{
		// The cadence is fixed when the platform request is made, so a change is a restart rather
		// than a reinterpretation.
		Harness harness = new Harness().Build();

		await harness.Broadcast.ShareWithAsync(Guid.NewGuid());
		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the first receiver");

		await harness.Profile.SetAsync(AccuracyProfile.Eco);

		await harness.UntilAsync(
			() => harness.Provider.WatchCount == 2 && harness.Provider.LastProfile == AccuracyProfile.Eco,
			"the receiver to restart on the new profile");
	}

	[Fact]
	public async Task Disposing_ReleasesTheReceiver()
	{
		// On the phone this is a foreground service and its permanent notification. An orphaned
		// one is the worst bug this code could ship.
		Harness harness = new Harness().Build();

		await harness.Broadcast.ShareWithAsync(Guid.NewGuid());
		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");

		await harness.Broadcast.DisposeAsync();

		harness.Provider.Stopped.ShouldBeTrue();
	}

	[Fact]
	public async Task TheFirstTime_TheRiderIsToldWhatWillHappenBeforeAnythingStarts()
	{
		// Google Play's prominent-disclosure requirement, and the shape it insists on: the app's
		// own words, before the platform's permission dialog, with a real way to say no. A
		// rejection here is a rejection of the whole release, so the wording is asserted rather
		// than merely the fact of a dialog.
		Harness harness = new Harness().Build(disclosureAccepted: false);

		Task sharing = harness.Broadcast.ShareWithAsync(Guid.NewGuid());

		await harness.UntilAsync(() => harness.Confirm.Current is not null, "the disclosure to be shown");

		harness.Confirm.Current!.Message.ShouldContain("even when the app is closed or not in use",
			customMessage: "Play checks this wording against a video at review.");

		harness.Provider.WatchCount.ShouldBe(0, "nothing may start before the traveller has read it.");

		harness.Confirm.Respond(true);
		await sharing;

		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start after agreement");
	}

	[Fact]
	public async Task RefusingTheDisclosure_StartsNothing()
	{
		Harness harness = new Harness().Build(disclosureAccepted: false);

		Task sharing = harness.Broadcast.ShareWithAsync(Guid.NewGuid());
		await harness.UntilAsync(() => harness.Confirm.Current is not null, "the disclosure to be shown");

		harness.Confirm.Respond(false);
		await sharing;

		harness.Provider.WatchCount.ShouldBe(0);
		harness.Broadcast.IsRequested.ShouldBeFalse(
			"a refused disclosure must not leave a reason behind that silently retries.");
		harness.Broadcast.NeedsAttention.ShouldBeTrue();
	}

	[Fact]
	public async Task TheDisclosure_IsShownOncePerDevice()
	{
		// Asked every ride, it becomes something riders dismiss without reading — which is the
		// opposite of what a disclosure is for.
		Harness harness = new Harness().Build(disclosureAccepted: false);

		Task first = harness.Broadcast.ShareWithAsync(Guid.NewGuid());
		await harness.UntilAsync(() => harness.Confirm.Current is not null, "the disclosure");
		harness.Confirm.Respond(true);
		await first;

		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");
		await harness.Broadcast.StopAllAsync();

		await harness.Broadcast.ShareWithAsync(Guid.NewGuid());

		harness.Confirm.Current.ShouldBeNull("the device has already been told.");
		await harness.UntilAsync(() => harness.Provider.WatchCount == 2, "the receiver to start again");
	}

	[Fact]
	public void AMeasurementTooLargeForTheWire_IsClamped_NotWrapped()
	{
		// A cell-tower fix can report tens of kilometres of accuracy. Cast to short it wraps to a
		// negative number, and negative accuracy is a value nothing downstream has a meaning for.
		PositionUpdate update = LocationBroadcastState.ToUpdate(
			new LocationFix(Latitude, Longitude, 40_000, null, null, Start));

		update.AccuracyM.ShouldBe(short.MaxValue);
		update.SpeedMps.ShouldBeNull("the platform did not say, which is not the same as zero (§8).");
		update.HeadingDeg.ShouldBeNull();
	}
}
