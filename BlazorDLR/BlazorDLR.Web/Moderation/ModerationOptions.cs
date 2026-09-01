namespace DLR.Server.Moderation;

/// <summary>
/// How long moderation evidence is kept (§17.7).
/// <para>
/// Its own section rather than a key under <c>Maintenance</c>, because this is a moderation policy
/// decision that the nightly job happens to enforce - the operator who wants to argue about the
/// number is arguing about how long a copy of deleted content may exist, not about what time the
/// sweep runs.
/// </para>
/// </summary>
public sealed class ModerationOptions
{
	/// <summary>Configuration section name.</summary>
	public const string Section = "Moderation";

	/// <summary>
	/// Days a <em>resolved</em> report and its content snapshot are kept before the nightly job
	/// takes them (§7.11, §17.7).
	/// <para>
	/// Only resolved ones. An open report is evidence somebody is still waiting on, and ageing it
	/// out would make a backlog into a silent amnesty. The snapshot is a copy of content that may
	/// well have been deleted from everywhere else, so keeping it forever is its own privacy
	/// problem - which is the whole reason there is a number here at all.
	/// </para>
	/// </summary>
	public int ReportRetentionDays { get; set; } = 90;
}
