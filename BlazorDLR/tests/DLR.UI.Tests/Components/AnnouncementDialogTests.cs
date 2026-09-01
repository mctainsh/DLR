using BlazorDLR.Shared.Components;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Client;
using DLR.Core.Contracts.Announcements;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Components;

/// <summary>
/// The dialog a message from the server is drawn in (§20.2).
/// <para>
/// The case worth having a test for is the one this feature exists for: a message arriving over
/// the hub while the app is already open, drawn without a navigation and without a reload.
/// </para>
/// </summary>
public sealed class AnnouncementDialogTests : BunitContext
{
	[Fact]
	public async Task AnAnnouncementFromTheLaunchCheck_IsDrawn()
	{
		StartupCheckState state = Wire(Notices.Supported(Notices.Announcement("Server restart", NoticeSeverity.Urgent)));

		await state.CheckAsync();

		IRenderedComponent<AnnouncementDialog> dialog = Render<AnnouncementDialog>();

		dialog.Markup.ShouldContain("Server restart");
		dialog.Markup.ShouldContain("Something is happening.");
		dialog.Find(".notice-dialog").ClassList.ShouldContain("urgent");
	}

	[Fact]
	public async Task OneArrivingOverTheHub_IsDrawnWithoutANavigation()
	{
		StartupCheckState state = Wire(Notices.Supported());

		await state.CheckAsync();

		IRenderedComponent<AnnouncementDialog> dialog = Render<AnnouncementDialog>();

		dialog.FindAll(".notice-dialog").ShouldBeEmpty();

		dialog.InvokeAsync(() => state.Receive(Notices.Announcement("The server goes down in ten minutes")));

		dialog.WaitForAssertion(() =>
			dialog.Markup.ShouldContain("The server goes down in ten minutes"));
	}

	[Fact]
	public async Task ClearingIt_TakesItOffTheScreen()
	{
		InMemoryDeviceSettings settings = new();
		StartupCheckState state = Wire(Notices.Supported(Notices.Announcement("Server restart")), settings);

		await state.CheckAsync();

		IRenderedComponent<AnnouncementDialog> dialog = Render<AnnouncementDialog>();

		await dialog.Find("button.notice-ok").ClickAsync(new());

		dialog.WaitForAssertion(() => dialog.Markup.ShouldNotContain("Server restart"));

		(await settings.GetAsync(StartupCheckState.DismissedKey)).ShouldNotBeNullOrWhiteSpace();
	}

	[Fact]
	public async Task NothingWaiting_DrawsNothing()
	{
		StartupCheckState state = Wire(Notices.Supported());

		await state.CheckAsync();

		// FindAll rather than the markup: the component carries its own <style>, so every class
		// name it draws with appears in the markup whether anything is on screen or not.
		Render<AnnouncementDialog>().FindAll(".notice-backdrop").ShouldBeEmpty();
	}

	[Fact]
	public async Task ABuildBehindTheRecommendation_IsOfferedTheStore()
	{
		StartupCheckState state = Wire(
			new StartupCheck(ClientSupport.UpdateAvailable, "1.0.0.0", "9.0.0.0", []),
			platform: "Android - 14.0");

		await state.CheckAsync();

		IRenderedComponent<AnnouncementDialog> dialog = Render<AnnouncementDialog>();

		dialog.Markup.ShouldContain("9.0.0.0");
		dialog.Find("a.notice-store").GetAttribute("href").ShouldBe(ClientRelease.PlayStoreUrl);
	}

	private StartupCheckState Wire(
		StartupCheck check,
		InMemoryDeviceSettings? settings = null,
		string platform = "xunit")
	{
		FakeApiClient api = new() { StartupResult = check };
		InMemoryDeviceSettings store = settings ?? new InMemoryDeviceSettings();

		StartupCheckState state = new(
			api, store, new FakeTimeProvider(Notices.Now), new FakeFormFactor { Platform = platform });

		Services.AddSingleton<IApiClient>(api);
		Services.AddSingleton<IDeviceSettings>(store);
		Services.AddSingleton(state);

		return state;
	}
}
