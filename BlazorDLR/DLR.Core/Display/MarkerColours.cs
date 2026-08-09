namespace DLR.Core.Display;

/// <summary>
/// The colour a rider's own live-position marker is drawn in on a group ride's map (§5.3, §16.3).
/// <para>
/// <strong>This is the rider's marker, not an authored one.</strong> A marker somebody placed on
/// the road carries an icon from the curated set and is drawn on a white plate; the thing this
/// colour paints is the label beside a rider's arrow, so twenty people on one map can tell each
/// other apart at a glance rather than by reading twenty identical pins.
/// </para>
/// <para>
/// <strong>Only the background is chosen.</strong> The text and the border are
/// <see cref="ForegroundFor"/>'s answer — black or white, whichever is legible on it — because a
/// rider who could pick both could pick yellow on white and vanish from the ride, and the person
/// who suffers for that is whoever is trying to find them at a junction.
/// </para>
/// </summary>
public static class MarkerColours
{
	/// <summary>
	/// What an account that has never chosen gets: white, with black text and a black border.
	/// <para>
	/// Deliberately not the app's accent blue. The arrow that says which way a rider is heading is
	/// blue, and a blue arrow on a blue label is one shape rather than two.
	/// </para>
	/// </summary>
	public const string Default = HexColour.White;

	/// <summary>
	/// The colours the profile screen offers (§7.14).
	/// <para>
	/// A curated list rather than a free colour wheel: these are picked to stay apart from each
	/// other and from OSM's own tile palette at arm's length in sunlight, which is not a property
	/// a colour wheel has any way to enforce. Anything that validates is still stored — the wire
	/// accepts any <c>#rrggbb</c> — so a later screen may widen the choice without a migration.
	/// </para>
	/// </summary>
	public static IReadOnlyList<string> Palette { get; } =
	[
		"#ffffff", // white
		"#111827", // near-black
		"#2563eb", // blue
		"#dc2626", // red
		"#16a34a", // green
		"#9333ea", // purple
		"#ea580c", // orange
		"#0891b2", // teal
		"#facc15", // yellow
		"#ec4899", // pink
		"#84cc16", // lime
		"#94a3b8", // slate
	];

	/// <summary>
	/// Validates a colour on its way in from a client.
	/// <para>
	/// Null and blank are <em>valid</em> and mean "no choice recorded" — that is how a rider goes
	/// back to <see cref="Default"/>, and rejecting it would make the setting one-way. A non-blank
	/// string that is not <c>#rrggbb</c> is a client bug and is refused rather than silently
	/// defaulted, so it is found by whoever wrote it instead of by a rider wondering why their
	/// colour did not stick.
	/// </para>
	/// </summary>
	/// <param name="colour">The candidate, as the client sent it.</param>
	/// <param name="normalised">The lower-cased colour, or null for "no choice".</param>
	/// <returns><c>false</c> only for a non-blank string that is not a colour.</returns>
	public static bool TryNormalise(string? colour, out string? normalised)
	{
		if (string.IsNullOrWhiteSpace(colour))
		{
			normalised = null;

			return true;
		}

		if (!HexColour.IsHex(colour))
		{
			normalised = null;

			return false;
		}

		normalised = colour.ToLowerInvariant();

		return true;
	}

	/// <summary>
	/// The colour actually drawn: the rider's choice when they have made one, otherwise
	/// <see cref="Default"/>. Every render path goes through this so an account with nothing
	/// stored and an account holding something unreadable look the same rather than differently
	/// broken.
	/// </summary>
	/// <param name="colour">The stored choice, or null.</param>
	/// <returns>A <c>#rrggbb</c> colour.</returns>
	public static string Or(string? colour) => HexColour.Normalise(colour, Default);

	/// <summary>
	/// The ink for text and border on top of <paramref name="colour"/> — black or white, whichever
	/// reads (§16.3).
	/// </summary>
	/// <param name="colour">The rider's stored choice, or null.</param>
	/// <returns><c>#000000</c> or <c>#ffffff</c>.</returns>
	public static string ForegroundFor(string? colour) => HexColour.ContrastingForeground(Or(colour));
}
