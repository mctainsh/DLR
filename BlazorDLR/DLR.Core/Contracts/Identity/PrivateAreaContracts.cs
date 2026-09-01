namespace DLR.Core.Contracts.Identity;

/// <summary>
/// A rider's home private area, as it travels and as the account holds it (§10.1, §7.14).
/// <para>
/// <strong>This is account data, not device data, and that was a deliberate reversal.</strong>
/// The area used to live only in the phone's own settings store on the argument that a centre
/// is a statement of where somebody lives, so the only copy that cannot leak is the one the
/// server never sees. What that argument left out is how the setting is actually lost: an app
/// update, a reinstall, a cleared web store or a new phone all wipe it, and a rider who thinks
/// they still have a private area and does not is <em>worse</em> off than one whose server
/// holds the circle - they are broadcasting from their doorstep believing they are not.
/// </para>
/// <para>
/// So the account is now the source of truth and the device keeps a cache of it. What that
/// costs is stated plainly rather than hidden: the server can read this, an operator with
/// database access can read it, it is in the nightly backups, and it is in the account export
/// because it is the rider's data. What it never does is reach another rider - there is no
/// endpoint that answers with somebody else's area, it is absent from
/// <see cref="SharedProfile"/> by construction, and no position, track or export handed to a
/// third party carries it.
/// </para>
/// </summary>
/// <param name="Latitude">Centre latitude in decimal degrees.</param>
/// <param name="Longitude">Centre longitude in decimal degrees.</param>
/// <param name="RadiusM">Radius in metres. Clamped to [<see cref="MinRadiusM"/>, <see cref="MaxRadiusM"/>].</param>
public sealed record PrivateAreaSettings(double Latitude, double Longitude, double RadiusM)
{
	/// <summary>
	/// What a newly-placed area gets. A kilometre is a few streets in every direction - wide
	/// enough that the centre is not the obvious middle of a small hole in a track, and narrow
	/// enough that the ride still picks the rider up before the first junction.
	/// </summary>
	public const double DefaultRadiusM = 1000;

	/// <summary>
	/// Smallest radius offered. Below this a consumer GPS's own error is a large fraction of the
	/// circle, so a fix reported just outside it still comes from inside - the setting would look
	/// like it was working while doing nothing.
	/// </summary>
	public const double MinRadiusM = 100;

	/// <summary>
	/// Largest radius offered. Beyond this it stops being a private area around a place and
	/// becomes "do not share on this ride", which is what the per-ride sharing switch is for (§5.6).
	/// </summary>
	public const double MaxRadiusM = 10_000;

	/// <summary>
	/// Brings values from a control - or read back from a store that once held something else -
	/// into range: the radius is clamped, and a centre that is not a real coordinate is refused.
	/// <para>
	/// Lives on the contract rather than in either the client or the server, because both apply
	/// it and they must not be able to disagree: the phone clamps so the control reports what was
	/// stored, and the endpoint clamps again because a disabled control is a courtesy and not a
	/// rule (§7.14).
	/// </para>
	/// </summary>
	/// <returns>The usable area, or <c>null</c> when the centre is not a point on the earth.</returns>
	public PrivateAreaSettings? Normalised()
	{
		if (!double.IsFinite(Latitude) || !double.IsFinite(Longitude)
			|| Latitude is < -90 or > 90 || Longitude is < -180 or > 180)
		{
			return null;
		}

		return this with
		{
			RadiusM = Math.Clamp(double.IsFinite(RadiusM) ? RadiusM : DefaultRadiusM, MinRadiusM, MaxRadiusM),
		};
	}
}

/// <summary>
/// <c>GET /api/v1/me/private-area</c> - the caller's own area, or the fact that they have none.
/// <para>
/// <strong>A wrapper rather than a bare nullable body, and that is the whole point of the
/// type.</strong> "The account has no private area" and "we could not ask" are different
/// answers and the client gates broadcasting on telling them apart: the first means share from
/// everywhere, the second means share from nowhere until we know. A <c>null</c> body and a
/// failed request would deserialise to the same thing.
/// </para>
/// </summary>
/// <param name="Area">The area, or <c>null</c> when the account has none set.</param>
public sealed record PrivateAreaResponse(PrivateAreaSettings? Area)
{
	/// <summary>The answer for an account that has not set one - the shipped state.</summary>
	public static readonly PrivateAreaResponse None = new(Area: null);
}
