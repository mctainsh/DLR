using System.Net;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Rides;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.State;

/// <summary>
/// What a relaunch puts back (§5.6, §5.7, §18.6).
/// <para>
/// The case underneath all of these is the same one: the app went away mid-ride - the battery, the
/// OS reclaiming it, a WebView reload - and came back with the sharing flag still standing on the
/// server and no receiver running behind it. So what is worth asserting is which launches are put
/// back and which are left alone: a server that says the ride is gone against a phone that could
/// not ask, and a phone at all against a browser tab that lost nothing by being reloaded.
/// </para>
/// </summary>
public sealed class LaunchRestoreTests
{
	private static readonly DateTimeOffset Start = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

	private static readonly Guid Rider = Guid.Parse("11111111-1111-1111-1111-111111111111");

	private static readonly Guid Ride = Guid.Parse("22222222-2222-2222-2222-222222222222");

	private sealed class Harness
	{
		public FakeApiClient Api { get; } = new();

		public InMemoryDeviceSettings Settings { get; } = new();

		public FakeTimeProvider Clock { get; } = new(Start);

		public AuthState Auth { get; private set; } = default!;

		public CurrentRideState CurrentRide { get; private set; } = default!;

		public LaunchRestore Restore { get; private set; } = default!;

		/// <summary>The device's receiver, or null on a launch built without one.</summary>
		public LocationBroadcastState? Broadcast { get; private set; }

		/// <summary>
		/// Builds the launch. <paramref name="remembered"/> is what the device last wrote - a ride
		/// id, or null for a device that has never opened one.
		/// <para>
		/// A phone by default, because that is the device this exists for.
		/// <paramref name="withReceiver"/> false is a browser (§18.6), and
		/// <paramref name="gpsSupported"/> false is a MAUI desktop head, whose provider is the no-op.
		/// </para>
		/// </summary>
		public async Task<Harness> BuildAsync(
			Guid? remembered = null,
			bool signedIn = true,
			bool withReceiver = true,
			bool gpsSupported = true)
		{
			if (remembered is { } ride)
			{
				await Settings.SetAsync(CurrentRideState.StorageKey, ride.ToString("N"));
			}

			Auth = new AuthState(Api, new FakeTokenStore(), Clock);

			if (signedIn)
			{
				await Auth.ApplySessionAsync(new TokenResponse(
					AccessToken: "access",
					ExpiresIn: 900,
					RefreshToken: "refresh",
					User: new AuthenticatedUser(Rider, "DaveSmith", HasEmail: true, EmailConfirmed: true)));
			}

			if (withReceiver)
			{
				// Already disclosed, so the receiver coming up does not put Play's background-location
				// modal in front of a test that is about the launch rather than about the modal.
				await Settings.SetAsync(LocationDisclosure.StorageKey, "1");

				PrivateAreaState privateAreas = new(Settings, Api);

				Broadcast = new LocationBroadcastState(
					new FakeLocationProvider { IsSupported = gpsSupported },
					new FakeRideHubClient(),
					Api,
					privateAreas,
					new LocationUpdateRateState(Settings),
					new TrackRecordingState(Settings, Api, privateAreas),
					new LocationDisclosure(Settings, new ConfirmService()),
					Clock);
			}

			CurrentRide = new CurrentRideState(Settings);
			Restore = new LaunchRestore(Api, Auth, CurrentRide, Broadcast);
			return this;
		}
	}

	private static RideDetail Adventure(bool sharing) =>
		new(
			Ride,
			"Saturday hills",
			null,
			Start,
			JoinPolicyDto.Approval,
			50,
			2,
			IsOrganiser: false,
			JoinCode: null,
			new RidePermissions(),
			[new RideMemberSummary(Rider, "DaveSmith", "Member", Start, Sharing: sharing)]);

	[Fact]
	public async Task NothingWasOpen_NothingIsRestored()
	{
		Harness harness = await new Harness().BuildAsync();

		(await harness.Restore.RestoreAsync()).ShouldBeNull(
			"§18.6: a device that has never opened an adventure has nothing to be put back on.");

		harness.Api.Calls.ShouldNotContain(nameof(IApiClient.GetRideAsync),
			"a launch with no remembered adventure must not spend a round trip asking about one.");
	}

	[Fact]
	public async Task InABrowser_NothingIsRestored()
	{
		Harness harness = await new Harness().BuildAsync(Ride, withReceiver: false);
		harness.Api.RideResult = Adventure(sharing: true);

		(await harness.Restore.RestoreAsync()).ShouldBeNull(
			"§18.6: a reloaded tab lost no receiver, because this host never had one - so there is "
			+ "nothing to put back, and moving somebody off the page they opened would buy nothing.");

		harness.Api.Calls.ShouldNotContain(nameof(IApiClient.GetRideAsync),
			"and the question is settled before the round trip, not after it.");
	}

	[Fact]
	public async Task OnADeviceWithNoGps_NothingIsRestored()
	{
		Harness harness = await new Harness().BuildAsync(Ride, gpsSupported: false);
		harness.Api.RideResult = Adventure(sharing: true);

		(await harness.Restore.RestoreAsync()).ShouldBeNull(
			"§18.6: the Windows and macOS heads take NoopLocationProvider, so they are the browsers "
			+ "as far as this is concerned - a desktop is not what the OS reclaims mid-ride.");
	}

	[Fact]
	public async Task SignedOut_NothingIsRestored()
	{
		Harness harness = await new Harness().BuildAsync(Ride, signedIn: false);
		harness.Api.RideResult = Adventure(sharing: true);

		(await harness.Restore.RestoreAsync()).ShouldBeNull(
			"§7.9: a launch with no session has nobody to restore - the rider signs in first.");
	}

	[Fact]
	public async Task TheLastAdventure_IsReopened()
	{
		Harness harness = await new Harness().BuildAsync(Ride);
		harness.Api.RideResult = Adventure(sharing: true);

		(await harness.Restore.RestoreAsync()).ShouldBe($"group-rides/live/{Ride}",
			"§18.6: the last adventure is the screen the rider was on when the app went away.");
	}

	[Fact]
	public async Task TheLastAdventure_WithSharingOff_IsStillReopened()
	{
		Harness harness = await new Harness().BuildAsync(Ride);
		harness.Api.RideResult = Adventure(sharing: false);

		(await harness.Restore.RestoreAsync()).ShouldBe($"group-rides/live/{Ride}",
			"§5.6: a rider with their own GPS off is still on the ride, and the map of everybody else "
			+ "is the whole reason they have the app open.");
	}

	[Fact]
	public async Task AdventureThatIsGone_IsForgotten()
	{
		Harness harness = await new Harness().BuildAsync(Ride);
		harness.Api.RideException = new ApiException(
			new ApiError(HttpStatusCode.NotFound, "Not found", []));

		(await harness.Restore.RestoreAsync()).ShouldBeNull();

		harness.CurrentRide.RideId.ShouldBeNull(
			"§5.2: the server saying the ride is not this rider's is the one answer that clears the "
			+ "globe - otherwise it leads somewhere that 404s for the rest of the app's life.");
	}

	[Fact]
	public async Task LaunchedInATunnel_ForgetsNothing()
	{
		Harness harness = await new Harness().BuildAsync(Ride);
		harness.Api.RideException = new HttpRequestException("No such host is known.");

		(await harness.Restore.RestoreAsync()).ShouldBeNull(
			"a phone that could not ask has learnt nothing, and must not act as though it had.");

		harness.CurrentRide.RideId.ShouldBe(Ride,
			"§4.4: an adventure must never be forgotten because a phone went through a tunnel.");
	}

	[Fact]
	public async Task StillSharing_TheReceiverIsStarted()
	{
		Harness harness = await new Harness().BuildAsync(Ride);
		harness.Api.RideResult = Adventure(sharing: true);

		await harness.Restore.RestoreAsync();

		await BackgroundWait.UntilAsync(
			() => harness.Broadcast!.Rides.Contains(Ride),
			"§5.7: the flag survived the process and the receiver did not - a rider the server still "
			+ "has on the map must not be one whose pin has stopped moving.",
			() => $"Status={harness.Broadcast!.Status}.");
	}

	[Fact]
	public async Task SharingTurnedOff_TheReceiverStaysDown()
	{
		Harness harness = await new Harness().BuildAsync(Ride);
		harness.Api.RideResult = Adventure(sharing: false);

		await harness.Restore.RestoreAsync();

		harness.Broadcast!.IsRequested.ShouldBeFalse(
			"§5.6: the flag is off because the rider turned it off, and a launch is not a reason to "
			+ "put a phone back on the air behind their back.");
	}

	[Fact]
	public async Task RunsOncePerLaunch()
	{
		Harness harness = await new Harness().BuildAsync(Ride);
		harness.Api.RideResult = Adventure(sharing: true);

		(await harness.Restore.RestoreAsync()).ShouldNotBeNull();

		(await harness.Restore.RestoreAsync()).ShouldBeNull(
			"§18.6: this is a launch hook, and a second render must not pull the rider back onto a "
			+ "ride screen they have since navigated off.");
	}
}
