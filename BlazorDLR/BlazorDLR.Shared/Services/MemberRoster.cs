using DLR.Core.Contracts.Rides;
using DLR.Core.Display;
using DLR.Core.Tracks;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// How the rider list is ordered (§5.4, §5.6).
/// <para>
/// Four orders rather than one, because "who is on this ride" is four different questions
/// depending on why it is being asked, and no single order answers more than one of them.
/// </para>
/// </summary>
public enum MemberSort
{
	/// <summary>
	/// Freshest fix first. The default: on a live screen the thing that decides whether a row is
	/// worth reading at all is whether anything is still coming in from it.
	/// </summary>
	LastActive = 0,

	/// <summary>By name. The stable order — the only one where a row does not move under a thumb.</summary>
	Name = 1,

	/// <summary>Furthest along the planned route first, which is §5.4's leader.</summary>
	AlongRoute = 2,

	/// <summary>Nearest to the reader first.</summary>
	Range = 3,
}

/// <summary>What a member's position is doing, as §5.6 insists it be said: three states, not two.</summary>
public enum MemberPresence
{
	/// <summary>Broadcasting, with a fix that has arrived recently enough to believe.</summary>
	Sharing = 0,

	/// <summary>
	/// Broadcasting, but nothing fresh is arriving — no fix at all, or one old enough to be a
	/// tunnel rather than a position. Their last known point is still on the map.
	/// </summary>
	NoSignal = 1,

	/// <summary>
	/// They chose not to share with this ride. A decision, not a network condition, and their pin
	/// is not on the map at all.
	/// </summary>
	NotSharing = 2,
}

/// <summary>One rider, as the live rider list draws them.</summary>
/// <param name="UserId">Which rider.</param>
/// <param name="UserName">Their handle, which is also the map label (§7.2).</param>
/// <param name="Role">What they may do. Blank and "Rider" are the ordinary case and go unlabelled.</param>
/// <param name="Colour">
/// The <c>#rrggbb</c> their marker is drawn in on the map (§16.3), already defaulted — the list and
/// the map have to name the same rider the same way or the swatch is worse than no swatch.
/// </param>
/// <param name="IsSelf">Whether this row is the person reading it.</param>
/// <param name="Presence">Sharing, no signal, or not sharing.</param>
/// <param name="FixAge">
/// How old their newest fix is, or null when the ride holds none for them. Null and
/// <see cref="TimeSpan.Zero"/> are very different answers and must not be collapsed.
/// </param>
/// <param name="RangeMetres">
/// Straight-line distance from the reader to them, or null when either end has no position.
/// </param>
/// <param name="AlongMetres">Distance along the planned route (§5.4), or null with no route or no fix.</param>
/// <param name="OffMetres">Distance from the route at that point, or null for the same reasons.</param>
/// <param name="OffRoute">Whether <paramref name="OffMetres"/> is past the off-route threshold.</param>
/// <param name="GapMetres">How far back from the leader they are, along the route. Zero for the leader.</param>
/// <param name="IsLeader">Whether they are the furthest along (§5.4).</param>
public readonly record struct MemberRow(
	Guid UserId,
	string UserName,
	string Role,
	string Colour,
	bool IsSelf,
	MemberPresence Presence,
	TimeSpan? FixAge,
	double? RangeMetres,
	double? AlongMetres,
	double? OffMetres,
	bool OffRoute,
	double? GapMetres,
	bool IsLeader);

/// <summary>
/// Everything the "Ride members live" screen shows about who is on a ride, derived in one place
/// (§5.3, §5.4, §5.6).
/// <para>
/// <strong>Here rather than in the component, for the reason <see cref="RiderMarker"/> gives.</strong>
/// Six columns, three presence states and four orders is a pile of rules, and a rule that can only
/// be checked by rendering it and reading the markup is a rule that gets checked once. Everything
/// below is pure — members in, rows out — so the ordering and the arithmetic are testable without
/// a renderer, and the component is left holding layout and nothing else.
/// </para>
/// <para>
/// <strong>No clock is read here.</strong> The instant arrives as an argument, which is what lets a
/// test state "this fix is ninety seconds old" as a fact rather than by sleeping — and is what
/// <c>ClockRules</c> requires of every non-test assembly anyway.
/// </para>
/// </summary>
public static class MemberRoster
{
	/// <summary>
	/// How stale a fix has to be before a sharing rider reads as "no signal" rather than "sharing".
	/// <para>
	/// Positions fan out to a ride once every five seconds (§5.3), so this is twelve missed ticks —
	/// long enough that a phone changing cell or a dropped hub connection does not flicker the whole
	/// list, short enough that a rider who went into a tunnel two minutes ago is not still being
	/// presented as live. It is the same distinction §5.6 draws between <em>not sharing</em> and
	/// <em>no signal</em>, applied to a fix that has stopped moving rather than to one that never
	/// arrived: to somebody waiting at a junction those are the same fact.
	/// </para>
	/// </summary>
	public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(90);

	/// <summary>Metres off the route above which a rider counts as "off-route" (§5.4).</summary>
	public const double OffRouteThresholdMetres = 50;

	/// <summary>
	/// Builds the list one render draws.
	/// </summary>
	/// <param name="members">The ride's members, from the snapshot plus whatever the hub has changed.</param>
	/// <param name="positions">
	/// Everyone's latest fix, keyed by rider (§5.3), or null for a caller that has none — which is
	/// the only case where <see cref="RideMemberSummary.HasPosition"/> is consulted instead.
	/// </param>
	/// <param name="route">
	/// The planned route riders are projected against (§5.4), the way round this device reads it, or
	/// null for a ride with no route.
	/// </param>
	/// <param name="from">
	/// Where the reader is, for the range column, or null when this device does not know — a browser
	/// with nothing shared, or a phone still waiting for its first fix.
	/// </param>
	/// <param name="selfUserId">Who is reading, so their own row can say so.</param>
	/// <param name="now">The instant ages are measured against.</param>
	/// <param name="sort">Which of the four orders to return them in.</param>
	/// <returns>One row per member, ordered.</returns>
	public static IReadOnlyList<MemberRow> Build(
		IReadOnlyList<RideMemberSummary> members,
		IReadOnlyDictionary<Guid, RiderPositionDto>? positions,
		IReadOnlyList<TrackPoint>? route,
		(double Latitude, double Longitude)? from,
		Guid? selfUserId,
		DateTimeOffset now,
		MemberSort sort)
	{
		if (members is null || members.Count == 0)
		{
			return [];
		}

		bool projectable = route is { Count: >= 2 };
		List<MemberRow> rows = new(members.Count);

		foreach (RideMemberSummary member in members)
		{
			RiderPositionDto? fix = positions is not null
				&& positions.TryGetValue(member.UserId, out RiderPositionDto? found)
					? found
					: null;

			// The age is clamped at zero rather than rendered negative: a fix is stamped by the
			// device that took it (§5.7), so a phone whose clock runs a few seconds fast produces
			// one from the future, and "in 4 s" is not a thing a position can be.
			TimeSpan? age = fix is null
				? null
				: now - fix.RecordedUtc is { Ticks: < 0 }
					? TimeSpan.Zero
					: now - fix.RecordedUtc;

			double? along = null;
			double? off = null;
			double? range = null;

			if (fix is not null)
			{
				TrackPoint point = new(PositionScale.ToDegrees(fix.Lat), PositionScale.ToDegrees(fix.Lon));

				if (projectable && GapCalculator.Project(route!, point) is { } projection)
				{
					along = projection.AlongMetres;
					off = projection.OffMetres;
				}

				if (from is { } me)
				{
					range = Distance.BetweenM(new TrackPoint(me.Latitude, me.Longitude), point);
				}
			}

			rows.Add(new MemberRow(
				UserId: member.UserId,
				UserName: string.IsNullOrWhiteSpace(member.UserName) ? "?" : member.UserName,
				Role: member.Role ?? string.Empty,
				Colour: MarkerColours.Or(member.MarkerColour),
				IsSelf: selfUserId is { } self && self == member.UserId,
				Presence: PresenceOf(member, fix, age, positions is not null),
				FixAge: age,
				RangeMetres: range,
				AlongMetres: along,
				OffMetres: off,
				OffRoute: off > OffRouteThresholdMetres,
				GapMetres: null,
				IsLeader: false));
		}

		return Order(WithGaps(rows), sort);
	}

	/// <summary>
	/// §5.6's three states, and the one rule that decides between them.
	/// <para>
	/// <strong>Not sharing and no signal must never collapse into one grey row.</strong> One is a
	/// decision — their pin is not on the map at all — and the other is a network condition, with
	/// their last known point still sitting there. Told apart, a group can wait at the junction for
	/// the second and not for the first; collapsed, that is the small ambiguity that leaves somebody
	/// behind.
	/// </para>
	/// </summary>
	/// <param name="member">The member row from the snapshot.</param>
	/// <param name="fix">Their latest position, or null when the ride holds none.</param>
	/// <param name="age">How old that fix is.</param>
	/// <param name="havePositions">
	/// Whether the caller supplied a position set at all. When it did not — a list rendered without
	/// one — the snapshot's own <see cref="RideMemberSummary.HasPosition"/> is the whole of what is
	/// known, and freshness is simply not a question that can be asked.
	/// </param>
	private static MemberPresence PresenceOf(
		RideMemberSummary member,
		RiderPositionDto? fix,
		TimeSpan? age,
		bool havePositions)
	{
		if (!member.Sharing)
		{
			return MemberPresence.NotSharing;
		}

		if (!havePositions)
		{
			return member.HasPosition ? MemberPresence.Sharing : MemberPresence.NoSignal;
		}

		// A fix that stopped arriving is the same fact as one that never did — see StaleAfter.
		return fix is not null && age < StaleAfter ? MemberPresence.Sharing : MemberPresence.NoSignal;
	}

	/// <summary>
	/// Marks the leader and measures everybody else back from them (§5.4).
	/// <para>
	/// Done over the whole list before it is ordered, so the leader is the leader whichever of the
	/// four orders is on screen — a rider reading the alphabetical list must not find a different
	/// person at the front of the ride from the one the route order names.
	/// </para>
	/// </summary>
	private static List<MemberRow> WithGaps(List<MemberRow> rows)
	{
		double? leaderAlong = null;

		foreach (MemberRow row in rows)
		{
			if (row.AlongMetres is { } along && (leaderAlong is null || along > leaderAlong))
			{
				leaderAlong = along;
			}
		}

		if (leaderAlong is not { } furthest)
		{
			return rows;
		}

		// A leader is claimed once. Two riders on the same metre of road is unlikely and not
		// impossible, and two rows both saying "leader" reads as a bug rather than as a dead heat.
		bool claimed = false;

		for (int index = 0; index < rows.Count; index++)
		{
			if (rows[index].AlongMetres is not { } along)
			{
				continue;
			}

			bool leader = !claimed && along >= furthest;
			claimed |= leader;

			rows[index] = rows[index] with { IsLeader = leader, GapMetres = furthest - along };
		}

		return rows;
	}

	/// <summary>
	/// Puts the rows in the asked-for order.
	/// <para>
	/// <strong>A row with nothing to sort on goes last, in every order.</strong> A rider who is not
	/// sharing has no range, no age and no place on the route, and floating them to the top — which
	/// is where a null sorts by default — would fill the head of the list with the rows carrying the
	/// least information. Names break every tie, so the order is total and a repaint never shuffles
	/// two rows that compare equal.
	/// </para>
	/// </summary>
	private static IReadOnlyList<MemberRow> Order(List<MemberRow> rows, MemberSort sort)
	{
		IOrderedEnumerable<MemberRow> ordered = sort switch
		{
			// Freshest first, so the ride's most recent news is at the top.
			MemberSort.LastActive => rows
				.OrderBy(row => row.FixAge is null)
				.ThenBy(row => row.FixAge ?? TimeSpan.Zero),

			// Furthest along first — the leader, then the rest of the ride behind them (§5.4).
			MemberSort.AlongRoute => rows
				.OrderBy(row => row.AlongMetres is null)
				.ThenByDescending(row => row.AlongMetres ?? 0),

			// Nearest first. The reader's own row sorts to the top at zero metres, which is right:
			// it is the point everything else is measured from.
			MemberSort.Range => rows
				.OrderBy(row => row.RangeMetres is null)
				.ThenBy(row => row.RangeMetres ?? 0),

			_ => rows.OrderBy(row => 0),
		};

		return [.. ordered.ThenBy(row => row.UserName, StringComparer.OrdinalIgnoreCase)];
	}

	/// <summary>
	/// A distance in the words the gap list already uses: whole metres below a kilometre, one
	/// decimal above. The question a range answers is "round the corner, or way back" (§5.4), and
	/// both readings are clearer without the digits nobody acts on.
	/// </summary>
	/// <param name="metres">The distance, or null when it is not known.</param>
	/// <returns>Something readable, and an em dash rather than a zero for "not known".</returns>
	public static string FormatDistance(double? metres) => metres switch
	{
		null => "—",
		< 1000 => $"{(int)Math.Round(metres.Value)} m",
		_ => $"{metres.Value / 1000:0.0} km",
	};

	/// <summary>
	/// How old a fix is, coarser the older it gets — the same treatment the offline banner gives a
	/// cached ride, and for the same reason: "48 s" and "49 s" are one fact to somebody deciding
	/// whether to believe a pin, and a number that ticks every second is a thing to watch rather
	/// than a thing to read.
	/// </summary>
	/// <param name="age">Time since the fix was taken, or null when there is no fix.</param>
	/// <returns>Something readable, and an em dash rather than "0 s" for "no fix at all".</returns>
	public static string FormatAge(TimeSpan? age) => age switch
	{
		null => "—",
		{ TotalSeconds: < 10 } => "now",
		{ TotalMinutes: < 1 } => $"{(int)age.Value.TotalSeconds} s",
		{ TotalHours: < 1 } => $"{(int)age.Value.TotalMinutes} min",
		{ TotalDays: < 1 } => $"{(int)age.Value.TotalHours} h",
		_ => $"{(int)age.Value.TotalDays} d",
	};

	/// <summary>The label a sort option carries in the picker.</summary>
	/// <param name="sort">Which order.</param>
	/// <returns>The rider-facing name.</returns>
	public static string Label(MemberSort sort) => sort switch
	{
		MemberSort.LastActive => "Last active",
		MemberSort.Name => "Alphabetical",
		MemberSort.AlongRoute => "Position along the route",
		MemberSort.Range => "Range",
		_ => sort.ToString(),
	};
}
