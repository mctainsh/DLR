using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;

namespace DLR.UI.Tests.State;

/// <summary>
/// Which adventures this device has already put the consent prompt up for (§5.6, §18.6).
/// <para>
/// This is a consent gate, so both ways of being wrong are asserted rather than assumed: never
/// asking is a rider silently left off the map they would have agreed to be on, and asking on
/// every load is the nag that teaches people to dismiss the prompt without reading it.
/// </para>
/// </summary>
public sealed class ConsentAskedStateTests
{
	private static readonly Guid TheRide = Guid.NewGuid();
	private static readonly Guid AnotherRide = Guid.NewGuid();

	private static async Task<ConsentAskedState> LoadedAsync(InMemoryDeviceSettings settings)
	{
		ConsentAskedState state = new(settings);

		await state.LoadAsync();

		return state;
	}

	[Fact]
	public async Task ADeviceThatHasAskedNobody_AsksAboutEveryAdventure()
	{
		ConsentAskedState state = await LoadedAsync(new InMemoryDeviceSettings());

		state.WasAsked(TheRide).ShouldBeFalse();
		state.WasAsked(AnotherRide).ShouldBeFalse();
	}

	[Fact]
	public async Task AnAdventureThatWasAsked_IsNotAskedAgain()
	{
		InMemoryDeviceSettings settings = new();

		ConsentAskedState state = await LoadedAsync(settings);

		await state.MarkAskedAsync(TheRide);

		state.WasAsked(TheRide).ShouldBeTrue();
		state.WasAsked(AnotherRide).ShouldBeFalse(
			"§5.6: the decision is per adventure - answering for one says nothing about another.");
	}

	[Fact]
	public async Task TheAnswer_SurvivesARelaunch()
	{
		InMemoryDeviceSettings settings = new();

		ConsentAskedState first = await LoadedAsync(settings);
		await first.MarkAskedAsync(TheRide);

		// A second instance over the same store is what the next launch sees.
		ConsentAskedState next = await LoadedAsync(settings);

		next.WasAsked(TheRide).ShouldBeTrue(
			"§18.6: a prompt answered before the phone was reclaimed must not come back on the next launch.");
	}

	[Fact]
	public async Task ForgettingAnAdventure_AsksAgain()
	{
		InMemoryDeviceSettings settings = new();

		ConsentAskedState state = await LoadedAsync(settings);
		await state.MarkAskedAsync(TheRide);

		await state.ForgetAsync(TheRide);

		state.WasAsked(TheRide).ShouldBeFalse(
			"a rider who rejoins an adventure they were removed from is asked afresh.");

		ConsentAskedState next = await LoadedAsync(settings);

		next.WasAsked(TheRide).ShouldBeFalse("and the device store agrees.");
	}

	[Fact]
	public async Task TheStoreIsBounded_AndDropsTheLeastRecentlyAsked()
	{
		InMemoryDeviceSettings settings = new();

		ConsentAskedState state = await LoadedAsync(settings);

		Guid first = Guid.NewGuid();

		await state.MarkAskedAsync(first);

		for (int index = 0; index < ConsentAskedState.MaxTracked; index++)
		{
			await state.MarkAskedAsync(Guid.NewGuid());
		}

		state.WasAsked(first).ShouldBeFalse(
			"the cap is what stops a year of adventures accumulating in a store nothing sweeps; "
			+ "being asked once more about the oldest is the right way for it to fail.");
	}

	[Fact]
	public async Task AnEmptyStore_RemovesTheKeyRatherThanWritingNothing()
	{
		InMemoryDeviceSettings settings = new();

		ConsentAskedState state = await LoadedAsync(settings);

		await state.MarkAskedAsync(TheRide);
		await state.ForgetAsync(TheRide);

		(await settings.GetAsync(ConsentAskedState.StorageKey)).ShouldBeNull();
	}

	[Theory]
	[InlineData("")]
	[InlineData("not-a-version")]
	[InlineData("2|ffffffffffffffffffffffffffffffff")]
	[InlineData("1|00000000000000000000000000000000")]
	[InlineData("1|half-written")]
	public async Task AValueThisVersionCannotRead_AsksRatherThanStaysSilent(string stored)
	{
		InMemoryDeviceSettings settings = new();

		await settings.SetAsync(ConsentAskedState.StorageKey, stored);

		ConsentAskedState state = await LoadedAsync(settings);

		state.WasAsked(TheRide).ShouldBeFalse(
			"§5.6: of the two ways to misread the store, asking twice is the one that does not "
			+ "quietly drop a consent prompt.");
	}

	[Fact]
	public async Task ASecondLoad_JoinsTheFirstRatherThanReadingAgain()
	{
		InMemoryDeviceSettings settings = new();

		await settings.SetAsync(ConsentAskedState.StorageKey, "1|" + TheRide.ToString("N"));

		ConsentAskedState state = new(settings);

		await Task.WhenAll(state.LoadAsync(), state.LoadAsync());

		state.WasAsked(TheRide).ShouldBeTrue(
			"a caller that arrives while the read is in flight must not conclude nobody was asked.");
	}
}
