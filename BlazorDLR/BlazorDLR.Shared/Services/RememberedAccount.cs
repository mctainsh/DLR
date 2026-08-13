using System.Globalization;
using DLR.Core.Contracts.Identity;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// Who signed in on this device last, kept so the app can open on their rides without asking the
/// server first (§7.9).
/// <para>
/// <strong>Why the app needs this at all.</strong> §7.4 puts the refresh token in
/// <c>SecureStorage</c> and the access token in memory only, which is right — but it means the
/// only thing a relaunch has is an opaque 256-bit string, and turning that into a
/// <c>ClaimsPrincipal</c> takes a round trip. A rider who relaunches in a dead zone has a refresh
/// token, a cached ride and no way to reach the token endpoint, and without this they are bounced
/// to the Welcome screen and asked to sign in to an account they never signed out of.
/// </para>
/// <para>
/// <strong>It is not a credential.</strong> Nothing here authorises anything: it is a name, an id
/// and two flags, and every API call still needs an access token this cannot mint. Adopting it
/// unlocks the app's own screens against data that is already on the device — which is the same
/// trust boundary the device's file system already sits on — and the first call that reaches a
/// server is authenticated the ordinary way or fails. That is why <c>AuthState.RestoreAsync</c>
/// only adopts one when a refresh token is present beside it: the account is the label, and the
/// token in the Keychain is the thing that actually says somebody signed in here.
/// </para>
/// <para>
/// Device-local, hand-encoded and versioned like <see cref="LiveMapView"/> and
/// <see cref="RouteStyle"/>, and it goes in <see cref="IDeviceSettings"/> rather than
/// <see cref="IOfflineStore"/> because unlike a ride it is four small fields.
/// </para>
/// </summary>
/// <param name="UserId">The account's id — the token's <c>sub</c>, and what "mine" is decided against.</param>
/// <param name="UserName">The permanent handle (§7.2), which is also the map label.</param>
/// <param name="HasEmail">Whether a recovery address exists, for §7.8's ladder.</param>
/// <param name="EmailConfirmed">Whether it has been confirmed.</param>
public sealed record RememberedAccount(
	Guid UserId,
	string UserName,
	bool HasEmail,
	bool EmailConfirmed)
{
	/// <summary>
	/// The <see cref="IDeviceSettings"/> key. Namespaced like <c>dlr.current-ride</c>, with the
	/// format version inside the value rather than in the key.
	/// </summary>
	public const string StorageKey = "dlr.account";

	/// <summary>The account as the session that produced it described them.</summary>
	/// <param name="user">The user from a <see cref="TokenResponse"/>.</param>
	public static RememberedAccount From(AuthenticatedUser user) =>
		new(user.Id, user.UserName, user.HasEmail, user.EmailConfirmed);

	/// <summary>
	/// The record as one string, leading <c>1</c> being the format version — the same arrangement
	/// as <see cref="LiveMapView.Encode"/>.
	/// <para>
	/// The handle is percent-encoded. §7.2's rules do not allow a <c>|</c> today, but the
	/// separator's correctness should not depend on a validation rule in a different assembly
	/// staying the way it is.
	/// </para>
	/// </summary>
	public string Encode() => string.Join('|', [
		"1",
		UserId.ToString("N", CultureInfo.InvariantCulture),
		Uri.EscapeDataString(UserName),
		HasEmail ? "1" : "0",
		EmailConfirmed ? "1" : "0",
	]);

	/// <summary>
	/// Reads back what <see cref="Encode"/> wrote.
	/// <para>
	/// All-or-nothing: half an identity is worse than none, because the half that survives is the
	/// half the app would render as somebody's name. A <c>null</c> means the rider signs in, which
	/// is exactly where a device that had stored nothing would put them.
	/// </para>
	/// </summary>
	/// <param name="encoded">A string from <see cref="Encode"/>, or <c>null</c> on a device that has never stored one.</param>
	public static RememberedAccount? Decode(string? encoded)
	{
		if (string.IsNullOrWhiteSpace(encoded))
		{
			return null;
		}

		string[] parts = encoded.Split('|');

		if (parts.Length < 5 || parts[0] != "1")
		{
			return null;
		}

		if (!Guid.TryParse(parts[1], out Guid userId) || userId == Guid.Empty)
		{
			return null;
		}

		string userName = Uri.UnescapeDataString(parts[2]);

		return string.IsNullOrWhiteSpace(userName)
			? null
			: new RememberedAccount(userId, userName, HasEmail: parts[3] == "1", EmailConfirmed: parts[4] == "1");
	}
}
