using DLR.Server.Data.Identity;

namespace DLR.Server.Data.Tracks;

/// <summary>
/// One rider's verdict on one shared route (§6.2).
/// <para>
/// <strong>The primary key is the rule</strong>, exactly as it is for
/// <see cref="Data.Comments.CommentReaction"/>: <c>(TrackId, UserId)</c> means one rating per
/// rider per route as a shape rather than as something every write path has to remember, so
/// rating again replaces rather than accumulates. An average over this table is then the average
/// of what people currently think, not of every time anybody changed their mind.
/// </para>
/// <para>
/// There is no row for "no opinion". Clearing a rating deletes it — a stored nought would average
/// in as the worst possible score for every rider who tapped a star and thought better of it,
/// which is the opposite of what they meant (<see cref="DLR.Core.Tracks.TrackRatings"/>).
/// </para>
/// </summary>
public sealed class TrackRating
{
	/// <summary>Which route.</summary>
	public Guid TrackId { get; set; }

	/// <summary>Who rated it.</summary>
	public Guid UserId { get; set; }

	/// <summary>
	/// One to five, whole.
	/// <para>
	/// A <c>smallint</c> with a check constraint rather than an enum: the scale is arithmetic —
	/// the only thing anybody ever does with the column is average it — and an enum stored as a
	/// string, which is this project's convention for the ones that are labels, cannot be.
	/// </para>
	/// </summary>
	public short Stars { get; set; }

	/// <summary>When they first rated it. Kept when they change their mind, so that a rating has an age.</summary>
	public DateTimeOffset CreatedUtc { get; set; }

	/// <summary>When they last changed it, or null if they never have.</summary>
	public DateTimeOffset? UpdatedUtc { get; set; }

	/// <summary>The route, for cascade deletion.</summary>
	public Track? Track { get; set; }

	/// <summary>The account, for cascade deletion (§10.1).</summary>
	public AppUser? User { get; set; }
}
