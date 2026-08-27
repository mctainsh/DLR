using DLR.Core.Tracks;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// The filter every fix passes before it is published (§4.2).
/// <para>
/// <strong>Why a gate at all.</strong> A phone's location service does not answer the question
/// the app is asking. It reports what it has, as often as it can, including the fix it took from
/// a cell tower while the GPS was still warming up, the one that puts a stationary rider three
/// streets away because they are parked under a bridge, and — at a red light — the same point
/// once a second for two minutes. Publishing all of that costs the rider's battery and uplink,
/// costs every other member of the ride a redraw, and puts a pin somewhere nobody is.
/// </para>
/// <para>
/// Four rules, in the order §4.2 states them:
/// <list type="number">
///   <item><strong>Accuracy gate</strong> — a fix whose own reported accuracy is worse than the
///     profile allows says "I am somewhere in this circle", and past a point the circle is bigger
///     than the thing being drawn.</item>
///   <item><strong>Speed sanity</strong> — a jump implying more than <see cref="MaxSpeedMps"/>
///     from the last accepted fix did not happen on a motorcycle, so it is a bad fix rather than
///     a fast one.</item>
///   <item><strong>Min interval</strong> — the profile's cadence, which is the battery budget.</item>
///   <item><strong>Min distance</strong> — a rider who has not moved has nothing to say.</item>
/// </list>
/// The last two are an <em>or</em>, not an <em>and</em>: either enough time or enough ground is
/// reason to send. A pure min-distance rule leaves a rider stopped at a junction looking stale to
/// everyone waiting for them, and a pure min-time rule sends the same point over and over.
/// </para>
/// <para>
/// <strong>Stateful but pure.</strong> It holds the last fix that <em>reached the ride</em>, and
/// nothing else — no clock, no I/O, no platform. The caller supplies the fix and the fix carries
/// its own time, so the whole of §4.2's filter is exercised in tests by feeding it a sequence.
/// </para>
/// <para>
/// <strong>Two calls, not one.</strong> <see cref="Evaluate"/> says whether a fix is worth sending;
/// <see cref="Confirm"/> says that one arrived. Splitting them is what stops a failed send from
/// costing a rider the profile's whole interval, and it is why the speed rule needs
/// <see cref="MaxConsecutiveImplausible"/> — a reference point that is never confirmed, or that is
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
	/// become the new reference — that is what makes the rule work at all — so a reference that is
	/// itself wrong refuses <em>everything</em> measured against it, and the only way out was for
	/// the rider to travel far enough that the arithmetic fell back under
	/// <see cref="MaxSpeedMps"/>. For a 20 km error that is 222 seconds of a pin that has stopped
	/// moving, on every other rider's map, with nothing in the app able to say why.
	/// </para>
	/// <para>
	/// Three refusals, so the <em>fourth</em> such fix is the one that goes: one bad fix is the case
	/// the rule exists for, and a run that survives three more chances to disagree with itself is
	/// not a receiver correcting itself — it is the reference being stale. The count is of fixes
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

	private readonly AccuracyProfile _profile;
	private LocationFix? _lastAccepted;

	/// <summary>
	/// The last fix this gate <em>approved</em>, whether or not it ever reached the ride. Only the
	/// speed rule reads it, and only because <see cref="_lastAccepted"/> can stay null for a whole
	/// outage — see <see cref="Evaluate"/>.
	/// </summary>
	private LocationFix? _lastApproved;

	private int _implausibleRun;

	/// <summary>Creates a gate for one accuracy profile (§4.2).</summary>
	/// <param name="profile">The rider's chosen profile. A change of profile is a new gate.</param>
	public PositionGate(AccuracyProfile profile) => _profile = profile;

	/// <summary>Which profile this gate enforces.</summary>
	public AccuracyProfile Profile => _profile;

	/// <summary>
	/// The last fix that actually reached the ride, or <c>null</c> before the first one — see
	/// <see cref="Confirm"/> for why it is that and not the last one this gate approved.
	/// </summary>
	public LocationFix? LastAccepted => _lastAccepted;

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
			// Not a point on the earth. Rare, and always a platform bug or a mocked provider —
			// but it would be stored, drawn, and used to compute somebody's gap to the group.
			return PositionGateDecision.Rejected(PositionGateReason.NotACoordinate);
		}

		if (fix.AccuracyM is { } accuracy && accuracy > MaxAccuracyM(_profile))
		{
			return PositionGateDecision.Rejected(PositionGateReason.TooInaccurate);
		}

		// The two rules above answer from the fix alone, so they neither confirm nor impeach the
		// reference point and deliberately leave the run below where it stands.
		lock (_gate)
		{
			// Speed sanity is measured against the freshest fix this gate has any evidence for —
			// approved or confirmed — and the two are not the same thing.
			//
			// Confirm only runs on a send that succeeded, so on a link that is down _lastAccepted
			// stays null for the whole outage. Checking the speed rule against that alone meant a
			// rider with no uplink had no reference at all: every fix took the first-fix branch
			// below, unchecked, and the first cell-tower jump of the day was published the moment
			// the link came back. The cadence rules still measure from the confirmed fix, which is
			// the whole point of the split — a failed send must cost a retry, not an interval.
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
					// is the thing more likely to be wrong — see MaxConsecutiveImplausible. Both
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
				// appear on the map now, not at the end of the first interval. Also the retry path —
				// nothing has been confirmed yet, so no cadence has started to wait out.
				return Approve(fix);
			}

			TimeSpan elapsed = fix.RecordedUtc - previous.RecordedUtc;

			if (elapsed < TimeSpan.Zero)
			{
				// A fix stamped before one already accepted — a platform replaying a cached point, or
				// a clock correction landing mid-ride. Publishing it would move every other rider's
				// map backwards.
				return PositionGateDecision.Rejected(PositionGateReason.OutOfOrder);
			}

			double movedM = Distance.BetweenM(
				new TrackPoint(previous.Latitude, previous.Longitude),
				new TrackPoint(fix.Latitude, fix.Longitude));

			if (elapsed >= MinInterval(_profile) || movedM >= MinDistanceM(_profile))
			{
				return Approve(fix);
			}

			return PositionGateDecision.Rejected(PositionGateReason.TooSoonAndTooClose);
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
	/// seconds — or a full minute on Eco — before the app would even try again, while the ride
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
	/// started again in Melbourne would have their first fix rejected as an implausible speed —
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
	/// How often a fix may be published, per profile (§4.2).
	/// <para>
	/// These are <em>publish</em> rates, not capture rates. The platform still delivers fixes at
	/// its own cadence and the recorder (§15.1) still keeps them at whatever interval the rider
	/// chose on the Location screen — this gate decides only what costs uplink and costs every
	/// other rider on the ride a redraw.
	/// </para>
	/// </summary>
	/// <param name="profile">The rider's profile.</param>
	public static TimeSpan MinInterval(AccuracyProfile profile) => profile switch
	{
		AccuracyProfile.Eco => TimeSpan.FromSeconds(60),
		AccuracyProfile.Precise => TimeSpan.FromSeconds(10),
		_ => TimeSpan.FromSeconds(30),
	};

	/// <summary>How far a rider must have moved to be worth publishing early, per profile (§4.2).</summary>
	/// <param name="profile">The rider's profile.</param>
	public static double MinDistanceM(AccuracyProfile profile) => profile switch
	{
		AccuracyProfile.Eco => 50,
		AccuracyProfile.Precise => 5,
		_ => 10,
	};

	/// <summary>
	/// The worst reported accuracy a fix may carry and still be published.
	/// <para>
	/// §4.2 fixes the cadence and the distance per profile and leaves this to implementation. Four
	/// times the profile's min distance, clamped to [30 m, 100 m]: it has to stay well above a
	/// consumer GPS's good-day error (5–10 m) or a cold start would never produce a publishable
	/// fix, and well below the point where the error circle is larger than the gaps §5.4 reports.
	/// </para>
	/// <para>
	/// The upper clamp is what stops Eco's 50 m step widening the accuracy gate to 200 m. A fix
	/// with a 200 m error circle is a cell-tower fix, and drawing one on a group ride's map puts a
	/// rider two suburbs from where they are — the profile is a battery decision and must not
	/// quietly become a "publish anything" decision.
	/// </para>
	/// </summary>
	/// <param name="profile">The rider's profile.</param>
	public static double MaxAccuracyM(AccuracyProfile profile) => Math.Clamp(MinDistanceM(profile) * 4, 30, 100);
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

	/// <summary>Too far from the last fix, too fast — bad data rather than speed.</summary>
	ImplausibleSpeed = 4,

	/// <summary>Inside the profile's interval and inside its distance: nothing new to say.</summary>
	TooSoonAndTooClose = 5,
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
