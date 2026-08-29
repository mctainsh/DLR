using BlazorDLR.Shared.Components;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Contracts.Rides;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Layout;

/// <summary>
/// The rail's GPS switch (§4.3, §5.6, §18.6): what the receiver is doing, and the one tap that
/// turns it off and on.
/// <para>
/// Two rules carry the weight here. The state has to be readable by <em>shape</em> and not only by
/// colour — this is read through a visor, in daylight, by riders a fair number of whom cannot tell
/// the amber from the green — and turning the GPS off has to clear the sharing flag on the server
/// as well as stopping the receiver, or the rider's last pin stands on everybody else's map with
/// nothing arriving to move it.
/// </para>
/// </summary>
public sealed class GpsRailButtonTests : BunitContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private const double Latitude = -33.868;
	private const double Longitude = 151.209;

	private readonly FakeApiClient _api = new();
	private readonly FakeLocationProvider _provider = new();
	private readonly InMemoryDeviceSettings _settings = new();
	private readonly FakeTimeProvider _clock = new(FixedInstant);

	/// <summary>
	/// Everything the switch stands on, with the receiver registered — the MAUI host's graph.
	/// The disclosure is already accepted, as it is on every launch after the first; the one test
	/// that cares about the receiver never starting does not need a dialog to say so.
	/// </summary>
	private void Wire()
	{
		_settings.SetAsync(LocationBroadcastState.DisclosureStorageKey, "1").AsTask().Wait();

		Services.AddSingleton<IApiClient>(_api);
		Services.AddSingleton<TimeProvider>(_clock);
		Services.AddSingleton<IDeviceSettings>(_settings);
		Services.AddSingleton<ILocationProvider>(_provider);
		Services.AddSingleton<IRideHubClient>(new FakeRideHubClient());
		Services.AddSingleton<ConfirmService>();
		Services.AddSingleton<PrivateAreaState>();
		Services.AddSingleton<LocationUpdateRateState>();
		Services.AddSingleton<TrackRecordingState>();
		Services.AddSingleton<CurrentRideState>();
		Services.AddSingleton<LocationBroadcastState>();
	}

	private T Resolve<T>() where T : notnull => Services.GetRequiredService<T>();

	private static LocationFix Fix() => new(Latitude, Longitude, 5, 12.5, 90, FixedInstant);

	/// <summary>The glyph class the switch is currently drawing, without the family and the fixed-width modifier.</summary>
	private static string Shape(IRenderedComponent<GpsRailButton> component) =>
		component.Find("button.gps-switch i").ClassList
			.Single(name => name.StartsWith("fa-", StringComparison.Ordinal) && name != "fa-fw");

	/// <summary>The tone class, which grades the shape rather than replacing it.</summary>
	private static string Tone(IRenderedComponent<GpsRailButton> component) =>
		component.Find("button.gps-switch").ClassList
			.Single(name => name.StartsWith("gps-", StringComparison.Ordinal) && name != "gps-switch");

	[Fact]
	public void OnAHostWithNoReceiver_TheRailCarriesNoSwitchAtAll()
	{
		// Both browsers (§18.6) register no LocationBroadcastState. A rail item that permanently
		// said "this device has no GPS" would be a permanent tap target for a permanent non-answer.
		Services.AddSingleton<TimeProvider>(_clock);
		Services.AddSingleton<IDeviceSettings>(_settings);
		Services.AddSingleton<CurrentRideState>();

		IRenderedComponent<GpsRailButton> component = Render<GpsRailButton>();

		component.FindAll("button.gps-switch").ShouldBeEmpty();
	}

	[Fact]
	public void Resting_ItIsGreyAndItsOwnShape()
	{
		Wire();

		IRenderedComponent<GpsRailButton> component = Render<GpsRailButton>();

		Shape(component).ShouldBe("fa-circle-minus");
		Tone(component).ShouldBe("gps-idle");
	}

	[Fact]
	public async Task Broadcasting_ChangesTheShapeAsWellAsTheColour()
	{
		// The rule the whole control exists under: colour alone would leave a rider who cannot
		// separate amber from green with one glyph and no state.
		Wire();
		LocationBroadcastState broadcast = Resolve<LocationBroadcastState>();

		IRenderedComponent<GpsRailButton> component = Render<GpsRailButton>();
		string resting = Shape(component);

		await broadcast.ShareWithAsync(Guid.NewGuid());
		_provider.Emit(Fix());

		component.WaitForAssertion(
			() =>
			{
				Tone(component).ShouldBe("gps-live");
				Shape(component).ShouldBe("fa-location-crosshairs");
				Shape(component).ShouldNotBe(resting);
			},
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void WhereTheGpsCannotRun_TheSwitchIsShownButNotOffered()
	{
		// A MAUI desktop head, whose platform provider is a stub. The state is still worth drawing
		// — it is the answer to "why am I not on the map" — but there is nothing for a tap to do.
		Wire();
		_provider.IsSupported = false;

		IRenderedComponent<GpsRailButton> component = Render<GpsRailButton>();

		Shape(component).ShouldBe("fa-circle-minus");
		component.Find("button.gps-switch").HasAttribute("disabled").ShouldBeTrue();
	}

	[Fact]
	public async Task Tapping_WhileSharing_ClearsTheFlagOnTheServerAndStopsTheReceiver()
	{
		Wire();
		LocationBroadcastState broadcast = Resolve<LocationBroadcastState>();
		Guid ride = Guid.NewGuid();

		IRenderedComponent<GpsRailButton> component = Render<GpsRailButton>();

		await broadcast.ShareWithAsync(ride);
		component.WaitForAssertion(
			() => component.Find("button.gps-switch").GetAttribute("aria-pressed").ShouldBe("true"),
			timeout: TimeSpan.FromSeconds(3));

		await component.Find("button.gps-switch").ClickAsync(new());

		component.WaitForAssertion(
			() =>
			{
				_api.SetSharingRequests.ShouldContain((ride, new SetSharingRequest(false)));
				_provider.Stopped.ShouldBeTrue();
				component.Find(".gps-toast").TextContent.ShouldContain("Location off");
			},
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Tapping_WhileOff_SharesWithTheAdventureThisDeviceIsOn()
	{
		Wire();
		Guid ride = Guid.NewGuid();
		await Resolve<CurrentRideState>().SetAsync(ride);

		IRenderedComponent<GpsRailButton> component = Render<GpsRailButton>();

		await component.Find("button.gps-switch").ClickAsync(new());

		component.WaitForAssertion(
			() =>
			{
				_api.SetSharingRequests.ShouldContain((ride, new SetSharingRequest(true)));
				_provider.WatchCount.ShouldBe(1);
				component.Find(".gps-toast").TextContent.ShouldContain("Location on");
			},
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Tapping_WithNoAdventureAnywhere_SaysSoRatherThanPretending()
	{
		Wire();

		IRenderedComponent<GpsRailButton> component = Render<GpsRailButton>();

		await component.Find("button.gps-switch").ClickAsync(new());

		component.WaitForAssertion(
			() =>
			{
				component.Find(".gps-toast").TextContent.ShouldContain("Open a group adventure first");
				_api.SetSharingRequests.ShouldBeEmpty();
				_provider.WatchCount.ShouldBe(0);
			},
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task AServerThatRefuses_IsReported_RatherThanClaimingTheGpsIsOn()
	{
		Wire();
		await Resolve<CurrentRideState>().SetAsync(Guid.NewGuid());
		_api.SetSharingException = new ApiException(
			new ApiError(System.Net.HttpStatusCode.ServiceUnavailable, "The adventure could not be reached.", []));

		IRenderedComponent<GpsRailButton> component = Render<GpsRailButton>();

		await component.Find("button.gps-switch").ClickAsync(new());

		component.WaitForAssertion(
			() =>
			{
				component.Find(".gps-toast").TextContent.ShouldContain("The adventure could not be reached.");
				_provider.WatchCount.ShouldBe(0);
			},
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void TheMessageGoesByItself()
	{
		// It states what just happened and then leaves. A close button would be a 48 px target for
		// something that removes itself in three seconds, pressed by a rider taking a hand off the
		// bars — which is exactly the wrong trade.
		Wire();

		IRenderedComponent<GpsRailButton> component = Render<GpsRailButton>();
		component.Find("button.gps-switch").Click();

		component.WaitForAssertion(
			() => component.FindAll(".gps-toast").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		_clock.Advance(TimeSpan.FromSeconds(4));

		component.WaitForAssertion(
			() => component.FindAll(".gps-toast").ShouldBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void TheSwitchNamesItsStateAndWhatTheTapWillDo()
	{
		// A glyph has no accessible name and there is no caption beside it, so the anchor's own
		// name is the whole of what a screen reader — or a tooltip — has to go on.
		Wire();

		IRenderedComponent<GpsRailButton> component = Render<GpsRailButton>();
		AngleSharp.Dom.IElement button = component.Find("button.gps-switch");

		string name = button.GetAttribute("aria-label")!;
		name.ShouldContain("Not sharing your location.");
		name.ShouldContain("Tap to turn your location on.");
		button.GetAttribute("title").ShouldBe(name);
		button.GetAttribute("aria-pressed").ShouldBe("false");
		button.QuerySelector("i")!.GetAttribute("aria-hidden").ShouldBe("true");
	}
}
