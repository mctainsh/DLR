namespace BlazorDLR.Shared.Services;

/// <summary>
/// How often this device sends its position to the adventures it is sharing with (§4.2) — three
/// numbers the rider sets, and the only description of the publish rate in the app.
/// <para>
/// <strong>Three rules, and they answer different questions.</strong>
/// <list type="number">
///   <item><see cref="DistanceM"/> — travel this far and the new position goes.</item>
///   <item><see cref="Maximum"/> — go this long without sending anything and the current position
///     goes anyway, so a rider stopped at a servo is not a pin that quietly ages.</item>
///   <item><see cref="Minimum"/> — never send more often than this, however fast the ground is
///     moving. A distance trigger inside this window is <em>held</em>, not dropped: the position
///     that goes when the window closes is the latest one, not the one that rang the bell.</item>
/// </list>
/// </para>
/// <para>
/// <strong>This replaced Eco / Balanced / Precise.</strong> Those were three fixed pairs of
/// exactly the first two numbers with a third the app never let anybody see, and the names were
/// doing the work the numbers should have: riders asking "how often does this actually send?"
/// could not be answered without the design document. The trade is a screen with three controls
/// instead of one, in exchange for a setting that says what it does.
/// </para>
/// <para>
/// <strong>The invariant is structural.</strong> Every value is snapped to one of the lists below
/// on the way in, and <see cref="Maximum"/> is chosen from the ones greater than
/// <see cref="Minimum"/> — so an instance that says "send at most every 60 s and at least every
/// 10 s" cannot be constructed, from the settings screen, from the device store, or from a test.
/// </para>
/// </summary>
public sealed record LocationUpdateRate
{
	/// <summary>The update distances offered, in metres.</summary>
	public static readonly double[] Distances = [5, 10, 25, 50, 100, 500];

	/// <summary>The maximum update times offered.</summary>
	public static readonly TimeSpan[] Maximums =
	[
		TimeSpan.FromSeconds(10),
		TimeSpan.FromSeconds(30),
		TimeSpan.FromSeconds(60),
		TimeSpan.FromSeconds(120),
		TimeSpan.FromMinutes(5),
		TimeSpan.FromMinutes(10),
	];

	/// <summary>The minimum update times offered.</summary>
	/// <remarks>
	/// The largest of these is smaller than the second-smallest <see cref="Maximums"/> entry, so
	/// there is always a legal maximum left however the rider sets the floor.
	/// </remarks>
	public static readonly TimeSpan[] Minimums =
	[
		TimeSpan.FromSeconds(2),
		TimeSpan.FromSeconds(5),
		TimeSpan.FromSeconds(10),
		TimeSpan.FromSeconds(30),
		TimeSpan.FromSeconds(60),
	];

	/// <summary>
	/// What a device that has never chosen gets: 25 m, at most a minute apart, never closer
	/// together than five seconds.
	/// </summary>
	public static readonly LocationUpdateRate Default =
		new(25, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5));

	/// <summary>Builds a rate, snapping each value to the offered list and holding the invariant.</summary>
	/// <param name="distanceM">How far the rider travels before the new position goes.</param>
	/// <param name="maximum">The longest the ride may hear nothing.</param>
	/// <param name="minimum">The shortest gap between two sends.</param>
	public LocationUpdateRate(double distanceM, TimeSpan maximum, TimeSpan minimum)
	{
		DistanceM = Nearest(Distances, distanceM);
		Minimum = Nearest(Minimums, minimum);

		// From the legal ones only, which is what makes "maximum is greater than minimum" a
		// property of the type rather than a rule three screens have to remember.
		Maximum = Nearest([.. Maximums.Where(candidate => candidate > Minimum)], maximum);
	}

	/// <summary>Travel this far and the new position is sent.</summary>
	public double DistanceM { get; }

	/// <summary>Nothing sent for this long and the current position is sent anyway.</summary>
	public TimeSpan Maximum { get; }

	/// <summary>Never send twice inside this. Always shorter than <see cref="Maximum"/>.</summary>
	public TimeSpan Minimum { get; }

	/// <summary>The same rate with a different update distance.</summary>
	/// <param name="distanceM">The new distance.</param>
	public LocationUpdateRate WithDistance(double distanceM) => new(distanceM, Maximum, Minimum);

	/// <summary>The same rate with a different maximum.</summary>
	/// <param name="maximum">The new maximum.</param>
	public LocationUpdateRate WithMaximum(TimeSpan maximum) => new(DistanceM, maximum, Minimum);

	/// <summary>
	/// The same rate with a different minimum, taking the maximum up with it when it has to.
	/// </summary>
	/// <remarks>
	/// A rider moving the floor past the ceiling means the floor, not a refusal: the constructor
	/// picks the nearest legal maximum, which is the next one above the new minimum.
	/// </remarks>
	/// <param name="minimum">The new minimum.</param>
	public LocationUpdateRate WithMinimum(TimeSpan minimum) => new(DistanceM, Maximum, minimum);

	/// <summary>The rate as one line, for the settings screen and the log.</summary>
	/// <returns>Something like <c>every 25 m, at most 60 s apart, at least 5 s apart</c>.</returns>
	public override string ToString() =>
		$"every {DistanceM:0} m, at most {Describe(Maximum)} apart, at least {Describe(Minimum)} apart";

	/// <summary>A duration in the words the settings screen uses.</summary>
	/// <param name="span">How long.</param>
	/// <returns>Seconds under two minutes, minutes above.</returns>
	public static string Describe(TimeSpan span) =>
		span < TimeSpan.FromMinutes(2)
			? $"{span.TotalSeconds:0} s"
			: $"{span.TotalMinutes:0} min";

	/// <summary>
	/// Reads back what <see cref="Encode"/> wrote, or the corresponding rate for one of the three
	/// profiles this setting replaced.
	/// <para>
	/// <strong>The old profile is carried across rather than dropped.</strong> Eco, Balanced and
	/// Precise were a distance and a maximum, and all six of those numbers are on the lists above,
	/// so the translation is exact — a rider who chose Precise for a track day keeps 5 m and 10 s
	/// instead of silently waking up on the new default.
	/// </para>
	/// </summary>
	/// <param name="stored">What the device store holds, or <c>null</c> on a device that has never chosen.</param>
	/// <returns>The rate, or <see cref="Default"/> for anything unreadable.</returns>
	public static LocationUpdateRate Decode(string? stored)
	{
		if (string.IsNullOrWhiteSpace(stored))
			return Default;

		string[] parts = stored.Split('/');

		if (parts.Length == 3
			&& double.TryParse(parts[0], out double distance)
			&& double.TryParse(parts[1], out double maximum)
			&& double.TryParse(parts[2], out double minimum))
		{
			return new LocationUpdateRate(
				distance,
				TimeSpan.FromSeconds(maximum),
				TimeSpan.FromSeconds(minimum));
		}

		return stored.Trim().ToLowerInvariant() switch
		{
			"eco" => new LocationUpdateRate(50, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10)),
			"balanced" => new LocationUpdateRate(10, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5)),
			"precise" => new LocationUpdateRate(5, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2)),
			_ => Default,
		};
	}

	/// <summary>The rate as the device store holds it.</summary>
	/// <returns>Three numbers, so a stored value is readable by a person looking at it.</returns>
	public string Encode() =>
		$"{DistanceM:0}/{Maximum.TotalSeconds:0}/{Minimum.TotalSeconds:0}";

	/// <summary>The offered value closest to what was asked for.</summary>
	/// <param name="offered">The list, which is never empty.</param>
	/// <param name="wanted">What was asked for.</param>
	private static double Nearest(double[] offered, double wanted) =>
		offered.MinBy(candidate => Math.Abs(candidate - wanted));

	/// <inheritdoc cref="Nearest(double[], double)" />
	private static TimeSpan Nearest(TimeSpan[] offered, TimeSpan wanted) =>
		offered.MinBy(candidate => Math.Abs((candidate - wanted).Ticks));
}
