using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using DLR.Core.Client;
using DLR.Core.Contracts.Announcements;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.State;

/// <summary>
/// What the server said at launch, and what this device has already been told (§20).
/// <para>
/// The dismissal record is device-local, so everything here is about one store: what goes into it,
/// what comes back out of it, and what happens when it is unreadable or refuses to be written -
/// which on the web is a browser with site data blocked, and is a case that must cost one repeated
/// dialog rather than a failed launch.
/// </para>
/// </summary>
public sealed class StartupCheckStateTests
{
	[Fact]
	public async Task TheServerIsToldWhichBuildIsAsking()
	{
		(StartupCheckState state, FakeApiClient api, _) = Wire(Notices.Supported());

		await state.CheckAsync();

		api.LastClientVersion.ShouldBe(
			StartupCheckState.ClientVersion,
			"the server decides the verdict, so it has to be told what it is deciding about");
	}

	[Fact]
	public async Task AnAnnouncementInTheCheck_IsWaitingToBeShown()
	{
		(StartupCheckState state, _, _) = Wire(Notices.Supported(Notices.Announcement("Server restart")));

		await state.CheckAsync();

		state.Current!.Title.ShouldBe("Server restart");
	}

	[Fact]
	public async Task ClearingOne_RemembersItOnThisDevice()
	{
		(StartupCheckState state, _, InMemoryDeviceSettings settings) =
			Wire(Notices.Supported(Notices.Announcement("Server restart")));

		await state.CheckAsync();
		await state.DismissAsync();

		state.Current.ShouldBeNull();

		(await settings.GetAsync(StartupCheckState.DismissedKey))
			.ShouldNotBeNullOrWhiteSpace("a dismissal nobody wrote down is a message shown again next launch");
	}

	[Fact]
	public async Task OneAlreadyCleared_IsNotShownAgain()
	{
		AnnouncementDto announcement = Notices.Announcement("Server restart");

		InMemoryDeviceSettings settings = new();

		(StartupCheckState first, _, _) = Wire(Notices.Supported(announcement), settings);
		await first.CheckAsync();
		await first.DismissAsync();

		// A second launch on the same device, against a server still serving the same message.
		(StartupCheckState second, _, _) = Wire(Notices.Supported(announcement), settings);
		await second.CheckAsync();

		second.Current.ShouldBeNull();
	}

	[Fact]
	public async Task AnExpiredDismissal_IsForgotten()
	{
		AnnouncementDto announcement = Notices.Announcement("Server restart", expiresIn: TimeSpan.FromHours(1));

		InMemoryDeviceSettings settings = new();
		FakeTimeProvider clock = new(Notices.Now);

		(StartupCheckState first, _, _) = Wire(Notices.Supported(announcement), settings, clock);
		await first.CheckAsync();
		await first.DismissAsync();

		clock.Advance(TimeSpan.FromHours(2));

		// A later launch, and a later dismissal - which is what rewrites the stored list. The
		// expired entry is dropped on the way through, so the string is bounded by what is still
		// live rather than growing for ever.
		AnnouncementDto current = Notices.Announcement("Something else");

		(StartupCheckState second, _, _) = Wire(Notices.Supported(current), settings, clock);
		await second.CheckAsync();
		await second.DismissAsync();

		string stored = (await settings.GetAsync(StartupCheckState.DismissedKey))!;

		stored.ShouldContain(current.Id.ToString("N"));
		stored.ShouldNotContain(announcement.Id.ToString("N"));
	}

	[Fact]
	public async Task TheSameOneFromBothPaths_IsShownOnce()
	{
		AnnouncementDto announcement = Notices.Announcement("Server restart");

		(StartupCheckState state, _, _) = Wire(Notices.Supported(announcement));

		await state.CheckAsync();
		state.Receive(announcement);

		state.Current.ShouldNotBeNull();

		await state.DismissAsync();

		state.Current.ShouldBeNull(
			"a rider who was connected when the sweep ran and then relaunched has been sent it twice");
	}

	[Fact]
	public async Task OnePushedOverTheHub_IsShownWithoutALaunch()
	{
		(StartupCheckState state, _, _) = Wire(Notices.Supported());

		await state.CheckAsync();

		state.Current.ShouldBeNull();

		bool raised = false;
		state.Changed += () => raised = true;

		state.Receive(Notices.Announcement("The server goes down in ten minutes"));

		state.Current!.Title.ShouldBe("The server goes down in ten minutes");
		raised.ShouldBeTrue("whatever is drawing it has to be told to look again");
	}

	[Fact]
	public async Task OneThatHasAlreadyExpired_IsNeverShown()
	{
		(StartupCheckState state, _, _) = Wire(Notices.Supported());

		await state.CheckAsync();

		state.Receive(Notices.Announcement("Stale", expiresIn: TimeSpan.FromHours(-1)));

		state.Current.ShouldBeNull();
	}

	[Fact]
	public async Task AFailedCheck_ChangesNothing()
	{
		// No StartupResult wired, so the fake throws - a server that has never heard of the
		// endpoint, or a phone with no signal.
		(StartupCheckState state, _, _) = Wire(check: null);

		await state.CheckAsync();

		state.IsUnsupported.ShouldBeFalse("a wall a flat tunnel could raise would be worse than no wall");
		state.Support.ShouldBeNull();
		state.Current.ShouldBeNull();
	}

	[Fact]
	public async Task ABuildBelowTheFloor_IsWalledOff()
	{
		(StartupCheckState state, _, _) = Wire(new StartupCheck(
			ClientSupport.Unsupported, "9.0.0.0", "9.0.0.0", []));

		await state.CheckAsync();

		state.IsUnsupported.ShouldBeTrue();
		state.MinimumVersion.ShouldBe("9.0.0.0");
	}

	[Fact]
	public async Task TheWallIsLatchedAtTheFirstCheck()
	{
		FakeApiClient api = new() { StartupResult = Notices.Supported() };
		InMemoryDeviceSettings settings = new();
		StartupCheckState state = new(
			api, settings, new FakeTimeProvider(Notices.Now), new FakeFormFactor());

		await state.CheckAsync();

		api.StartupResult = new StartupCheck(ClientSupport.Unsupported, "9.0.0.0", "9.0.0.0", []);

		await state.CheckAsync();

		state.IsUnsupported.ShouldBeFalse(
			"walling the app off mid-session would take the map away from a rider out on a road; "
				+ "the binary has not changed and the verdict takes effect at the next launch");
	}

	[Fact]
	public async Task ABuildBehindTheRecommendation_IsOfferedAnUpdateOnce()
	{
		(StartupCheckState state, _, InMemoryDeviceSettings settings) = Wire(
			new StartupCheck(ClientSupport.UpdateAvailable, "1.0.0.0", "9.0.0.0", []),
			platform: "Android - 14.0");

		await state.CheckAsync();

		state.UpdateOffered.ShouldBeTrue();
		state.UpdateUrl.ShouldBe(ClientRelease.PlayStoreUrl);

		await state.DismissAsync();

		state.UpdateOffered.ShouldBeFalse();
		(await settings.GetAsync(StartupCheckState.UpdateOfferKey)).ShouldBe("9.0.0.0");
	}

	[Fact]
	public async Task AnAnnouncementOutranksTheUpdateOffer()
	{
		(StartupCheckState state, _, _) = Wire(new StartupCheck(
			ClientSupport.UpdateAvailable, "1.0.0.0", "9.0.0.0", [Notices.Announcement("Server restart")]));

		await state.CheckAsync();

		state.UpdateOffered.ShouldBeFalse("one thing at a time, and a person outranks a version number");

		await state.DismissAsync();

		state.UpdateOffered.ShouldBeTrue();
	}

	private static (StartupCheckState State, FakeApiClient Api, InMemoryDeviceSettings Settings) Wire(
		StartupCheck? check,
		InMemoryDeviceSettings? settings = null,
		FakeTimeProvider? clock = null,
		string platform = "xunit")
	{
		FakeApiClient api = new() { StartupResult = check };
		InMemoryDeviceSettings store = settings ?? new InMemoryDeviceSettings();

		StartupCheckState state = new(
			api,
			store,
			clock ?? new FakeTimeProvider(Notices.Now),
			new FakeFormFactor { Platform = platform });

		return (state, api, store);
	}
}
