namespace DLR.Core.Contracts.Tracks;

/// <summary>
/// <c>PUT /api/v1/tracks/{id}/rating</c> - one rider's verdict on a shared route (§6.2).
/// <para>
/// A whole request rather than a bare integer in the body, on <see cref="RateTrackRequest"/>'s
/// neighbours' reasoning: a naked <c>5</c> on the wire has no room to grow a second field, and the
/// first thing anybody asks a rating for afterwards is a note to go with it.
/// </para>
/// <para>
/// Clearing is <c>DELETE</c>, never a zero here. A zero would average in as "terrible" for every
/// rider who tapped a star and changed their mind (<see cref="DLR.Core.Tracks.TrackRatings"/>).
/// </para>
/// </summary>
/// <param name="Stars">One to five, whole. Anything else is a <c>400</c>.</param>
public sealed record RateTrackRequest(int Stars);

/// <summary>
/// How a shared route stands (§6.2).
/// <para>
/// The tally and the caller's own answer together, exactly as
/// <see cref="DLR.Core.Contracts.Comments.ReactionCounts"/> carries both - a widget that has to
/// draw "4.5 from 12" and highlight the rider's own three stars needs one round trip for it, not
/// two. It is never a list of who rated what: that is a different question, it would be the bulk
/// of the payload, and on a browse list of twenty routes it would be twenty lists nothing renders.
/// </para>
/// </summary>
/// <param name="Average">
/// The mean of every star given, or null when nobody has rated it. Null rather than zero - zero is
/// not a rating this scale can express (§8's rule about zero meaning something).
/// </param>
/// <param name="Count">How many riders have rated it.</param>
/// <param name="Mine">The caller's own rating, or null if they have not rated it.</param>
public sealed record TrackRatingSummary(double? Average, int Count, int? Mine)
{
	/// <summary>Nothing rated yet - the shape a route with no ratings comes back as.</summary>
	public static readonly TrackRatingSummary None = new(null, 0, null);
}
