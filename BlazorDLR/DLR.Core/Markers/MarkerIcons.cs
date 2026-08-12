namespace DLR.Core.Markers;

/// <summary>
/// The curated icon set, and the GPX <c>&lt;sym&gt;</c> mapping (§16.2, §16.6).
/// <para>
/// <strong>Keys are strings, and unknown ones are stored rather than rejected.</strong> An app one
/// version ahead sends <c>ferry</c>; a server that has never heard of it stores it, and an older
/// client that cannot draw it falls back to <see cref="Fallback"/>. An enum ordinal would need a
/// migration in lockstep across two app stores' release cadences, which is not a thing that
/// happens. The server validates length and character set; the client owns the drawing.
/// </para>
/// </summary>
public static class MarkerIcons
{
	/// <summary>What a client draws when it does not recognise a key.</summary>
	public const string Fallback = "note";

	/// <summary>Longest key the column accepts (§16.7).</summary>
	public const int MaxLength = 32;

	/// <summary>
	/// The keys this version knows how to draw (§16.2).
	/// <para>
	/// A key is only in here when the client can actually draw it, which means there is a
	/// <c>{key}.png</c> in <c>BlazorDLR.Shared/wwwroot/markers/</c>.
	/// <c>MarkerIconAssetRules</c> is what holds the two together.
	/// </para>
	/// </summary>
	public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
	{
		"hazard", "gravel", "water-crossing", "gate", "fuel", "food", "coffee", "water",
		"toilet", "camping", "parking", "viewpoint", "photo", "repair", "medical",
		"regroup", "stopped", "start", "finish", "turn",
		"fire", "crash", "mushroom", "sheep", "bear", "snake", "kangaroo", "crocodile",
		Fallback,
	};

	/// <summary>
	/// GPX symbol names onto icon keys. Deliberately small: the common Garmin and OsmAnd names
	/// people actually have in their files, with everything else falling back rather than being
	/// guessed at.
	/// </summary>
	private static readonly Dictionary<string, string> FromSymbol = new(StringComparer.OrdinalIgnoreCase)
	{
		["danger"] = "hazard",
		["hazard"] = "hazard",
		["caution"] = "hazard",
		["gas station"] = "fuel",
		["fuel"] = "fuel",
		["restaurant"] = "food",
		["food"] = "food",
		["bar"] = "food",
		["coffee"] = "coffee",
		["drinking water"] = "water",
		["water"] = "water",
		["restroom"] = "toilet",
		["toilet"] = "toilet",
		["campground"] = "camping",
		["camping"] = "camping",
		["parking area"] = "parking",
		["parking"] = "parking",
		["scenic area"] = "viewpoint",
		["viewpoint"] = "viewpoint",
		["photo"] = "photo",
		["camera"] = "photo",
		["car repair"] = "repair",
		["repair"] = "repair",
		["medical facility"] = "medical",
		["medical"] = "medical",
		["gate"] = "gate",
		["ford"] = "water-crossing",
		["water-crossing"] = "water-crossing",
		["gravel"] = "gravel",
		["regroup"] = "regroup",
		["stopped"] = "stopped",
		["flag, green"] = "start",
		["start"] = "start",
		["flag, red"] = "finish",
		["finish"] = "finish",
		["turn"] = "turn",
		["fire"] = "fire",
		["crash"] = "crash",
		["accident"] = "crash",
		["mushroom"] = "mushroom",
		["sheep"] = "sheep",
		["bear"] = "bear",
		["snake"] = "snake",
		["kangaroo"] = "kangaroo",
		["crocodile"] = "crocodile",
	};

	/// <summary>Icon keys onto the GPX symbol name written on export.</summary>
	private static readonly Dictionary<string, string> ToSymbol = new(StringComparer.Ordinal)
	{
		["hazard"] = "hazard",
		["fuel"] = "fuel",
		["food"] = "food",
		["coffee"] = "coffee",
		["water"] = "water",
		["toilet"] = "toilet",
		["camping"] = "camping",
		["parking"] = "parking",
		["viewpoint"] = "viewpoint",
		["photo"] = "photo",
		["repair"] = "repair",
		["medical"] = "medical",
		["gate"] = "gate",
		["water-crossing"] = "water-crossing",
		["gravel"] = "gravel",
		["regroup"] = "regroup",
		["stopped"] = "stopped",
		["start"] = "start",
		["finish"] = "finish",
		["turn"] = "turn",
		["fire"] = "fire",
		["crash"] = "crash",
		["mushroom"] = "mushroom",
		["sheep"] = "sheep",
		["bear"] = "bear",
		["snake"] = "snake",
		["kangaroo"] = "kangaroo",
		["crocodile"] = "crocodile",
		[Fallback] = Fallback,
	};

	/// <summary>Whether a client of this version can draw the key.</summary>
	/// <param name="icon">The key.</param>
	/// <returns>True when it is in the curated set.</returns>
	public static bool IsKnown(string icon) => Known.Contains(icon);

	/// <summary>
	/// Turns a GPX <c>&lt;sym&gt;</c> into an icon key (§16.6).
	/// </summary>
	/// <param name="symbol">The symbol name, or null.</param>
	/// <returns>The mapped key, or <see cref="Fallback"/>.</returns>
	public static string ForSymbol(string? symbol)
	{
		if (string.IsNullOrWhiteSpace(symbol))
		{
			return Fallback;
		}

		string trimmed = symbol.Trim();

		if (FromSymbol.TryGetValue(trimmed, out string? mapped))
		{
			return mapped;
		}

		// A symbol shaped like one of our keys is kept as one, even when this version has never
		// heard of it. That is what makes the round trip lossless for a marker created by a newer
		// client (§16.2) — flattening `ferry` to `note` here would quietly destroy it on any
		// export/import cycle through an older server.
		//
		// Anything not shaped like a key — "Flag, Blue", "Scenic Area" — still falls back, so a
		// foreign file's symbols do not become junk icon keys.
		return IsStorable(trimmed) ? trimmed : Fallback;
	}

	/// <summary>The <c>&lt;sym&gt;</c> to write for an icon key.</summary>
	/// <param name="icon">The key.</param>
	/// <returns>The symbol name.</returns>
	/// <remarks>
	/// An unknown key is written out as itself rather than flattened to the fallback, so a file
	/// exported by a newer client and re-imported here keeps the icon it was given.
	/// </remarks>
	public static string ToGpxSymbol(string icon) =>
		ToSymbol.TryGetValue(icon, out string? symbol) ? symbol : icon;

	/// <summary>
	/// Whether a key is <em>storable</em> — length and character set only, not membership (§16.2).
	/// </summary>
	/// <param name="icon">The key.</param>
	/// <returns>True when it may be stored.</returns>
	public static bool IsStorable(string? icon) =>
		!string.IsNullOrWhiteSpace(icon)
		&& icon.Length <= MaxLength
		&& icon.All(character =>
			char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-');
}
