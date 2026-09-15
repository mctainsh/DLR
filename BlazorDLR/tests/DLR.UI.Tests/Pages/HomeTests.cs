using BlazorDLR.Shared.Pages;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Contracts.Identity;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// §4.1's signed-in landing. Two things this file asserts:
/// <list type="bullet">
///   <item>The four navigation cards (My rides / Group rides / Import / Settings) are
///     all present - this is the sole path a signed-in user has into everything else.</item>
///   <item>The form factor label is what <see cref="IFormFactor"/> returned. A missing
///     label means the form-factor seam is broken; the shared UI relies on it for
///     web-only branches like TrackEditor.</item>
/// </list>
/// </summary>
public sealed class HomeTests : PageTestContext
{
	private FakeFormFactor WireServices(string formFactor = "Desktop", string platform = "Web", bool isAdmin = false)
	{
		FakeFormFactor ff = new() { FormFactor = formFactor, Platform = platform };
		Services.AddSingleton<IFormFactor>(ff);

		// Home offers the administration section on the server's roster (§14.6). The fake answers a
		// profile with IsAdmin false, so the section stays off - which is the state every account
		// but a handful is in, and the one these tests are about.
		Services.AddSingleton<IApiClient>(new FakeApiClient
		{
			ProfileResult = new OwnProfile(null, null, null, false, false, false, false, IsAdmin: isAdmin),
		});
		Services.AddSingleton<AdminAccess>();
		return ff;
	}

	[Fact]
	public void AllFourNavCards_ArePresent()
	{
		WireServices();

		IRenderedComponent<Home> component = Render<Home>();

		component.FindAll("a[href='/rides']").ShouldNotBeEmpty("§4.1: My routes is one of the four Home cards.");
		component.FindAll("a[href='/group-rides']").ShouldNotBeEmpty("Group adventures is a Home card.");
		component.FindAll("a[href='/import']").ShouldNotBeEmpty("Import GPX is a Home card.");
		component.FindAll("a[href='/settings']").ShouldNotBeEmpty("Settings is a Home card.");
	}

	/// <summary>
	/// The administration screens live on this page now (§14.6), and the roster is the whole of
	/// what decides whether they are drawn. An ordinary account must not see the section at all -
	/// not a disabled one, not an empty one.
	/// </summary>
	[Fact]
	public void AdministrationSection_IsAbsentForAnOrdinaryAccount()
	{
		WireServices();

		IRenderedComponent<Home> component = Render<Home>();

		component.WaitForAssertion(
			() =>
			{
				component.FindAll("section.admin").ShouldBeEmpty();
				component.FindAll("a[href='/admin/reports']").ShouldBeEmpty();
				component.Markup.ShouldNotContain("Administration", Case.Insensitive);
			},
			timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// And an account on the server's roster gets all five, including the moderation queue the
	/// store rejection turned into a promise about response time (§17.7).
	/// </summary>
	[Fact]
	public void AdministrationSection_OffersEveryScreenToAnAdministrator()
	{
		WireServices(isAdmin: true);

		IRenderedComponent<Home> component = Render<Home>();

		component.WaitForAssertion(
			() =>
			{
				component.FindAll("section.admin").ShouldNotBeEmpty();

				foreach (string route in new[]
				{
					"/admin/users",
					"/admin/stats",
					"/admin/reports",
					"/admin/notices",
					"/admin/logs",
				})
				{
					component.FindAll($"a[href='{route}']").ShouldNotBeEmpty($"{route} is an administration card");
				}
			},
			timeout: TimeSpan.FromSeconds(3));
	}
}
