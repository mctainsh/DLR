using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using DLR.Core.Contracts.Identity;
using DLR.UI.Tests.Fakes;

namespace DLR.UI.Tests.State;

/// <summary>
/// The gate every fix passes before it is recorded or sent (§10.1).
/// <para>
/// The area lives on the rider's account and is cached on the device, which is the reversal of
/// the original design: device-only storage lost people their circle to app updates and
/// reinstalls, in silence. What is worth asserting is the behaviour around that pair — that the
/// answer before either has spoken is the safe one, that the account wins when both have, that
/// a phone with no signal still hides what it hid yesterday, and that "the account has no area"
/// is never confused with "this device has not been told".
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

	/// <summary>The rider's account, and one device signed in to it.</summary>
	private static (PrivateAreaState State, CountingSettings Settings, FakeApiClient Api) Device(FakeApiClient? account = null)
	{
		CountingSettings settings = new();
		FakeApiClient api = account ?? new FakeApiClient();

		return (new PrivateAreaState(settings, api), settings, api);
	}

	[Fact]
	public void BeforeLoad_EveryPositionIsHidden()
	{
		(PrivateAreaState state, _, _) = Device();

		// The whole argument for this feature is that a fix from home must not escape. Until
		// either the account or this device's cache has answered, this object does not know
		// whether there is an area, and the two ways of being wrong are not equivalent:
		// suppressing costs a moment of being unplaced, publishing costs the thing the setting
		// exists to protect.
		state.IsLoaded.ShouldBeFalse();
		state.HidesLocation(0, 0).ShouldBeTrue();
		state.HidesLocation(-33.868, 151.209).ShouldBeTrue();
	}

	[Fact]
	public async Task AfterLoad_WithNoAreaOnTheAccount_NothingIsHidden()
	{
		(PrivateAreaState state, _, _) = Device();

		await state.LoadAsync();

		state.IsSet.ShouldBeFalse();
		state.Area.ShouldBeNull();
		state.IsFromAccount.ShouldBeTrue();
		state.HidesLocation(-33.868, 151.209).ShouldBeFalse(
			"an account that never set an area shares from everywhere, which is the shipped state.");
	}

	[Fact]
	public async Task HidesLocation_IsTrueInside_AndFalseOutside()
	{
		(PrivateAreaState state, _, _) = Device();
		await state.SetAsync(Home);

		state.HidesLocation(Home.Latitude, Home.Longitude).ShouldBeTrue();
		state.HidesLocation(-33.918, 151.209).ShouldBeFalse("~5.5 km away is outside a 1 km circle.");
	}

	[Fact]
	public async Task HidesLocation_AcceptsAFixStraightFromThePlatform()
	{
		(PrivateAreaState state, _, _) = Device();
		await state.SetAsync(Home);

		LocationFix atHome = new(Home.Latitude, Home.Longitude, 5, 0, null, DateTimeOffset.UnixEpoch);
		LocationFix onTheRoad = new(-33.918, 151.209, 5, 12, 180, DateTimeOffset.UnixEpoch);

		// The recorder holds a LocationFix, not two doubles. An overload it does not have to
		// unpack is one fewer place to pass longitude where latitude was wanted.
		state.HidesLocation(atHome).ShouldBeTrue();
		state.HidesLocation(onTheRoad).ShouldBeFalse();
	}

	[Fact]
	public async Task SetAsync_PutsTheAreaOnTheAccount_SoTheNextDeviceHasIt()
	{
		FakeApiClient account = new();

		(PrivateAreaState phone, _, _) = Device(account);
		await phone.SetAsync(Home);

		// The rider signs in on a second handset. Nothing of theirs has ever touched this
		// device's store — and that used to be the end of the private area.
		(PrivateAreaState newPhone, _, _) = Device(account);
		await newPhone.LoadAsync();

		newPhone.IsSet.ShouldBeTrue();
		newPhone.Area!.Latitude.ShouldBe(Home.Latitude, tolerance: 1e-6);
		newPhone.HidesLocation(Home.Latitude, Home.Longitude).ShouldBeTrue(
			"the whole point of moving this to the account is that a new phone arrives already hiding.");
	}

	[Fact]
	public async Task LoadAsync_AsksTheAccountOnce_HoweverManyCallersAsk()
	{
		FakeApiClient account = new() { PrivateAreaResult = Home.ToSettings() };
		(PrivateAreaState state, CountingSettings settings, _) = Device(account);

		// The host's startup and the settings screen both call this without coordinating.
		await state.LoadAsync();
		await state.LoadAsync();
		await state.LoadAsync();

		account.PrivateAreaReads.ShouldBe(1, "each of these is a request over the rider's mobile data.");
		settings.Reads.ShouldBe(1, "on the web the cache read is a JS interop round trip.");
		state.Area!.Latitude.ShouldBe(Home.Latitude, tolerance: 1e-6);
	}

	[Fact]
	public async Task LoadAsync_BroadcastsOnce_SoAScreenAlreadyOpenCatchesUp()
	{
		FakeApiClient account = new() { PrivateAreaResult = Home.ToSettings() };
		(PrivateAreaState state, _, _) = Device(account);

		int changes = 0;
		state.Changed += () => changes++;

		await state.LoadAsync();
		await state.LoadAsync();

		changes.ShouldBe(1);
	}

	[Fact]
	public async Task LoadAsync_WithNoNetwork_UsesTheCircleThisDeviceCachedLastTime()
	{
		FakeApiClient account = new();
		(PrivateAreaState state, CountingSettings settings, _) = Device(account);
		await state.SetAsync(Home);

		// Same handset tomorrow, in a tunnel. The gate has to keep answering: a rider standing at
		// home with no signal is exactly when getting this wrong matters.
		account.PrivateAreaException = new HttpRequestException("no network");

		PrivateAreaState relaunched = new(settings, account);
		await relaunched.LoadAsync();

		relaunched.IsLoaded.ShouldBeTrue();
		relaunched.IsFromAccount.ShouldBeFalse("the screen has to be able to say which copy it is showing.");
		relaunched.SyncError.ShouldNotBeNull();
		relaunched.HidesLocation(Home.Latitude, Home.Longitude).ShouldBeTrue();
	}

	[Fact]
	public async Task LoadAsync_OnAnUntoldDeviceWithNoNetwork_KeepsTheGateShut()
	{
		FakeApiClient account = new()
		{
			PrivateAreaResult = Home.ToSettings(),
			PrivateAreaException = new HttpRequestException("no network"),
		};

		(PrivateAreaState state, _, _) = Device(account);
		await state.LoadAsync();

		// Nothing cached and nothing answered: this device genuinely does not know. Suppressing
		// costs nothing real here — publishing a position needs the same network the read just
		// failed on — and the alternative is broadcasting from a doorstep the account knows about.
		state.IsLoaded.ShouldBeFalse();
		state.HidesLocation(Home.Latitude, Home.Longitude).ShouldBeTrue();
		state.HidesLocation(0, 0).ShouldBeTrue();
	}

	[Fact]
	public async Task LoadAsync_RetriesUntilTheAccountAnswers()
	{
		FakeApiClient account = new()
		{
			PrivateAreaResult = Home.ToSettings(),
			PrivateAreaException = new HttpRequestException("no network"),
		};

		(PrivateAreaState state, _, _) = Device(account);
		await state.LoadAsync();

		account.PrivateAreaException = null;
		await state.LoadAsync();

		state.IsFromAccount.ShouldBeTrue();
		state.SyncError.ShouldBeNull();
		state.IsSet.ShouldBeTrue(
			"a rider must not be stuck behind a shut gate for the rest of the session over one failed request.");
	}

	[Fact]
	public async Task LoadAsync_PrefersTheAccountOverAStaleCache()
	{
		FakeApiClient account = new();
		(PrivateAreaState phone, CountingSettings settings, _) = Device(account);
		await phone.SetAsync(Home);

		// The rider moved house on their other handset.
		PrivateArea moved = new(-37.814, 144.963, 2_000);
		account.PrivateAreaResult = moved.ToSettings();

		PrivateAreaState relaunched = new(settings, account);
		await relaunched.LoadAsync();

		relaunched.Area!.Latitude.ShouldBe(moved.Latitude, tolerance: 1e-6);
		relaunched.HidesLocation(Home.Latitude, Home.Longitude).ShouldBeFalse(
			"the account is the source of truth; the device store is a cache of it.");
	}

	[Fact]
	public async Task SetAsync_ClosesTheGateBeforeItPersists()
	{
		FakeApiClient account = new();
		(PrivateAreaState state, CountingSettings settings, _) = Device(account);

		bool hiddenWhenAnnounced = false;
		state.Changed += () => hiddenWhenAnnounced = state.HidesLocation(Home.Latitude, Home.Longitude);

		await state.SetAsync(Home);

		hiddenWhenAnnounced.ShouldBeTrue(
			"the fix arriving during the round trip is the one the traveller just asked to hide.");

		PrivateAreaState reopened = new(settings, account);
		await reopened.LoadAsync();
		reopened.HidesLocation(Home.Latitude, Home.Longitude).ShouldBeTrue(
			"the point of the setting is that it survives a restart.");
	}

	[Fact]
	public async Task SetAsync_WithNoNetwork_StillProtectsThisPhone_AndSaysItDidNotTravel()
	{
		FakeApiClient account = new() { PrivateAreaException = new HttpRequestException("no network") };
		(PrivateAreaState state, CountingSettings settings, _) = Device(account);

		await Should.ThrowAsync<HttpRequestException>(async () => await state.SetAsync(Home));

		// Thrown so the screen can say the account has not got it — but the circle is in force
		// here and cached here, because the rider standing in it asked for that now.
		state.HidesLocation(Home.Latitude, Home.Longitude).ShouldBeTrue();
		state.IsFromAccount.ShouldBeFalse();

		PrivateAreaState relaunched = new(settings, account);
		await relaunched.LoadAsync();
		relaunched.HidesLocation(Home.Latitude, Home.Longitude).ShouldBeTrue();
	}

	[Fact]
	public async Task SetAsync_ClampsARadiusAControlHandsIt()
	{
		(PrivateAreaState state, _, _) = Device();

		await state.SetAsync(Home with { RadiusM = 5 });

		state.Area!.RadiusM.ShouldBe(PrivateArea.MinRadiusM,
			"a typed radius is clamped rather than rejected — but the screen then reports what was stored.");
	}

	[Fact]
	public async Task SetAsync_RefusesACentreThatIsNotAPointOnTheEarth()
	{
		(PrivateAreaState state, _, FakeApiClient account) = Device();

		await Should.ThrowAsync<ArgumentException>(async () =>
			await state.SetAsync(new PrivateArea(double.NaN, 151.209, 1_000)));

		state.IsSet.ShouldBeFalse("a refused write must not leave a half-set area behind.");
		account.PrivateAreaResult.ShouldBeNull("and it must not have reached the account either.");
	}

	[Fact]
	public async Task ClearAsync_ForgetsItOnTheAccount_AndOnThisDevice()
	{
		FakeApiClient account = new();
		(PrivateAreaState state, CountingSettings settings, _) = Device(account);

		await state.SetAsync(Home);
		await state.ClearAsync();

		state.IsSet.ShouldBeFalse();
		state.HidesLocation(Home.Latitude, Home.Longitude).ShouldBeFalse();
		account.PrivateAreaResult.ShouldBeNull();

		PrivateAreaState reopened = new(settings, account);
		await reopened.LoadAsync();
		reopened.IsSet.ShouldBeFalse();
	}

	[Fact]
	public async Task ClearAsync_CachesAnExplicitNone_RatherThanForgettingTheKey()
	{
		FakeApiClient account = new();
		(PrivateAreaState state, CountingSettings settings, _) = Device(account);

		await state.SetAsync(Home);
		await state.ClearAsync();

		// This reverses the original decision, which removed the key. Removing it makes a rider
		// who deliberately cleared their area indistinguishable from a device that has never
		// asked — and the gate treats those differently on purpose, so that rider would find
		// themselves silently unable to share the next time they opened the app offline.
		settings.Removed.ShouldNotContain(PrivateAreaState.StorageKey);
		(await settings.GetAsync(PrivateAreaState.StorageKey)).ShouldBe(PrivateArea.NoneMarker);

		account.PrivateAreaException = new HttpRequestException("no network");

		PrivateAreaState offline = new(settings, account);
		await offline.LoadAsync();

		offline.IsLoaded.ShouldBeTrue();
		offline.HidesLocation(Home.Latitude, Home.Longitude).ShouldBeFalse(
			"they removed it; a tunnel must not put it back.");
	}

	[Fact]
	public async Task ClearAsync_WithNoNetwork_StopsHidingHere_AndSaysSoLoudly()
	{
		FakeApiClient account = new();
		(PrivateAreaState state, _, _) = Device(account);
		await state.SetAsync(Home);

		account.PrivateAreaException = new HttpRequestException("no network");

		await Should.ThrowAsync<HttpRequestException>(async () => await state.ClearAsync());

		state.IsSet.ShouldBeFalse();
		state.IsFromAccount.ShouldBeFalse();
		state.SyncError.ShouldNotBeNull("the screen has to say the rider's other devices are still hiding it.");
	}

	[Fact]
	public void TryDecodeCached_SeparatesNoArea_FromNotYetTold()
	{
		// The three states the cache has to carry. Decode collapses the first two, which is why
		// the gate reads this one instead.
		PrivateArea.TryDecodeCached(Home.Encode(), out PrivateArea? stored).ShouldBeTrue();
		stored.ShouldNotBeNull();

		PrivateArea.TryDecodeCached(PrivateArea.NoneMarker, out PrivateArea? none).ShouldBeTrue();
		none.ShouldBeNull();

		PrivateArea.TryDecodeCached(null, out PrivateArea? untold).ShouldBeFalse();
		untold.ShouldBeNull();

		PrivateArea.TryDecodeCached("2|whatever", out PrivateArea? unreadable).ShouldBeFalse(
			"an unreadable cache is not an account saying \"none\" — ask the server.");
		unreadable.ShouldBeNull();
	}

	[Fact]
	public void RoundTrip_KeepsTheCentreWhereTheRiderPutIt()
	{
		PrivateArea area = new(-33.8688197, 151.2092955, 750);

		PrivateArea? read = PrivateArea.Decode(area.Encode());

		// Six decimals, which is about 0.1 m. The coordinate is a house and the rider is lining
		// the circle up with their own roof, so a coarser field would walk it on every save.
		read.ShouldNotBeNull();
		read.Latitude.ShouldBe(area.Latitude, tolerance: 1e-6);
		read.Longitude.ShouldBe(area.Longitude, tolerance: 1e-6);
		read.RadiusM.ShouldBe(750);
	}
}
