using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;

namespace DLR.UI.Tests.State;

/// <summary>
/// The gate every fix passes before it is recorded or sent (§10.1, §18.6).
/// <para>
/// The persistence is one call into whichever platform store the host bound. What is worth
/// asserting is the behaviour around it: that the answer before the device has been read is
/// the safe one, that the read happens once, and that removing the area forgets the key
/// rather than storing a "none" nobody can distinguish from a stale value.
/// </para>
/// </summary>
public sealed class PrivateAreaStateTests
{
	private static readonly PrivateArea Home = new(-33.868, 151.209, 1_000);

	/// <summary>An <see cref="IDeviceSettings"/> that counts reads and records removals.</summary>
	private sealed class CountingSettings : IDeviceSettings
	{
		private readonly InMemoryDeviceSettings _inner = new();
		private readonly List<string> _removed = [];

		public int Reads { get; private set; }

		public IReadOnlyList<string> Removed => _removed;

		public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default)
		{
			Reads++;
			return _inner.GetAsync(key, cancellationToken);
		}

		public ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default) =>
			_inner.SetAsync(key, value, cancellationToken);

		public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
		{
			_removed.Add(key);
			return _inner.RemoveAsync(key, cancellationToken);
		}
	}

	[Fact]
	public void BeforeLoad_EveryPositionIsHidden()
	{
		PrivateAreaState state = new(new CountingSettings());

		// The whole argument for this feature is that a fix from home must not escape. Until the
		// device has been read this object does not know whether there is an area, and the two
		// ways of being wrong are not equivalent: suppressing costs a moment of being unplaced,
		// publishing costs the thing the setting exists to protect.
		state.IsLoaded.ShouldBeFalse();
		state.HidesLocation(0, 0).ShouldBeTrue();
		state.HidesLocation(-33.868, 151.209).ShouldBeTrue();
	}

	[Fact]
	public async Task AfterLoad_WithNoAreaStored_NothingIsHidden()
	{
		PrivateAreaState state = new(new CountingSettings());

		await state.LoadAsync();

		state.IsSet.ShouldBeFalse();
		state.Area.ShouldBeNull();
		state.HidesLocation(-33.868, 151.209).ShouldBeFalse(
			"a device that never set an area shares from everywhere, which is the shipped state.");
	}

	[Fact]
	public async Task HidesLocation_IsTrueInside_AndFalseOutside()
	{
		PrivateAreaState state = new(new CountingSettings());
		await state.SetAsync(Home);

		state.HidesLocation(Home.Latitude, Home.Longitude).ShouldBeTrue();
		state.HidesLocation(-33.918, 151.209).ShouldBeFalse("~5.5 km away is outside a 1 km circle.");
	}

	[Fact]
	public async Task HidesLocation_AcceptsAFixStraightFromThePlatform()
	{
		PrivateAreaState state = new(new CountingSettings());
		await state.SetAsync(Home);

		LocationFix atHome = new(Home.Latitude, Home.Longitude, 5, 0, null, DateTimeOffset.UnixEpoch);
		LocationFix onTheRoad = new(-33.918, 151.209, 5, 12, 180, DateTimeOffset.UnixEpoch);

		// The recorder holds a LocationFix, not two doubles. An overload it does not have to
		// unpack is one fewer place to pass longitude where latitude was wanted.
		state.HidesLocation(atHome).ShouldBeTrue();
		state.HidesLocation(onTheRoad).ShouldBeFalse();
	}

	[Fact]
	public async Task LoadAsync_ReadsTheDeviceOnce_HoweverManyCallersAsk()
	{
		CountingSettings settings = new();
		await settings.SetAsync(PrivateAreaState.StorageKey, Home.Encode());

		PrivateAreaState state = new(settings);

		// The host's startup and the settings screen both call this without coordinating.
		await state.LoadAsync();
		await state.LoadAsync();
		await state.LoadAsync();

		settings.Reads.ShouldBe(1, "on the web each of these is a JS interop round trip.");
		state.Area!.Latitude.ShouldBe(Home.Latitude, tolerance: 1e-6);
	}

	[Fact]
	public async Task LoadAsync_BroadcastsOnce_SoAScreenAlreadyOpenCatchesUp()
	{
		CountingSettings settings = new();
		await settings.SetAsync(PrivateAreaState.StorageKey, Home.Encode());

		PrivateAreaState state = new(settings);

		int changes = 0;
		state.Changed += () => changes++;

		await state.LoadAsync();
		await state.LoadAsync();

		changes.ShouldBe(1);
	}

	[Fact]
	public async Task SetAsync_ClosesTheGateBeforeItPersists()
	{
		CountingSettings settings = new();
		PrivateAreaState state = new(settings);

		bool hiddenWhenAnnounced = false;
		state.Changed += () => hiddenWhenAnnounced = state.HidesLocation(Home.Latitude, Home.Longitude);

		await state.SetAsync(Home);

		hiddenWhenAnnounced.ShouldBeTrue(
			"the fix arriving during the storage round trip is the one the traveller just asked to hide.");

		PrivateAreaState reopened = new(settings);
		await reopened.LoadAsync();
		reopened.HidesLocation(Home.Latitude, Home.Longitude).ShouldBeTrue(
			"the point of the setting is that it survives a restart.");
	}

	[Fact]
	public async Task SetAsync_ClampsARadiusAControlHandsIt()
	{
		PrivateAreaState state = new(new CountingSettings());

		await state.SetAsync(Home with { RadiusM = 5 });

		state.Area!.RadiusM.ShouldBe(PrivateArea.MinRadiusM,
			"a typed radius is clamped rather than rejected — but the screen then reports what was stored.");
	}

	[Fact]
	public async Task SetAsync_RefusesACentreThatIsNotAPointOnTheEarth()
	{
		PrivateAreaState state = new(new CountingSettings());

		await Should.ThrowAsync<ArgumentException>(async () =>
			await state.SetAsync(new PrivateArea(double.NaN, 151.209, 1_000)));

		state.IsSet.ShouldBeFalse("a refused write must not leave a half-set area behind.");
	}

	[Fact]
	public async Task ClearAsync_ForgetsTheKey_RatherThanStoringANoneMarker()
	{
		CountingSettings settings = new();
		PrivateAreaState state = new(settings);

		await state.SetAsync(Home);
		await state.ClearAsync();

		state.IsSet.ShouldBeFalse();
		state.HidesLocation(Home.Latitude, Home.Longitude).ShouldBeFalse();
		settings.Removed.ShouldContain(PrivateAreaState.StorageKey);
		(await settings.GetAsync(PrivateAreaState.StorageKey)).ShouldBeNull();

		PrivateAreaState reopened = new(settings);
		await reopened.LoadAsync();
		reopened.IsSet.ShouldBeFalse();
	}
}
