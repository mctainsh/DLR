namespace DLR.Server.Positions;

/// <summary>
/// The ride timers and windows (§5.5, §5.6).
/// <para>
/// Two cadences that are easy to conflate and must not be: the broadcast is <em>network
/// fan-out</em>, the flush is <em>durability</em>. Changing one because the other looked wrong is
/// how a 5 s map becomes a 5 s write amplification.
/// </para>
/// </summary>
public sealed class RideOptions
{
	/// <summary>Configuration section name.</summary>
	public const string Section = "Ride";

	/// <summary>Network fan-out to SignalR groups. SRV-23 consumes this.</summary>
	public int BroadcastSeconds { get; set; } = 5;

	/// <summary>Write-behind period. The cost of a hard kill is bounded by this.</summary>
	public int FlushSeconds { get; set; } = 10;

	/// <summary>
	/// How old a stored position may be and still be rehydrated (§5.5). A stale point must not
	/// reappear on the map as if it were current.
	/// </summary>
	public int StalenessMinutes { get; set; } = 15;

	/// <summary>
	/// How long a stored position may go without a fix before the nightly sweep deletes it and
	/// clears the rider's sharing switch (§5.6, §7.11).
	/// <para>
	/// A backstop for the row nothing else reclaims — a phone that died, an app uninstalled, an
	/// adventure nobody deletes. Not a privacy promise: a rider still sharing is still sharing.
	/// </para>
	/// </summary>
	public int PositionIdleDays { get; set; } = 14;

	/// <summary>
	/// How many planned routes one ride may carry (§5.4).
	/// <para>
	/// A ride has a handful — the short one, the long one, the way home. The cap is here because
	/// every member downloads every route's line on every load of the ride, so an unbounded set is
	/// a payload an organiser can grow on everyone else's connection.
	/// </para>
	/// </summary>
	public int MaxRoutesPerRide { get; set; } = 10;
}
