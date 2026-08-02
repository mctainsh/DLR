using BlazorDLR.Shared.Pages.GroupRides;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Contracts.Rides;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// §5.8 through the UI, not through the server: the composer disappears when
/// <c>RidePermissions.AllowMemberComments</c> is off, unless the current viewer is
/// the organiser. This is the "posting disabled when permission revoked" property
/// that <c>SharedFrontend.md §7 Phase 4</c> names.
/// </summary>
public sealed class RideThreadTests : BunitContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private static FakeApiClient WireServices(BunitContext context, RidePermissions permissions, bool isOrganiser)
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
				MemberCount: 3,
				IsOrganiser: isOrganiser,
				JoinCode: null,
				Permissions: permissions,
				Members: Array.Empty<RideMemberSummary>()),
		};

		FakeTimeProvider clock = new(FixedInstant);
		context.Services.AddSingleton<IApiClient>(api);
		context.Services.AddSingleton<IRideHubClient>(new FakeRideHubClient());
		context.Services.AddSingleton<TimeProvider>(clock);
		context.Services.AddSingleton(new AuthState(api, new FakeTokenStore(), clock));
		return api;
	}

	[Fact]
	public void PermissionRevoked_ComposerIsHidden_ForOrdinaryMember()
	{
		RidePermissions revoked = new(AllowMemberMarkers: true, AllowMemberComments: false, AllowMemberPhotos: true);
		FakeApiClient api = WireServices(this, permissions: revoked, isOrganiser: false);

		Guid rideId = api.RideResult!.Id;
		IRenderedComponent<RideThread> component = Render<RideThread>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
		{
			// The read side always renders; the compose surface must NOT.
			component.Markup.Contains("Ride thread", StringComparison.Ordinal).ShouldBeTrue(
				"§17.7: revoking the permission does not hide the thread — only the composer.");
			component.FindAll("form.composer").Count.ShouldBe(0,
				"§5.8: with AllowMemberComments off and no organiser role, the composer must be absent.");
			component.FindAll("textarea").Count.ShouldBe(0,
				"a lingering textarea is a fallible client-side guard — the entire compose surface should be gone.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void PermissionRevoked_ComposerRemains_ForOrganiser()
	{
		RidePermissions revoked = new(AllowMemberMarkers: true, AllowMemberComments: false, AllowMemberPhotos: true);
		FakeApiClient api = WireServices(this, permissions: revoked, isOrganiser: true);

		Guid rideId = api.RideResult!.Id;
		IRenderedComponent<RideThread> component = Render<RideThread>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
		{
			component.FindAll("form.composer").Count.ShouldBe(1,
				"§5.8: turning off member comments does not silence the organiser — announcements are still allowed.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void PermissionAllowed_ComposerIsPresent_ForOrdinaryMember()
	{
		RidePermissions allowed = new(AllowMemberMarkers: true, AllowMemberComments: true, AllowMemberPhotos: true);
		FakeApiClient api = WireServices(this, permissions: allowed, isOrganiser: false);

		Guid rideId = api.RideResult!.Id;
		IRenderedComponent<RideThread> component = Render<RideThread>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
		{
			component.FindAll("form.composer").Count.ShouldBe(1,
				"the default permissions leave the composer available to every member.");
		}, timeout: TimeSpan.FromSeconds(3));
	}
}
