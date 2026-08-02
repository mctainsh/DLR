namespace DLR.Core.Contracts.Rides;

/// <summary>
/// How latitude and longitude travel and are stored (§5.3, §5.5).
/// </summary>
public static class PositionScale
{
	/// <summary>
	/// Degrees are multiplied by this and rounded to an integer — about one metre of resolution.
	/// <para>
	/// The same representation on the wire and at rest, so a position is never converted between
	/// the two and never accumulates float drift by being. It also roughly halves the row.
	/// </para>
	/// </summary>
	public const double Factor = 100_000d;

	/// <summary>Scales a degree value for transport and storage.</summary>
	/// <param name="degrees">The value in degrees.</param>
	/// <returns>The scaled integer.</returns>
	public static int FromDegrees(double degrees) => (int)Math.Round(degrees * Factor);

	/// <summary>Converts a scaled value back to degrees.</summary>
	/// <param name="scaled">The scaled integer.</param>
	/// <returns>The value in degrees.</returns>
	public static double ToDegrees(int scaled) => scaled / Factor;
}

/// <summary>
/// One rider's fix, published once per tick (§5.7).
/// <para>
/// <strong>There is deliberately no ride id.</strong> Publishing per ride would multiply the
/// rider's uplink and battery by the number of rides they are in, for data the server can
/// trivially copy — and, more importantly, consent is per ride, so the <em>server</em> has to be
/// the thing that applies it. A client choosing which rides to publish to is a client that can
/// get it wrong in the direction that leaks.
/// </para>
/// </summary>
/// <param name="Lat">Latitude, scaled by <see cref="PositionScale.Factor"/>.</param>
/// <param name="Lon">Longitude, scaled by <see cref="PositionScale.Factor"/>.</param>
/// <param name="RecordedUtc">When the device took the fix, not when the server saw it.</param>
/// <param name="SpeedMps">Metres per second, when known.</param>
/// <param name="HeadingDeg">Degrees from north, when known.</param>
/// <param name="AccuracyM">Reported accuracy in metres, when known.</param>
public sealed record PositionUpdate(
	int Lat,
	int Lon,
	DateTimeOffset RecordedUtc,
	short? SpeedMps = null,
	short? HeadingDeg = null,
	short? AccuracyM = null);

/// <summary>What a publish landed in (§5.7).</summary>
/// <param name="RideIds">
/// Every ride the fix was written to — the rides where this rider's own consent flag is set. An
/// empty list is the correct answer for a rider who is sharing with nobody, not an error.
/// </param>
public sealed record PublishResult(IReadOnlyList<Guid> RideIds);

/// <summary>One rider's latest position, as the ride sees it (§5.3).</summary>
/// <param name="UserId">Which rider.</param>
/// <param name="UserName">Their handle, which is also the map label (§7.2).</param>
/// <param name="Lat">Latitude, scaled.</param>
/// <param name="Lon">Longitude, scaled.</param>
/// <param name="SpeedMps">Metres per second, when known.</param>
/// <param name="HeadingDeg">Degrees from north, when known.</param>
/// <param name="RecordedUtc">When the fix was taken.</param>
public sealed record RiderPositionDto(
	Guid UserId,
	string UserName,
	int Lat,
	int Lon,
	short? SpeedMps,
	short? HeadingDeg,
	DateTimeOffset RecordedUtc);

/// <summary>
/// One rider's place in a broadcast batch (§5.3).
/// <para>
/// Deliberately narrower than <see cref="RiderPositionDto"/>: no username, because the client
/// already has the member list and a username is immutable (§7.2), so repeating it in a message
/// sent every five seconds to every member is bytes spent on something that cannot change.
/// </para>
/// </summary>
/// <param name="UserId">Which rider.</param>
/// <param name="Lat">Latitude, scaled by <see cref="PositionScale.Factor"/>.</param>
/// <param name="Lon">Longitude, scaled.</param>
/// <param name="SpeedMps">Metres per second, when known.</param>
/// <param name="HeadingDeg">Degrees from north, when known.</param>
/// <param name="RecordedUtc">When the fix was taken.</param>
public sealed record PositionFix(
	Guid UserId,
	int Lat,
	int Lon,
	short? SpeedMps,
	short? HeadingDeg,
	DateTimeOffset RecordedUtc);

/// <summary>
/// Every member's latest position, sent once per ride per tick (§5.3).
/// <para>
/// <strong>Batch, do not relay.</strong> Relaying each fix as it arrives is <c>n × n</c> messages
/// per tick; one batch to the ride's group is <c>1 × n</c>. At fifty riders that is the difference
/// between 2,500 messages every five seconds and fifty.
/// </para>
/// </summary>
/// <param name="RideId">Which ride.</param>
/// <param name="Positions">Everyone currently sharing, with a fix.</param>
public sealed record PositionBatch(Guid RideId, IReadOnlyList<PositionFix> Positions);

/// <summary>
/// The rider's own answer to the §5.6 prompt.
/// </summary>
/// <param name="Share">
/// True to broadcast to this ride, false to stop. Turning it off <em>deletes</em> the stored
/// position rather than merely ceasing to update it.
/// </param>
public sealed record SetSharingRequest(bool Share);

/// <summary>Where a rider's sharing has got to, for the member list (§5.6).</summary>
/// <param name="Sharing">Whether they are broadcasting to this ride.</param>
/// <param name="HasPosition">
/// Whether a position is actually stored. <em>Not sharing</em> and <em>no signal</em> mean
/// completely different things to somebody waiting at a junction, so they must stay
/// distinguishable rather than collapsing into one grey pin (§5.6).
/// </param>
public sealed record SharingState(bool Sharing, bool HasPosition);

/// <summary>How a ride is being ended (§5.6).</summary>
public enum RideEndingDto
{
	/// <summary>
	/// The default. The live channel closes and every position row is deleted, immediately.
	/// </summary>
	Immediate = 0,

	/// <summary>
	/// A bounded wind-down: members who were sharing keep sharing until they stop or the window
	/// expires. Implemented in SRV-25.
	/// </summary>
	WindDown = 1,
}

/// <summary><c>POST /api/v1/group-rides/{id}/ending</c> (§5.6).</summary>
/// <param name="Ending">Which of the two endings the organiser chose.</param>
public sealed record EndRideRequest(RideEndingDto Ending = RideEndingDto.Immediate);
