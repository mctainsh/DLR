using DLR.Core.Markers;

namespace BlazorDLR.Shared.Markers;

/// <summary>
/// How the curated icon keys (§16.2) are shown to a human: a readable label and a colour
/// emoji, for the composer's picker and for the map overlay.
/// <para>
/// <strong>This lives on the client, not in <see cref="MarkerIcons"/>.</strong> §16.2's rule
/// is that the server validates the key's length and character set and the client owns the
/// drawing — an emoji is drawing. Keeping it here means a newer client can render
/// <c>ferry</c> without the server knowing what a ferry looks like.
/// </para>
/// <para>
/// Emoji rather than a bespoke icon font because every host we ship is a browser or a
/// WebView, all of which already have colour emoji, and because a key the running version
/// has never seen still degrades to something drawable (<see cref="MarkerIcons.Fallback"/>)
/// rather than to a blank.
/// </para>
/// </summary>
public static class MarkerIconGlyphs
{
	/// <summary>
	/// Every curated key, in the order the composer offers them: the things a rider marks
	/// mid-ride first, the housekeeping keys last. Alphabetical would bury "hazard" between
	/// "gravel" and "medical", and hazard is the one someone reaches for while pulled over.
	/// <para>
	/// This is the single list. A key present in <see cref="MarkerIcons.Known"/> but absent
	/// here is a real omission, and <c>AddMarkerTests</c> is what says so — an earlier
	/// version quietly appended the stragglers with the note glyph and their raw key as a
	/// label, which made that assertion unfailable and the mistake invisible.
	/// </para>
	/// </summary>
	private static readonly MarkerIconOption[] Curated =
	[
		new("hazard", "Hazard", "⚠️"),
		new("gravel", "Gravel", "\U0001FAA8"),
		new("water-crossing", "Water crossing", "\U0001F30A"),
		new("gate", "Gate", "\U0001F6A7"),
		new("turn", "Turn", "↩️"),
		new("regroup", "Regroup", "\U0001F91D"),
		new("stopped", "Stopped", "\U0001F6D1"),
		new("start", "Start", "\U0001F7E2"),
		new("finish", "Finish", "\U0001F3C1"),
		new("fuel", "Fuel", "⛽"),
		new("food", "Food", "\U0001F354"),
		new("coffee", "Coffee", "☕"),
		new("water", "Drinking water", "\U0001F6B0"),
		new("toilet", "Toilet", "\U0001F6BB"),
		new("camping", "Camping", "\U0001F3D5️"),
		new("parking", "Parking", "\U0001F17F️"),
		new("viewpoint", "Viewpoint", "\U0001F3DE️"),
		new("photo", "Photo", "\U0001F4F7"),
		new("repair", "Repair", "\U0001F527"),
		new("medical", "Medical", "\U0001F691"),
		new(MarkerIcons.Fallback, "Note", "\U0001F4DD"),
	];

	private static readonly Dictionary<string, MarkerIconOption> ByKey =
		Curated.ToDictionary(option => option.Key, StringComparer.Ordinal);

	/// <summary>Every curated key in picker order, with its label and emoji (§16.2).</summary>
	public static IReadOnlyList<MarkerIconOption> PickerOptions => Curated;

	/// <summary>The colour emoji for a key, falling back to the note glyph for unknown keys.</summary>
	/// <param name="icon">The icon key, which may be one this version has never seen (§16.2).</param>
	/// <returns>A colour emoji string.</returns>
	public static string Emoji(string? icon) =>
		icon is not null && ByKey.TryGetValue(icon, out MarkerIconOption option)
			? option.Emoji
			: ByKey[MarkerIcons.Fallback].Emoji;

	/// <summary>The human label for a key. An unknown key shows its own text rather than lying.</summary>
	/// <param name="icon">The icon key.</param>
	/// <returns>A label safe to render.</returns>
	public static string Label(string? icon)
	{
		if (icon is null)
		{
			return ByKey[MarkerIcons.Fallback].Label;
		}

		return ByKey.TryGetValue(icon, out MarkerIconOption option) ? option.Label : icon;
	}

	/// <summary>
	/// What to call a marker in a row, a heading or a confirmation — its title, or the icon's
	/// name when it has none (§16.2).
	/// <para>
	/// A title is optional: on the map that is the point, because the pin is its icon and the
	/// overlay simply draws no label. In a <em>list</em> it is not — a row reading only
	/// "Alice" with a blank where the name goes looks like data that failed to load rather
	/// than like a marker somebody chose not to name. "Gravel" is what they would have typed.
	/// </para>
	/// <para>
	/// Deliberately not used by the overlay. Drawing the icon's own name under the icon is the
	/// word twice, and the map is the one place with no room for it.
	/// </para>
	/// </summary>
	/// <param name="title">The stored title, which may be empty.</param>
	/// <param name="icon">The marker's icon key, used for the fallback.</param>
	/// <returns>Something safe to render as a name.</returns>
	public static string Name(string? title, string? icon) =>
		string.IsNullOrWhiteSpace(title) ? Label(icon) : title;
}

/// <summary>One row in the composer's icon picker.</summary>
/// <param name="Key">The curated key that travels on the wire (§16.2).</param>
/// <param name="Label">Readable name.</param>
/// <param name="Emoji">A colour emoji.</param>
public readonly record struct MarkerIconOption(string Key, string Label, string Emoji);
