using BlazorDLR.Shared.Pages.Settings;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// Settings → Location: everything this device does with its own position (§4.2, §10.1, §15.1).
/// <para>
/// Three groups of rules meet on one screen, and the reason they are on one screen is that riders
/// conflate them:
/// <list type="bullet">
///   <item>the accuracy profile, which is how often a position is <em>sent</em> — a battery
///     decision;</item>
///   <item>the recording interval, which is how much of a ride this phone <em>keeps</em> for the
///     rider themselves, and the save that puts it in their tracks;</item>
///   <item>the private area, which is the circle neither of the above happens out of. It moved
///     here from the Profile screen — the tests below came with it.</item>
/// </list>
/// </para>
/// </summary>
public sealed class LocationSettingsTests : PageTestContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private const double Latitude = -33.868;
	private const double Longitude = 151.209;

	/// <summary>
	/// The map behind the private-area picker. <c>InitAsync</c> throws so <c>SkiaMapOverlay</c> —
	/// whose <c>SKCanvasView</c> is browser-only — never mounts; <c>RideMap</c> shows its
	/// stated-error branch and still forwards taps, which is what these tests drive.
	/// </summary>
	private readonly FakeMapInterop _map = new()
	{
		InitException = new InvalidOperationException("No base map in bUnit."),
	};

	private readonly FakeApiClient _api = new();

	private readonly FakeLocationProvider _provider = new();

	private void Wire()
	{
		Services.AddSingleton<IApiClient>(_api);
		Services.AddSingleton<TimeProvider>(new FakeTimeProvider(FixedInstant));
		Services.AddSingleton<IMapInterop>(_map);
		Services.AddSingleton<IDeviceSettings, InMemoryDeviceSettings>();
		Services.AddSingleton<ILocationProvider>(_provider);
		Services.AddSingleton<IRideHubClient>(new FakeRideHubClient());
		Services.AddSingleton<ConfirmService>();
		Services.AddSingleton<PrivateAreaState>();
		Services.AddSingleton<GpsProfileState>();
		Services.AddSingleton<TrackRecordingState>();
		Services.AddSingleton<LocationBroadcastState>();
	}

	private T Resolve<T>() where T : notnull => Services.GetRequiredService<T>();

	/// <summary>
	/// Renders the screen and waits for the private-area picker, which appears only once
	/// <c>PrivateAreaState</c> has read the device — the section shows "Reading this device…"
	/// until then, because a screen that offered the controls first would be inviting somebody
	/// to overwrite an area it had not looked at yet.
	/// </summary>
	private IRenderedComponent<Location> RenderPage()
	{
		IRenderedComponent<Location> component = Render<Location>();

		component.WaitForAssertion(() =>
			component.FindAll(".area-picker").Count.ShouldBe(1),
			timeout: TimeSpan.FromSeconds(3));

		return component;
	}

	// ---------- Recording (§15.1) ----------

	[Fact]
	public void Recording_IsOnByDefault_AtTenMetres()
	{
		// The shipped answer, and the one a rider must not have to visit this screen to get.
		Wire();

		IRenderedComponent<Location> component = RenderPage();

		component.WaitForAssertion(() =>
		{
			component.Find(".recording input[type=checkbox]").HasAttribute("checked").ShouldBeTrue(
				"§15.1: a device that has never chosen records.");
			component.Find(".intervals .interval.chosen").TextContent.Trim().ShouldBe("10 m");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void Recording_OffersTheFiveIntervals()
	{
		Wire();

		IRenderedComponent<Location> component = RenderPage();

		component.FindAll(".intervals .interval")
			.Select(button => button.TextContent.Trim())
			.ShouldBe(new[] { "5 m", "10 m", "50 m", "100 m", "500 m" });
	}

	[Fact]
	public async Task Recording_ChoosingAnInterval_StoresItOnTheDevice()
	{
		Wire();

		IRenderedComponent<Location> component = RenderPage();
		TrackRecordingState recording = Resolve<TrackRecordingState>();

		component.WaitForAssertion(() => recording.IsLoaded.ShouldBeTrue(), timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() =>
			component.FindAll(".intervals .interval")[3].Click());

		component.WaitForAssertion(() => recording.IntervalM.ShouldBe(100), timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Recording_TurningItOff_StoresItAndSaysWhatThatMeans()
	{
		Wire();

		IRenderedComponent<Location> component = RenderPage();
		TrackRecordingState recording = Resolve<TrackRecordingState>();

		component.WaitForAssertion(() => recording.IsLoaded.ShouldBeTrue(), timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() =>
			component.Find(".recording input[type=checkbox]").Change(false));

		component.WaitForAssertion(() =>
		{
			recording.IsEnabled.ShouldBeFalse();

			// The switch stops the recorder; it is not a delete button, and the copy has to say so
			// or a rider turning it off mid-tour will assume they have lost the morning.
			component.Markup.Contains("anything already recorded is kept", StringComparison.OrdinalIgnoreCase)
				.ShouldBeTrue();
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void Recording_WithNothingRecorded_SaysSoRatherThanOfferingASave()
	{
		Wire();

		IRenderedComponent<Location> component = RenderPage();

		component.WaitForAssertion(() =>
		{
			component.FindAll(".track-actions").ShouldBeEmpty(
				"there is nothing to save or delete until a fix has been recorded.");
			component.Markup.Contains("Nothing recorded yet", StringComparison.OrdinalIgnoreCase)
				.ShouldBeTrue();
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Recording_WithATrackInProgress_ShowsItsDetailAndOffersSaveAndDelete()
	{
		Wire();

		TrackRecordingState recording = Resolve<TrackRecordingState>();
		await recording.LoadAsync();
		await RideAsync(recording, 10);

		IRenderedComponent<Location> component = RenderPage();

		component.WaitForAssertion(() =>
		{
			component.Find(".track-detail").TextContent.ShouldContain("10");
			component.Find(".track-actions button.primary").ShouldNotBeNull();
			component.Find(".track-actions button.danger").ShouldNotBeNull();
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Recording_Saving_UploadsTheTrackAndClearsIt()
	{
		Wire();

		TrackRecordingState recording = Resolve<TrackRecordingState>();
		await recording.LoadAsync();
		await RideAsync(recording, 10);

		IRenderedComponent<Location> component = RenderPage();

		component.WaitForAssertion(() =>
			component.FindAll(".track-actions button.primary").Count.ShouldBe(1),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() =>
			component.Find(".recording .name input").Input("Coast run"));

		await component.InvokeAsync(() =>
			component.Find(".track-actions button.primary").Click());

		component.WaitForAssertion(() =>
		{
			_api.UploadedTracks.ShouldHaveSingleItem().Name.ShouldBe("Coast run");
			recording.HasTrack.ShouldBeFalse();
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// §15.1: the save waits for a name. The rider is asked while the ride is fresh, because the
	/// alternative is naming it weeks later against a date and a distance — and a list of rides
	/// three of which read "Untitled" is a list nobody can use.
	/// </summary>
	[Fact]
	public async Task Recording_WithoutAName_TheSaveIsOff_AndTheDeleteIsNot()
	{
		Wire();

		TrackRecordingState recording = Resolve<TrackRecordingState>();
		await recording.LoadAsync();
		await RideAsync(recording, 10);

		IRenderedComponent<Location> component = RenderPage();

		component.WaitForAssertion(() =>
		{
			component.Find(".track-actions button.primary").HasAttribute("disabled").ShouldBeTrue(
				"there is nothing to press until the adventure has a name.");
			component.Find(".track-actions button.danger").HasAttribute("disabled").ShouldBeFalse(
				"you are not naming something you are throwing away.");
			component.FindAll(".name-required").ShouldNotBeEmpty("and the screen says why.");
		}, timeout: TimeSpan.FromSeconds(3));

		// Whitespace is not a name.
		await component.InvokeAsync(() =>
			component.Find(".recording .name input").Input("   "));

		component.Find(".track-actions button.primary").HasAttribute("disabled").ShouldBeTrue();

		await component.InvokeAsync(() =>
			component.Find(".recording .name input").Input("Coast run"));

		component.WaitForAssertion(() =>
		{
			component.Find(".track-actions button.primary").HasAttribute("disabled").ShouldBeFalse();
			component.FindAll(".name-required").ShouldBeEmpty();
		}, timeout: TimeSpan.FromSeconds(3));

		_api.UploadedTracks.ShouldBeEmpty("nothing was saved by typing a name, only enabled.");
	}

	[Fact]
	public async Task Recording_TheSaveOffersToLeaveOutThePrivateArea_AndDefaultsToDoingSo()
	{
		// The default is the point. A saved track is the one artefact of a ride that outlives it,
		// and a ride that starts and ends at home starts and ends on the rider's doorstep.
		Wire();

		PrivateAreaState areas = Resolve<PrivateAreaState>();
		await areas.SetAsync(new PrivateArea(Latitude, Longitude, PrivateArea.MinRadiusM));

		TrackRecordingState recording = Resolve<TrackRecordingState>();
		await recording.LoadAsync();
		await RideAsync(recording, 15, stepM: 30);

		IRenderedComponent<Location> component = RenderPage();

		component.WaitForAssertion(() =>
			component.FindAll(".track-actions button.primary").Count.ShouldBe(1),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() =>
			component.Find(".recording .name input").Input("Coast run"));

		// The switch exists, and it is on before anybody touches it.
		component.Find(".exclude-private input[type=checkbox]").HasAttribute("checked").ShouldBeTrue(
			"§10.1: cutting the private area out is what happens unless the traveller says otherwise.");

		await component.InvokeAsync(() =>
			component.Find(".track-actions button.primary").Click());

		component.WaitForAssertion(() =>
		{
			_api.UploadedTracks.ShouldHaveSingleItem().Points.Count.ShouldBe(11,
				"§10.1: the four points inside the circle are cut out before the track leaves the device.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Recording_Deleting_AsksFirst_AndTellsNobodyButTheDevice()
	{
		Wire();

		TrackRecordingState recording = Resolve<TrackRecordingState>();
		await recording.LoadAsync();
		await RideAsync(recording, 10);

		IRenderedComponent<Location> component = RenderPage();
		ConfirmService confirm = Resolve<ConfirmService>();

		component.WaitForAssertion(() =>
			component.FindAll(".track-actions button.danger").Count.ShouldBe(1),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() =>
			component.Find(".track-actions button.danger").Click());

		// The one dialog in the app, rather than window.confirm — and it is asked before anything
		// is thrown away.
		component.WaitForAssertion(() => confirm.Current.ShouldNotBeNull(), timeout: TimeSpan.FromSeconds(3));
		recording.HasTrack.ShouldBeTrue("nothing may be deleted before the traveller has answered.");

		await component.InvokeAsync(() => confirm.Respond(true));

		component.WaitForAssertion(() =>
		{
			recording.HasTrack.ShouldBeFalse();
			_api.UploadedTracks.ShouldBeEmpty("delete is a device operation; the server was never told.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	// ---------- Accuracy (§4.2) ----------

	[Fact]
	public void Accuracy_PrintsTheRatesTheGateActuallyEnforces()
	{
		// The screen is the only place these numbers are stated to a rider, so it prints them
		// from PositionGate rather than repeating them — a copy that drifted would be a lie about
		// the rider's own battery.
		Wire();

		IRenderedComponent<Location> component = RenderPage();
		string markup = component.Markup;

		markup.ShouldContain("every 60 s, or every 50 m");
		markup.ShouldContain("every 30 s, or every 10 m");
		markup.ShouldContain("every 10 s, or every 5 m");
	}

	// ---------- The private area (§10.1, §18.6) ----------

	private async Task PlaceAndSaveAreaAsync(IRenderedComponent<Location> component, double latitude, double longitude)
	{
		// The real base maps raise this from a JS SDK event; the fake raises it directly.
		await component.InvokeAsync(() => _map.RaiseClick(latitude, longitude));

		await component.InvokeAsync(() =>
			component.Find(".area-actions button.primary").Click());
	}

	[Fact]
	public async Task PrivateArea_TapPlacesTheCentre_AndSavingStoresItOnTheDevice()
	{
		Wire();

		IRenderedComponent<Location> component = RenderPage();
		await PlaceAndSaveAreaAsync(component, Latitude, Longitude);

		PrivateAreaState state = Resolve<PrivateAreaState>();

		component.WaitForAssertion(() => state.IsSet.ShouldBeTrue(), timeout: TimeSpan.FromSeconds(3));

		state.Area!.Latitude.ShouldBe(Latitude, tolerance: 1e-6);
		state.Area.Longitude.ShouldBe(Longitude, tolerance: 1e-6);
		state.Area.RadiusM.ShouldBe(PrivateArea.DefaultRadiusM,
			"§10.1: a newly placed area is a kilometre until the traveller says otherwise.");

		// And the gate it exists to drive is now closed over that point.
		state.HidesLocation(Latitude, Longitude).ShouldBeTrue();
		state.HidesLocation(-33.918, Longitude).ShouldBeFalse();
	}

	[Fact]
	public async Task PrivateArea_NeverReachesTheServer()
	{
		Wire();

		IRenderedComponent<Location> component = RenderPage();
		await PlaceAndSaveAreaAsync(component, Latitude, Longitude);

		PrivateAreaState state = Resolve<PrivateAreaState>();
		component.WaitForAssertion(() => state.IsSet.ShouldBeTrue(), timeout: TimeSpan.FromSeconds(3));

		// The headline claim of the feature, asserted rather than assumed: saving an area makes no
		// call at all. An area that travelled would be a precise statement of where the rider
		// lives, held by the one party the setting is meant to keep it from.
		_api.Calls.ShouldNotContain(nameof(IApiClient.UpdateProfileAsync));
		_api.Calls.ShouldNotContain(nameof(IApiClient.UploadTrackAsync));
	}

	[Fact]
	public async Task PrivateArea_Remove_ForgetsIt_AndSaysSharingResumes()
	{
		Wire();

		IRenderedComponent<Location> component = RenderPage();
		await PlaceAndSaveAreaAsync(component, Latitude, Longitude);

		PrivateAreaState state = Resolve<PrivateAreaState>();
		component.WaitForAssertion(() => state.IsSet.ShouldBeTrue(), timeout: TimeSpan.FromSeconds(3));

		// The remove button only exists once there is something to remove.
		await component.InvokeAsync(() =>
			component.Find(".area-actions button.danger").Click());

		component.WaitForAssertion(() =>
		{
			state.IsSet.ShouldBeFalse();
			state.HidesLocation(Latitude, Longitude).ShouldBeFalse(
				"removing the area means this device shares from everywhere again.");
			component.FindAll(".area-actions button.danger").ShouldBeEmpty(
				"with no area set there is nothing to remove.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task PrivateArea_Reopened_OpensTheMapOverTheStoredArea()
	{
		Wire();

		// Deliberately nowhere near the camera the picker opens on when nothing is stored — an
		// area placed *at* the default would make every assertion below true either way.
		const double PerthLatitude = -31.95;
		const double PerthLongitude = 115.86;

		IRenderedComponent<Location> first = RenderPage();
		await PlaceAndSaveAreaAsync(first, PerthLatitude, PerthLongitude);

		PrivateAreaState state = Resolve<PrivateAreaState>();
		first.WaitForAssertion(() => state.IsSet.ShouldBeTrue(), timeout: TimeSpan.FromSeconds(3));

		// Reopen the screen. PrivateAreaState is scoped, so it has already read the device and the
		// picker renders on the *first* pass — before any OnAfterRender work could move it.
		IRenderedComponent<Location> reopened = RenderPage();

		reopened.WaitForAssertion(() =>
		{
			// The camera the base map was actually opened with, not the parameter the component
			// happens to hold afterwards: a camera changed after init is a camera the map has no
			// way to notice, and the symptom is a picker sitting on the shipped default.
			_map.LastOptions.ShouldNotBeNull();
			_map.LastOptions!.Camera.Latitude.ShouldBe(PerthLatitude, tolerance: 1e-6,
				"reopening the screen must open the map over the area that is in force, not over the default.");
			_map.LastOptions.Camera.Longitude.ShouldBe(PerthLongitude, tolerance: 1e-6);
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void PrivateArea_CopyStatesThatItStaysOnTheDevice_AndThatYouStillAppearInTheRide()
	{
		Wire();

		IRenderedComponent<Location> component = RenderPage();
		string markup = component.Markup;

		// §10.1's discipline: the copy has to describe what the code does. Both halves matter —
		// what the setting protects, and what it costs (no other device knows about it).
		markup.Contains("never sent to the server", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(
			"the traveller is entitled to know the area is not held anywhere but here.");
		markup.Contains("present", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(
			"§5.6: inside the area you are still in the adventure — you simply have no position on the map.");
		markup.Contains("another phone", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(
			"the cost of device-local storage is stated, not buried.");
	}

	/// <summary>Rides north from the fixture's origin, one fix every <paramref name="stepM"/> metres.</summary>
	private static async Task RideAsync(TrackRecordingState recording, int fixes, double stepM = 20)
	{
		for (int index = 0; index < fixes; index++)
		{
			await recording.OfferAsync(new LocationFix(
				Latitude + (stepM * index / 111_320d),
				Longitude,
				5,
				12.5,
				90,
				FixedInstant.AddSeconds(index * 2)));
		}
	}
}
