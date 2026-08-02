using BlazorDLR.Shared.Pages.GroupRides;
using BlazorDLR.Shared.Services;
using Bunit;
using DLR.Core.Contracts.Rides;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// §5.8's three permission switches. Two properties that matter:
/// <list type="bullet">
///   <item>The three switches (markers / comments / photos) are independent — the
///     composer must send the exact combination the organiser last selected. Photos
///     is not a consequence of comments even though the two are often paired.</item>
///   <item>Non-organisers see a plain refusal and no controls. The server enforces
///     this too, but a UI that shows the switches to non-organisers would be a lie
///     about who owns the ride.</item>
/// </list>
/// </summary>
public sealed class RidePermissionsPageTests : BunitContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private FakeApiClient WireServices(bool isOrganiser, RidePermissions? initial = null)
	{
		Guid rideId = Guid.NewGuid();
		FakeApiClient api = new()
		{
			RideResult = new RideDetail(
				Id: rideId,
				Name: "Test ride",
				Description: null,
				StartUtc: FixedInstant,
				State: RideStateDto.Open,
				JoinPolicy: JoinPolicyDto.Approval,
				MemberCap: 50,
				MemberCount: 1,
				IsOrganiser: isOrganiser,
				JoinCode: null,
				Permissions: initial ?? new RidePermissions(),
				Members: Array.Empty<RideMemberSummary>()),
		};
		Services.AddSingleton<IApiClient>(api);
		return api;
	}

	[Fact]
	public void NonOrganiser_SeesRefusal_NotTheSwitches()
	{
		FakeApiClient api = WireServices(isOrganiser: false);

		IRenderedComponent<RidePermissionsPage> component = Render<RidePermissionsPage>(parameters => parameters
			.Add(p => p.RideId, api.RideResult!.Id));

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("Only the organiser can change these", StringComparison.Ordinal).ShouldBeTrue(
				"§5.8: a member looking at this page sees a plain refusal — not the switches.");
			component.FindAll("input[type=checkbox]").Count.ShouldBe(0,
				"showing the switches to a member is a lie about who owns the ride.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Organiser_TogglesPhotosOff_LeavesMarkersAndCommentsOn()
	{
		RidePermissions initial = new(AllowMemberMarkers: true, AllowMemberComments: true, AllowMemberPhotos: true);
		FakeApiClient api = WireServices(isOrganiser: true, initial);

		IRenderedComponent<RidePermissionsPage> component = Render<RidePermissionsPage>(parameters => parameters
			.Add(p => p.RideId, api.RideResult!.Id));

		component.WaitForAssertion(() =>
			component.FindAll("input[type=checkbox]").Count.ShouldBe(3), timeout: TimeSpan.FromSeconds(3));

		// Uncheck the third switch — photos.
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement[] switches = component.FindAll("input[type=checkbox]").ToArray();
			switches[2].Change(false);
		});
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement save = component.FindAll("button.primary")
				.First(b => b.TextContent.Contains("Save", StringComparison.Ordinal));
			save.Click();
		});

		component.WaitForAssertion(() => api.LastUpdatedPermissions.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		RidePermissions sent = api.LastUpdatedPermissions!;
		sent.AllowMemberMarkers.ShouldBeTrue("markers were left on — the switch is independent.");
		sent.AllowMemberComments.ShouldBeTrue("comments were left on — turning off photos does not silence conversation (§5.8).");
		sent.AllowMemberPhotos.ShouldBeFalse("§5.8: photos is its own switch, not a consequence of comments.");
	}
}
