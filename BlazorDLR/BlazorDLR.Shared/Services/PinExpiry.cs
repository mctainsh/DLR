using System.Globalization;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// How long a rider's pin stays on the live map after the last fix behind it (§5.3, §18.6).
/// <para>
/// <strong>The problem it answers.</strong> A position sits in the ride's cache until the rider
/// stops sharing or the wind-down expires (§5.6), and it is rebroadcast every tick whether or not
/// it has moved. So a phone that went flat, lost signal in a valley or was left in a jacket at the
/// café leaves a pin on the map that looks exactly like a rider standing there — and the group
/// rides back for it. Past some age the honest answer is nothing at all: the map stops claiming to
/// know where that rider is, and the member list, which says <em>when</em> each fix was taken, is
/// where somebody goes to find out what happened to them.
/// </para>
/// <para>
/// <strong>The pin and the gap beside it.</strong> The neighbours panel (§5.4) is aged out on the
/// same span, because it makes the stronger claim of the two: a pin at least sits where the rider
/// was, and "300 m ahead" is about now and about a gap somebody is deciding whether to close. See
/// <see cref="NeighbourList.Nearest"/>, which takes this as an argument like every other rule
/// there. The reader's own row is not aged — it is the anchor the rest are measured from.
/// </para>
/// <para>
/// <strong>A device preference, like <see cref="RouteStyle"/>.</strong> Two riders on the same
/// ride can hold different answers and neither is wrong — a commuter group crossing a city and a
/// weekend run through a range disagree about how long a silent rider is still news. It is stored
/// through <see cref="IDeviceSettings"/>, never sent to the server, and changes nothing about what
/// the ride holds: the fix is still there, still in everybody else's batch, and still on the
/// member list with its age beside it.
/// </para>
/// <para>
/// <strong>Rider pins only.</strong> Markers a rider placed (§16.1) are authored things that stay
/// until somebody deletes them — a blind crest is no less blind an hour later — and this rider's
/// own mark is drawn from the device's own receiver rather than from the ride, so neither is aged
/// out here.
/// </para>
/// <para>
/// Deliberately longer than <see cref="MemberRoster.StaleAfter"/> and independent of it. Ninety
/// seconds is when a fix stops reading as <em>live</em> — the list greys the row and says how old
/// it is, with the pin still on the map where it was last seen, which is exactly what somebody
/// waiting at a junction needs. This is the later, blunter question of when that last known point
/// stops being worth drawing at all.
/// </para>
/// </summary>
public static class PinExpiry
{
	/// <summary>
	/// The <see cref="IDeviceSettings"/> key. Namespaced like <c>dlr.route-style</c>, and holding
	/// whole minutes rather than an encoded record — one number needs no format version, because
	/// there is no second field a later build could disagree about the order of.
	/// </summary>
	public const string StorageKey = "dlr.pin-expiry";

	/// <summary>
	/// What the settings screen offers, shortest first (§4.5).
	/// <para>
	/// A fixed set rather than a free minutes box. Every value here is a judgement about how long a
	/// silent rider is still news, and the two ends of a number field — a minute, a fortnight — are
	/// a map that empties mid-ride and one that never forgets anything. Six answers span the ways a
	/// group actually rides, and the list is short enough to read on a phone at the side of a road.
	/// </para>
	/// </summary>
	public static readonly IReadOnlyList<TimeSpan> Options =
	[
		TimeSpan.FromMinutes(5),
		TimeSpan.FromMinutes(10),
		TimeSpan.FromMinutes(30),
		TimeSpan.FromHours(1),
		TimeSpan.FromHours(2),
		TimeSpan.FromHours(6),
	];

	/// <summary>
	/// What a device that has never chosen uses: ten minutes.
	/// <para>
	/// Two minutes of silence is a tunnel, a cutting or a cell handover, and a map that drops a
	/// rider for one of those is worse than the ghost pin it is trying to prevent. Ten is long
	/// enough that nothing routine reaches it and short enough that a pin still on screen is a
	/// rider who was really there recently.
	/// </para>
	/// </summary>
	public static readonly TimeSpan Default = TimeSpan.FromMinutes(10);

	/// <summary>
	/// How the choice reads on the settings screen and nowhere else — the map draws no label for
	/// it, because a rider mid-ride should be reading the road rather than their own preferences.
	/// </summary>
	/// <param name="keepFor">One of <see cref="Options"/>, or any span.</param>
	public static string Label(TimeSpan keepFor) => keepFor switch
	{
		{ TotalMinutes: < 1 } => "Under a minute",
		{ TotalHours: < 1 } => $"{(int)keepFor.TotalMinutes} minutes",
		{ TotalHours: < 2 } => "1 hour",
		_ => $"{(int)keepFor.TotalHours} hours",
	};

	/// <summary>Whole minutes, for <see cref="IDeviceSettings"/> and for a <c>&lt;select&gt;</c>'s value.</summary>
	/// <param name="keepFor">The chosen span.</param>
	public static string Encode(TimeSpan keepFor) =>
		((int)Nearest(keepFor).TotalMinutes).ToString(CultureInfo.InvariantCulture);

	/// <summary>
	/// Reads back what <see cref="Encode"/> wrote, or answers <see cref="Default"/> for a device
	/// that has never chosen, one with storage blocked, and the prerender pass — which are the same
	/// answer to a caller (see <see cref="IDeviceSettings"/>).
	/// <para>
	/// Snapped to <see cref="Options"/> rather than taken at face value. The stored value comes off
	/// a device we do not control and is rendered back as a dropdown: a number that is not on the
	/// list would leave that dropdown showing something other than what the map is doing, which is
	/// the one failure a settings screen must not have.
	/// </para>
	/// </summary>
	/// <param name="encoded">A string from <see cref="Encode"/>, or <c>null</c>.</param>
	public static TimeSpan Decode(string? encoded) =>
		int.TryParse(encoded, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes) && minutes > 0
			? Nearest(TimeSpan.FromMinutes(minutes))
			: Default;

	/// <summary>
	/// Whether a fix is too old to draw a pin from.
	/// <para>
	/// The clock arrives as an argument like everywhere else in this namespace, so a test states
	/// "this fix is eleven minutes old" as a fact rather than by sleeping — and <c>ClockRules</c>
	/// requires it of every non-test assembly anyway.
	/// </para>
	/// <para>
	/// A fix from the future is never expired: it is stamped by the device that took it (§5.7), so
	/// a phone whose clock runs fast produces one, and the sign of that difference says nothing
	/// about how long ago the rider was there.
	/// </para>
	/// </summary>
	/// <param name="recordedUtc">When the fix was taken.</param>
	/// <param name="now">The instant it is being judged against.</param>
	/// <param name="keepFor">How long a pin outlives its fix on this device.</param>
	public static bool IsExpired(DateTimeOffset recordedUtc, DateTimeOffset now, TimeSpan keepFor) =>
		now - recordedUtc > keepFor;

	/// <summary>
	/// The offered value closest to <paramref name="keepFor"/>. A tie goes to the longer of the
	/// two, because the cost of the two mistakes is not symmetric: keeping a pin a few minutes
	/// past its welcome is a stale mark on a map, and dropping one early takes a rider who is
	/// still out there off it.
	/// </summary>
	/// <param name="keepFor">Any span.</param>
	private static TimeSpan Nearest(TimeSpan keepFor)
	{
		TimeSpan closest = Default;

		foreach (TimeSpan option in Options)
		{
			if (Math.Abs((option - keepFor).Ticks) <= Math.Abs((closest - keepFor).Ticks))
			{
				closest = option;
			}
		}

		return closest;
	}
}
