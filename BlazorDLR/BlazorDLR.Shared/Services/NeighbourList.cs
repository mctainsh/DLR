namespace BlazorDLR.Shared.Services;

/// <summary>One rider as the live map's neighbours panel draws them (§5.4).</summary>
/// <param name="UserId">Which rider.</param>
/// <param name="UserName">Their handle, which is also the label on their pin (§7.2).</param>
/// <param name="Colour">
/// The <c>#rrggbb</c> their marker is drawn in (§16.3), already defaulted. It is the whole point of
/// the swatch: the panel names the rider the map has just drawn a few hundred metres up the road,
/// and the two have to agree or the square is worse than no square.
/// </param>
/// <param name="IsSelf">Whether this row is the person reading it — the one everything else is measured from.</param>
/// <param name="Presence">Sharing or no signal, so a frozen number can say it is frozen (§5.6).</param>
/// <param name="RelativeMetres">
/// How far along the route they are from the reader: positive ahead, negative behind. Zero for the
/// reader's own row.
/// </param>
public readonly record struct NeighbourRow(
	Guid UserId,
	string UserName,
	string Colour,
	bool IsSelf,
	MemberPresence Presence,
	double RelativeMetres);

/// <summary>
/// The riders immediately around this one on the planned route (§5.4) — the list behind the live
/// map's neighbours panel.
/// <para>
/// <strong>A different question from the "Ride members live" screen's, off the same arithmetic.</strong>
/// That screen answers "who is on this ride and what is each of them doing", which is a whole list
/// and four orders. This answers the one thing a rider asks with a phone in a bar mount and a group
/// strung out over a few kilometres: <em>who is just up the road, and who is just behind me</em>.
/// Everybody else on a fifty-rider list is noise at 40 km/h, so the panel carries
/// <see cref="DefaultCount"/> neighbours and no more.
/// </para>
/// <para>
/// <strong>Nearest is measured along the route, not through the air.</strong> Two riders either side
/// of a river are a hundred metres apart and twenty minutes apart, and on a ride the second number
/// is the one that decides whether anybody waits. The straight-line figure is what the members
/// screen's "Range" column is for.
/// </para>
/// <para>
/// Built on top of <see cref="MemberRoster"/> rather than beside it. The projection, the presence
/// rules and the colour default are already settled there and settling them a second time is how
/// two screens start disagreeing about who the leader is — so this takes that screen's rows and does
/// nothing but choose and order them.
/// </para>
/// <para>
/// <strong>Pure, and no clock is read here.</strong> Same posture as <see cref="MemberRoster"/>, for
/// the same reasons: the rules are testable without a renderer, and <c>ClockRules</c> would refuse
/// the ambient read anyway.
/// </para>
/// </summary>
public static class NeighbourList
{
	/// <summary>
	/// How many other riders the panel carries.
	/// <para>
	/// Four, which with the reader's own row is five lines. It is a small translucent panel over a
	/// map that is the page — the map is what the rider came for — and five lines is about what can
	/// be read at a glance without becoming a list to study. The two riders either side are the ones
	/// that matter; the pair beyond them are what says whether the group is together or strung out.
	/// </para>
	/// </summary>
	public const int DefaultCount = 4;

	/// <summary>
	/// Below this many metres apart, two riders are level.
	/// <para>
	/// A fix is good to a few metres, and the projection onto a simplified route (§15.5) adds its own
	/// error on top. "12 m ahead" is that noise presented as news: it is not a gap anybody can see
	/// out of a helmet, and it changes sign every second or two, which on a panel read at speed is
	/// the one thing worse than saying nothing.
	/// </para>
	/// </summary>
	public const double LevelMetres = 15;

	/// <summary>
	/// Picks the riders nearest this one along the route and puts them in road order.
	/// </summary>
	/// <param name="rows">
	/// Every member, as <see cref="MemberRoster.Build"/> derived them — in any order, since this
	/// imposes its own.
	/// </param>
	/// <param name="selfUserId">Who is reading. Without one there is nobody for "ahead" to be ahead of.</param>
	/// <param name="selfAlongMetres">
	/// Where the reader is along the route, or null when that is not known — no route, or a device
	/// with no fix of its own yet.
	/// <para>
	/// Passed in rather than read off the reader's own row, and that is the point of the argument: on
	/// a phone the live map draws this rider from the device's own GPS rather than from the ride's
	/// round-tripped copy of it (see the live map's <c>MyPoint</c>), and a panel measuring everyone
	/// else from a five-second-old copy of the reader would have the whole group drifting past them
	/// on a straight road.
	/// </para>
	/// </param>
	/// <param name="count">How many other riders to carry. Defaults to <see cref="DefaultCount"/>.</param>
	/// <param name="keepFor">
	/// How long a fix is still worth naming somebody's place from — <see cref="PinExpiry"/>, the same
	/// span the map draws pins for — or null to name every rider the ride holds a fix for.
	/// <para>
	/// It is the same argument the map makes, and it is stronger here. A pin at least sits where the
	/// rider was; this panel turns that fix into "300 m ahead", which is a claim about now and about
	/// a gap somebody is deciding whether to close. A phone that went flat at the last stop would sit
	/// in the panel as a traveller drifting steadily backwards, and the four lines the panel has are
	/// then spent on somebody who is not there — pushing off it a rider who is.
	/// </para>
	/// <para>
	/// The reader's own row is never aged out: on a phone it is measured from this device's own
	/// receiver rather than from the ride (see <paramref name="selfAlongMetres"/>), and dropping the
	/// anchor everything else is measured from would empty the panel rather than trim it. The
	/// members screen is where an old fix still belongs, with its age written beside it — see
	/// <see cref="MemberRoster"/>.
	/// </para>
	/// </param>
	/// <returns>
	/// The chosen riders <em>and</em> the reader, furthest along the route first — so the rider off
	/// the front is at the top of the panel and the one off the back is at the bottom, which is the
	/// order they are actually in on the road. Empty when there is nothing to say: no reader, no
	/// place on the route for them, or nobody else with a place on it.
	/// </returns>
	public static IReadOnlyList<NeighbourRow> Nearest(
		IReadOnlyList<MemberRow> rows,
		Guid? selfUserId,
		double? selfAlongMetres,
		int count = DefaultCount,
		TimeSpan? keepFor = null)
	{
		if (rows is null || rows.Count == 0 || selfUserId is not { } me || selfAlongMetres is not { } mine)
		{
			return [];
		}

		MemberRow? reader = null;
		List<(MemberRow Row, double Along)> others = new(rows.Count);

		foreach (MemberRow row in rows)
		{
			if (row.UserId == me)
			{
				reader = row;
				continue;
			}

			// Too long since they were last heard from to keep turning that fix into a gap (§18.6).
			// The age is clamped at zero for a fix from the future — see MemberRoster — so a phone
			// whose clock runs fast is never dropped for it.
			if (keepFor is { } limit && row.FixAge > limit)
				continue;

			// No projection, no place in this panel. A rider who is not sharing has no fix at all, and
			// one on a ride with no route has nothing to be along — and "—" on a panel whose entire
			// content is one number per rider is a row that costs a line and answers nothing.
			if (row.AlongMetres is { } along)
			{
				others.Add((row, along));
			}
		}

		if (reader is not { } self || others.Count == 0)
		{
			// A panel listing one rider, and it is you, says nothing the map was not already saying.
			return [];
		}

		// Nearest by the gap along the road, ahead and behind treated alike: the rider 200 m up the
		// road and the rider 200 m back are equally the answer to "where is the group". Names break
		// the tie so a repaint never swaps two riders who are the same distance away in opposite
		// directions.
		List<(MemberRow Row, double Along)> nearest =
		[
			.. others
				.OrderBy(entry => Math.Abs(entry.Along - mine))
				.ThenBy(entry => entry.Row.UserName, StringComparer.OrdinalIgnoreCase)
				.Take(Math.Max(0, count)),
		];

		if (nearest.Count == 0)
		{
			return [];
		}

		nearest.Add((self, mine));

		// Road order, leader first. The reader sorts into it by their own distance-along like anybody
		// else, which is what puts them at the top when they are off the front and at the bottom when
		// they are off the back — the panel's shape is the answer before any of the numbers are read.
		return
		[
			.. nearest
				.OrderByDescending(entry => entry.Along)
				.ThenBy(entry => entry.Row.UserName, StringComparer.OrdinalIgnoreCase)
				.Select(entry => new NeighbourRow(
					UserId: entry.Row.UserId,
					UserName: entry.Row.UserName,
					Colour: entry.Row.Colour,
					IsSelf: entry.Row.UserId == me,
					Presence: entry.Row.Presence,
					RelativeMetres: entry.Along - mine)),
		];
	}

	/// <summary>
	/// A gap along the route in the words the panel uses: how far, and which side of the reader.
	/// <para>
	/// <strong>Words rather than a <c>+</c> and a <c>−</c>.</strong> The order of the rows already
	/// says which way, so the sign would be the second thing saying it — and a minus sign at 0.8 rem
	/// through a visor in daylight is the first character to be lost. "back" and "ahead" survive
	/// being half-read.
	/// </para>
	/// </summary>
	/// <param name="metres">Distance along the route from the reader: positive ahead, negative behind.</param>
	/// <returns>Something readable at a glance — see <see cref="LevelMetres"/> for the middle case.</returns>
	public static string FormatRelative(double metres)
	{
		double magnitude = Math.Abs(metres);

		if (!double.IsFinite(metres) || magnitude < LevelMetres)
		{
			return "level";
		}

		return metres > 0
			? $"+ {MemberRoster.FormatDistance(magnitude)}"
			: $"- {MemberRoster.FormatDistance(magnitude)}";
	}
}
