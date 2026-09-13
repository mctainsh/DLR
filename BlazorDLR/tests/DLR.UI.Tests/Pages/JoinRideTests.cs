using BlazorDLR.Shared.Pages.GroupRides;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Contracts.Rides;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// §5.2's join-by-code entry point. Two properties:
/// <list type="bullet">
///   <item>The composer trims the code and passes the message through only when it is
///     non-blank - a blank textarea must arrive as <c>null</c>, not as an empty string
///     that the server has to distinguish from "no message".</item>
///   <item>The <c>Joined</c> flag on the <see cref="JoinResult"/> decides where to
///     navigate: straight to the ride when true, back to the list when false. That is
///     the observable difference between an Open ride and an Approval ride at the
///     moment of joining.</item>
/// </list>
/// </summary>
public sealed class JoinRideTests : PageTestContext
{
	private FakeApiClient WireServices()
	{
		FakeApiClient api = new();
		Services.AddSingleton<IApiClient>(api);
		return api;
	}

	/// <summary>
	/// The same page on a host that has a GPS - everything <see cref="LocationBroadcastState"/>
	/// needs, which is the one service the join screen asks the container for.
	/// </summary>
	private FakeApiClient WirePhone()
	{
		FakeApiClient api = WireServices();

		Services.AddSingleton<IRideHubClient>(new FakeRideHubClient());
		Services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
		Services.AddSingleton<IDeviceSettings, InMemoryDeviceSettings>();
		Services.AddSingleton<ConfirmService>();
		Services.AddSingleton<ILocationProvider, FakeLocationProvider>();
		Services.AddSingleton<PrivateAreaState>();
		Services.AddSingleton<LocationUpdateRateState>();
		Services.AddSingleton<TrackRecordingState>();
		Services.AddSingleton<LocationDisclosure>();
		Services.AddSingleton<LocationBroadcastState>();

		return api;
	}

	/// <summary>Fills in a code and submits, which is the whole of the screen.</summary>
	private static async Task JoinAsync(IRenderedComponent<JoinRide> component)
	{
		await component.InvokeAsync(() => component.Find("input[placeholder='AB3K9Z']").Change("AB3K9Z"));
		await component.InvokeAsync(() => component.Find("form").Submit());
	}

	[Fact]
	public async Task Submit_TrimsCode_AndSendsNullMessageWhenBlank()
	{
		FakeApiClient api = WireServices();

		IRenderedComponent<JoinRide> component = Render<JoinRide>();

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement code = component.Find("input[placeholder='AB3K9Z']");
			code.Change("  AB3K9Z  ");
		});

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement form = component.Find("form");
			form.Submit();
		});

		component.WaitForAssertion(() => api.LastJoinRideByCodeRequest.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		JoinByCodeRequest sent = api.LastJoinRideByCodeRequest!;
		sent.Code.ShouldBe("AB3K9Z", "codes are trimmed - a copy-pasted code with trailing whitespace must still work.");
		sent.Message.ShouldBeNull(
			"§5.2: an untouched message field must arrive as null, so the server does not have to distinguish it from an empty string.");
	}

	[Fact]
	public async Task Submit_SendsTrimmedMessage_WhenTypedOne()
	{
		FakeApiClient api = WireServices();

		IRenderedComponent<JoinRide> component = Render<JoinRide>();

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement code = component.Find("input[placeholder='AB3K9Z']");
			code.Change("AB3K9Z");
		});
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement message = component.Find("textarea");
			message.Change("  I'm the Sunday regular. Cheers.  ");
		});
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement form = component.Find("form");
			form.Submit();
		});

		component.WaitForAssertion(() => api.LastJoinRideByCodeRequest.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		api.LastJoinRideByCodeRequest!.Message.ShouldBe("I'm the Sunday regular. Cheers.",
			"the message is trimmed but the words are the joiner's - the organiser sees exactly what was typed.");
	}

	[Fact]
	public async Task Joining_OnAPhone_DoesNotStartSharing_BecauseNobodyHasBeenAskedYet()
	{
		// This used to turn sharing on, on the argument that typing in an organiser's code is
		// itself the decision. App Review rejected 8.0.0 under guideline 5.1.2(i) for it: a
		// traveller has to be asked before their location is shown to other people, and has to be
		// able to say no. A default is not an ask.
		//
		// It also silently disabled the ask. GroupRideLive raises the §5.6 consent prompt only
		// while sharing is off, so a joiner arriving with the flag already set was never shown it.
		// The ride screen is where the question is put; this page's job is to get them there.
		FakeApiClient api = WirePhone();
		api.JoinResult = new JoinResult(Guid.NewGuid(), Joined: true, RequestId: null);

		IRenderedComponent<JoinRide> component = Render<JoinRide>();
		await JoinAsync(component);

		component.WaitForAssertion(
			() => api.LastJoinRideByCodeRequest.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		api.SetSharingRequests.ShouldBeEmpty(
			"joining is not consent to be shown on a map - the ride screen asks, and takes no for an answer.");
	}

	[Fact]
	public async Task Joining_InABrowser_DoesNotTouchSharing()
	{
		// The same answer as the phone above, reached by a second route: §18.6 says there is no
		// receiver here at all, so the flag would be a consent record no fix could ever follow.
		// Kept as its own test because the two reasons are independent - if joining ever starts
		// setting the flag again, this is the one that still has to hold.
		FakeApiClient api = WireServices();
		api.JoinResult = new JoinResult(Guid.NewGuid(), Joined: true, RequestId: null);

		IRenderedComponent<JoinRide> component = Render<JoinRide>();
		await JoinAsync(component);

		component.WaitForAssertion(
			() => api.LastJoinRideByCodeRequest.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		api.SetSharingRequests.ShouldBeEmpty(
			"a browser cannot broadcast, so it must not record consent to.");
	}

	[Fact]
	public async Task JoiningAnApprovalRide_SharesNothingYet()
	{
		// Joined: false is a pending request - there is no membership to carry a sharing flag, and
		// the server would answer 404. The default lands when they are admitted and open the ride.
		FakeApiClient api = WirePhone();
		api.JoinResult = new JoinResult(Guid.NewGuid(), Joined: false, RequestId: Guid.NewGuid());

		IRenderedComponent<JoinRide> component = Render<JoinRide>();
		await JoinAsync(component);

		component.WaitForAssertion(
			() => api.LastJoinRideByCodeRequest.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		api.SetSharingRequests.ShouldBeEmpty();
	}
}
