using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;

namespace DLR.UI.Tests.State;

/// <summary>
/// Google Play's prominent disclosure (§4.3, §10.2).
/// <para>
/// The wording is asserted rather than merely the fact of a dialog, because a rejection here is a
/// rejection of the whole release and the copy is what gets read at review. Version 8.0.0.28 was
/// rejected over exactly this - the disclosure named live sharing and never mentioned that the same
/// fixes are written into a track, so one of the two uses of the collected data was undisclosed.
/// </para>
/// </summary>
public sealed class LocationDisclosureTests
{
	private static (LocationDisclosure Disclosure, ConfirmService Confirm, InMemoryDeviceSettings Settings) Build()
	{
		InMemoryDeviceSettings settings = new();
		ConfirmService confirm = new();

		return (new LocationDisclosure(settings, confirm), confirm, settings);
	}

	[Fact]
	public void TheCopy_NamesTheDataItself()
	{
		LocationDisclosure.Message.ShouldContain("collects location data",
			customMessage: "Play's finding against 8.0.0.28 was that the disclosure did not name what is collected.");
	}

	[Fact]
	public void TheCopy_NamesEveryUseTheDataIsPutTo()
	{
		// Two uses, not one. Every fix the receiver produces is offered to the recorder before any
		// publish gate sees it (§15.1), so a saved track is a second use of the same data - and it
		// is the one the rejected version left out.
		LocationDisclosure.Message.ShouldContain("show you to the other",
			customMessage: "live sharing is a use of the data and must be named.");
		LocationDisclosure.Message.ShouldContain("record the track",
			customMessage: "recording is the second use, and leaving it out is what 8.0.0.28 was rejected for.");
	}

	[Fact]
	public void TheCopy_CarriesPlaysRequiredBackgroundClause()
	{
		LocationDisclosure.Message.ShouldContain("even when the app is closed or not in use",
			customMessage: "Play checks this wording against a video at review.");
	}

	[Fact]
	public void TheCopy_KeepsTheRequiredClauseInTheSameSentenceAsTheUses()
	{
		// Split across paragraphs it stops being Play's form and becomes two claims that happen to
		// sit near each other. The first line has to carry the whole sentence.
		string opening = LocationDisclosure.Message.Split('\n')[0];

		opening.ShouldContain("collects location data");
		opening.ShouldContain("record the track");
		opening.ShouldContain("even when the app is closed or not in use");
	}

	[Fact]
	public async Task ADeviceThatHasNotBeenTold_IsToldBeforeItAnswers()
	{
		(LocationDisclosure disclosure, ConfirmService confirm, _) = Build();

		Task<bool> asking = disclosure.AcceptedAsync();

		confirm.Current.ShouldNotBeNull("nothing may reach the platform before the app has said what it does.");
		confirm.Current!.Title.ShouldBe(LocationDisclosure.Title);
		confirm.Current.CancelText.ShouldNotBeNullOrWhiteSpace(
			"Play requires an explicit way to decline, not only a way to agree.");

		confirm.Respond(true);

		(await asking).ShouldBeTrue();
	}

	[Fact]
	public async Task Accepting_IsRememberedSoTheDialogIsShownOncePerDevice()
	{
		(LocationDisclosure disclosure, ConfirmService confirm, _) = Build();

		Task<bool> first = disclosure.AcceptedAsync();
		confirm.Respond(true);
		await first;

		(await disclosure.AcceptedAsync()).ShouldBeTrue();
		confirm.Current.ShouldBeNull("asked every time, it becomes something people dismiss without reading.");
	}

	[Fact]
	public async Task Declining_IsNotRemembered()
	{
		(LocationDisclosure disclosure, ConfirmService confirm, InMemoryDeviceSettings settings) = Build();

		Task<bool> asking = disclosure.AcceptedAsync();
		confirm.Respond(false);

		(await asking).ShouldBeFalse();
		(await settings.GetAsync(LocationDisclosure.StorageKey)).ShouldBeNull(
			"a refusal is not an answer to keep - the next attempt has to put the words up again.");
	}

	[Fact]
	public async Task ADeviceThatAcceptedTheOldCopy_IsAskedAgain()
	{
		// The key is suffixed and the suffix moves when the disclosure gains a use. A device that
		// agreed to copy which never mentioned recording has not agreed to this.
		InMemoryDeviceSettings settings = new();
		await settings.SetAsync("dlr.location-disclosure", "1");

		ConfirmService confirm = new();
		LocationDisclosure disclosure = new(settings, confirm);

		Task<bool> asking = disclosure.AcceptedAsync();

		confirm.Current.ShouldNotBeNull();

		confirm.Respond(true);
		await asking;
	}
}
