using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;

namespace DLR.UI.Tests.State;

/// <summary>
/// Whether this device has been shown the introduction (§18.6).
/// <para>
/// The persistence is one call into whichever platform store the host bound, so what is worth
/// asserting is the behaviour around it: that a device with nothing stored is shown the deck,
/// that a device that has been through it is not shown it again - including after a restart -
/// that a value it cannot read is the same as no value, and that resetting brings it back.
/// </para>
/// </summary>
public sealed class IntroTourStateTests
{
	/// <summary>An <see cref="IDeviceSettings"/> that records what was written and removed.</summary>
	private sealed class RecordingSettings : IDeviceSettings
	{
		private readonly InMemoryDeviceSettings _inner = new();
		private readonly List<string> _removed = [];

		public int Writes { get; private set; }

		public IReadOnlyList<string> Removed => _removed;

		public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
			_inner.GetAsync(key, cancellationToken);

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
	public async Task ADeviceWithNothingStored_IsShownTheIntroduction()
	{
		IntroTourState state = new(new RecordingSettings());

		(await state.ShouldShowAsync()).ShouldBeTrue(
			"a first run has nothing stored, and that is precisely the launch the deck exists for.");
	}

	[Fact]
	public async Task ADeviceThatHasSeenIt_IsNotShownItAgainOnTheNextLaunch()
	{
		// Two states over one store is what a restart looks like from here: the app that wrote it
		// is gone, and the one that reads it has only the device to go on.
		RecordingSettings settings = new();

		await new IntroTourState(settings).MarkSeenAsync();

		(await new IntroTourState(settings).ShouldShowAsync()).ShouldBeFalse(
			"the whole point of persisting it is that the second launch goes straight into the app.");
	}

	[Fact]
	public async Task MarkingItSeenTwice_WritesOnce()
	{
		// Skip, then Get started on a deck reopened from Settings - a device write per press, on
		// the phone a Preferences round trip, for a value that has not moved.
		RecordingSettings settings = new();
		IntroTourState state = new(settings);

		await state.MarkSeenAsync();
		await state.MarkSeenAsync();

		settings.Writes.ShouldBe(1);
	}

	[Fact]
	public async Task AnUnreadableValue_IsTheSameAsNoValue()
	{
		// A hand-edited localStorage entry, or a key this app never wrote. "Not seen" is the safe
		// reading: the cost of being wrong is one skippable screen.
		RecordingSettings settings = new();
		await settings.SetAsync(IntroTourState.StorageKey, "sometime last week");

		(await new IntroTourState(settings).ShouldShowAsync()).ShouldBeTrue();
	}

	[Fact]
	public async Task AnOlderDeck_IsSupersededByANewOne()
	{
		// What bumping IntroTour.Version buys: a device holding the version before this one is
		// shown the new deck once, rather than never.
		RecordingSettings settings = new();
		await settings.SetAsync(IntroTourState.StorageKey, (IntroTour.Version - 1).ToString());

		(await new IntroTourState(settings).ShouldShowAsync()).ShouldBeTrue();
	}

	[Fact]
	public async Task Resetting_RemovesTheKeyRatherThanStoringAZero()
	{
		RecordingSettings settings = new();
		IntroTourState state = new(settings);
		await state.MarkSeenAsync();

		await state.ResetAsync();

		settings.Removed.ShouldContain(IntroTourState.StorageKey);
		(await state.ShouldShowAsync()).ShouldBeTrue("a reset device is a device that has not seen the deck.");
	}

	[Fact]
	public void TheDeck_HasSlidesAndEveryOneOfThemSaysSomething()
	{
		// A deck that ships empty would strand the launch redirect on a page with nothing on it,
		// and this is the only place the content is checked at all.
		IntroTour.Slides.ShouldNotBeEmpty();

		foreach (IntroSlide slide in IntroTour.Slides)
		{
			slide.Icon.ShouldNotBeNullOrWhiteSpace();
			slide.Title.ShouldNotBeNullOrWhiteSpace();
			slide.Body.ShouldNotBeNullOrWhiteSpace();
		}
	}
}
