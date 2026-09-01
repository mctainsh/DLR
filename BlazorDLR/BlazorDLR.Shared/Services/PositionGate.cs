using DLR.Core.Tracks;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// The filter every fix passes before it is published (§4.2).
/// <para>
/// <strong>Why a gate at all.</strong> A phone's location service does not answer the question
/// the app is asking. It reports what it has, as often as it can, including the fix it took from
/// a cell tower while the GPS was still warming up, the one that puts a stationary rider three
/// streets away because they are parked under a bridge, and - at a red light - the same point
/// once a second for two minutes. Publishing all of that costs the rider's battery and uplink,
/// costs every other member of the ride a redraw, and puts a pin somewhere nobody is.
/// </para>
/// <para>
/// Five rules. The first two throw a fix out on its own merits; the last three are the rider's
/// <see cref="LocationUpdateRate"/>, said in the order they are asked:
/// <list type="number">
///   <item><strong>Accuracy</strong> - a fix whose own reported accuracy is worse than
///     <see cref="MaxAccuracyM"/> says "I am somewhere in this circle", and past a point the
///     circle is bigger than the thing being drawn.</item>
///   <item><strong>Speed sanity</strong> - a jump implying more than <see cref="MaxSpeedMps"/>
///     from the last accepted fix did not happen on a motorcycle, so it is a bad fix rather than
///     a fast one.</item>
///   <item><strong>Minimum</strong> - nothing goes inside the rider's floor, whatever else is
///     true. A fix that has travelled the distance is <em>held</em> here rather than dropped:
///     the receiver keeps producing better ones, and the first one past the floor is what goes.</item>
///   <item><strong>Maximum</strong> - nothing sent for that long and this one goes, moved or not.</item>
///   <item><strong>Distance</strong> - travelled far enough, so there is something new to say.</item>
/// </list>
/// The last two are an <em>or</em>: either enough ground or enough time is reason to send. A pure
/// distance rule leaves a rider stopped at a junction looking stale to everyone waiting for them,
/// and a pure time rule sends the same point over and over.
/// </para>
/// <para>
/// <strong>Stateful but pure.</strong> It holds the last fix that <em>reached the ride</em>, and
/// nothing else - no clock, no I/O, no platform. The caller supplies the fix and the fix carries
/// its own time, so the whole of §4.2's filter is exercised in tests by feeding it a sequence.
/// </para>
/// <para>
/// <strong>Two calls, not one.</strong> <see cref="Evaluate"/> says whether a fix is worth sending;
/// <see cref="Confirm"/> says that one arrived. Splitting them is what stops a failed send from
/// costing a rider the profile's whole interval, and it is why the speed rule needs
/// <see cref="MaxConsecutiveImplausible"/> - a reference point that is never confirmed, or that is
/// simply wrong, must not be able to silence a rider indefinitely.
/// </para>
/// </summary>
public sealed class PositionGate
{
	/// <summary>
	/// Fastest a fix may imply before it is treated as bad data rather than as speed: 90 m/s,
	/// about 324 km/h. Above any road-legal speed and above a track day, and far below the
	/// hundreds of metres a first GPS fix commonly jumps when it corrects itself.
	/// </summary>
	public const double MaxSpeedMps = 90;

	/// <summary>
	/// How many fixes in a row may be refused as <see cref="PositionGateReason.ImplausibleSpeed"/>
	/// before the gate concludes the fault is its own reference point and starts again.
	/// <para>
	/// The rule is asymmetric on purpose and it had to be taught this. A refused fix does not
	/// become the new reference - that is what makes the rule work at all - so a reference that is
	/// itself wrong refuses <em>everything</em> measured against it, and the only way out was for
	/// the rider to travel far enough that the arithmetic fell back under
	/// <see cref="MaxSpeedMps"/>. For a 20 km error that is 222 seconds of a pin that has stopped
	/// moving, on every other rider's map, with nothing in the app able to say why.
	/// </para>
	/// <para>
	/// Three refusals, so the <em>fourth</em> such fix is the one that goes: one bad fix is the case
	/// the rule exists for, and a run that survives three more chances to disagree with itself is
	/// not a receiver correcting itself - it is the reference being stale. The count is of fixes
	/// refused, which is why the escape is one past it rather than on it. At the Balanced cadence
	/// that is about eight seconds of caution instead of nearly four minutes of silence.
	/// </para>
	/// </summary>
	public const int MaxConsecutiveImplausible = 3;

	/// <summary>
	/// Below this a fix cannot be speed-checked usefully: two fixes a few milliseconds apart make
	/// the arithmetic divide by something close to zero, and every fix would look like a teleport.
	/// </summary>
	private static readonly TimeSpan MinSpeedCheckInterval = TimeSpan.FromMilliseconds(250);

	/// <summary>
	/// Guards the reference point. Held briefly and uncontended in the ordinary case, but the two
	/// callers really are two threads since the publish moved off the pump (see
	/// <see cref="Confirm"/>): the fix loop evaluates and the sender confirms.
	/// </summary>
	private readonly object _gate = new();

	private readonly LocationUpdateRate _rate;
	private LocationFix? _lastAccepted;

	/// <summary>
	/// The last fix this gate <em>approved</em>, whether or not it ever reached the ride. Only the
	/// speed rule reads it, and only because <see cref="_lastAccepted"/> can stay null for a whole
	/// outage - see <see cref="Evaluate"/>.
	/// </summary>
	private LocationFix? _lastApproved;

	private int _implausibleRun;

	/// <summary>Creates a gate for one update rate (§4.2).</summary>
	/// <param name="rate">The rider's chosen rate. A change of rate is a new gate.</param>
	public PositionGate(LocationUpdateRate rate) => _rate = rate;

	/// <summary>Which rate this gate enforces.</summary>
	public LocationUpdateRate Rate => _rate;

	/// <summary>
	/// The worst reported accuracy a fix may carry and still be published, for this gate's rate.
	/// </summary>
	public double MaxAccuracyM => MaxAccuracyFor(_rate.DistanceM);

	/// <summary>
	/// The last fix that actually reached the ride, or <c>null</c> before the first one - see
	/// <see cref="Confirm"/> for why it is that and not the last one this gate approved.
	/// </summary>
	public LocationFix? LastAccepted => _lastAccepted;

	/// <summary>
	/// The last fix this gate was willing to publish, whether or not it ever landed - what the
	/// keepalive restates when the receiver stops producing fixes at all.
	/// <para>
	/// Deliberately not <see cref="LastAccepted"/>: on a link that has been down since the watch
	/// started nothing has ever landed, and that is precisely the run where a rider most needs the
	/// retry.
	/// </para>
	/// </summary>
	public LocationFix? LastApproved
	{
		get
		{
			lock (_gate)
			{
				return _lastApproved;
			}
		}
	}

	/// <summary>
	/// Whether this fix should be published, and why not when it should not.
	/// <para>
	/// The reason is returned rather than logged because the UI says it out loud: a rider who can
	/// see "waiting for a better fix" reads a slow map as the sky rather than as a broken app.
	/// </para>
	/// <para>
	/// <strong>Approving a fix is not the same as sending it.</strong> This moves no state that a
	/// later fix is measured against; <see cref="Confirm"/> does, and only for a fix that arrived.
	/// </para>
	/// </summary>
	/// <param name="fix">The fix as the platform reported it.</param>
	/// <returns>The decision, and the rule that made it.</returns>
	public PositionGateDecision Evaluate(LocationFix fix)
	{
		if (!double.IsFinite(fix.Latitude) || !double.IsFinite(fix.Longitude)
			|| fix.Latitude is < -90 or > 90 || fix.Longitude is < -180 or > 180)
		{
			// Not a point on the earth. Rare, and always a platform bug or a mocked provider -
			// but it would be stored, drawn, and used to compute somebody's gap to the group.
			return PositionGateDecision.Rejected(PositionGateReason.NotACoordinate);
		}

		if (fix.AccuracyM is { } accuracy && accuracy > MaxAccuracyM)
		{
			return PositionGateDecision.Rejected(PositionGateReason.TooInaccurate);
		}

		// The two rules above answer from the fix alone, so they neither confirm nor impeach the
		// reference point and deliberately leave the run below where it stands.
		lock (_gate)
		{
			// Speed sanity is measured against the freshest fix this gate has any evidence for -
			// approved or confirmed - and the two are not the same thing.
			//
			// Confirm only runs on a send that succeeded, so on a link that is down _lastAccepted
			// stays null for the whole outage. Checking the speed rule against that alone meant a
			// rider with no uplink had no reference at all: every fix took the first-fix branch
			// below, unchecked, and the first cell-tower jump of the day was published the moment
			// the link came back. The cadence rules still measure from the confirmed fix, which is
			// the whole point of the split - a failed send must cost a retry, not an interval.
			if (Newest(_lastApproved, _lastAccepted) is { } reference)
			{
				TimeSpan since = fix.RecordedUtc - reference.RecordedUtc;

				double jumpedM = Distance.BetweenM(
					new TrackPoint(reference.Latitude, reference.Longitude),
					new TrackPoint(fix.Latitude, fix.Longitude));

				if (since >= MinSpeedCheckInterval && jumpedM / since.TotalSeconds > MaxSpeedMps)
				{
					if (++_implausibleRun <= MaxConsecutiveImplausible)
					{
						return PositionGateDecision.Rejected(PositionGateReason.ImplausibleSpeed);
					}

					// Enough fixes in a row have now disagreed with the reference that the reference
					// is the thing more likely to be wrong - see MaxConsecutiveImplausible. Both
					// references are forgotten, so this fix is judged as a first fix and the next one
					// is judged against it.
					_implausibleRun = 0;
					_lastAccepted = null;

					return Approve(fix);
				}
			}

			// A fix that got this far agrees with the reference, whatever happens to it below.
			_implausibleRun = 0;

			if (_lastAccepted is not { } previous)
			{
				// The first usable fix always goes: a rider who has just turned sharing on wants to
				// appear on the map now, not at the end of the first interval. Also the retry path -
				// nothing has been confirmed yet, so no cadence has started to wait out.
				return Approve(fix);
			}

			TimeSpan elapsed = fix.RecordedUtc - previous.RecordedUtc;

			if (elapsed < TimeSpan.Zero)
			{
				// A fix stamped before one already accepted - a platform replaying a cached point, or
				// a clock correction landing mid-ride. Publishing it would move every other rider's
				// map backwards.
				return PositionGateDecision.Rejected(PositionGateReason.OutOfOrder);
			}

			if (elapsed < _rate.Minimum)
			{
				// The floor, and it is checked before the other two on purpose: it outranks them.
				// Nothing is remembered about this fix, because nothing needs to be - the receiver
				// goes on producing better ones, and the first that clears the floor is measured
				// against the same reference and carries the same news, only fresher.
				return PositionGateDecision.Rejected(PositionGateReason.HeldByMinimum);
			}

			if (elapsed >= _rate.Maximum)
			{
				return Approve(fix);
			}

			double movedM = Distance.BetweenM(
				new TrackPoint(previous.Latitude, previous.Longitude),
				new TrackPoint(fix.Latitude, fix.Longitude));

			if (movedM >= _rate.DistanceM)
			{
				return Approve(fix);
			}

			return PositionGateDecision.Rejected(PositionGateReason.NothingNewToSay);
		}
	}

	/// <summary>
	/// Records that this gate approved a fix, and says so. Call under <see cref="_gate"/>.
	/// <para>
	/// Never walks backwards: an approved fix older than one already held would make the speed rule
	/// measure from the past and read ordinary riding as a teleport.
	/// </para>
	/// </summary>
	/// <param name="fix">The fix being approved.</param>
	private PositionGateDecision Approve(LocationFix fix)
	{
		if (_lastApproved is not { } approved || fix.RecordedUtc > approved.RecordedUtc)
		{
			_lastApproved = fix;
		}

		return PositionGateDecision.Accepted;
	}

	/// <summary>The later of two fixes, either of which may be absent.</summary>
	private static LocationFix? Newest(LocationFix? first, LocationFix? second)
	{
		if (first is not { } left)
		{
			return second;
		}

		if (second is not { } right)
		{
			return first;
		}

		return left.RecordedUtc >= right.RecordedUtc ? left : right;
	}

	/// <summary>
	/// Records that a fix this gate approved actually reached the ride, making it what every later
	/// fix is measured against.
	/// <para>
	/// <strong>Delivery, not approval, is what moves the cadence on.</strong> The gate used to
	/// advance the moment it said yes, which meant a fix that then failed to send still spent the
	/// profile's whole interval: a rider whose link had just come back waited out another 30
	/// seconds - or a full minute on a coarse rate - before the app would even try again, while the ride
	/// looked at a pin that was minutes old. Measuring from the last fix that landed makes a
	/// failed send cost a retry rather than an interval.
	/// </para>
	/// <para>
	/// Anything not newer than what is already held is ignored, so a send that overtakes another
	/// cannot walk the reference backwards.
	/// </para>
	/// </summary>
	/// <param name="fix">The fix that reached the server.</param>
	public void Confirm(LocationFix fix)
	{
		lock (_gate)
		{
			if (_lastAccepted is { } previous && fix.RecordedUtc <= previous.RecordedUtc)
			{
				return;
			}

			_lastAccepted = fix;
			_implausibleRun = 0;
		}
	}

	/// <summary>
	/// Forgets the last accepted fix, so the next one is treated as a first fix.
	/// <para>
	/// What a stop and restart calls. Without it, a rider who stopped sharing in Sydney and
	/// started again in Melbourne would have their first fix rejected as an implausible speed -
	/// and the gap between the two is exactly when the app is not watching.
	/// </para>
	/// </summary>
	public void Reset()
	{
		lock (_gate)
		{
			_lastAccepted = null;
			_lastApproved = null;
			_implausibleRun = 0;
		}
	}

	/// <summary>
	/// The worst reported accuracy a fix may carry and still be published, for an update distance.
	/// <para>
	/// §4.2 leaves this to implementation, and it is not something the rider is asked: it is a
	/// question about whether a fix is worth drawing at all, not about how often to draw one. Four
	/// times the update distance, clamped to [30 m, 50 m] - it has to stay well above a consumer
	/// GPS's good-day error (5–10 m) or a cold start would never produce a publishable fix, and
	/// well below the point where the error circle is larger than the gaps §5.4 reports.
	/// </para>
	/// <para>
	/// The upper clamp is what stops a 500 m update distance widening this to 2 km. A fix with an
	/// error circle that size is a cell-tower fix, and drawing one on a group ride's map puts a
	/// rider two suburbs from where they are - asking for coarse <em>updates</em> is not asking to
	/// be drawn in the wrong place.
	/// </para>
	/// </summary>
	/// <param name="distanceM">The rider's update distance.</param>
	public static double MaxAccuracyFor(double distanceM) => Math.Clamp(distanceM * 4, 30, 50);
}

/// <summary>Why <see cref="PositionGate"/> refused a fix, or that it did not.</summary>
public enum PositionGateReason
{
	/// <summary>The fix was published.</summary>
	Accepted = 0,

	/// <summary>Latitude or longitude was not a point on the earth.</summary>
	NotACoordinate = 1,

	/// <summary>The platform's own accuracy estimate was worse than the profile allows.</summary>
	TooInaccurate = 2,

	/// <summary>Stamped before a fix already accepted.</summary>
	OutOfOrder = 3,

	/// <summary>Too far from the last fix, too fast - bad data rather than speed.</summary>
	ImplausibleSpeed = 4,

	/// <summary>
	/// Inside the update distance and inside the maximum: the rider has not moved far enough to be
	/// worth saying, and the ride has not been waiting long enough to be told anyway.
	/// </summary>
	NothingNewToSay = 5,

	/// <summary>
	/// Inside the rider's minimum update time. Distinct from <see cref="NothingNewToSay"/> because
	/// it is the one refusal that says nothing about the fix: it may be carrying a mile of new
	/// road, and it is refused because the last send is too recent.
	/// </summary>
	HeldByMinimum = 6,
}

/// <summary>One gate decision.</summary>
/// <param name="Publish">Whether the caller should send this fix.</param>
/// <param name="Reason">Which rule decided, so the UI can say what the map is waiting for.</param>
public readonly record struct PositionGateDecision(bool Publish, PositionGateReason Reason)
{
	/// <summary>The fix passed every rule.</summary>
	public static PositionGateDecision Accepted => new(true, PositionGateReason.Accepted);

	/// <summary>The fix was refused by one rule.</summary>
	/// <param name="reason">Which one.</param>
	public static PositionGateDecision Rejected(PositionGateReason reason) => new(false, reason);
}
