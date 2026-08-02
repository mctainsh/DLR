namespace DLR.Server.Data.Tracks;

/// <summary>
/// The pre-edit original, kept for the undo window (§15.6).
/// <para>
/// Edits are destructive and the motivating use case is privacy, which pulls in two directions:
/// keeping the original forever makes trimming your house meaningless, and keeping nothing makes
/// a misclick unrecoverable. Seven days is the resolution.
/// </para>
/// <para>
/// <strong>Exactly one row per track, at most.</strong> A second edit inside the window replaces
/// the retained original and restarts the clock. Undo is a safety net for the last action, not a
/// history feature — and unbounded revisions would quietly triple storage on a €4 VPS.
/// </para>
/// </summary>
public sealed class TrackRevision
{
	/// <summary>The track this is the previous version of, and the primary key.</summary>
	public Guid TrackId { get; set; }

	/// <summary>The version number the retained blob was.</summary>
	public int Version { get; set; }

	/// <summary>Where the pre-edit points are (§9.1).</summary>
	public string BlobRef { get; set; } = string.Empty;

	/// <summary>When the edit that displaced it happened.</summary>
	public DateTimeOffset ReplacedUtc { get; set; }

	/// <summary>
	/// When the nightly job may delete it (§7.11).
	/// <para>
	/// Also what <c>undo</c> checks: past this instant the original is gone as far as the API is
	/// concerned, whether or not the sweep has run yet. Two answers to "can I undo" — one from
	/// the clock and one from whether a job happened to fire — is one answer too many.
	/// </para>
	/// </summary>
	public DateTimeOffset PurgeAfterUtc { get; set; }

	/// <summary>The track, for cascade deletion.</summary>
	public Track? Track { get; set; }
}
