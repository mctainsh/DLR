namespace BlazorDLR.Shared.Services;

/// <summary>
/// What the app says when somebody is about to block a traveller (§16.5, §17.7).
/// <para>
/// <strong>One copy of the promise, because there are now two places to make it.</strong> Blocking
/// is offered from the Live members list and from beside a post in a thread, and the sentence has to
/// say the same thing in both - it is a statement about what the service will do, and the two
/// wordings had already drifted apart on whether the block is announced and where it is undone.
/// This is the same argument <c>TermsGate</c> makes about its own sentence.
/// </para>
/// </summary>
public static class BlockPrompt
{
	/// <summary>The confirmation's title.</summary>
	/// <param name="userName">Who is about to be blocked.</param>
	public static string Title(string userName) => $"Block {userName}?";

	/// <summary>
	/// The confirmation's body: what stops being visible, that it is not announced, and how to undo
	/// it. All three matter - a rider who thinks blocking tells the other person will not use it.
	/// </summary>
	/// <param name="userName">Who is about to be blocked.</param>
	public static string Body(string userName) =>
		$"You will not see {userName} on the map, and they will not see you. Their posts, "
		+ "reactions, markers and poll votes are hidden from you too.\n"
		+ "They are not told. You can undo this in Settings → Blocked travellers.";
}
