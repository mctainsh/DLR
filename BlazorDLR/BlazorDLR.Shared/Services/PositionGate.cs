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
/// <strong>Stateful but pure.</strong> It holds the last fix it accepted, and nothing else — no
/// clock, no I/O, no platform. The caller supplies the fix and the fix carries its own time, so
/// the whole of §4.2's filter is exercised in tests by feeding it a sequence.
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
	/// Below this a fix cannot be speed-checked usefully: two fixes a few milliseconds apart make
	/// the arithmetic divide by something close to zero, and every fix would look like a teleport.
	/// </summary>
	private static readonly TimeSpan MinSpeedCheckInterval = TimeSpan.FromMilliseconds(250);

	private readonly AccuracyProfile _profile;
	private LocationFix? _lastAccepted;

	/// <summary>Creates a gate for one accuracy profile (§4.2).</summary>
	/// <param name="profile">The rider's chosen profile. A change of profile is a new gate.</param>
	public PositionGate(AccuracyProfile profile) => _profile = profile;

	/// <summary>Which profile this gate enforces.</summary>
	public AccuracyProfile Profile => _profile;

	/// <summary>The last fix this gate let through, or <c>null</c> before the first one.</summary>
	public LocationFix? LastAccepted => _lastAccepted;

	/// <summary>
	/// Whether this fix should be published, and why not when it should not.
	/// <para>
	/// The reason is returned rather than logged because the UI says it out loud: a rider who can
	/// see "waiting for a better fix" reads a slow map as the sky rather than as a broken app.
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

		if (_lastAccepted is not { } previous)
		{
			// The first usable fix always goes: a rider who has just turned sharing on wants to
			// appear on the map now, not at the end of the first interval.
			return Accept(fix);
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

		if (elapsed >= MinSpeedCheckInterval && movedM / elapsed.TotalSeconds > MaxSpeedMps)
		{
			return PositionGateDecision.Rejected(PositionGateReason.ImplausibleSpeed);
		}

		if (elapsed >= MinInterval(_profile) || movedM >= MinDistanceM(_profile))
		{
			return Accept(fix);
		}

		return PositionGateDecision.Rejected(PositionGateReason.TooSoonAndTooClose);
	}

	/// <summary>
	/// Forgets the last accepted fix, so the next one is treated as a first fix.
	/// <para>
	/// What a stop and restart calls. Without it, a rider who stopped sharing in Sydney and
	/// started again in Melbourne would have their first fix rejected as an implausible speed —
	/// and the gap between the two is exactly when the app is not watching.
	/// </para>
	/// </summary>
	public void Reset() => _lastAccepted = null;

	private PositionGateDecision Accept(LocationFix fix)
	{
		_lastAccepted = fix;
		return PositionGateDecision.Accepted;
	}

	/// <summary>How often a fix may be published, per profile (§4.2).</summary>
	/// <param name="profile">The rider's profile.</param>
	public static TimeSpan MinInterval(AccuracyProfile profile) => profile switch
	{
		AccuracyProfile.Eco => TimeSpan.FromSeconds(10),
		AccuracyProfile.Precise => TimeSpan.FromSeconds(1),
		_ => TimeSpan.FromSeconds(5),
	};

	/// <summary>How far a rider must have moved to be worth publishing early, per profile (§4.2).</summary>
	/// <param name="profile">The rider's profile.</param>
	public static double MinDistanceM(AccuracyProfile profile) => profile switch
	{
		AccuracyProfile.Eco => 25,
		AccuracyProfile.Precise => 5,
		_ => 10,
	};

	/// <summary>
	/// The worst reported accuracy a fix may carry and still be published.
	/// <para>
	/// §4.2 fixes the cadence and the distance per profile and leaves this to implementation. Four
	/// times the profile's min distance, floored at 30 m: it has to stay well above a consumer
	/// GPS's good-day error (5–10 m) or a cold start would never produce a publishable fix, and
	/// well below the point where the error circle is larger than the gaps §5.4 reports.
	/// </para>
	/// </summary>
	/// <param name="profile">The rider's profile.</param>
	public static double MaxAccuracyM(AccuracyProfile profile) => Math.Max(30, MinDistanceM(profile) * 4);
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
