namespace BlazorDLR.Shared.Services;

/// <summary>
/// The "ride this one the other way round" half of the route-display preference (§18.6, §5.4):
/// the set of tracks a rider has reversed, stored on the device beside <see cref="RouteColourMap"/>
/// and under a key of its own.
/// <para>
/// <strong>What reversing is for.</strong> A track is a direction as well as a shape - it is what
/// the chevrons along the line point at, and it is the order §5.4's gap list measures "distance
/// along the route" in. A GPX recorded riding a loop clockwise, attached to a ride going
/// anti-clockwise, draws arrows pointing back at the group and ranks the leader last. Reversing is
/// the one-tap answer, and it costs nothing: the same points read end to start.
/// </para>
/// <para>
/// <strong>A set, not a map of flags.</strong> The stored value is the tracks that <em>are</em>
/// reversed; a track nobody has touched is simply absent. Storing <c>id=false</c> for every route
/// a rider has ever looked at would grow the value with no change in meaning.
/// </para>
/// <para>
/// <strong>Keyed on track id, not on position in the ride</strong> - the same choice
/// <see cref="RouteColourMap"/> makes and for the same reason. The direction belongs to that GPX,
/// so it follows the file onto next month's ride instead of following whatever ends up second in
/// some other list.
/// </para>
/// </summary>
public static class RouteDirectionMap
{
	/// <summary>An empty set, for a device that has never reversed anything.</summary>
	public static IReadOnlySet<Guid> Empty { get; } = new HashSet<Guid>();

	/// <summary>
	/// The set as one string for <see cref="IDeviceSettings"/>: a format version, then one track
	/// id per reversed route.
	/// <para>
	/// Same shape and same reasoning as <see cref="RouteColourMap.Encode"/> - one key, one round
	/// trip, and no reflection-based serialiser in an assembly that gets trimmed into a WASM
	/// download.
	/// </para>
	/// </summary>
	/// <param name="reversed">The tracks drawn back to front.</param>
	public static string Encode(IReadOnlySet<Guid> reversed)
	{
		// "1" alone is a legitimate value: it is what a device that has just un-reversed its last
		// route stores, and it decodes to an empty set rather than to "unreadable".
		IEnumerable<string> entries = reversed.Select(trackId => trackId.ToString("N"));

		return string.Join('|', entries.Prepend("1"));
	}

	/// <summary>
	/// Reads back what <see cref="Encode"/> wrote.
	/// <para>
	/// Entry by entry: one unparseable id costs that route its reversal - which draws the way the
	/// GPX was recorded, and looks stock - rather than costing every other route its own. The
	/// value comes off a device we do not control.
	/// </para>
	/// </summary>
	/// <param name="encoded">A string from <see cref="Encode"/>, or <c>null</c> on a device that has never stored one.</param>
	public static IReadOnlySet<Guid> Decode(string? encoded)
	{
		if (string.IsNullOrWhiteSpace(encoded))
		{
			return Empty;
		}

		string[] parts = encoded.Split('|');

		// Unknown format version: keep nothing. Guessing at entries written by a build that
		// structured them differently is how a route ends up drawn backwards for no reason the
		// rider can trace.
		if (parts[0] != "1")
		{
			return Empty;
		}

		HashSet<Guid> reversed = [];

		foreach (string entry in parts.Skip(1))
		{
			if (Guid.TryParseExact(entry, "N", out Guid trackId))
			{
				reversed.Add(trackId);
			}
		}

		return reversed;
	}
}
