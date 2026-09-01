using DLR.Core.Markers;

namespace DLR.Core.Tracks;

/// <summary>
/// What a track's description may contain (§15.1, §6.2).
/// <para>
/// The sibling of <see cref="TrackNaming"/>, and here for the same reason: the rule is needed at
/// the keyboard so a rider is stopped before they lose what they typed, on the server because a
/// client is untrusted input, and at the column so nothing longer can be stored. One rule stated
/// once beats three that drift apart.
/// </para>
/// </summary>
public static class TrackDescription
{
	/// <summary>
	/// The longest description that is stored, matching <c>track.description</c>.
	/// <para>
	/// Two thousand characters is roughly a page. Long enough for the surface, the traffic, where
	/// the road is broken and where to stop; short enough that a browse list can hold a page of
	/// them without the response becoming the reason the list is slow.
	/// </para>
	/// </summary>
	public const int MaxLength = 2000;

	/// <summary>
	/// A description as it should be stored, or <c>null</c> when nothing is left.
	/// </summary>
	/// <param name="description">What the rider typed.</param>
	/// <remarks>
	/// <see cref="MarkerText.Clean"/> keeps newlines and tabs and drops the rest of the control
	/// range, which is what a paragraph of prose needs - a description is the one field on a track
	/// most likely to be written over several lines and pasted in from somewhere else.
	/// </remarks>
	public static string? Clean(string? description) => MarkerText.Clean(description);

	/// <summary>Whether a cleaned description is short enough to store.</summary>
	/// <param name="description">The candidate.</param>
	public static bool IsStorable(string? description) => (Clean(description)?.Length ?? 0) <= MaxLength;
}
