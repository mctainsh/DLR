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
public sealed class RideThreadTests : PageTestContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private static FakeApiClient WireServices(BunitContext context, RidePermissions permissions, bool isOrganiser)
	{
		Guid rideId = Guid.NewGuid();
		FakeApiClient api = new()
		{
			RideResult = new RideDetail(
				Id: rideId,
				Name: "Test adventure",
				Description: null,
				StartUtc: FixedInstant,
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
		context.Services.AddSingleton<ConfirmService>();
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
			component.Markup.Contains("Adventure thread", StringComparison.Ordinal).ShouldBeTrue(
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

	/// <summary>
	/// Typing enables Post — on the keystroke, not on the blur.
	/// <para>
	/// bUnit's <c>Input</c> raises <c>oninput</c> and nothing else, which is exactly the event a
	/// plain <c>@@bind</c> ignores: it listens on <c>onchange</c>, and a browser does not raise that
	/// until the field loses focus. On a phone there is often nothing to move focus to, so the
	/// rider typed a whole message and watched a greyed-out button. This test fails against
	/// <c>@@bind</c> alone and passes against <c>@@bind:event="oninput"</c>.
	/// </para>
	/// </summary>
	[Fact]
	public void TypingAComment_EnablesPost_BeforeTheFieldLosesFocus()
	{
		RidePermissions allowed = new(AllowMemberMarkers: true, AllowMemberComments: true, AllowMemberPhotos: true);
		FakeApiClient api = WireServices(this, permissions: allowed, isOrganiser: false);

		Guid rideId = api.RideResult!.Id;
		IRenderedComponent<RideThread> component = Render<RideThread>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(
			() => component.FindAll("form.composer textarea").Count.ShouldBe(1),
			timeout: TimeSpan.FromSeconds(3));

		component.Find("form.composer button.primary").HasAttribute("disabled").ShouldBeTrue(
			"an empty composer has nothing to post.");

		// oninput only. No blur, no change event — the phone case.
		component.Find("form.composer textarea").Input("See you at the servo.");

		component.Find("form.composer button.primary").HasAttribute("disabled").ShouldBeFalse(
			"Post must enable as the rider types, not when the textarea finally loses focus.");
	}

	/// <summary>
	/// The same property for the poll half, which fails for one extra reason: the options live in
	/// <c>PollComposer</c>, and a keystroke in a child renders the child only. Without the child
	/// telling this page the spec moved, Post would go on reading a stale <c>BuildSpec()</c>.
	/// </summary>
	[Fact]
	public void TypingAPoll_EnablesPost_BeforeTheFieldsLoseFocus()
	{
		RidePermissions allowed = new(AllowMemberMarkers: true, AllowMemberComments: true, AllowMemberPhotos: true);
		FakeApiClient api = WireServices(this, permissions: allowed, isOrganiser: false);

		Guid rideId = api.RideResult!.Id;
		IRenderedComponent<RideThread> component = Render<RideThread>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(
			() => component.FindAll("form.composer textarea").Count.ShouldBe(1),
			timeout: TimeSpan.FromSeconds(3));

		component.Find("form.composer .poll-toggle input").Change(true);
		component.Find("form.composer textarea").Input("Servo or the bakery?");

		component.Find("form.composer button.primary").HasAttribute("disabled").ShouldBeTrue(
			"§17.5: a poll needs two options before there is anything to post.");

		IReadOnlyList<AngleSharp.Dom.IElement> options = component.FindAll(".poll-composer .option input");
		options.Count.ShouldBe(2, "a fresh poll composer offers the two options §17.5 requires.");
		options[0].Input("Servo");
		component.FindAll(".poll-composer .option input")[1].Input("Bakery");

		component.Find("form.composer button.primary").HasAttribute("disabled").ShouldBeFalse(
			"Post must enable as the options are typed, not when each field loses focus.");
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
