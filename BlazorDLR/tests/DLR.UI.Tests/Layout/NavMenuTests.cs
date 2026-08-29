using BlazorDLR.Shared.Layout;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Contracts.Comments;
using DLR.Core.Contracts.Identity;
using DLR.UI.Tests.Fakes;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Layout;

/// <summary>
/// The one navigation surface both hosts share. §7.9 says the unauthenticated landing
/// is Welcome — everything else redirects there — so the nav's job is to expose the
/// signed-in surface only when the user is signed in, and to expose Welcome when the
/// user is not. Getting this wrong on either side would either give an anonymous
/// caller broken links or hide the app from a signed-in one.
/// </summary>
public sealed class NavMenuTests : BunitContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private readonly InMemoryDeviceSettings _settings = new();
	private readonly FakeRideHubClient _hub = new();

	private AuthState WireAuth()
	{
		FakeApiClient api = new();
		FakeTokenStore tokens = new();
		FakeTimeProvider clock = new(FixedInstant);
		AuthState auth = new(api, tokens, clock);
		Services.AddSingleton<IApiClient>(api);
		Services.AddSingleton<ITokenStore>(tokens);
		Services.AddSingleton<TimeProvider>(clock);
		Services.AddSingleton(auth);
		Services.AddSingleton<AuthenticationStateProvider>(auth);
		Services.AddRealAuthorizationPipeline();

		// The globe's destination comes off the device store (§18.6) — the in-memory stand-in,
		// as everywhere else in these tests.
		Services.AddSingleton<IDeviceSettings>(_settings);
		Services.AddSingleton<CurrentRideState>();

		// The thread item's unread count (§17.6), which the rail draws from posts arriving on the
		// hub — so a nav rendered without one would not compile a badge at all.
		Services.AddSingleton<IRideHubClient>(_hub);
		Services.AddSingleton<UnreadThreadState>();

		this.CascadeAuthenticationState(auth);
		return auth;
	}

	private async Task<AuthState> SignInAsync()
	{
		AuthState auth = WireAuth();
		await auth.ApplySessionAsync(new TokenResponse(
			AccessToken: "access",
			ExpiresIn: 900,
			RefreshToken: "refresh",
			User: new AuthenticatedUser(Guid.NewGuid(), "DaveSmith", HasEmail: true, EmailConfirmed: true)));
		this.CascadeAuthenticationState(auth);
		return auth;
	}

	[Fact]
	public void Anonymous_ShowsOnlyWelcomeLink()
	{
		WireAuth();

		IRenderedComponent<NavMenu> component = Render<NavMenu>();

		component.WaitForAssertion(() =>
		{
			component.FindAll("a[href='welcome']").ShouldNotBeEmpty(
				"§7.9: an anonymous nav must lead to Welcome — that is the only signed-out destination.");
			component.FindAll("a[href='settings']").Count.ShouldBe(0,
				"§7.9: the signed-in surface must not appear on an anonymous nav — its links are dead until sign-in.");
			component.FindAll("a[href='group-rides']").Count.ShouldBe(0,
				"the globe, the traveller list and the thread all fall back to the group adventures "
				+ "list, so an anonymous rail must not carry that either.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Rail_EveryLink_CarriesAFontAwesomeGlyph_AndAnAccessibleName()
	{
		// The glyph is a font class: invisible to a screen reader, and absent entirely if
		// the stylesheet fails to load. With no visible caption beside it, aria-label and
		// title are the whole of the destination's accessible name.
		await SignInAsync();

		IRenderedComponent<NavMenu> component = Render<NavMenu>();

		component.WaitForAssertion(() =>
		{
			AngleSharp.Dom.IElement[] links = component.FindAll("a.rail-item").ToArray();
			links.Length.ShouldBe(5, "§18.6: the signed-in rail carries exactly five destinations.");
			foreach (AngleSharp.Dom.IElement link in links)
			{
				link.HasAttribute("aria-label").ShouldBeTrue(
					"a font glyph has no accessible name of its own — the anchor must carry one.");
				link.HasAttribute("title").ShouldBeTrue(
					"a mouse hover surfaces the destination name via the title attribute.");

				AngleSharp.Dom.IElement glyph = link.QuerySelectorAll("i").ShouldHaveSingleItem();
				glyph.ClassList.ShouldContain("fa",
					"the rail is drawn with Font Awesome — `fa` is what resolves the family and the solid weight.");
				glyph.ClassList.Any(name => name.StartsWith("fa-", StringComparison.Ordinal)
					&& name != "fa-fw").ShouldBeTrue("each item names an actual icon, not just the fixed-width modifier.");
				glyph.GetAttribute("aria-hidden").ShouldBe("true",
					"the glyph is decoration; announcing it would read the destination name twice.");

				link.TextContent.Trim().ShouldBeEmpty(
					"the rail is glyph-only. Which is exactly why the two attributes above are not "
					+ "optional — with no caption, they are the only name the destination has.");
			}
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Authenticated_ShowsFullSignedInSurface_NotWelcome()
	{
		// Sign in a synthetic session so AuthorizeView reaches the Authorized branch.
		await SignInAsync();

		IRenderedComponent<NavMenu> component = Render<NavMenu>();

		component.WaitForAssertion(() =>
		{
			// Every signed-in destination is present.
			component.FindAll("a[href='']").ShouldNotBeEmpty("Home link (href='') is the root the signed-in nav opens with.");
			component.FindAll("a[href='group-rides']").ShouldNotBeEmpty();
			component.FindAll("a[href='settings']").ShouldNotBeEmpty();

			// And the Welcome link is gone — a signed-in user has no reason for it.
			component.FindAll("a[href='welcome']").Count.ShouldBe(0,
				"§7.9: the Welcome link is the signed-out entry point and disappears once the user signs in.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>The globe, found by its glyph — one of the three rail items whose href moves.</summary>
	private static AngleSharp.Dom.IElement Globe(IRenderedComponent<NavMenu> component) =>
		component.FindAll("a.rail-item").Single(link => link.QuerySelector("i.fa-globe") is not null);

	/// <summary>The rider list, found the same way. It shares the globe's two destinations.</summary>
	private static AngleSharp.Dom.IElement Members(IRenderedComponent<NavMenu> component) =>
		component.FindAll("a.rail-item").Single(link => link.QuerySelector("i.fa-users") is not null);

	/// <summary>The adventure thread, found the same way, and on the same two destinations.</summary>
	private static AngleSharp.Dom.IElement Thread(IRenderedComponent<NavMenu> component) =>
		component.FindAll("a.rail-item").Single(link => link.QuerySelector("i.fa-comments") is not null);

	[Fact]
	public async Task Members_OnADeviceWithNoRide_LeadsToTheGroupRidesList()
	{
		// There are no members to list until a ride has been opened, so the item falls back to
		// where the globe does — and has to say so rather than promising a list and opening a
		// chooser.
		await SignInAsync();

		IRenderedComponent<NavMenu> component = Render<NavMenu>();

		component.WaitForAssertion(() =>
		{
			AngleSharp.Dom.IElement members = Members(component);
			members.GetAttribute("href").ShouldBe("group-rides");
			members.GetAttribute("aria-label").ShouldBe("Pick a group adventure");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Members_AfterARideWasOpened_LeadsToThatRidesRiderList()
	{
		// The slot the group rides list used to hold. That list is still on the Home screen and
		// is not something anybody opens twice in a ride; "where is everyone" is (§18.6).
		Guid rideId = Guid.NewGuid();
		await _settings.SetAsync(CurrentRideState.StorageKey, rideId.ToString("N"));

		await SignInAsync();

		IRenderedComponent<NavMenu> component = Render<NavMenu>();

		component.WaitForAssertion(() =>
		{
			AngleSharp.Dom.IElement members = Members(component);
			members.GetAttribute("href").ShouldBe($"group-rides/members/{rideId}");
			members.GetAttribute("aria-label").ShouldBe("Live members");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Globe_OnADeviceWithNoRide_LeadsToTheGroupRidesList()
	{
		// Nothing written to the device store: the rider has not opened a ride on this device, so
		// the one destination that makes sense is the list they pick one from. An item that led
		// nowhere — or was absent until a ride existed — would be a rail that changes shape.
		await SignInAsync();

		IRenderedComponent<NavMenu> component = Render<NavMenu>();

		component.WaitForAssertion(() =>
		{
			AngleSharp.Dom.IElement globe = Globe(component);
			globe.GetAttribute("href").ShouldBe("group-rides");
			globe.GetAttribute("aria-label").ShouldBe("Pick a group adventure",
				"the rail is glyph-only, so the name has to say which of the globe's two destinations is in force.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Globe_AfterARideWasOpened_LeadsBackToThatRide()
	{
		// The app restarting is exactly this: a device store that already holds a ride, and a rail
		// rendered cold with no navigation to learn it from.
		Guid rideId = Guid.NewGuid();
		await _settings.SetAsync(CurrentRideState.StorageKey, rideId.ToString("N"));

		await SignInAsync();

		IRenderedComponent<NavMenu> component = Render<NavMenu>();

		component.WaitForAssertion(() =>
		{
			AngleSharp.Dom.IElement globe = Globe(component);
			globe.GetAttribute("href").ShouldBe($"group-rides/live/{rideId}",
				"§18.6: the globe is the one-tap way back to the adventure, including after a restart.");
			globe.GetAttribute("aria-label").ShouldBe("Current adventure");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Globe_FollowsARideOpenedWhileTheRailIsOnScreen()
	{
		// The rail is in MainLayout and outlives every page under it, so the ride is picked up
		// from the shared state's broadcast rather than from a re-render of the nav itself.
		await SignInAsync();

		IRenderedComponent<NavMenu> component = Render<NavMenu>();
		component.WaitForAssertion(() => Globe(component).GetAttribute("href").ShouldBe("group-rides"));

		Guid rideId = Guid.NewGuid();
		await Services.GetRequiredService<CurrentRideState>().SetAsync(rideId);

		component.WaitForAssertion(
			() => Globe(component).GetAttribute("href").ShouldBe($"group-rides/live/{rideId}"),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Thread_OnADeviceWithNoRide_LeadsToTheGroupRidesList()
	{
		// No adventure means no conversation about one, so the item falls back where the globe and
		// the traveller list do — and says so, rather than promising a thread and opening a chooser.
		await SignInAsync();

		IRenderedComponent<NavMenu> component = Render<NavMenu>();

		component.WaitForAssertion(() =>
		{
			AngleSharp.Dom.IElement thread = Thread(component);
			thread.GetAttribute("href").ShouldBe("group-rides");
			thread.GetAttribute("aria-label").ShouldBe("Pick a group adventure");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Thread_AfterARideWasOpened_LeadsToThatRidesThread()
	{
		// The slot Import GPX used to hold. Importing is a once-per-route thing done at a desk and
		// still a Home card; what a rider checks over and over mid-ride is what the group is saying.
		Guid rideId = Guid.NewGuid();
		await _settings.SetAsync(CurrentRideState.StorageKey, rideId.ToString("N"));

		await SignInAsync();

		IRenderedComponent<NavMenu> component = Render<NavMenu>();

		component.WaitForAssertion(() =>
		{
			AngleSharp.Dom.IElement thread = Thread(component);
			thread.GetAttribute("href").ShouldBe($"group-rides/thread/{rideId}");
			thread.GetAttribute("aria-label").ShouldBe("Adventure thread");
			thread.QuerySelector(".count-badge").ShouldBeNull(
				"a badge is a claim that something is waiting; one on a thread nobody has posted to "
				+ "is a permanent decoration the rider learns to stop seeing.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Thread_WhenAPostArrives_WearsTheCount_AndSaysItInTheName()
	{
		// The rail is on screen and the thread is not — a rider on the live map, which is where
		// they are for most of a ride. Nothing else on any screen says the group is talking.
		Guid rideId = Guid.NewGuid();
		await _settings.SetAsync(CurrentRideState.StorageKey, rideId.ToString("N"));

		await SignInAsync();

		IRenderedComponent<NavMenu> component = Render<NavMenu>();
		component.WaitForAssertion(() => Thread(component).GetAttribute("href").ShouldBe($"group-rides/thread/{rideId}"));

		_hub.RaiseCommentPosted(Post(rideId, Guid.NewGuid()));
		_hub.RaiseCommentPosted(Post(rideId, Guid.NewGuid()));

		component.WaitForAssertion(() =>
		{
			AngleSharp.Dom.IElement thread = Thread(component);
			thread.QuerySelector(".count-badge").ShouldNotBeNull().TextContent.Trim().ShouldBe("2");

			// The rail is glyph-only, so the anchor's name is the whole of what a screen reader
			// gets — and a link's name replaces anything inside it, badge included.
			thread.GetAttribute("aria-label").ShouldBe("Adventure thread, 2 unread");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Thread_DoesNotCountTheRidersOwnPosts()
	{
		// Every post a rider makes comes straight back down the hub they published it on, so
		// without the author check the badge would count their own half of the conversation.
		Guid rideId = Guid.NewGuid();
		await _settings.SetAsync(CurrentRideState.StorageKey, rideId.ToString("N"));

		AuthState auth = await SignInAsync();

		IRenderedComponent<NavMenu> component = Render<NavMenu>();
		component.WaitForAssertion(() => Thread(component).GetAttribute("href").ShouldBe($"group-rides/thread/{rideId}"));

		_hub.RaiseCommentPosted(Post(rideId, auth.UserId!.Value));

		component.WaitForAssertion(
			() => Thread(component).QuerySelector(".count-badge").ShouldBeNull(),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Thread_AfterARestart_StillWearsWhatWasNotRead()
	{
		// The case the counts are persisted for: a phone the OS reclaimed mid-ride relaunches on
		// the home screen, and the two posts the rider had not read are still two posts.
		Guid rideId = Guid.NewGuid();
		await _settings.SetAsync(CurrentRideState.StorageKey, rideId.ToString("N"));
		await _settings.SetAsync(UnreadThreadState.StorageKey, $"1|{rideId:N}:2");

		await SignInAsync();

		IRenderedComponent<NavMenu> component = Render<NavMenu>();

		component.WaitForAssertion(
			() => Thread(component).QuerySelector(".count-badge").ShouldNotBeNull().TextContent.Trim().ShouldBe("2"),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Rail_PutsTheGpsSwitchAfterEveryDestination()
	{
		// Order is the whole of the placement rule: last child is the foot of the left rail and the
		// far right of the bar along the bottom, which is the corner a thumb reaches without leaving
		// the bars. Nothing in the CSS moves it — see MainLayout.razor.css.
		await SignInAsync();
		Services.AddSingleton<ILocationProvider>(new FakeLocationProvider());
		Services.AddSingleton<ConfirmService>();
		Services.AddSingleton<PrivateAreaState>();
		Services.AddSingleton<LocationUpdateRateState>();
		Services.AddSingleton<TrackRecordingState>();
		Services.AddSingleton<LocationBroadcastState>();

		IRenderedComponent<NavMenu> component = Render<NavMenu>();

		component.WaitForAssertion(
			() =>
			{
				AngleSharp.Dom.IElement[] items = component.FindAll(".rail-item").ToArray();
				items.Length.ShouldBe(6, "the five destinations, and the switch after them.");
				items[^1].ClassList.ShouldContain("gps-switch");
				items[^1].TagName.ShouldBe("BUTTON",
					"it does something rather than going somewhere — which is also why the rail still has five links.");
			},
			timeout: TimeSpan.FromSeconds(3));
	}

	private static CommentDto Post(Guid rideId, Guid authorId) =>
		new(
			Id: Guid.NewGuid(),
			GroupRideId: rideId,
			TrackId: null,
			AuthorId: authorId,
			AuthorUserName: "SarahJones",
			Kind: CommentKindDto.Text,
			Body: "Fuel at the servo in 8 km.",
			PhotoId: null,
			IsPinned: false,
			CreatedUtc: FixedInstant,
			PostedUtc: FixedInstant,
			EditedUtc: null,
			AuthoredEarlier: false);
}
