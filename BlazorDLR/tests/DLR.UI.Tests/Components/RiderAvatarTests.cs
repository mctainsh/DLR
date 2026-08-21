using System.Net;
using BlazorDLR.Shared.Components;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Components;

/// <summary>
/// The little round photograph beside a rider's name (§7.3).
/// <para>
/// Two properties carry the weight. It draws nothing at all for a rider with no photograph —
/// a name is complete on its own, and a list of grey placeholders that pop into faces looks
/// broken while it loads. And it survives a host that never registered the cache, because it is
/// decoration: a missing picture is the right price for a missing registration, and a member list
/// that will not render is not.
/// </para>
/// </summary>
public sealed class RiderAvatarTests : BunitContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private readonly FakeApiClient _api = new();
	private readonly FakeTimeProvider _clock = new(FixedInstant);

	private void Wire()
	{
		Services.AddSingleton<IApiClient>(_api);
		Services.AddSingleton<TimeProvider>(_clock);
		Services.AddSingleton(new HttpClient(new StubPhotoHandler()) { BaseAddress = new Uri("https://test.invalid") });
		Services.AddScoped<RiderAvatars>();
	}

	/// <summary>Opens the batch window — the fake clock only moves when a test moves it (§10.4).</summary>
	private void OpenTheWindow() => _clock.Advance(TimeSpan.FromMilliseconds(RiderAvatars.BatchWindowMs + 1));

	[Fact]
	public void ARiderWithAPhotograph_GetsACircle()
	{
		Wire();
		_api.AvatarsByUserName["Alice"] = Guid.NewGuid();

		IRenderedComponent<RiderAvatar> component = Render<RiderAvatar>(parameters => parameters
			.Add(p => p.UserName, "Alice"));

		OpenTheWindow();

		component.WaitForAssertion(() =>
		{
			AngleSharp.Dom.IElement image = component.Find("img.rider-avatar");

			// A data URL, because the photo endpoint is behind the bearer token and an <img src>
			// cannot carry one (§16.4).
			image.GetAttribute("src").ShouldStartWith("data:image/jpeg;base64,");
			// About twice the line height, which is the default size — see AvatarSize.Inline.
			image.ClassList.ShouldContain("inline");
			image.GetAttribute("alt").ShouldBe(string.Empty,
				"the name is already beside it in text — announcing it twice is worse than not at all");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void ARiderWithNoPhotograph_DrawsNothing()
	{
		Wire();

		IRenderedComponent<RiderAvatar> component = Render<RiderAvatar>(parameters => parameters
			.Add(p => p.UserName, "Bob"));

		OpenTheWindow();

		component.WaitForAssertion(
			() => component.FindAll("img").ShouldBeEmpty(
				"no placeholder and no spinner: a name is complete on its own."),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void TheDetailSize_IsAvailableForTheRiderPageThatDoesNotExistYet()
	{
		Wire();
		_api.AvatarsByUserName["Alice"] = Guid.NewGuid();

		IRenderedComponent<RiderAvatar> component = Render<RiderAvatar>(parameters => parameters
			.Add(p => p.UserName, "Alice")
			.Add(p => p.Size, RiderAvatar.AvatarSize.Detail));

		OpenTheWindow();

		component.WaitForAssertion(
			() => component.Find("img.rider-avatar").ClassList.ShouldContain("detail"),
			timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// The SSR host does not register the cache at all — it has no bearer token and no HttpClient,
	/// so it could not fetch a thumbnail if it tried (see the note in <c>Program.cs</c>). The
	/// prerendered markup must therefore carry the name and no picture rather than an exception.
	/// </summary>
	[Fact]
	public void WithNoCacheRegistered_ItRendersNothingRatherThanThrowing()
	{
		IRenderedComponent<RiderAvatar> component = Render<RiderAvatar>(parameters => parameters
			.Add(p => p.UserName, "Alice"));

		component.Markup.Trim().ShouldBeEmpty();
	}

	[Fact]
	public void ChangingTheNameParameter_ResolvesTheNewRider()
	{
		Wire();
		_api.AvatarsByUserName["Alice"] = Guid.NewGuid();

		IRenderedComponent<RiderAvatar> component = Render<RiderAvatar>(parameters => parameters
			.Add(p => p.UserName, "Bob"));

		OpenTheWindow();

		component.WaitForAssertion(() => component.FindAll("img").ShouldBeEmpty(), timeout: TimeSpan.FromSeconds(3));

		// A virtualised list reusing a row for a different rider, which is ordinary rather than rare.
		component.Render(parameters => parameters.Add(p => p.UserName, "Alice"));

		OpenTheWindow();

		component.WaitForAssertion(
			() => component.Find("img.rider-avatar").ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));
	}

	private sealed class StubPhotoHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			HttpResponseMessage response = new(HttpStatusCode.OK)
			{
				Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xD9]),
			};

			response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");

			return Task.FromResult(response);
		}
	}
}
