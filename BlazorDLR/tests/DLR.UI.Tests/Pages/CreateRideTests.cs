using System.Net;
using BlazorDLR.Shared.Pages.GroupRides;
using BlazorDLR.Shared.Services;
using Bunit;
using DLR.Core.Contracts.Rides;
using DLR.Core.Contracts.Tracks;
using DLR.UI.Tests.Fakes;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// §5.2's two-policy switch. The composer offers the organiser <em>Approval</em> or
/// <em>Open</em> and defaults to Approval — the safer of the two, because someone with
/// a bare code cannot join without the organiser deciding. The chosen policy has to
/// reach the API as-selected, and the default-start time has to be driven by
/// <see cref="TimeProvider"/> (§10.4) so tests advance a fake clock rather than sleeping.
/// </summary>
public sealed class CreateRideTests : PageTestContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 6, 15, 10, 0, 0, TimeSpan.Zero);

	private FakeApiClient WireServices()
	{
		FakeApiClient api = new();
		Services.AddSingleton<IApiClient>(api);
		Services.AddSingleton<TimeProvider>(new FakeTimeProvider(FixedInstant));
		return api;
	}

	[Fact]
	public void ApprovalPolicy_IsSelectedByDefault()
	{
		WireServices();

		IRenderedComponent<CreateRide> component = Render<CreateRide>();

		// The Approval radio is checked and the Open radio is not.
		AngleSharp.Dom.IElement approval = component
			.FindAll("input[type=radio]")
			.First(r => r.GetAttribute("value") == JoinPolicyDto.Approval.ToString());
		AngleSharp.Dom.IElement open = component
			.FindAll("input[type=radio]")
			.First(r => r.GetAttribute("value") == JoinPolicyDto.Open.ToString());

		approval.GetAttribute("checked").ShouldNotBeNull(
			"§5.2: Approval is the safer default — nobody enters until the organiser admits them.");
		open.GetAttribute("checked").ShouldBeNull();
	}

	[Fact]
	public async Task Submit_SendsNameAndPolicy_ToTheApi()
	{
		FakeApiClient api = WireServices();

		IRenderedComponent<CreateRide> component = Render<CreateRide>();

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement name = component.Find("input[placeholder='Saturday Coast Run']");
			name.Change("Sunday morning club run");
		});

		// Switch policy to Open.
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement open = component
				.FindAll("input[type=radio]")
				.First(r => r.GetAttribute("value") == JoinPolicyDto.Open.ToString());
			open.Change(true);
		});

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement form = component.Find("form");
			form.Submit();
		});

		component.WaitForAssertion(() => api.LastCreateRideRequest.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		CreateRideRequest sent = api.LastCreateRideRequest!;
		sent.Name.ShouldBe("Sunday morning club run", "the name is trimmed and passed through — §5.2's adventure identity.");
		sent.JoinPolicy.ShouldBe(JoinPolicyDto.Open,
			"the composer sends whichever policy the organiser last selected; the default is Approval, and the switch to Open must round-trip.");
	}

	// -- Choosing a route while creating (§5.4) -----------------------------------------------

	private static TrackSummary Track(Guid id, string? name, double distanceM) => new(
		Id: id,
		Name: name,
		CreatedUtc: FixedInstant,
		StartedUtc: null,
		EndedUtc: null,
		DistanceM: distanceM,
		DurationS: null,
		AscentM: null,
		MaxSpeedMps: null,
		PointCount: 100,
		SegmentCount: 1,
		Source: TrackSourceDto.Imported,
		Version: 1);

	/// <summary>
	/// The picker offers the caller's own tracks and nothing else (§15.4), plus the empty option
	/// that is the whole reason it can be ignored — a route is optional at creation.
	/// </summary>
	[Fact]
	public void RoutePicker_OffersTheCallersTracks_AndAnEmptyOption()
	{
		FakeApiClient api = WireServices();
		api.TracksResult = [Track(Guid.NewGuid(), "Coast loop", 42_000), Track(Guid.NewGuid(), null, 8_500)];

		IRenderedComponent<CreateRide> component = Render<CreateRide>();

		component.WaitForAssertion(
			() => component.FindAll("select option").Count.ShouldBe(3),
			timeout: TimeSpan.FromSeconds(3));

		IReadOnlyList<string> options = [.. component.FindAll("select option").Select(option => option.TextContent.Trim())];

		options[0].ShouldBe("No route — add one later",
			"the empty option is selected by default, because a route is optional here.");
		options[1].ShouldContain("Coast loop");
		options[1].ShouldContain("42.0 km", customMessage: "the distance is what tells two similarly named routes apart.");
		options[2].ShouldContain("Unnamed track", customMessage: "an unnamed track gets the screen's placeholder, not a server-invented name.");
	}

	/// <summary>
	/// The chosen route is attached after the create, because <c>POST /group-rides/{id}/routes</c>
	/// needs the id (§5.4), and the organiser still lands on the live map.
	/// </summary>
	[Fact]
	public async Task Submit_WithARouteChosen_AttachesItToTheNewRide()
	{
		FakeApiClient api = WireServices();
		Guid trackId = Guid.NewGuid();
		api.TracksResult = [Track(trackId, "Coast loop", 42_000)];

		IRenderedComponent<CreateRide> component = Render<CreateRide>();
		component.WaitForAssertion(() => component.FindAll("select option").Count.ShouldBe(2),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.Find("input[placeholder='Saturday Coast Run']").Change("Sunday morning club run"));
		await component.InvokeAsync(() => component.Find("select").Change(trackId.ToString()));
		await component.InvokeAsync(() => component.Find("form").Submit());

		component.WaitForAssertion(() => api.AddedRoutes.Count.ShouldBe(1), timeout: TimeSpan.FromSeconds(3));

		api.LastCreateRideRequest.ShouldNotBeNull();
		api.AddedRoutes[0].TrackId.ShouldBe(trackId,
			"the track the organiser picked is the one attached — §5.4's routes are a set, and this seeds it with one.");
		api.AddedRoutes[0].RideId.ShouldNotBe(Guid.Empty,
			"the attach is addressed to the ride that was just created, so it cannot happen before the create.");

		Services.GetRequiredService<NavigationManager>().Uri
			.ShouldContain("/group-rides/live/", customMessage: "the organiser still lands on the live map.");
	}

	/// <summary>Leaving the empty option alone attaches nothing — the route is genuinely optional.</summary>
	[Fact]
	public async Task Submit_WithNoRouteChosen_AttachesNothing()
	{
		FakeApiClient api = WireServices();
		api.TracksResult = [Track(Guid.NewGuid(), "Coast loop", 42_000)];

		IRenderedComponent<CreateRide> component = Render<CreateRide>();
		component.WaitForAssertion(() => component.FindAll("select option").Count.ShouldBe(2),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.Find("input[placeholder='Saturday Coast Run']").Change("Sunday morning club run"));
		await component.InvokeAsync(() => component.Find("form").Submit());

		component.WaitForAssertion(() => api.LastCreateRideRequest.ShouldNotBeNull(), timeout: TimeSpan.FromSeconds(3));
		api.AddedRoutes.ShouldBeEmpty("nothing was picked, so nothing is attached.");
	}

	/// <summary>
	/// A refused attach leaves a perfectly good adventure behind, so it is reported as exactly
	/// that. The form goes away rather than re-arming — a second Create would make a second
	/// adventure — and the way on is the ride's own Info screen, where routes are managed.
	/// </summary>
	[Fact]
	public async Task Submit_WhenTheAttachIsRefused_KeepsTheAdventure_AndSaysSo()
	{
		FakeApiClient api = WireServices();
		Guid trackId = Guid.NewGuid();
		api.TracksResult = [Track(trackId, "Coast loop", 42_000)];
		api.AddRideRouteException = new ApiException(new ApiError(HttpStatusCode.Conflict, "That route is already attached.", []));

		IRenderedComponent<CreateRide> component = Render<CreateRide>();
		component.WaitForAssertion(() => component.FindAll("select option").Count.ShouldBe(2),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.Find("input[placeholder='Saturday Coast Run']").Change("Sunday morning club run"));
		await component.InvokeAsync(() => component.Find("select").Change(trackId.ToString()));
		await component.InvokeAsync(() => component.Find("form").Submit());

		component.WaitForAssertion(() => component.FindAll("div.error").Count.ShouldBe(1), timeout: TimeSpan.FromSeconds(3));

		component.Find("div.error").TextContent
			.ShouldContain("The adventure was created", customMessage: "the create succeeded and the organiser has to be told so, or they make a second one.");
		component.Find("div.error").TextContent
			.ShouldContain("That route is already attached.", customMessage: "what the server said is what the organiser reads.");

		component.FindAll("form").ShouldBeEmpty("re-arming Create would create a second adventure.");
		component.Find("a.button").GetAttribute("href")
			.ShouldEndWith("/info", customMessage: "the way on is the ride's Info screen, where routes are managed.");

		Services.GetRequiredService<NavigationManager>().Uri
			.ShouldNotContain("/group-rides/live/", customMessage: "navigating away would take the message with it.");
	}
}
