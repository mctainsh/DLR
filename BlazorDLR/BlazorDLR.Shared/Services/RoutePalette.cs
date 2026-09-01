namespace BlazorDLR.Shared.Services;

/// <summary>
/// The colours a ride's planned routes are drawn in, so the same route is the same colour on
/// the map and in the list beside it (§5.4).
/// <para>
/// <strong>Assigned by position, not stored.</strong> A colour the organiser picked would be one
/// more thing to migrate, to validate and to get contrast-checked; a route's place in the list is
/// already stable - it is ordered by when it was attached, which nothing reorders - so the same
/// route keeps the same colour for everybody without a column.
/// </para>
/// <para>
/// The first entry is the design's primary blue, which is what a single-route ride was drawn in
/// before there could be several, so the common case looks exactly as it did. The rest are chosen
/// to stay apart from each other and from the blue rider darts on top of them.
/// </para>
/// </summary>
public static class RoutePalette
{
	private static readonly string[] Colours =
	[
		"#2563eb", // blue
		"#dc2626", // red
		"#16a34a", // green
		"#9333ea", // purple
		"#ea580c", // orange
		"#0891b2", // teal
	];

	/// <summary>How many distinct colours there are before they repeat.</summary>
	public static int Count => Colours.Length;

	/// <summary>
	/// The colour for the route at <paramref name="index"/> in the ride's list.
	/// <para>
	/// Wraps rather than throwing. A ride may carry more routes than there are colours, and
	/// two lines sharing a colour is a legibility problem; an exception on the drawing path is
	/// a blank map.
	/// </para>
	/// </summary>
	/// <param name="index">The route's position in the list. Negative values wrap the same way.</param>
	/// <returns>A <c>#RRGGBB</c> string, which is what the overlay parses.</returns>
	public static string At(int index) => Colours[((index % Colours.Length) + Colours.Length) % Colours.Length];
}
