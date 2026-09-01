using BlazorDLR.Shared.Pages;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Contracts.Tracks;
using DLR.UI.Tests.Fakes;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// The signed-in tracks list. Three states matter: loading, empty (with a call to
/// import) and populated. §8's numbers-vs-null rule leans on this page - a null
/// ascent must render as "-", not as "0", because zero ascent is a real value.
/// </summary>
public sealed class MyRidesTests : PageTestContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private FakeTrackRepository WireServices()
	{
		FakeTrackRepository repo = new();
		Services.AddSingleton<ITrackRepository>(repo);

		// The browse tab talks to IApiClient directly rather than through the repository - the
		// repository is the offline seam and there is no offline answer to "what did strangers
		// publish today" (§18.6). Registered for every test in here regardless of which tab it
		// looks at, because Blazor injects the property before either tab renders.
		Services.AddSingleton<IApiClient>(Api);

		return repo;
	}

	/// <summary>The fake behind the shared tab. One per test; the class is instantiated per test.</summary>
	private FakeApiClient Api { get; } = new();

	private static SharedTrackSummary Shared(string name, string owner = "riley", double? awayKm = null, string? description = null) =>
		new(
			Guid.NewGuid(),
			name,
			description,
			PhotoId: null,
			owner,
			DistanceM: 42_000,
			AscentM: 300,
			SharedUtc: FixedInstant,
			CentreLat: -34.9,
			CentreLon: 138.6,
			awayKm);

	[Fact]
	public void EmptyList_ShowsImportCallToAction()
	{
		WireServices();

		IRenderedComponent<MyRides> component = Render<MyRides>();

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("No adventures yet", StringComparison.Ordinal).ShouldBeTrue(
				"an empty list must call out the import path - a blank table is not an answer.");
			component.FindAll("a[href='/import']").ShouldNotBeEmpty(
				"the Import GPX button is always visible, empty state or not - importing is the primary way adventures land here in Phase 1.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void PopulatedList_RendersEachTrackRow()
	{
		FakeTrackRepository repo = WireServices();
		repo.Tracks.Add(new TrackSummary(
			Id: Guid.NewGuid(),
			Name: "Sunday morning gravel",
			CreatedUtc: FixedInstant,
			StartedUtc: null,
			EndedUtc: null,
			DistanceM: 25_000,
			DurationS: 3600,
			AscentM: 480,
			MaxSpeedMps: null,
			PointCount: 500,
			SegmentCount: 1,
			Source: TrackSourceDto.Imported,
			Version: 1));
		repo.Tracks.Add(new TrackSummary(
			Id: Guid.NewGuid(),
			Name: "Recorded loop",
			CreatedUtc: FixedInstant.AddDays(-1),
			StartedUtc: null,
			EndedUtc: null,
			DistanceM: 12_000,
			DurationS: 1800,
			AscentM: null,
			MaxSpeedMps: null,
			PointCount: 300,
			SegmentCount: 1,
			Source: TrackSourceDto.Recorded,
			Version: 1));

		IRenderedComponent<MyRides> component = Render<MyRides>();

		component.WaitForAssertion(() =>
		{
			string markup = component.Markup;
			markup.Contains("Sunday morning gravel", StringComparison.Ordinal).ShouldBeTrue();
			markup.Contains("Recorded loop", StringComparison.Ordinal).ShouldBeTrue();
			markup.Contains("Imported", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(
				"the source badge distinguishes imports from recordings - §15.4.");
			// §8: null ascent renders as an em dash, not as zero.
			markup.Contains("-", StringComparison.Ordinal).ShouldBeTrue(
				"§8: a null number renders as '-'. Zero ascent (dead flat) would render as '0 m'.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void ErrorState_ShowsMessageAndRetryButton()
	{
		FakeTrackRepository repo = WireServices();
		repo.ListException = new InvalidOperationException("network hiccup");

		IRenderedComponent<MyRides> component = Render<MyRides>();

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("network hiccup", StringComparison.Ordinal).ShouldBeTrue(
				"the exception message travels to the DOM - a bare 'could not load' is not diagnosable.");
			component.FindAll("button").Any(b => b.TextContent.Contains("Retry", StringComparison.Ordinal))
				.ShouldBeTrue("a transient error is a retryable event - the button offers the retry.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void SharedRouteInMyList_IsBadgedAsShared()
	{
		FakeTrackRepository repo = WireServices();
		repo.Tracks.Add(new TrackSummary(
			Guid.NewGuid(), "Coast run", FixedInstant, null, null, 82_000, null, 900, null, 900, 1,
			TrackSourceDto.Recorded, 1, Visibility: TrackVisibilityDto.Public));

		IRenderedComponent<MyRides> component = Render<MyRides>();

		component.WaitForAssertion(
			() => component.FindAll(".badge.shared").ShouldNotBeEmpty(
				"whether other people can see a route belongs on the row that lists it, not only "
				+ "on the screen that set it."),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void MyList_DoesNotBadgePrivateRoutes()
	{
		FakeTrackRepository repo = WireServices();
		repo.Tracks.Add(new TrackSummary(
			Guid.NewGuid(), "Private loop", FixedInstant, null, null, 12_000, null, null, null, 300, 1,
			TrackSourceDto.Recorded, 1));

		IRenderedComponent<MyRides> component = Render<MyRides>();

		component.WaitForAssertion(
			() => component.FindAll(".badge.shared").ShouldBeEmpty(
				"private is where every track starts, and a badge on every row would say nothing."),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void SharedTab_ListsOtherRidersRoutesWithOwnerAndBlurb()
	{
		WireServices();
		Api.SharedTracks.Add(Shared("Adelaide Hills loop", owner: "riley", description: "Broken tarmac after the second bridge."));

		IRenderedComponent<MyRides> component = RenderSharedTab();

		component.WaitForAssertion(() =>
		{
			string markup = component.Markup;
			markup.Contains("Adelaide Hills loop", StringComparison.Ordinal).ShouldBeTrue();
			markup.Contains("by riley", StringComparison.Ordinal).ShouldBeTrue(
				"a shared route with no name against it is a route from nobody (§7.3).");
			markup.Contains("Broken tarmac", StringComparison.Ordinal).ShouldBeTrue(
				"the description is what a browse row is being read for.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void SharedTab_NameFilterReachesTheServer()
	{
		WireServices();
		Api.SharedTracks.Add(Shared("Coast run north"));
		Api.SharedTracks.Add(Shared("Hills loop"));

		IRenderedComponent<MyRides> component = RenderSharedTab();

		component.WaitForAssertion(() => component.FindAll(".shared-list li").Count.ShouldBe(2),
			timeout: TimeSpan.FromSeconds(3));

		component.Find(".filters input").Input("coast");
		component.Find(".filters button[type=submit]").Click();

		component.WaitForAssertion(() =>
		{
			// The filter is the server's job, not the list's. What this asserts is that the text
			// actually travelled - a client-side filter would pass the row count check below
			// while leaving the other 4 000 rows on the server unfiltered.
			Api.SharedTrackQueries.ShouldContain(query => query.Name == "coast");
			component.FindAll(".shared-list li").Count.ShouldBe(1);
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void SharedTab_SearchingResetsToTheFirstPage()
	{
		WireServices();

		for (int index = 0; index < SharedTrackQuery.PageSize + 5; index++)
		{
			Api.SharedTracks.Add(Shared($"Route {index}"));
		}

		IRenderedComponent<MyRides> component = RenderSharedTab();

		component.WaitForAssertion(() => component.FindAll(".shared-list li").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		component.FindAll(".pager button").Last().Click();

		component.WaitForAssertion(() => Api.SharedTrackQueries.Last().Page.ShouldBe(2),
			timeout: TimeSpan.FromSeconds(3));

		component.Find(".filters input").Input("Route 1");
		component.Find(".filters button[type=submit]").Click();

		// Narrowing the filter while on page 2 must not leave the reader on a page the new
		// result set may not have - the classic way a filtered list comes back empty for no
		// reason the reader can see.
		component.WaitForAssertion(() => Api.SharedTrackQueries.Last().Page.ShouldBe(1),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void SharedTab_PagerCountsPagesAndStopsAtBothEnds()
	{
		WireServices();

		for (int index = 0; index < SharedTrackQuery.PageSize + 1; index++)
		{
			Api.SharedTracks.Add(Shared($"Route {index}"));
		}

		IRenderedComponent<MyRides> component = RenderSharedTab();

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("Page 1 of 2", StringComparison.Ordinal).ShouldBeTrue(
				"a cursor cannot say how many pages there are, which is why this list is numbered.");

			// Previous is off on page one. Next is not, because there is a page two.
			component.FindAll(".pager button").First().HasAttribute("disabled").ShouldBeTrue();
			component.FindAll(".pager button").Last().HasAttribute("disabled").ShouldBeFalse();
		}, timeout: TimeSpan.FromSeconds(3));

		component.FindAll(".pager button").Last().Click();

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("Page 2 of 2", StringComparison.Ordinal).ShouldBeTrue();
			component.FindAll(".pager button").Last().HasAttribute("disabled").ShouldBeTrue(
				"there is no page three, so Next has nowhere to go.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void SharedTab_RadiusWithNoCentreSaysSoRatherThanFilteringOnNothing()
	{
		WireServices();
		Api.SharedTracks.Add(Shared("Coast run north"));

		IRenderedComponent<MyRides> component = RenderSharedTab();

		component.WaitForAssertion(() => component.FindAll(".shared-list li").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		component.Find(".filters select").Change("50");

		component.WaitForAssertion(() =>
		{
			component.Find(".anchor-note").TextContent.Contains("Pick a point first", StringComparison.Ordinal)
				.ShouldBeTrue("a radius with no centre is not a narrower search, it is an unanswerable one.");

			// And nothing half-formed reached the server: no lat, no lon, no radius.
			Api.SharedTrackQueries.ShouldAllBe(query => !query.HasArea);
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void SharedTab_ErrorShowsTheMessageAndOffersRetry()
	{
		WireServices();
		Api.ListSharedTracksException = new InvalidOperationException("network hiccup");

		IRenderedComponent<MyRides> component = RenderSharedTab();

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("network hiccup", StringComparison.Ordinal).ShouldBeTrue();
			component.FindAll("button").Any(button => button.TextContent.Contains("Retry", StringComparison.Ordinal))
				.ShouldBeTrue();
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void UseMyLocation_ShowsPlaysDisclosureBeforeItTouchesThePlatform()
	{
		// The route Play found in 8.0.0.28. One fix for a search box still climbs the whole Android
		// permission ladder - background rung included - so the app's own words have to come first
		// here as much as they do in front of a broadcast (§4.3).
		WireServices();

		FakeLocationProvider gps = new();
		Services.AddSingleton<ILocationProvider>(gps);

		// Over the scoped store AddRideMapServices binds, so the disclosure below can be a
		// singleton - the shape every other suite that wires a receiver uses.
		Services.AddSingleton<IDeviceSettings>(new InMemoryDeviceSettings());
		Services.AddSingleton<ConfirmService>();
		Services.AddSingleton<LocationDisclosure>();

		IRenderedComponent<MyRides> component = RenderSharedTab();

		component.WaitForAssertion(
			() => component.FindAll("button").Any(button => button.TextContent.Contains("Use my location", StringComparison.Ordinal))
				.ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		component
			.FindAll("button")
			.First(button => button.TextContent.Contains("Use my location", StringComparison.Ordinal))
			.Click();

		ConfirmService confirm = Services.GetRequiredService<ConfirmService>();

		component.WaitForAssertion(() => confirm.Current.ShouldNotBeNull(), timeout: TimeSpan.FromSeconds(3));

		confirm.Current!.Message.ShouldContain("collects location data");
		gps.PermissionAsks.ShouldBe(0, "the system dialog may not be reached before the disclosure is answered.");

		confirm.Respond(true);

		component.WaitForAssertion(
			() => gps.PermissionAsks.ShouldBe(1, "agreeing lets the platform be asked, which is the point of asking."),
			timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// Renders the page on the shared tab, which is a query-string state rather than a field -
	/// see the note on <c>MyRides.TabName</c> for why.
	/// </summary>
	private IRenderedComponent<MyRides> RenderSharedTab()
	{
		// Navigated to rather than passed as a parameter, because that is what the query string
		// is: bUnit refuses to set a [SupplyParameterFromQuery] directly, and rightly - a test
		// that could would be testing a state the browser cannot produce.
		Services.GetRequiredService<NavigationManager>().NavigateTo("/rides?tab=shared");

		return Render<MyRides>();
	}
}
