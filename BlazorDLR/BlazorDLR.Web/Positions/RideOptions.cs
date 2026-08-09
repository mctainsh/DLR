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

	/// <summary>How often the wind-down sweep runs (§5.6). SRV-25 consumes this.</summary>
	public int WindDownSweepSeconds { get; set; } = 60;

	/// <summary>The wind-down's own length — a different thing from the sweep (§5.6).</summary>
	public int MaxWindDownMinutes { get; set; } = 120;

	/// <summary>
	/// How many rides one rider may have <em>live</em> at once (§5.7).
	/// <para>
	/// Being a member of many rides is fine; being live in many at once is what costs, because
	/// each live ride is its own 5 s inbound batch. Unbounded means a rider can be broadcast into
	/// fifty groups at once.
	/// </para>
	/// </summary>
	public int MaxConcurrentLiveRidesPerUser { get; set; } = 5;

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
