namespace BlazorDLR.Shared.Services;

/// <summary>
/// What the app says when somebody is about to report content (§10.2, §17.7).
/// <para>
/// <strong>One copy, for the reason <see cref="BlockPrompt"/> has one.</strong> Reporting is
/// offered on a post and on a marker, and the sentence is a statement about what the service will
/// do - it goes down at once, it stays down until we have looked at it, and two people see the
/// report. The two hand-written copies had already drifted apart in the commit that introduced
/// them, one saying "will see this report" and the other "sees the report".
/// </para>
/// <para>
/// The "hidden straight away" half is the part that must not be dropped: a reader who thought they
/// were only flagging something should know it disappears before they press it.
/// </para>
/// </summary>
public static class ReportPrompt
{
	/// <summary>The confirmation's title.</summary>
	/// <param name="what">What is being reported, lower case - "comment", "marker".</param>
	public static string Title(string what) => $"Report {what}?";

	/// <summary>
	/// The confirmation's body.
	/// </summary>
	/// <param name="subject">
	/// How to name the thing on this screen - "It", or a quoted marker name. The sentence reads on
	/// from it, so it starts the first clause.
	/// </param>
	public static string Body(string subject) =>
		$"{subject} is hidden from everyone straight away and stays hidden until we have reviewed "
		+ "it. The operator will see this report, and so will the organiser of the adventure.";
}
