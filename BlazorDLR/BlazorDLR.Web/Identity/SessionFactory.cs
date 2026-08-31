using DLR.Core.Contracts.Identity;
using DLR.Server.Data;
using DLR.Server.Data.Identity;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Identity;

/// <summary>
/// Turns an authenticated account into the token pair §7.4 hands back.
/// <para>
/// One place, because registration, the password grant and the refresh grant all produce the
/// same thing, and three copies of "issue an access token, issue a refresh token, describe the
/// user" is three places for the claim set to drift.
/// </para>
/// </summary>
/// <param name="database">The one context.</param>
/// <param name="tokens">Access tokens.</param>
/// <param name="refresh">Refresh tokens.</param>
/// <param name="alerts">The new-device security email (§7.10).</param>
/// <param name="clock">The project's clock (§10.4).</param>
public sealed class SessionFactory(
	DlrDbContext database,
	AccessTokenIssuer tokens,
	RefreshTokenService refresh,
	NewDeviceNotifier alerts,
	TimeProvider clock)
{
	/// <summary>Matches <c>device.name varchar(60)</c> (§7.10).</summary>
	private const int NameLimit = 60;

	/// <summary>Starts a session: a new device row if needed, and a new token family.</summary>
	/// <param name="user">Who signed in.</param>
	/// <param name="claimedDeviceId">
	/// The device id the client says it was given last time. Honoured only when it really is
	/// this account's device — an id belonging to somebody else is not rejected, it simply
	/// does not match, and this installation gets one of its own.
	/// </param>
	/// <param name="deviceName">
	/// What the rider will recognise the installation as. Never verified, and trimmed to the
	/// column rather than refused.
	/// </param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <param name="kind">
	/// Phone or browser (§7.5). Decided by which endpoint the caller reached, never by anything
	/// the client asserts — a client-supplied value would let a browser ask for the permanent
	/// session the distinction exists to withhold.
	/// </param>
	public async Task<TokenResponse> BeginAsync(
		AppUser user,
		Guid? claimedDeviceId,
		string? deviceName = null,
		DeviceKind kind = DeviceKind.Mobile,
		CancellationToken cancellationToken = default)
	{
		(Guid deviceId, bool isNew) =
			await ResolveDeviceAsync(user.Id, claimedDeviceId, deviceName, kind, cancellationToken);

		string refreshToken = await refresh.StartFamilyAsync(user.Id, deviceId, cancellationToken);

		// A *new* device, not the first one. The alert exists to tell somebody that another
		// party got into their account; at registration there is no other party and no prior
		// state, so it would arrive as noise attached to the very act that created the
		// account — and noise is how a security alert stops being read.
		if (isNew && await HasOtherDevicesAsync(user.Id, deviceId, cancellationToken))
		{
			await alerts.NewDeviceSignedInAsync(user, deviceName, cancellationToken);
		}

		return Describe(user, deviceId, refreshToken);
	}

	/// <summary>Continues an existing session with a rotated refresh token.</summary>
	/// <param name="user">Whose session.</param>
	/// <param name="deviceId">The device the family belongs to.</param>
	/// <param name="refreshToken">The successor just issued.</param>
	public TokenResponse Continue(AppUser user, Guid deviceId, string refreshToken) =>
		Describe(user, deviceId, refreshToken);

	private TokenResponse Describe(AppUser user, Guid deviceId, string refreshToken)
	{
		IssuedAccessToken access = tokens.Issue(user, deviceId);

		return new TokenResponse(
			access.Token,
			access.ExpiresInSeconds,
			refreshToken,
			new AuthenticatedUser(
				user.Id,
				user.UserName!,
				HasEmail: user.Email is not null,
				user.EmailConfirmed),
			deviceId);
	}

	private async Task<bool> HasOtherDevicesAsync(
		Guid userId,
		Guid deviceId,
		CancellationToken cancellationToken) =>
		await database
			.Set<Device>()
			.AnyAsync(device => device.UserId == userId && device.Id != deviceId, cancellationToken);

	private async Task<(Guid DeviceId, bool IsNew)> ResolveDeviceAsync(
		Guid userId,
		Guid? claimedDeviceId,
		string? deviceName,
		DeviceKind kind,
		CancellationToken cancellationToken)
	{
		deviceName = Shorten(deviceName);

		if (claimedDeviceId is { } claimed)
		{
			Device? existing = await database
				.Set<Device>()
				.SingleOrDefaultAsync(
					device => device.Id == claimed && device.UserId == userId,
					cancellationToken);

			// The kind has to match, not merely the id. A browser presenting a phone's device id
			// would otherwise adopt that row and inherit its permanent session, which is exactly
			// what §7.5 withholds from browsers. A mismatch is not an error — the installation
			// gets a row of its own, the same as any other id that does not match.
			if (existing is not null && existing.Kind == kind)
			{
				// A rename is the client telling us what the phone is called now. Nothing
				// depends on it, so a fresh value wins and a missing one changes nothing.
				if (!string.IsNullOrWhiteSpace(deviceName))
				{
					existing.Name = deviceName;
				}

				existing.LastSeenUtc = clock.GetUtcNow();

				await database.SaveChangesAsync(cancellationToken);

				return (claimed, IsNew: false);
			}
		}

		DateTimeOffset now = clock.GetUtcNow();

		Device created = new()
		{
			Id = Guid.NewGuid(),
			UserId = userId,
			Name = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName,
			Kind = kind,
			CreatedUtc = now,
			LastSeenUtc = now,
		};

		database.Add(created);

		await database.SaveChangesAsync(cancellationToken);

		return (created.Id, IsNew: true);
	}

	/// <summary>
	/// Cuts a client-supplied name to the column. The name is decoration — nothing reads it but a
	/// rider picking a row — so a long one is trimmed rather than refused, which keeps a handset
	/// with a chatty model string from failing the sign-in the name was attached to.
	/// </summary>
	private static string? Shorten(string? deviceName)
	{
		if (string.IsNullOrWhiteSpace(deviceName)) return null;

		string trimmed = deviceName.Trim();

		return trimmed.Length > NameLimit ? trimmed[..NameLimit] : trimmed;
	}
}
