using BlazorDLR.Shared.Pages.GroupRides;
using BlazorDLR.Shared.Services;
using Bunit;
using DLR.Core.Contracts.Rides;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

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
}
