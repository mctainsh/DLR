using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;

namespace DLR.UI.Tests.State;

/// <summary>
/// The ride the nav rail's globe leads back to (§18.6).
/// <para>
/// The persistence is one call into whichever platform store the host bound, so what is worth
/// asserting is the behaviour around it: that a device with nothing stored has a destination
/// anyway, that a ride opened outlives the process, that an unreadable value is the same as no
/// value, and that a read still in flight cannot overwrite a ride opened while it was running.
/// </para>
/// </summary>
public sealed class CurrentRideStateTests
{
	/// <summary>An <see cref="IDeviceSettings"/> that counts reads, records removals, and can be held open.</summary>
	private sealed class ControllableSettings : IDeviceSettings
	{
		private readonly InMemoryDeviceSettings _inner = new();
		private readonly List<string> _removed = [];

		public int Reads { get; private set; }

		public int Writes { get; private set; }

		public IReadOnlyList<string> Removed => _removed;

		/// <summary>Held by a test that wants a read to still be in flight when it acts.</summary>
		public TaskCompletionSource? Gate { get; set; }

		public async ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default)
		{
			Reads++;

			if (Gate is { } gate)
			{
				await gate.Task;
			}

			return await _inner.GetAsync(key, cancellationToken);
		}

		public ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default)
		{
			Writes++;
			return _inner.SetAsync(key, value, cancellationToken);
		}

		public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
		{
			_removed.Add(key);
			return _inner.RemoveAsync(key, cancellationToken);
		}
	}

	[Fact]
	public void BeforeLoad_TheGlobeStillHasSomewhereToGo()
	{
		CurrentRideState state = new(new ControllableSettings());

		// The rail renders on every page, including the prerender pass that cannot read a device
		// at all. "I do not know yet" and "there is no adventure" are the same destination — the list —
		// so a rail rendered cold is less specific, never broken.
		state.IsLoaded.ShouldBeFalse();
		state.RideId.ShouldBeNull();
		state.Href.ShouldBe("group-rides");
	}

	[Fact]
	public async Task ARideOpenedOnOneRun_IsThereOnTheNext()
	{
		// Two states over one store is what a restart looks like from here: the app that wrote it
		// is gone, and the one that reads it has only the device to go on.
		ControllableSettings settings = new();
		Guid rideId = Guid.NewGuid();

		await new CurrentRideState(settings).SetAsync(rideId);

		CurrentRideState relaunched = new(settings);
		await relaunched.LoadAsync();

		relaunched.RideId.ShouldBe(rideId);
		relaunched.Href.ShouldBe($"group-rides/live/{rideId}");
	}

	[Fact]
	public async Task Load_IsReadOnce_AndBroadcastsWhatItFound()
	{
		ControllableSettings settings = new();
		Guid rideId = Guid.NewGuid();
		await settings.SetAsync(CurrentRideState.StorageKey, rideId.ToString("N"));

		CurrentRideState state = new(settings);
		int changes = 0;
		state.Changed += () => changes++;

		await state.LoadAsync();
		await state.LoadAsync();

		settings.Reads.ShouldBe(1, "the rail must not pay for a device read every time it renders.");
		changes.ShouldBe(1);
		state.RideId.ShouldBe(rideId);
	}

	[Theory]
	[InlineData("")]
	[InlineData("not-a-ride")]
	[InlineData("00000000-0000-0000-0000-000000000000")]
	public async Task AnUnreadableValue_MeansNoRide(string stored)
	{
		// Same posture as PrivateArea.Decode: something we cannot read is a device that has not
		// opened a ride, which sends the rider to the list rather than to a route that 404s.
		ControllableSettings settings = new();
		await settings.SetAsync(CurrentRideState.StorageKey, stored);

		CurrentRideState state = new(settings);
		await state.LoadAsync();

		state.RideId.ShouldBeNull();
		state.Href.ShouldBe("group-rides");
	}

	[Fact]
	public async Task OpeningTheSameRideTwice_DoesNotWriteTwice()
	{
		// The live map re-supplies its parameters more than once per ride; on the web each write
		// is a JS interop round trip.
		ControllableSettings settings = new();
		CurrentRideState state = new(settings);
		Guid rideId = Guid.NewGuid();

		await state.SetAsync(rideId);
		await state.SetAsync(rideId);

		settings.Writes.ShouldBe(1);
	}

	[Fact]
	public async Task ARideOpenedWhileTheReadIsInFlight_Wins()
	{
		// The rail's read starts at first render and the page under it can open a ride before the
		// store answers. Applying the older value then would point the globe at the ride the rider
		// has just left — and it would do it silently.
		ControllableSettings settings = new();
		Guid previous = Guid.NewGuid();
		await settings.SetAsync(CurrentRideState.StorageKey, previous.ToString("N"));

		settings.Gate = new TaskCompletionSource();

		CurrentRideState state = new(settings);
		Task loading = state.LoadAsync();

		Guid opened = Guid.NewGuid();
		await state.SetAsync(opened);

		settings.Gate.SetResult();
		await loading;

		state.RideId.ShouldBe(opened);
	}

	[Fact]
	public async Task Clear_ForgetsTheKeyRatherThanStoringAnEmptyRide()
	{
		ControllableSettings settings = new();
		CurrentRideState state = new(settings);
		await state.SetAsync(Guid.NewGuid());

		await state.ClearAsync();

		state.RideId.ShouldBeNull();
		state.Href.ShouldBe("group-rides");
		settings.Removed.ShouldContain(CurrentRideState.StorageKey,
			"an empty id left behind is a value a later read has to be taught to disbelieve.");
	}

	[Fact]
	public async Task Forget_ClearsTheRideItNames()
	{
		ControllableSettings settings = new();
		CurrentRideState state = new(settings);
		Guid rideId = Guid.NewGuid();
		await state.SetAsync(rideId);

		await state.ForgetAsync(rideId);

		state.RideId.ShouldBeNull();
		settings.Removed.ShouldContain(CurrentRideState.StorageKey);
	}

	[Fact]
	public async Task Forget_LeavesADifferentRideAlone()
	{
		// A ride opening and another failing race each other on a slow connection: the rider taps
		// the globe, changes their mind and opens a second ride, and the first one's 404 lands
		// afterwards. Forgetting outright there would drop the ride they are now on.
		ControllableSettings settings = new();
		CurrentRideState state = new(settings);
		Guid current = Guid.NewGuid();
		await state.SetAsync(current);

		await state.ForgetAsync(Guid.NewGuid());

		state.RideId.ShouldBe(current);
		settings.Removed.ShouldBeEmpty();
	}

	[Fact]
	public async Task Forget_ReadsTheDeviceFirst_SoALaunchStraightIntoADeadRideStillForgetsIt()
	{
		// Nothing has called LoadAsync: the app was launched into a link to a ride that has since
		// been deleted, and the rail has not got round to its read. In memory there is no ride to
		// compare against, so the check has to go to the store.
		ControllableSettings settings = new();
		Guid rideId = Guid.NewGuid();
		await settings.SetAsync(CurrentRideState.StorageKey, rideId.ToString("N"));

		CurrentRideState state = new(settings);
		state.IsLoaded.ShouldBeFalse();

		await state.ForgetAsync(rideId);

		state.RideId.ShouldBeNull();
		settings.Removed.ShouldContain(CurrentRideState.StorageKey);
	}

	[Fact]
	public async Task Set_RefusesAnEmptyId()
	{
		CurrentRideState state = new(new ControllableSettings());

		await Should.ThrowAsync<ArgumentException>(() => state.SetAsync(Guid.Empty));
	}
}
