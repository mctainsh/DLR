using DLR.Server.Data.Rides;
using Microsoft.AspNetCore.Mvc;

namespace DLR.Server.Rides;

/// <summary>What a member is trying to add (§5.8).</summary>
public enum RideContent
{
	/// <summary>A marker on the ride's map (§16).</summary>
	Marker,

	/// <summary>A post in the thread (§17).</summary>
	Comment,

	/// <summary>A photograph, attached to either of the above (§16.4).</summary>
	Photo,
}

/// <summary>
/// The organiser's three content switches, enforced in one place (§5.8).
/// <para>
/// <strong>One method rather than a check per endpoint</strong>, for the reason SRV-21 learned the
/// hard way: markers, comments and both of their photo attachments are four separate write paths
/// carrying the same obligation, and four copies of a rule is how one of them eventually stops
/// applying it. Adding a content type here is adding an enum member and a switch arm; forgetting to
/// enforce it is not possible without deleting a call.
/// </para>
/// <para>
/// <strong>Enforcement is server-side and the UI merely agrees.</strong> The
/// <c>RidePermissionsChanged</c> hub message exists so a client can grey out a compose surface
/// rather than lie about it — but a member whose permission was revoked mid-compose is stopped
/// here, not by their own client choosing to behave.
/// </para>
/// </summary>
public static class RideContentPermissions
{
	/// <summary>
	/// Whether this member may add this kind of content to this ride right now.
	/// </summary>
	/// <param name="ride">The ride, carrying the three switches.</param>
	/// <param name="role">The caller's role in it.</param>
	/// <param name="content">What they are adding.</param>
	public static bool Allows(GroupRide ride, GroupRideRole role, RideContent content)
	{
		// Never restricted by their own switches (§5.8). An organiser who turned member markers
		// off did not thereby give up placing markers, and a rule that made them do so would be
		// read as a bug the first time it happened.
		if (role is GroupRideRole.Owner or GroupRideRole.Leader)
		{
			return true;
		}

		return content switch
		{
			RideContent.Marker => ride.AllowMemberMarkers,
			RideContent.Comment => ride.AllowMemberComments,
			RideContent.Photo => ride.AllowMemberPhotos,

			// Named rather than defaulted permissive. A new content type that nobody wired a
			// switch to would otherwise be allowed everywhere by omission, which is the failure
			// SRV-07's outcome switch already made this project pay for once.
			_ => throw new ArgumentOutOfRangeException(
				nameof(content),
				content,
				"Every kind of ride content needs a §5.8 answer, including new ones."),
		};
	}

	/// <summary>
	/// The refusal, worded so the rider knows it is a setting rather than a fault.
	/// <para>
	/// Separate from <see cref="Refuse"/> so that a caller which has <em>decided</em> a refusal
	/// but is not yet returning it — <see cref="Comments.ThreadAccess"/>, which carries the
	/// reason a thread is closed to somebody alongside the fact that it is — can hold the words
	/// without holding an <see cref="IActionResult"/>. The two must not drift, so there is one
	/// wording and the result is built from it.
	/// </para>
	/// </summary>
	/// <param name="content">What they were adding.</param>
	public static ProblemDetails Describe(RideContent content) => new()
	{
		Status = StatusCodes.Status403Forbidden,
		Title = "The organiser has turned this off",
		Detail = content switch
		{
			RideContent.Marker => "Only the organiser and leaders are adding markers to this adventure.",
			RideContent.Comment => "Only the organiser and leaders are posting to this adventure. " +
				"You can still read the thread, react and vote.",
			RideContent.Photo => "Photos are turned off for this adventure. Text still works.",
			_ => "That is turned off for this adventure.",
		},

		// Distinguishable, so a client can tell "you may not" from "that failed" (§5.8).
		Extensions = { ["permission"] = content.ToString() },
	};

	/// <summary>
	/// The refusal as an endpoint returns it.
	/// </summary>
	/// <param name="content">What they were adding.</param>
	public static IActionResult Refuse(RideContent content) => AsResult(Describe(content));

	/// <summary>
	/// Wraps a decided refusal in the response shape every endpoint in this project returns one
	/// in — the status on the result as well as in the body, and the problem content type, both
	/// of which <see cref="ControllerBase.Problem"/> would otherwise be doing invisibly.
	/// </summary>
	/// <param name="problem">The refusal.</param>
	public static IActionResult AsResult(ProblemDetails problem) => new ObjectResult(problem)
	{
		StatusCode = problem.Status ?? StatusCodes.Status403Forbidden,
		ContentTypes = { "application/problem+json" },
	};
}
