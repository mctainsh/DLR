using BlazorDLR.Shared.Pages.GroupRides;
using BlazorDLR.Shared.Services;
using Bunit;
using DLR.Core.Contracts.Rides;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// §5.2's organiser panel: pending requests, admit / decline / decline-and-block.
/// The three buttons must reach the API as three distinct decisions — a wrong wire
/// on decline-and-block is a case §7.7's reporting flow depends on.
/// </summary>
public sealed class RideRequestsTests : PageTestContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

	private FakeApiClient WireServices(IReadOnlyList<JoinRequestSummary> requests)
	{
		FakeApiClient api = new() { JoinRequestsResult = requests };
		Services.AddSingleton<IApiClient>(api);
		Services.AddSingleton<TimeProvider>(new FakeTimeProvider(FixedInstant));
		return api;
	}

	private static JoinRequestSummary Request(string userName, string? message = null) =>
		new(Id: Guid.NewGuid(), UserId: Guid.NewGuid(), UserName: userName, Message: message,
			RequestedUtc: FixedInstant.AddMinutes(-10));

	[Fact]
	public void EmptyList_ShowsFriendlyMessage()
	{
		WireServices(Array.Empty<JoinRequestSummary>());

		IRenderedComponent<RideRequests> component = Render<RideRequests>(parameters => parameters
			.Add(p => p.RideId, Guid.NewGuid()));

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("No pending requests", StringComparison.Ordinal).ShouldBeTrue(
				"an organiser looking at the pending list must see 'no pending requests' rather than a blank page.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Admit_SendsAdmitTrueBlockFalse()
	{
		JoinRequestSummary alice = Request("Alice");
		FakeApiClient api = WireServices(new[] { alice });

		Guid rideId = Guid.NewGuid();
		IRenderedComponent<RideRequests> component = Render<RideRequests>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
			component.FindAll("button.primary").ShouldNotBeEmpty(), timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement admit = component.FindAll("button.primary")
				.First(b => b.TextContent.Contains("Admit", StringComparison.Ordinal));
			admit.Click();
		});

		component.WaitForAssertion(() => api.DecideJoinRequests.Count.ShouldBe(1),
			timeout: TimeSpan.FromSeconds(3));
		(Guid ride, Guid requestId, DecideJoinRequest decision) = api.DecideJoinRequests[0];
		ride.ShouldBe(rideId, "the adventure id must round-trip — a decision on the wrong adventure is a bug that would silently admit the wrong people.");
		requestId.ShouldBe(alice.Id);
		decision.Admit.ShouldBeTrue();
		decision.Block.ShouldBeFalse();
	}

	[Fact]
	public async Task DeclineAndBlock_SendsAdmitFalseBlockTrue()
	{
		JoinRequestSummary alice = Request("Alice");
		FakeApiClient api = WireServices(new[] { alice });

		IRenderedComponent<RideRequests> component = Render<RideRequests>(parameters => parameters
			.Add(p => p.RideId, Guid.NewGuid()));

		component.WaitForAssertion(() =>
			component.FindAll("button.danger").ShouldNotBeEmpty(), timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement declineBlock = component.FindAll("button.danger")
				.First(b => b.TextContent.Contains("block", StringComparison.OrdinalIgnoreCase));
			declineBlock.Click();
		});

		component.WaitForAssertion(() => api.DecideJoinRequests.Count.ShouldBe(1),
			timeout: TimeSpan.FromSeconds(3));
		DecideJoinRequest decision = api.DecideJoinRequests[0].Request;
		decision.Admit.ShouldBeFalse();
		decision.Block.ShouldBeTrue("§5.2: decline-and-block is a distinct decision from a plain decline.");
	}

	[Fact]
	public void MessageOnRequest_IsShownVerbatim()
	{
		WireServices(new[] { Request("Alice", message: "I ride Sunday with the same club, cheers.") });

		IRenderedComponent<RideRequests> component = Render<RideRequests>(parameters => parameters
			.Add(p => p.RideId, Guid.NewGuid()));

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("I ride Sunday with the same club", StringComparison.Ordinal).ShouldBeTrue(
				"§5.2: the joiner's message is what the organiser reads before deciding — it must render.");
		}, timeout: TimeSpan.FromSeconds(3));
	}
}
