using BlazorDLR.Shared.Services;

namespace BlazorDLR.Shared.State;

/// <summary>
/// Google Play's <em>prominent disclosure</em>: the app's own account of what it collects and what
/// it does with it, shown before the platform's permission dialog is ever reached (§4.3, §10.2).
/// <para>
/// <strong>A store requirement with a specific shape, not a nicety.</strong> Play's policy requires
/// the app's own UI to name the data - location - and <em>every</em> use it is put to, in the form
/// "collects location data to enable [feature], even when the app is closed or not in use", with an
/// explicit accept and deny, before the runtime permission request. It is checked against a video at
/// review, and an app that goes straight to the system dialog is rejected however good its
/// in-context copy is elsewhere. See Documentation/store-release.md.
/// </para>
/// <para>
/// <strong>Its own type, so that there is one copy of the words and one gate in front of every
/// route to a fix.</strong> The disclosure used to live inside <see cref="LocationBroadcastState"/>,
/// which covers sharing and recording but not <c>My adventures</c> asking the platform for one fix
/// to centre a search on - and that route reaches the same three-rung Android permission ladder,
/// background rung included, with nothing said first. Version 8.0.0.28 was rejected for exactly
/// that class of hole.
/// </para>
/// <para>
/// Asked once per device and remembered. Repeating it every adventure would train travellers to
/// dismiss it, which is the opposite of what a disclosure is for.
/// </para>
/// </summary>
/// <param name="settings">Where the acknowledgement is remembered.</param>
/// <param name="confirm">The app's one dialog.</param>
public sealed class LocationDisclosure(IDeviceSettings settings, ConfirmService confirm)
{
	/// <summary>
	/// Marks that this device has been shown the disclosure below and accepted it. Device-local: it
	/// is a statement made to the person holding the phone, and a new phone has been told nothing.
	/// <para>
	/// <strong>Suffixed, and the suffix moves when the disclosure gains a use.</strong> The key was
	/// <c>dlr.location-disclosure</c> against copy that named live sharing and never mentioned that
	/// the same fixes are written into a track. A device that accepted <em>that</em> has not been
	/// told what this one says, so it is asked again rather than being counted as having agreed to
	/// something it never read.
	/// </para>
	/// </summary>
	public const string StorageKey = "dlr.location-disclosure.2";

	/// <summary>
	/// The heading. States the collection outright rather than asking a question about sharing -
	/// what Play found inadequate in 8.0.0.28 was a disclosure that read as a feature prompt.
	/// </summary>
	public const string Title = "Dumb Luck Routes collects location data";

	/// <summary>
	/// The disclosure itself, one paragraph per line (<c>ConfirmDialog</c> renders them as such).
	/// <para>
	/// <strong>The first paragraph is Play's required form and every clause in it is load-bearing:
	/// what is collected, both things it is used for, and "even when the app is closed or not in
	/// use". Revise the rest freely; leave that sentence alone.</strong> The recording half is the
	/// one the rejected version was missing - every fix the receiver produces is offered to the
	/// recorder before any publish gate sees it (§15.1), so a track is a second use of the same
	/// data and has to be disclosed as one.
	/// </para>
	/// </summary>
	public const string Message =
		"Dumb Luck Routes collects location data - your precise position - to show you to the other "
		+ "members of the group adventures you turn sharing on for, and to record the track of where "
		+ "you went, even when the app is closed or not in use.\n"
		+ "Your position goes to the members of those adventures and to nobody else. It is never sold, "
		+ "never used for advertising, and never given to a data broker or an analytics service.\n"
		+ "Your track is written to this phone. It stays there until you save it, and saving offers to "
		+ "cut out the private area you can set around home first.\n"
		// "Off until you turn it on" was true until joining on a phone started turning it on
		// (JoinRide.ShareByDefaultAsync). A disclosure that describes a default the app no longer has
		// is the one sentence here a reviewer can catch it out on.
		+ "Sharing is per adventure. It starts on for an adventure you join on this phone, the map says "
		+ "so in red whenever it is off, and you can turn it off at any time. Nothing is sent from "
		+ "inside your private area.";

	/// <summary>
	/// Shows the disclosure if this device has not already accepted it, and answers whether the app
	/// may go on to ask the platform for location.
	/// </summary>
	/// <param name="cancellationToken">Abandons the device-store reads and writes.</param>
	/// <returns>True when the traveller accepted, or had already accepted on this device.</returns>
	public async Task<bool> AcceptedAsync(CancellationToken cancellationToken = default)
	{
		if (await settings.GetAsync(StorageKey, cancellationToken) == "1")
			return true;

		bool accepted = await confirm.AskAsync(
			new ConfirmRequest(Title, Message, ConfirmText: "I agree", CancelText: "No thanks"));

		if (accepted)
			await settings.SetAsync(StorageKey, "1", cancellationToken);

		return accepted;
	}
}
