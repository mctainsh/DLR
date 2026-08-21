namespace DLR.Core.Tracks;

/// <summary>
/// The star scale a shared route is rated on (§6.2).
/// <para>
/// <strong>One to five whole stars, and no half stars.</strong> The scale is here rather than in
/// the endpoint because three places have to agree about it — the server that refuses a six, the
/// database's check constraint, and the widget that draws the row — and a scale that only two of
/// them knew would be a widget drawing four boxes for a column that accepts ten values.
/// </para>
/// <para>
/// There is deliberately no zero. Clearing a rating is deleting the row, not storing a nought:
/// a nought would average in as "terrible" for every rider who tapped a star and changed their
/// mind, which is the opposite of what they meant.
/// </para>
/// </summary>
public static class TrackRatings
{
	/// <summary>The worst a route can be rated.</summary>
	public const int MinStars = 1;

	/// <summary>The best.</summary>
	public const int MaxStars = 5;

	/// <summary>Whether a number is a rating this scale can store.</summary>
	/// <param name="stars">What the caller sent.</param>
	public static bool IsValid(int stars) => stars is >= MinStars and <= MaxStars;

	/// <summary>
	/// The average rounded to the half star a row of five glyphs can actually draw, or null when
	/// nobody has rated it.
	/// <para>
	/// Rounded here rather than by each renderer for the reason the scale itself lives here: the
	/// browse list, the detail page and the map callout would otherwise each pick a rounding, and
	/// the same route would read 4.5 on one screen and 4 on the next.
	/// </para>
	/// </summary>
	/// <param name="average">The mean of every star given, or null.</param>
	public static double? ToHalfStars(double? average) =>
		average is { } value ? Math.Round(value * 2, MidpointRounding.AwayFromZero) / 2 : null;
}
