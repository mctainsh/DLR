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
///     non-blank — a blank textarea must arrive as <c>null</c>, not as an empty string
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
	/// The same page on a host that has a GPS — everything <see cref="LocationBroadcastState"/>
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
		Services.AddSingleton<GpsProfileState>();
		Services.AddSingleton<TrackRecordingState>();
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
		sent.Code.ShouldBe("AB3K9Z", "codes are trimmed — a copy-pasted code with trailing whitespace must still work.");
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
			"the message is trimmed but the words are the joiner's — the organiser sees exactly what was typed.");
	}

	[Fact]
	public async Task Joining_OnAPhone_StartsSharingWithTheAdventure()
	{
		// §5.6's default was "off, and ask on the map". What that produced in the field was a
		// rider who had joined, believed they were on the map, and was not — the group finds out
		// by ringing to ask where they got to. Typing an organiser's code into your own phone is
		// the decision; the switch on the info page and the red strip on the map are how it is
		// unmade.
		Guid rideId = Guid.NewGuid();
		FakeApiClient api = WirePhone();
		api.JoinResult = new JoinResult(rideId, Joined: true, RequestId: null);

		IRenderedComponent<JoinRide> component = Render<JoinRide>();
		await JoinAsync(component);

		component.WaitForAssertion(
			() => api.SetSharingRequests.ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		(Guid SharedRide, SetSharingRequest Request) sent = api.SetSharingRequests.ShouldHaveSingleItem();
		sent.SharedRide.ShouldBe(rideId, "the flag is per adventure — this one, not the last one opened.");
		sent.Request.Share.ShouldBeTrue();
	}

	[Fact]
	public async Task Joining_InABrowser_DoesNotTouchSharing()
	{
		// §18.6: no receiver here, so the flag would be a consent record no fix will ever follow —
		// and a traveller watching from a laptop has not agreed to anything by typing a code.
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
		// Joined: false is a pending request — there is no membership to carry a sharing flag, and
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
