using System.Globalization;
using AngleSharp.Dom;
using BlazorDLR.Shared.Pages;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using Bunit;
using Bunit.TestDoubles;
using DLR.UI.Tests.Components;
using DLR.UI.Tests.Fakes;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// The introduction slide show (§18.6). What matters is not the copy - that is placeholder and
/// will be rewritten - but the four things a rider is entitled to on a screen that opens by
/// itself:
/// <list type="bullet">
///   <item>it shows one card at a time and pages forward,</item>
///   <item>it can be left from the first frame,</item>
///   <item>leaving it, however, stops it opening by itself again, and</item>
///   <item>where it lands afterwards is a route inside this app and nowhere else.</item>
/// </list>
/// <para>
/// In <see cref="Components.SourceOfferFooterCollection"/> because this page carries the AGPL
/// source offer, whose About response lives in a static cache - a footer rendered here while the
/// footer's own suite is mid-test is a value that suite never wired. Nothing in this file looks
/// at the footer; it is the write that has to be kept out of the way, not the assertion.
/// </para>
/// </summary>
public sealed class IntroTests : PageTestContext
{
	private InMemoryDeviceSettings _settings = new();

	private IntroTourState WireServices()
	{
		// The page focuses its own surface on first render so the arrow keys work without a click
		// first. That is a real JS interop call, and this suite is not about it.
		JSInterop.Mode = JSRuntimeMode.Loose;

		// The AGPL source offer at the foot of this page fetches /about (§14.6.2).
		Services.AddSingleton<IApiClient>(new FakeApiClient());

		_settings = new InMemoryDeviceSettings();
		IntroTourState tour = new(_settings);
		Services.AddSingleton<IDeviceSettings>(_settings);
		Services.AddSingleton(tour);
		return tour;
	}

	private static BunitNavigationManager Nav(IRenderedComponent<Intro> component) =>
		component.Services.GetRequiredService<NavigationManager>() as BunitNavigationManager
			?? throw new InvalidOperationException("bUnit did not register a BunitNavigationManager.");

	private static IElement Next(IRenderedComponent<Intro> component) =>
		component.Find(".intro-next");

	/// <summary>
	/// Puts the deck on the URL the launch redirect would have built. A
	/// <c>[SupplyParameterFromQuery]</c> parameter cannot be handed over as a component parameter
	/// - it is bound from the address, so the address is what a test has to set.
	/// </summary>
	private void OpenWithReturn(string route)
	{
		BunitNavigationManager nav = Services.GetRequiredService<NavigationManager>() as BunitNavigationManager
			?? throw new InvalidOperationException("bUnit did not register a BunitNavigationManager.");
		nav.NavigateTo($"/intro?return={Uri.EscapeDataString(route)}");
	}

	[Fact]
	public void TheFirstCard_IsTheFirstSlideAndOnlyThatOne()
	{
		WireServices();

		IRenderedComponent<Intro> component = Render<Intro>();

		component.Find("h1").TextContent.Trim().ShouldBe(IntroTour.Slides[0].Title);
		// The card, not the whole page: every dot carries its slide's title as a tooltip, which is
		// what makes a row of circles usable at all.
		component.Find(".intro-card").TextContent.Contains(IntroTour.Slides[1].Title, StringComparison.Ordinal)
			.ShouldBeFalse("a slide show renders one card; rendering the deck at once is a page, not an introduction.");
		component.Find(".intro-count").TextContent.Trim()
			.ShouldBe($"1 of {IntroTour.Slides.Count}", StringCompareShould.IgnoreCase);
	}

	[Fact]
	public void Next_PagesForwardThroughTheDeck()
	{
		WireServices();

		IRenderedComponent<Intro> component = Render<Intro>();
		Next(component).Click();

		component.Find("h1").TextContent.Trim().ShouldBe(IntroTour.Slides[1].Title);
	}

	[Fact]
	public void Back_IsDisabledOnTheFirstCardAndReturnsFromTheSecond()
	{
		WireServices();

		IRenderedComponent<Intro> component = Render<Intro>();
		component.Find(".intro-back").HasAttribute("disabled").ShouldBeTrue(
			"there is nothing behind the first card, and a live control that does nothing is a bug report.");

		Next(component).Click();
		component.Find(".intro-back").Click();

		component.Find("h1").TextContent.Trim().ShouldBe(IntroTour.Slides[0].Title);
	}

	[Fact]
	public void ADot_JumpsStraightToItsCard()
	{
		WireServices();

		IRenderedComponent<Intro> component = Render<Intro>();
		component.FindAll(".intro-dot")[^1].Click();

		component.Find("h1").TextContent.Trim().ShouldBe(IntroTour.Slides[^1].Title);
	}

	[Fact]
	public void TheLastCard_OffersTheWayOutRatherThanAnotherNext()
	{
		WireServices();

		IRenderedComponent<Intro> component = Render<Intro>();
		component.FindAll(".intro-dot")[^1].Click();

		Next(component).TextContent.Trim().ShouldBe("Get started");
	}

	[Fact]
	public async Task Skipping_LeavesAndStopsItOpeningAgain()
	{
		IntroTourState tour = WireServices();

		IRenderedComponent<Intro> component = Render<Intro>();
		component.Find(".intro-skip").Click();

		// A skip is a rider saying they do not want this screen. Asking again on the next launch
		// answers them with the same screen.
		// No polling: bUnit's Click waits on the handler, and the handler writes the device and
		// navigates before it returns.
		Nav(component).Uri.EndsWith("/", StringComparison.Ordinal).ShouldBeTrue(
			$"skipping must land on the app's home route; got '{Nav(component).Uri}'.");

		(await new IntroTourState(_settings).ShouldShowAsync()).ShouldBeFalse();
	}

	[Fact]
	public async Task Finishing_MarksTheDeckSeenAtTheVersionThatWasShown()
	{
		WireServices();

		IRenderedComponent<Intro> component = Render<Intro>();
		component.FindAll(".intro-dot")[^1].Click();
		Next(component).Click();

		Nav(component).Uri.EndsWith("/", StringComparison.Ordinal).ShouldBeTrue();

		(await _settings.GetAsync(IntroTourState.StorageKey))
			.ShouldBe(IntroTour.Version.ToString(CultureInfo.InvariantCulture));
	}

	[Fact]
	public void AReturnRoute_IsWhereItLandsAfterwards()
	{
		// The launch redirect carries this so a first run that opened on a shared invitation still
		// gets to the invitation once the deck is out of the way.
		WireServices();

		OpenWithReturn("/group-rides/join/ABCDEF");

		IRenderedComponent<Intro> component = Render<Intro>();
		component.Find(".intro-skip").Click();

		Nav(component).Uri.EndsWith("/group-rides/join/ABCDEF", StringComparison.Ordinal).ShouldBeTrue(
			$"the deck must hand the rider back to where they were headed; got '{Nav(component).Uri}'.");
	}

	[Fact]
	public void AnOffsiteReturnRoute_IsIgnored()
	{
		// ReturnTo arrives off the query string, so it is whatever was in the URL that opened this
		// screen. A protocol-relative value there would make the last button of the app's welcome
		// mat an open redirect.
		WireServices();

		OpenWithReturn("//example.com/phish");

		IRenderedComponent<Intro> component = Render<Intro>();
		component.Find(".intro-skip").Click();

		Nav(component).Uri.Contains("example.com", StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
			$"an off-site return must be dropped for home; got '{Nav(component).Uri}'.");
	}
}
