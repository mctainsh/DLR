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

		public LocationUpdateRateState Rate { get; private set; } = default!;

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
			Rate = new LocationUpdateRateState(Settings);
			Recording = new TrackRecordingState(Settings, Api, PrivateAreas);
			Broadcast = new LocationBroadcastState(
				Provider, Hub, Api, PrivateAreas, Rate, Recording, Settings, Confirm, Clock);
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

	/// <summary>A tighter rate than the default, for the tests about choosing one.</summary>
	private static readonly LocationUpdateRate Precise =
		new(5, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2));

	/// <summary>And a coarser one.</summary>
	private static readonly LocationUpdateRate Touring =
		new(100, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30));

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

		// Waited on the status rather than on the count: the fake records the send before the
		// sender has finished reacting to it, and LastPublishedUtc below is set on the far side
		// of that.
		await harness.UntilAsync(
			() => harness.Broadcast.Status == LocationBroadcastStatus.Broadcasting,
			"the first fix to reach the hub");

		harness.Hub.Published.Count.ShouldBe(1);

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

		// Waited on Broadcasting, not on the count: the gate measures from the last fix that
		// *landed* (PositionGate.Confirm), so a second fix emitted before the first was
		// confirmed would rightly be treated as a first fix and published.
		await harness.UntilAsync(
			() => harness.Broadcast.Status == LocationBroadcastStatus.Broadcasting,
			"the first fix");

		harness.Provider.Emit(Fix(secondsIn: 1));

		await harness.UntilAsync(
			() => harness.Broadcast.LastGateReason == PositionGateReason.HeldByMinimum,
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
	public async Task ASendThatNeverAnswers_DoesNotStopTheDeviceKnowingWhereItIs()
	{
		// The reported bug, in the smallest form that reproduces it. Publishing used to be awaited
		// inline in the fix loop, so a socket that had gone quiet without closing stopped
		// everything behind it: the rider's own mark, the recorder, and every fix waiting. On a
		// cell radio at speed that is minutes, and riders saw exactly that — a pin frozen for over
		// a minute and three kilometres, then a jump, then normal movement.
		Harness harness = new Harness().Build();
		harness.Hub.PublishHangs = true;
		harness.Api.PublishPositionHangs = true;

		await harness.Broadcast.ShareWithAsync(Guid.NewGuid());
		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");

		harness.Provider.Emit(Fix());
		await harness.UntilAsync(() => harness.Broadcast.OwnFix is not null, "the first fix");

		// Both transports are still hanging. The loop that reads fixes has to have carried on
		// regardless — this is the assertion the old code could not make.
		const double MovedLatitude = Latitude + 0.01;
		harness.Provider.Emit(Fix(latitude: MovedLatitude, secondsIn: 2));

		await harness.UntilAsync(
			() => harness.Broadcast.OwnFix?.Latitude == MovedLatitude,
			"the rider's own mark to keep moving while a send is stuck");
	}

	[Fact]
	public async Task ASendThatNeverAnswers_IsGivenUpOn_AndSaidOutLoud()
	{
		// Neither transport bounds itself usefully — a hub invoke waits for the server's completion
		// message and HttpClient's default is 100 seconds — so the deadline is this app's, and it
		// is driven off TimeProvider so this test does not have to wait out eight real ones.
		Harness harness = new Harness().Build();
		harness.Hub.PublishHangs = true;
		harness.Api.PublishPositionHangs = true;

		await harness.Broadcast.ShareWithAsync(Guid.NewGuid());
		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");

		harness.Provider.Emit(Fix());

		// Each deadline is armed immediately before its send, and the REST one is not armed until
		// the hub's has expired — so each is waited for and then run out in turn. Advancing blind
		// would race the sender and leave a timer that starts after the clock has already moved.
		await harness.UntilAsync(() => harness.Hub.PublishAttempts == 1, "the hub send to be in flight");
		harness.Clock.Advance(LocationBroadcastState.SendTimeout);

		await harness.UntilAsync(
			() => harness.Api.PublishPositionAttempts == 1,
			"the REST fallback to take over from the abandoned hub send");
		harness.Clock.Advance(LocationBroadcastState.SendTimeout);

		await harness.UntilAsync(
			() => harness.Broadcast.Status == LocationBroadcastStatus.Failed,
			"the broadcaster to give up on a link that has gone quiet");

		harness.Broadcast.Detail.ShouldNotBeNull();
		harness.Broadcast.Detail!.ShouldContain("no answer",
			customMessage: "a rider owed the reason their pin stopped moving is owed this one.");
	}

	[Fact]
	public async Task WhileASendIsStuck_ThePositionWaitingBehindIt_IsTheNewestOne()
	{
		// The other half of the stall, and the half that made it take minutes to unwind rather
		// than seconds. The queue behind a hung send used to be worked through in order, spending
		// a round trip each on positions the ride had already been overtaken by — so recovery
		// published a trail of history before it reached where the rider actually was.
		Harness harness = new Harness().Build();
		harness.Hub.PublishHangs = true;

		await harness.Broadcast.ShareWithAsync(Guid.NewGuid());
		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");

		harness.Provider.Emit(Fix());

		// Waited on the attempt, not on OwnFix: the point of the test is that the fixes below are
		// read while a send is genuinely stuck, which needs the send to have started.
		await harness.UntilAsync(() => harness.Hub.PublishAttempts == 1, "the first send to get stuck");

		// Three more while the first is stuck in the hub, each far enough past the profile's min
		// distance to be worth publishing — and each a step a motorcycle could actually have made.
		// A hundred metres in two seconds is about 200 km/h; the 0.01° steps this used to take were
		// 1.1 km in two seconds, which §4.2's speed rule refuses as bad data, and rightly.
		harness.Provider.Emit(Fix(latitude: Latitude + 0.001, secondsIn: 2));
		harness.Provider.Emit(Fix(latitude: Latitude + 0.002, secondsIn: 4));
		harness.Provider.Emit(Fix(latitude: Latitude + 0.003, secondsIn: 6));

		await harness.UntilAsync(
			() => harness.Broadcast.OwnFix?.RecordedUtc == Start.AddSeconds(6),
			"all four fixes to be read");

		// The link comes back: the stuck send is abandoned at its deadline and the next one goes.
		harness.Hub.PublishHangs = false;
		harness.Clock.Advance(LocationBroadcastState.SendTimeout);

		// Waited on a position actually landing, not on the status. Status reaches Broadcasting for
		// whichever send got through first, so waiting on it would let this assert against the
		// stale fix on a machine where the fallback happened to win the race.
		await harness.UntilAsync(
			() => harness.Api.PublishedPositions.Count + harness.Hub.Published.Count == 1,
			"the fix that got through to be published");

		PositionUpdate landed = harness.Api.PublishedPositions.Concat(harness.Hub.Published).Last();

		landed.RecordedUtc.ShouldBe(Start.AddSeconds(6),
			"the sender picks up where the rider is, not where they were three fixes ago.");
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
	public async Task TheReceiverRunsAtTheRateTheRiderChose()
	{
		Harness harness = new Harness().Build();
		await harness.Rate.SetAsync(Precise);

		await harness.Broadcast.ShareWithAsync(Guid.NewGuid());

		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");
		harness.Provider.LastRate.ShouldBe(Precise);
	}

	[Fact]
	public async Task ChangingTheRateMidRide_RestartsTheReceiverOnIt()
	{
		// The platform request is made when the watch starts, so a change is a restart rather than
		// a reinterpretation.
		Harness harness = new Harness().Build();

		await harness.Broadcast.ShareWithAsync(Guid.NewGuid());
		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the first receiver");

		await harness.Rate.SetAsync(Touring);

		await harness.UntilAsync(
			() => harness.Provider.WatchCount == 2 && harness.Provider.LastRate == Touring,
			"the receiver to restart on the new rate");
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
	public async Task AParkedPhone_KeepsReachingTheRide_WithoutTheReceiverSayingAnything()
	{
		// The bug this exists for. Android's fused request carries a minimum displacement, so a
		// phone that has not moved is told nothing at all — and every publishing rule in the app
		// runs on a fix arriving. A rider parked at the start of a ride sent two positions in
		// twenty-five minutes and the screen said "Sharing your location" throughout.
		Harness harness = new Harness().Build();
		using LogCapture capture = new();

		await harness.Broadcast.ShareWithAsync(Guid.NewGuid());
		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");

		harness.Provider.Emit(Fix());

		await harness.UntilAsync(
			() => harness.Broadcast.Status == LocationBroadcastStatus.Broadcasting,
			"the first fix to reach the hub");

		// Nothing else is emitted for the rest of this test: the receiver has gone quiet, which is
		// what a stationary phone looks like from up here.
		TimeSpan interval = LocationUpdateRate.Default.Maximum;
		harness.Clock.Advance(interval);

		await harness.UntilAsync(
			() => harness.Hub.Published.Count == 2,
			"the parked phone to restate where it is");

		PositionUpdate restated = harness.Hub.Published[1];
		restated.Lat.ShouldBe(PositionScale.FromDegrees(Latitude));
		restated.Lon.ShouldBe(PositionScale.FromDegrees(Longitude));
		restated.RecordedUtc.ShouldBe(
			Start + interval,
			"a restatement carries the moment it is made: the cache drops anything not newer than "
			+ "what it holds, so the receiver's original stamp would be a no-op on the server.");

		harness.Broadcast.LastPublishedUtc.ShouldBe(Start + interval);

		// The totals reach the log too — the line that would have made this diagnosable from the
		// rider's own log rather than by inference from what the status did not say.
		capture.Text.ShouldContain("GPS totals:");
	}

	[Fact]
	public async Task AKeepalive_IsNotWhatTheNextRealFix_IsMeasuredAgainst()
	{
		// A restatement is stamped with the moment it was sent, and a platform stamp trails
		// wall-clock. Confirmed into the gate, it would make the next genuine fix look like a fix
		// from the past — refused as out of order, for a whole interval, every interval.
		Harness harness = new Harness().Build();

		await harness.Broadcast.ShareWithAsync(Guid.NewGuid());
		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");

		harness.Provider.Emit(Fix());
		await harness.UntilAsync(
			() => harness.Broadcast.Status == LocationBroadcastStatus.Broadcasting,
			"the first fix to reach the hub");

		TimeSpan interval = LocationUpdateRate.Default.Maximum;
		harness.Clock.Advance(interval);
		await harness.UntilAsync(() => harness.Hub.Published.Count == 2, "the restatement");

		// The receiver comes back with a fix stamped a second before the restatement was sent —
		// the ordinary case, not a contrived one.
		harness.Provider.Emit(Fix(latitude: Latitude + 0.001, secondsIn: (int)interval.TotalSeconds - 1));

		await harness.UntilAsync(() => harness.Hub.Published.Count == 3, "the real fix behind it");

		harness.Hub.Published[2].Lat.ShouldBe(PositionScale.FromDegrees(Latitude + 0.001));
		harness.Broadcast.LastGateReason.ShouldBe(
			PositionGateReason.Accepted,
			"the restatement must not have become the reference the receiver is judged against.");
	}

	[Fact]
	public async Task ARefusedFix_DoesNotOverwrite_AReportedFailure()
	{
		// A refusal is proof the receiver is alive and proof of nothing else. It used to be allowed
		// to move any status at all, so the reason a rider's pin had stopped — named by the
		// transport that refused it — was replaced two seconds later by "waiting for a GPS fix",
		// which is a fault in the sky rather than in the network.
		Harness harness = new Harness().Build();
		harness.Hub.PublishException = new InvalidOperationException("hub refused");
		harness.Api.PublishPositionException = new ApiException(new ApiError(
			System.Net.HttpStatusCode.ServiceUnavailable, "The server is not answering.", []));

		await harness.Broadcast.ShareWithAsync(Guid.NewGuid());
		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");

		harness.Provider.Emit(Fix());
		await harness.UntilAsync(
			() => harness.Broadcast.Status == LocationBroadcastStatus.Failed,
			"the broadcaster to report both transports refusing");

		// A fix under a tin roof: worse accuracy than Balanced allows, so §4.2 refuses it on the
		// fix alone. This is the ordinary sequel to a link failure rather than a contrived one, and
		// it is the refusal that used to relabel the whole thing.
		harness.Provider.Emit(new LocationFix(Latitude, Longitude, 100, null, null, Start.AddSeconds(2)));
		await harness.UntilAsync(
			() => harness.Broadcast.LastGateReason == PositionGateReason.TooInaccurate,
			"the second fix to be refused by the gate");

		harness.Broadcast.Status.ShouldBe(
			LocationBroadcastStatus.Failed,
			"the link is still the thing that is broken, and it is still what the rider is owed.");
	}

	[Fact]
	public async Task WhenNothingReachesTheRide_TheStatusStopsSayingItIs()
	{
		// The backstop. "Broadcasting" is set by a send that landed and used to be set by nothing
		// else and cleared by nothing at all, so any path that stopped delivering without
		// reporting a failure left the one sentence a rider reads permanently wrong.
		Harness harness = new Harness().Build();

		await harness.Broadcast.ShareWithAsync(Guid.NewGuid());
		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");

		harness.Provider.Emit(Fix());
		await harness.UntilAsync(
			() => harness.Broadcast.Status == LocationBroadcastStatus.Broadcasting,
			"the first fix to reach the hub");

		// Both transports go quiet without refusing anything, which is what a black-holed cell
		// socket does. Sends are then abandoned as superseded rather than failed — the one outcome
		// that reports nothing — so nothing after this lands and nothing says so.
		harness.Hub.PublishHangs = true;
		harness.Api.PublishPositionHangs = true;

		TimeSpan interval = LocationUpdateRate.Default.Maximum;
		harness.Provider.Emit(Fix(latitude: Latitude + 0.001, secondsIn: (int)interval.TotalSeconds));
		await harness.UntilAsync(() => harness.Hub.PublishAttempts == 2, "the second send to get stuck");

		// Parked in the slot behind the stuck send, so every keepalive tick from here finds a fix
		// already waiting and leaves it alone.
		harness.Provider.Emit(Fix(latitude: Latitude + 0.002, secondsIn: (int)interval.TotalSeconds * 2));

		harness.Clock.Advance(LocationBroadcastState.StaleAfter(LocationUpdateRate.Default) + interval);

		await harness.UntilAsync(
			() => harness.Broadcast.Status == LocationBroadcastStatus.Stale,
			"the status to stop claiming the rider is on the map");

		harness.Broadcast.Describe().ShouldContain(
			"nothing has reached the adventure",
			customMessage: "the sentence a rider reads has to be the one that is true.");
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

	// ---------- The rail’s switch (§5.6, §18.6) ----------

	[Fact]
	public async Task Suspending_ClearsTheFlagOnTheServer_AsWellAsStoppingTheReceiver()
	{
		// The half that is easy to forget. A receiver stopped while the flag still stands leaves
		// this rider’s last pin on everybody else’s map with nothing arriving to move it — stopped,
		// a few streets from wherever they went dark, which is worse than not being on it at all.
		Harness harness = new Harness().Build();
		Guid ride = Guid.NewGuid();

		await harness.Broadcast.ShareWithAsync(ride);
		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");

		string? failure = await harness.Broadcast.SuspendAsync();

		failure.ShouldBeNull();
		harness.Api.SetSharingRequests.ShouldContain((ride, new SetSharingRequest(false)));
		harness.Broadcast.Status.ShouldBe(LocationBroadcastStatus.Off);
		harness.Broadcast.IsRequested.ShouldBeFalse();
		harness.Broadcast.IsSuspended.ShouldBeTrue(
			"the switch has to remember what it turned off, or the same tap cannot turn it back on.");
	}

	[Fact]
	public async Task Resuming_PutsBackExactlyWhatTheSwitchTookAway()
	{
		Harness harness = new Harness().Build();
		Guid ride = Guid.NewGuid();

		await harness.Broadcast.ShareWithAsync(ride);
		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");
		await harness.Broadcast.SuspendAsync();

		// No current ride passed, deliberately: what was suspended wins over wherever the device
		// happens to be pointing, or a rider sharing with two adventures comes back on one.
		IReadOnlyList<Guid> targets = harness.Broadcast.ResumeTargets(currentRideId: null);
		targets.ShouldBe([ride]);

		(await harness.Broadcast.ResumeAsync(targets)).ShouldBeNull();

		await harness.UntilAsync(() => harness.Provider.WatchCount == 2, "the receiver to come back");
		harness.Api.SetSharingRequests.ShouldContain((ride, new SetSharingRequest(true)));
		harness.Broadcast.IsSuspended.ShouldBeFalse();
	}

	[Fact]
	public void WithNothingSuspended_TheSwitchOffersTheAdventureThisDeviceIsOn()
	{
		// The relaunch case, which is most of them: the suspended set lives for as long as the app
		// does, and a rider looking at their adventure means that one when they turn the GPS on.
		Harness harness = new Harness().Build();
		Guid current = Guid.NewGuid();

		harness.Broadcast.ResumeTargets(current).ShouldBe([current]);
		harness.Broadcast.ResumeTargets(currentRideId: null).ShouldBeEmpty(
			"there is no such thing as broadcasting to nobody — the caller has to be able to say so.");
	}

	[Fact]
	public async Task AServerThatRefusesTheFlag_LeavesTheReceiverOff()
	{
		// A phone publishing into a ride whose flag is off spends a foreground service and a GPS on
		// fixes the server drops, while the app tells the rider they are being seen.
		Harness harness = new Harness().Build();
		Guid ride = Guid.NewGuid();
		harness.Api.SetSharingException = new ApiException(
			new ApiError(System.Net.HttpStatusCode.ServiceUnavailable, "The adventure could not be reached.", []));

		string? refusal = await harness.Broadcast.ResumeAsync([ride]);

		refusal.ShouldBe("The adventure could not be reached.");
		harness.Provider.WatchCount.ShouldBe(0);
		harness.Broadcast.IsRequested.ShouldBeFalse();
	}

	[Fact]
	public async Task ARefusalOnTheWayOut_StopsTheReceiverAnyway_AndSaysWhy()
	{
		// The opposite trade to the test above. The rider asked for the GPS off; a request that did
		// not land is not a reason to go on transmitting. The caller reports it instead.
		Harness harness = new Harness().Build();

		await harness.Broadcast.ShareWithAsync(Guid.NewGuid());
		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");

		harness.Api.SetSharingException = new ApiException(
			new ApiError(System.Net.HttpStatusCode.ServiceUnavailable, "No network.", []));

		(await harness.Broadcast.SuspendAsync()).ShouldBe("No network.");

		harness.Broadcast.Status.ShouldBe(LocationBroadcastStatus.Off);
		harness.Provider.Stopped.ShouldBeTrue();
	}

	[Fact]
	public async Task ARideThatGoesAway_IsNotSomethingTheSwitchBringsBack()
	{
		// Ended, left or removed. Resurrecting one of those would put a rider back on a map they
		// are not on, from a control that says nothing about which adventure it means.
		Harness harness = new Harness().Build();
		Guid ride = Guid.NewGuid();

		await harness.Broadcast.ShareWithAsync(ride);
		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");

		await harness.Broadcast.SuspendAsync();
		await harness.Broadcast.StopSharingAsync(ride);

		harness.Broadcast.IsSuspended.ShouldBeFalse();
		harness.Broadcast.ResumeTargets(currentRideId: null).ShouldBeEmpty();
	}

	[Fact]
	public async Task TheTwoOffs_DoNotReadTheSame()
	{
		// "Nothing is asking" and "you turned it off" are different answers to the only question a
		// rider is asking of the switch, and the second one is the one they can undo.
		Harness harness = new Harness().Build();

		harness.Broadcast.Describe().ShouldBe("Not sharing your location.");

		await harness.Broadcast.ShareWithAsync(Guid.NewGuid());
		await harness.UntilAsync(() => harness.Provider.WatchCount == 1, "the receiver to start");
		await harness.Broadcast.SuspendAsync();

		harness.Broadcast.Describe().ShouldBe("You turned your location off on this phone.");
	}
}
