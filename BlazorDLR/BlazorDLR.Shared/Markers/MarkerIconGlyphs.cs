using DLR.Core.Markers;

namespace BlazorDLR.Shared.Markers;

/// <summary>
/// How the curated icon keys (§16.2) are shown to a human: a readable label and the PNG that
/// draws them, for the composer's picker, the marker lists and the map overlay.
/// <para>
/// <strong>This lives on the client, not in <see cref="MarkerIcons"/>.</strong> §16.2's rule
/// is that the server validates the key's length and character set and the client owns the
/// drawing - a picture is drawing. Keeping it here means a newer client can render
/// <c>ferry</c> without the server knowing what a ferry looks like.
/// </para>
/// <para>
/// The artwork is a 48×48 PNG per key in <c>wwwroot/markers/</c>, shipped by the shared RCL so
/// all three hosts draw the identical bytes - one picture per key, the same on iOS, Android and
/// the web, and one we can draw for keys no font has a symbol for.
/// </para>
/// </summary>
public static class MarkerIconGlyphs
{
	/// <summary>Where the shared RCL's icons are served from, on every host.</summary>
	private const string AssetRoot = "_content/BlazorDLR.Shared/markers/";

	/// <summary>
	/// Every curated key, in the order the composer offers them: the things a rider marks
	/// mid-ride first, then the wildlife, then the housekeeping keys. Alphabetical would bury
	/// "hazard" between "gravel" and "medical", and hazard is the one someone reaches for
	/// while pulled over.
	/// <para>
	/// This is the single list. A key present in <see cref="MarkerIcons.Known"/> but absent
	/// here is a real omission, and <c>AddMarkerTests</c> is what says so - an earlier
	/// version quietly appended the stragglers with the note glyph and their raw key as a
	/// label, which made that assertion unfailable and the mistake invisible.
	/// </para>
	/// </summary>
	private static readonly MarkerIconOption[] Curated =
	[
		new("hazard", "Hazard"),
		new("crash", "Crash"),
		new("gravel", "Gravel"),
		new("water-crossing", "Water crossing"),
		new("gate", "Gate"),
		new("turn", "Turn"),
		new("fire", "Fire"),
		new("kangaroo", "Kangaroo"),
		new("sheep", "Sheep"),
		new("bear", "Bear"),
		new("snake", "Snake"),
		new("crocodile", "Crocodile"),
		new("mushroom", "Mushroom"),
		new("regroup", "Regroup"),
		new("stopped", "Stopped"),
		new("start", "Start"),
		new("finish", "Finish"),
		new("fuel", "Fuel"),
		new("food", "Food"),
		new("coffee", "Coffee"),
		new("water", "Drinking water"),
		new("toilet", "Toilet"),
		new("camping", "Camping"),
		new("parking", "Parking"),
		new("viewpoint", "Viewpoint"),
		new("photo", "Photo"),
		new("repair", "Repair"),
		new("medical", "Medical"),
		new(MarkerIcons.Fallback, "Note"),
	];

	private static readonly Dictionary<string, MarkerIconOption> ByKey =
		Curated.ToDictionary(option => option.Key, StringComparer.Ordinal);

	/// <summary>Every curated key in picker order, with its label (§16.2).</summary>
	public static IReadOnlyList<MarkerIconOption> PickerOptions => Curated;

	/// <summary>
	/// The URL of the icon for a key, falling back to the note icon for unknown keys.
	/// <para>
	/// Resolved through <see cref="ByKey"/> rather than by pasting the key into the path, so a
	/// key this version has never seen lands on the note icon instead of on a 404 that would
	/// leave a broken-image box on the map (§16.2).
	/// </para>
	/// </summary>
	/// <param name="icon">The icon key, which may be one this version has never seen.</param>
	/// <returns>A host-relative URL to a 48×48 PNG.</returns>
	public static string AssetPath(string? icon) =>
		AssetRoot + (icon is not null && ByKey.ContainsKey(icon) ? icon : MarkerIcons.Fallback) + ".png";

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
	/// What to call a marker in a row, a heading or a confirmation - its title, or the icon's
	/// name when it has none (§16.2).
	/// <para>
	/// A title is optional: on the map that is the point, because the pin is its icon and the
	/// overlay simply draws no label. In a <em>list</em> it is not - a row reading only
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
public readonly record struct MarkerIconOption(string Key, string Label);
