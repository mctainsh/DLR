namespace BlazorDLR.Shared.Services;

/// <summary>
/// The "this particular route is orange" half of the route-display preference (§18.6, §5.4):
/// a colour a rider has pinned to one specific track, stored on the device alongside
/// <see cref="RouteStyle"/> but under a key of its own.
/// <para>
/// <strong>Separate from <see cref="RouteStyle"/> on purpose.</strong> A style is five scalars
/// that every device has exactly one of; this is a map that grows with the tracks the rider has
/// bothered to colour. Encoding an unbounded map into the same fixed-width record would make
/// both of them harder to read back, and a device that has styled nothing should not carry a
/// per-route key at all.
/// </para>
/// <para>
/// <strong>Keyed on track id, not on position in the ride.</strong> Position is what
/// <see cref="RoutePalette"/> already uses and it is stable within one ride — but the same GPX
/// attached to next month's ride would be a different position and would lose the colour, which
/// is not what "make this route orange" means to the person who said it.
/// </para>
/// </summary>
public static class RouteColourMap
{
	/// <summary>An empty map, for a device that has never pinned a colour to anything.</summary>
	public static IReadOnlyDictionary<Guid, string> Empty { get; } = new Dictionary<Guid, string>();

	/// <summary>
	/// The map as one string for <see cref="IDeviceSettings"/>: a format version, then one
	/// <c>id=#rrggbb</c> entry per pinned colour.
	/// <para>
	/// Same shape and same reasoning as <see cref="RouteStyle.Encode"/> — one key, one round
	/// trip, and no reflection-based serialiser in an assembly that gets trimmed into a WASM
	/// download.
	/// </para>
	/// </summary>
	/// <param name="colours">The pinned colours. Entries whose colour is not <c>#rrggbb</c> are dropped rather than stored.</param>
	public static string Encode(IReadOnlyDictionary<Guid, string> colours)
	{
		// "1" alone is a legitimate value: it is what a device that has just cleared its last
		// override stores, and it decodes to an empty map rather than to "unreadable".
		IEnumerable<string> entries = colours
			.Where(entry => RouteStyle.NormaliseColour(entry.Value, "").Length > 0)
			.Select(entry => $"{entry.Key:N}={RouteStyle.NormaliseColour(entry.Value, "")}");

		return string.Join('|', entries.Prepend("1"));
	}

	/// <summary>
	/// Reads back what <see cref="Encode"/> wrote.
	/// <para>
	/// Entry by entry: one unparseable id or colour costs that route its pinned colour — which
	/// falls back to the palette and looks stock — rather than costing every other route its
	/// own. The value comes off a device we do not control.
	/// </para>
	/// </summary>
	/// <param name="encoded">A string from <see cref="Encode"/>, or <c>null</c> on a device that has never stored one.</param>
	public static IReadOnlyDictionary<Guid, string> Decode(string? encoded)
	{
		if (string.IsNullOrWhiteSpace(encoded))
		{
			return Empty;
		}

		string[] parts = encoded.Split('|');

		// Unknown format version: keep nothing. Guessing at entries written by a build that
		// structured them differently is how a route ends up a colour nobody chose.
		if (parts[0] != "1")
		{
			return Empty;
		}

		Dictionary<Guid, string> colours = [];

		foreach (string entry in parts.Skip(1))
		{
			int split = entry.IndexOf('=', StringComparison.Ordinal);
			if (split <= 0)
			{
				continue;
			}

			if (!Guid.TryParseExact(entry[..split], "N", out Guid trackId))
			{
				continue;
			}

			string colour = RouteStyle.NormaliseColour(entry[(split + 1)..], "");
			if (colour.Length == 0)
			{
				continue;
			}

			colours[trackId] = colour;
		}

		return colours;
	}
}
