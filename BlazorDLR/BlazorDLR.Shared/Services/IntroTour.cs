namespace BlazorDLR.Shared.Services;

/// <summary>
/// One card of the introduction slide show — a glyph, a heading and a paragraph.
/// </summary>
/// <param name="Icon">
/// The Font Awesome class for the slide's glyph, without the <c>fa fa-fw</c> prefix the page
/// adds. The set is vendored into <c>wwwroot/lib/fontawesome/</c> like the nav rail's, so a
/// slide never depends on a network the phone may not have.
/// </param>
/// <param name="Title">The one line the slide is about. Rendered as the card's heading.</param>
/// <param name="Body">
/// Two or three sentences at most. A rider skims this on a phone before they have any reason
/// to trust the app, and a paragraph they scroll is a paragraph they skip.
/// </param>
public sealed record IntroSlide(string Icon, string Title, string Body);

/// <summary>
/// The introduction shown once on a device's first run, and on demand from Settings ever after.
/// <para>
/// <strong>The copy here is placeholder.</strong> It says roughly the right things about the
/// product so the flow can be walked end to end and the screens judged, but every string below
/// is meant to be rewritten by whoever owns the product's words. The shape is what is being
/// committed to: a short deck, one idea per card, skippable from the first frame.
/// </para>
/// <para>
/// <strong>Content, not markup, and in one place.</strong> The page renders whatever this list
/// holds — adding, cutting or reordering a slide is an edit here and nothing else. That is also
/// what makes the deck testable without rendering it.
/// </para>
/// <para>
/// <strong>No links, no actions, no per-slide branching.</strong> A slide that can be acted on
/// is a screen, and a screen belongs in the app rather than in the thing a rider is trying to
/// get past. The only ways out of the deck are "next" and "skip".
/// </para>
/// </summary>
public static class IntroTour
{
	/// <summary>
	/// Which edition of the deck this is, and what <see cref="State.IntroTourState"/> stores once
	/// a rider has finished it.
	/// <para>
	/// Bumping this shows the introduction again — once — to every device that had already seen
	/// the old one. That is the point of storing a number rather than a flag: a materially new
	/// deck is worth 20 seconds of an existing rider's time, and nothing else in the app has a
	/// way to say so. Do <em>not</em> bump it for a typo fix; a rider who is shown this twice for
	/// no reason learns to skip it, which costs the one showing that mattered.
	/// </para>
	/// </summary>
	public const int Version = 1;

	/// <summary>
	/// The deck, in order. Five cards: what the app is for, the live map, markers, the thread,
	/// and what it does with a rider's location — that last one deliberately before the account
	/// screen rather than after it.
	/// </summary>
	public static IReadOnlyList<IntroSlide> Slides { get; } =
	[
		new IntroSlide(
			"fa-mountain-sun",
			"Welcome to Dumb Luck Routes",
			"Travel together, know where each other is, and stop knowing the moment the adventure ends. "
			+ "Here is the whole app in about twenty seconds."),

		new IntroSlide(
			"fa-globe",
			"See the group on one map",
			"Start an adventure or join one with a code, and everybody on it appears on the same live map. "
			+ "No hunting for a rider who took the other fork — you can see where they are."),

		new IntroSlide(
			"fa-map-pin",
			"Drop a marker on anything",
			"Fuel, a photo, a washed-out crossing, the cafe worth stopping at. "
			+ "Markers land on everyone's map straight away, and stay with the adventure afterwards."),

		new IntroSlide(
			"fa-comments",
			"Talk without leaving the road",
			"Every adventure has its own thread for photos, questions and votes on where to go next. "
			+ "It is quicker than a group chat and it is already about the right ride."),

		new IntroSlide(
			"fa-user-shield",
			"You decide who sees you",
			"Sharing is only ever with the adventure you are on, and it stops when the adventure does. "
			+ "Set a private area around home and your position is never sent from inside it."),
	];
}
