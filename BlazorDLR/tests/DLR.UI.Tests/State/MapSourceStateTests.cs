using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using DLR.UI.Tests.Fakes;

namespace DLR.UI.Tests.State;

/// <summary>
/// The tile source held in memory and broadcast (§4.5) — the same shape as
/// <see cref="RouteStyleState"/>, plus the one thing that is specific to maps: what a device does
/// with an offline source it cannot serve.
/// </summary>
public sealed class MapSourceStateTests
{
	private static readonly MapSource Custom =
		MapSource.Custom("https://tiles.example.com/{z}/{x}/{y}.png", "© Example", 18);

	/// <summary>A phone: a device store, and somewhere to keep a pack.</summary>
	private static (MapSourceState State, InMemoryDeviceSettings Settings) Phone()
	{
		InMemoryDeviceSettings settings = new();
		return (new MapSourceState(settings, new FakeMapPackStore()), settings);
	}

	/// <summary>A browser: a device store, but nowhere to keep a pack (§18.6).</summary>
	private static (MapSourceState State, InMemoryDeviceSettings Settings) Browser()
	{
		InMemoryDeviceSettings settings = new();
		return (new MapSourceState(settings, new UnavailableMapPackStore()), settings);
	}

	[Fact]
	public async Task ADeviceThatHasNeverChosen_GetsOpenStreetMap()
	{
		(MapSourceState state, _) = Phone();

		await state.LoadAsync();

		state.Chosen.ShouldBe(MapSource.Default);
		state.Effective.ShouldBe(MapSource.Default);
	}

	[Fact]
	public async Task AChoiceSurvivesTheProcess()
	{
		(MapSourceState first, InMemoryDeviceSettings settings) = Phone();

		await first.SetAsync(Custom);

		// A second state over the same store is how a test spells "the app was restarted".
		MapSourceState second = new(settings, new FakeMapPackStore());
		await second.LoadAsync();

		second.Chosen.ShouldBe(Custom);
		second.Effective.ShouldBe(Custom);
	}

	[Fact]
	public async Task SettingIt_BroadcastsSoAnOpenMapRestyles()
	{
		(MapSourceState state, _) = Phone();
		await state.LoadAsync();

		int changes = 0;
		state.Changed += () => changes++;

		await state.SetAsync(Custom);

		changes.ShouldBe(1,
			"RideMap listens to this — without the broadcast the settings screen's preview would not move.");
	}

	[Fact]
	public async Task SettingTheSameSourceTwice_DoesNotBroadcastAgain()
	{
		(MapSourceState state, _) = Phone();
		await state.SetAsync(Custom);

		int changes = 0;
		state.Changed += () => changes++;

		await state.SetAsync(Custom);

		changes.ShouldBe(0, "a value that has not moved must not restyle a map that is already right.");
	}

	[Fact]
	public async Task AnUnusableSourceIsRefused_RatherThanStored()
	{
		(MapSourceState state, InMemoryDeviceSettings settings) = Phone();

		await Should.ThrowAsync<ArgumentException>(
			() => state.SetAsync(MapSource.Custom("http://tiles.example.com/{z}/{x}/{y}.png", "© Example")));

		state.Chosen.ShouldBe(MapSource.Default, "the state keeps what was working.");
		(await settings.GetAsync(MapSource.StorageKey)).ShouldBeNull("and nothing reached the device.");
	}

	[Fact]
	public async Task OnAHostWithNowhereToKeepAPack_OfflineDrawsOpenStreetMapInstead()
	{
		// §18.6: offline is a phone property. A rider who set a pack on their phone and then opens
		// the site must get a working map, not a blank one — the routes and pins drawn on top are
		// what the screen is for.
		(MapSourceState phone, InMemoryDeviceSettings settings) = Phone();
		await phone.SetAsync(MapSource.OfflinePack("au-nsw"));

		MapSourceState browser = new(settings, new UnavailableMapPackStore());
		await browser.LoadAsync();

		browser.CanUseOffline.ShouldBeFalse();
		browser.Chosen.Kind.ShouldBe(MapSourceKind.Offline, "what the rider picked is not overwritten…");
		browser.Effective.ShouldBe(MapSource.Default, "…but it is not what gets drawn here.");
	}

	[Fact]
	public async Task OnAPhone_OfflineIsWhatGetsDrawn()
	{
		(MapSourceState state, _) = Phone();

		await state.SetAsync(MapSource.OfflinePack("au-nsw"));

		state.CanUseOffline.ShouldBeTrue();
		state.Effective.Kind.ShouldBe(MapSourceKind.Offline);
	}

	[Fact]
	public async Task Load_IsIdempotent_SoEveryMapOnThePageCanCallIt()
	{
		(MapSourceState state, InMemoryDeviceSettings settings) = Browser();
		await settings.SetAsync(MapSource.StorageKey, Custom.Encode());

		await state.LoadAsync();
		await state.LoadAsync();

		state.Chosen.ShouldBe(Custom);
		state.IsLoaded.ShouldBeTrue();
	}

	[Fact]
	public async Task AChoiceMadeWhileTheDeviceReadIsInFlight_IsNotOverwritten()
	{
		// The settings screen can be quicker than the store on a cold start. What is in memory is
		// newer than what the device held, and applying the older value would silently undo a
		// choice the rider had just made.
		(MapSourceState state, InMemoryDeviceSettings settings) = Phone();
		await settings.SetAsync(MapSource.StorageKey, MapSource.OfflinePack("au-nsw").Encode());

		Task load = state.LoadAsync();
		await state.SetAsync(Custom);
		await load;

		state.Chosen.ShouldBe(Custom);
	}

	[Fact]
	public async Task Reset_GoesBackToTheDefault_AndForgetsTheKey()
	{
		(MapSourceState state, InMemoryDeviceSettings settings) = Phone();
		await state.SetAsync(Custom);

		await state.ResetAsync();

		state.Chosen.ShouldBe(MapSource.Default);
		(await settings.GetAsync(MapSource.StorageKey)).ShouldBeNull(
			"the key is removed rather than set to today's default — a later change to what ships must reach this device.");
	}
}
