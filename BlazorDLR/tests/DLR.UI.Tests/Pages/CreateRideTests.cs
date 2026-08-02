using BlazorDLR.Shared.Pages.GroupRides;
using BlazorDLR.Shared.Services;
using Bunit;
using DLR.Core.Contracts.Rides;
using DLR.UI.Tests.Fakes;
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
public sealed class CreateRideTests : BunitContext
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
		sent.Name.ShouldBe("Sunday morning club run", "the name is trimmed and passed through — §5.2's ride identity.");
		sent.JoinPolicy.ShouldBe(JoinPolicyDto.Open,
			"the composer sends whichever policy the organiser last selected; the default is Approval, and the switch to Open must round-trip.");
	}
}
