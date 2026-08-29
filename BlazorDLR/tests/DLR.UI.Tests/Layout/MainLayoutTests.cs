using BlazorDLR.Shared.Layout;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using Bunit;
using Bunit.TestDoubles;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Rides;
using DLR.UI.Tests.Fakes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Layout;

/// <summary>
/// The one layout every page composes with. What matters here is the <c>@Body</c> slot —
/// the layout hands the routed page a place to render, and its own chrome (nav rail, confirm
/// modal) sits around it. The AGPL source-offer footer lives on the
/// pre-auth pages (Welcome / SignIn / Register / etc.) rather than the layout — see
/// <c>SourceOfferFooterTests</c>. The <c>#blazor-error-ui</c> element lives in
/// <c>BlazorDLR.Web/Components/App.razor</c>, the SSR shell that wraps every route.
/// </summary>
public sealed class MainLayoutTests : BunitContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private (FakeApiClient Api, AuthState Auth, InMemoryDeviceSettings Settings) WireServices()
	{
		FakeApiClient api = new()
		{
			AboutResult = new AboutInfo("AGPL-3.0-only", "https://github.com/mctainsh/dlr",
				"abcd123456789012", "1.0.0+abcd1234", FixedInstant),
		};
		FakeTokenStore tokens = new();
		FakeTimeProvider clock = new(FixedInstant);
		AuthState auth = new(api, tokens, clock);

		Services.AddSingleton<IApiClient>(api);
		Services.AddSingleton<ITokenStore>(tokens);
		Services.AddSingleton<TimeProvider>(clock);
		Services.AddSingleton(auth);
		Services.AddSingleton<AuthenticationStateProvider>(auth);
		Services.AddRealAuthorizationPipeline();
		this.CascadeAuthenticationState(auth);

		ConfirmService confirm = new();
		Services.AddSingleton(confirm);

		// The rail this layout mounts carries the current-ride globe (§18.6), which reads the
		// device store for the ride it points at. In-memory here — no localStorage, no MAUI
		// preferences, so a render never has to hit JS.
		InMemoryDeviceSettings settings = new();
		Services.AddSingleton<IDeviceSettings>(settings);
		Services.AddSingleton<CurrentRideState>();

		// And the unread count worn on the same rail's thread item (§17.6), which the rail injects
		// whether or not anything has ever been posted.
		Services.AddSingleton<UnreadThreadState>();

		// The layout is also where the first-run introduction is decided (§18.6) — it is the one
		// component above both Home and Welcome, so it is the only place that can own "the first
		// thing you ever see". In-memory here, which reads as "never seen": see
		// FirstRun_OpensTheIntroduction below.
		Services.AddSingleton<IntroTourState>();

		// And where the last launch's adventure and GPS are put back (§5.7, §18.6). Resolvable here
		// for the same reason as the two above: the layout injects it, so a layout that cannot
		// build it is a layout no page renders inside. Nothing is stored, so it finds no adventure.
		Services.AddSingleton<LaunchRestore>();

		// The layout is where CommentNotifier gets its lifetime (§17.6): injecting it is what
		// constructs it, and constructing it is what subscribes it to the hub. So these have to
		// resolve here even though this suite is about the Body slot — a layout that cannot be
		// built is a layout no page renders inside.
		FakeRideHubClient hub = new();
		Services.AddSingleton<IRideHubClient>(hub);
		Services.AddSingleton<INotificationService, NoopNotificationService>();
		Services.AddSingleton<NotificationRouting>();
		Services.AddSingleton<CommentNotifier>();

		// A phone, because the launch restore acts on no other kind of device (§18.6): the receiver
		// is what a reclaimed app lost, and a host with none has nothing to put back. Every layout
		// test gets one so the wiring is the shipped one; only the tests below store an adventure
		// for it to find.
		PrivateAreaState privateAreas = new(settings, api);

		Services.AddSingleton(new LocationBroadcastState(
			new FakeLocationProvider(),
			hub,
			api,
			privateAreas,
			new LocationUpdateRateState(settings),
			new TrackRecordingState(settings, api, privateAreas),
			settings,
			confirm,
			clock));

		return (api, auth, settings);
	}

	/// <summary>The adventure a mid-ride relaunch is put back on.</summary>
	private static readonly Guid Ride = Guid.Parse("33333333-3333-3333-3333-333333333333");

	/// <summary>
	/// A launch that has been through the deck already and has an adventure to go back to — the
	/// arrangement both restore tests below start from, and every launch but the first.
	/// </summary>
	private async Task<BunitNavigationManager> LaunchMidRideAsync(RideStateDto state)
	{
		(FakeApiClient api, AuthState auth, InMemoryDeviceSettings settings) = WireServices();

		// Disclosed already, so the receiver coming up does not put Play's background-location
		// modal over the layout.
		await settings.SetAsync(LocationBroadcastState.DisclosureStorageKey, "1");

		api.RideResult = new RideDetail(
			Ride, "Saturday hills", null, FixedInstant, state, JoinPolicyDto.Approval, 50, 2,
			IsOrganiser: false, JoinCode: null, new RidePermissions(),
			[new RideMemberSummary(Rider, "DaveSmith", "Member", FixedInstant, Sharing: true)]);

		await auth.ApplySessionAsync(new TokenResponse(
			AccessToken: "access",
			ExpiresIn: 900,
			RefreshToken: "refresh",
			User: new AuthenticatedUser(Rider, "DaveSmith", HasEmail: true, EmailConfirmed: true)));

		await Services.GetRequiredService<IntroTourState>().MarkSeenAsync();
		await settings.SetAsync(CurrentRideState.StorageKey, Ride.ToString("N"));

		return Services.GetRequiredService<NavigationManager>() as BunitNavigationManager
			?? throw new InvalidOperationException("bUnit did not register a BunitNavigationManager.");
	}

	/// <summary>Who is coming back.</summary>
	private static readonly Guid Rider = Guid.Parse("44444444-4444-4444-4444-444444444444");

	[Fact]
	public void FirstRun_OpensTheIntroduction()
	{
		WireServices();
		BunitNavigationManager nav = Services.GetRequiredService<NavigationManager>() as BunitNavigationManager
			?? throw new InvalidOperationException("bUnit did not register a BunitNavigationManager.");

		IRenderedComponent<MainLayout> component = Render<MainLayout>(parameters => parameters
			.Add(p => p.Body, (RenderFragment)(builder => builder.AddMarkupContent(0, "<p>routed page</p>"))));

		// The device store is empty, which is what a phone that has just been installed on looks
		// like — and the one launch where a rider has no idea what this app is.
		component.WaitForAssertion(() =>
			nav.Uri.Contains("/intro", StringComparison.Ordinal).ShouldBeTrue(
				$"§18.6: a first run must open the introduction; got '{nav.Uri}'."),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task ADeviceThatHasSeenTheIntroduction_IsTakenStraightIntoTheApp()
	{
		WireServices();
		BunitNavigationManager nav = Services.GetRequiredService<NavigationManager>() as BunitNavigationManager
			?? throw new InvalidOperationException("bUnit did not register a BunitNavigationManager.");
		string before = nav.Uri;

		// Marked seen before the layout is built — the second launch on a device, which is every
		// launch but one.
		await Services.GetRequiredService<IntroTourState>().MarkSeenAsync();

		IRenderedComponent<MainLayout> component = Render<MainLayout>(parameters => parameters
			.Add(p => p.Body, (RenderFragment)(builder => builder.AddMarkupContent(0, "<p>routed page</p>"))));

		component.WaitForAssertion(() =>
			component.Markup.Contains("routed page", StringComparison.Ordinal).ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		nav.Uri.ShouldBe(before, "a rider who has been through the deck must never be shown it again by itself.");
	}

	[Fact]
	public async Task LaunchedMidRide_ReopensTheAdventure()
	{
		BunitNavigationManager nav = await LaunchMidRideAsync(RideStateDto.Live);

		IRenderedComponent<MainLayout> component = Render<MainLayout>(parameters => parameters
			.Add(p => p.Body, (RenderFragment)(builder => builder.AddMarkupContent(0, "<p>routed page</p>"))));

		component.WaitForAssertion(() =>
			nav.Uri.EndsWith($"group-rides/live/{Ride}", StringComparison.Ordinal).ShouldBeTrue(
				$"§18.6: an app the OS reclaimed mid-ride must come back on the ride; got '{nav.Uri}'."),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task LaunchedThroughTheSignInBounce_ReopensTheAdventureWhenItLands()
	{
		BunitNavigationManager nav = await LaunchMidRideAsync(RideStateDto.Live);

		// The cold launch this whole thing exists for: Home is [Authorize] and the session is still
		// being restored, so the router has already sent the rider to Welcome.
		nav.NavigateTo("welcome");

		IRenderedComponent<MainLayout> component = Render<MainLayout>(parameters => parameters
			.Add(p => p.Body, (RenderFragment)(builder => builder.AddMarkupContent(0, "<p>routed page</p>"))));

		LocationBroadcastState broadcast = Services.GetRequiredService<LocationBroadcastState>();

		component.WaitForAssertion(() =>
			broadcast.Rides.Contains(Ride).ShouldBeTrue(
				"§5.7: the GPS half is owed to the rider wherever the launch landed."),
			timeout: TimeSpan.FromSeconds(3));

		// Lets the layout's own continuation finish on the renderer's dispatcher before the hop.
		await component.InvokeAsync(() => { });

		nav.Uri.EndsWith("welcome", StringComparison.Ordinal).ShouldBeTrue(
			"nothing may move the rider off Welcome while the session is still landing — its own "
			+ "'you are signed in, go home' is already queued and would overtake it.");

		// And now Welcome sends them home, which is the launch actually landing.
		nav.NavigateTo("/");

		component.WaitForAssertion(() =>
			nav.Uri.EndsWith($"group-rides/live/{Ride}", StringComparison.Ordinal).ShouldBeTrue(
				$"§18.6: the adventure is reopened once the bounce lands; got '{nav.Uri}'."),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task LaunchedOntoAScreenOfItsOwn_TheAdventureDoesNotTakeIt()
	{
		BunitNavigationManager nav = await LaunchMidRideAsync(RideStateDto.Live);
		nav.NavigateTo("settings");
		string asked = nav.Uri;

		IRenderedComponent<MainLayout> component = Render<MainLayout>(parameters => parameters
			.Add(p => p.Body, (RenderFragment)(builder => builder.AddMarkupContent(0, "<p>routed page</p>"))));

		component.WaitForAssertion(() =>
			component.Markup.Contains("routed page", StringComparison.Ordinal).ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		nav.Uri.ShouldBe(asked,
			"a launch aimed at a screen — a tapped notification, a shared link, a reloaded deep "
			+ "link — keeps its destination; only the home route means \"wherever I was\".");

		// And it does not lie in wait either: the rider walking to Home later is them choosing
		// Home, not the launch finally landing.
		nav.NavigateTo("/");

		component.WaitForAssertion(() =>
			component.Markup.Contains("routed page", StringComparison.Ordinal).ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		nav.Uri.EndsWith("/", StringComparison.Ordinal).ShouldBeTrue(
			$"a launch that lost its claim on the screen must not take it back; got '{nav.Uri}'.");
	}

	[Fact]
	public void Body_IsRenderedByTheLayout()
	{
		WireServices();

		IRenderedComponent<MainLayout> component = Render<MainLayout>(parameters => parameters
			.Add(p => p.Body, (RenderFragment)(builder => builder.AddMarkupContent(0, "<p>routed page</p>"))));

		component.WaitForAssertion(() =>
			component.Markup.Contains("routed page", StringComparison.Ordinal).ShouldBeTrue(
				"the layout's Body slot must render whatever the router pushes into it."),
			timeout: TimeSpan.FromSeconds(3));
	}
}
