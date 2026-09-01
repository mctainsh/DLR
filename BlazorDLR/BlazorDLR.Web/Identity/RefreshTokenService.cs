using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using DLR.Server.Data;
using DLR.Server.Data.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DLR.Server.Identity;

/// <summary>
/// Issues, rotates and revokes refresh tokens (§7.4).
/// </summary>
/// <param name="database">The one context.</param>
/// <param name="clock">The project's clock (§10.4).</param>
/// <param name="grace">Where a successor waits out the idempotency window.</param>
/// <param name="options">Session length.</param>
/// <param name="logger">Where a reuse is recorded.</param>
public sealed class RefreshTokenService(
	DlrDbContext database,
	TimeProvider clock,
	RefreshTokenGraceCache grace,
	IOptions<JwtOptions> options,
	ILogger<RefreshTokenService> logger)
{
	/// <summary>Bytes of entropy in a refresh token. 256 bits, unguessable, opaque.</summary>
	public const int TokenBytes = 32;

	/// <summary>Starts a new session on a device, at the head of a new family.</summary>
	/// <param name="userId">Whose session.</param>
	/// <param name="deviceId">Which installation.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public async Task<string> StartFamilyAsync(
		Guid userId,
		Guid deviceId,
		CancellationToken cancellationToken = default)
	{
		DeviceKind kind = await KindOfAsync(deviceId, cancellationToken);

		(_, string raw) = Issue(userId, deviceId, Guid.NewGuid(), kind);

		await database.SaveChangesAsync(cancellationToken);

		return raw;
	}

	/// <summary>
	/// How long a session on this device lasts (§7.5, §18.5).
	/// <para>
	/// Read from the <em>device</em> rather than carried on the token, so a rotation cannot change
	/// it. A browser whose successor came back with a mobile lifetime would have converted a
	/// thirty-day session into a permanent one by refreshing once - and it would have done it on
	/// the very call the client makes at every start-up.
	/// </para>
	/// </summary>
	private async Task<DeviceKind> KindOfAsync(Guid deviceId, CancellationToken cancellationToken) =>
		await database
			.Set<Device>()
			.Where(device => device.Id == deviceId)
			.Select(device => device.Kind)
			.SingleOrDefaultAsync(cancellationToken);

	/// <summary>Redeems a token, following §7.4's rotation and reuse rules.</summary>
	/// <param name="presented">The token the client sent.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public async Task<RefreshOutcome> RedeemAsync(
		string presented,
		CancellationToken cancellationToken = default)
	{
		byte[] hash = Hash(presented);

		RefreshToken? token = await database
			.Set<RefreshToken>()
			.SingleOrDefaultAsync(row => row.TokenHash == hash, cancellationToken);

		if (token is null)
		{
			// Unknown - which is usually a guess, and occasionally the last token of an account
			// the §7.11 sweep deleted. The tombstone is the only thing that can tell the two
			// apart, and telling them apart is what lets the app say "this account was removed
			// after 180 days without use" instead of something that reads as a bug.
			bool wasDeleted = await database
				.Set<DeletedAccountToken>()
				.AnyAsync(row => row.TokenHash == hash, cancellationToken);

			return wasDeleted ? RefreshOutcome.AccountDeleted() : RefreshOutcome.Rejected();
		}

		if (token.RevokedUtc is not null)
		{
			return RefreshOutcome.Rejected();
		}

		DateTimeOffset now = clock.GetUtcNow();

		if (now >= token.ExpiresUtc)
		{
			return RefreshOutcome.Rejected();
		}

		return token.UsedUtc is null
			? await RotateAsync(token, cancellationToken)
			: await ReplayAsync(token, now, cancellationToken);
	}

	/// <summary>Revokes every token in a family, whatever state each one is in.</summary>
	/// <param name="familyId">The chain to end.</param>
	/// <param name="reason">One of <see cref="RevocationReasons"/>.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public async Task RevokeFamilyAsync(
		Guid familyId,
		string reason,
		CancellationToken cancellationToken = default)
	{
		DateTimeOffset now = clock.GetUtcNow();

		await database
			.Set<RefreshToken>()
			.Where(row => row.FamilyId == familyId && row.RevokedUtc == null)
			.ExecuteUpdateAsync(
				row => row
					.SetProperty(entity => entity.RevokedUtc, now)
					.SetProperty(entity => entity.RevokedReason, reason),
				cancellationToken);
	}

	/// <summary>
	/// Ends the family a raw token belongs to - a browser signing out (§7.5).
	/// <para>
	/// The whole family, not just the token presented. A browser holds one chain, and revoking only
	/// the current link would leave its predecessor redeemable by anything that kept a copy - which
	/// on the shared computer that made web sessions expire at all is the entire scenario.
	/// </para>
	/// </summary>
	/// <param name="presented">The raw token from the cookie.</param>
	/// <param name="reason">One of <see cref="RevocationReasons"/>.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public async Task RevokeByTokenAsync(
		string presented,
		string reason,
		CancellationToken cancellationToken = default)
	{
		byte[] hash = Hash(presented);

		Guid familyId = await database
			.Set<RefreshToken>()
			.Where(row => row.TokenHash == hash)
			.Select(row => row.FamilyId)
			.SingleOrDefaultAsync(cancellationToken);

		// An unknown token signs out nothing and says nothing. Sign-out answers 204 either way -
		// a distinguishable refusal would make it an oracle for whether a token is live.
		if (familyId != Guid.Empty)
		{
			await RevokeFamilyAsync(familyId, reason, cancellationToken);
		}
	}

	/// <summary>Ends every session on an account, optionally sparing one device (§7.7).</summary>
	/// <param name="userId">Whose sessions.</param>
	/// <param name="reason">One of <see cref="RevocationReasons"/>.</param>
	/// <param name="exceptDeviceId">
	/// The device to leave signed in. Used by <c>change-password</c>, which has no reason to
	/// sign somebody out of the phone in their hand; <c>reset-password</c> passes nothing,
	/// because a reset is what people do when they think somebody else is in the account.
	/// </param>
	/// <param name="cancellationToken">Cancellation.</param>
	public async Task RevokeAllForUserAsync(
		Guid userId,
		string reason,
		Guid? exceptDeviceId = null,
		CancellationToken cancellationToken = default)
	{
		DateTimeOffset now = clock.GetUtcNow();

		await database
			.Set<RefreshToken>()
			.Where(row => row.UserId == userId
				&& row.RevokedUtc == null
				&& (exceptDeviceId == null || row.DeviceId != exceptDeviceId))
			.ExecuteUpdateAsync(
				row => row
					.SetProperty(entity => entity.RevokedUtc, now)
					.SetProperty(entity => entity.RevokedReason, reason),
				cancellationToken);
	}

	/// <summary>The normal path: mark this one used and hand back its successor.</summary>
	private async Task<RefreshOutcome> RotateAsync(
		RefreshToken token,
		CancellationToken cancellationToken)
	{
		DeviceKind kind = await KindOfAsync(token.DeviceId, cancellationToken);

		// Sliding, for a web session: each rotation issues a successor with a fresh window, so a
		// browser in daily use never expires and one abandoned for a month does (§18.5).
		(RefreshToken successor, string raw) = Issue(token.UserId, token.DeviceId, token.FamilyId, kind);

		token.UsedUtc = clock.GetUtcNow();
		token.SuccessorId = successor.Id;

		await database.SaveChangesAsync(cancellationToken);

		// Only after the write. Remembering a successor that failed to persist would answer
		// the next replay with a token no row backs.
		grace.Remember(successor.Id, raw);

		return RefreshOutcome.Rotated(token.UserId, token.DeviceId, raw);
	}

	/// <summary>
	/// A token presented twice. Either the client asked concurrently, or the token exists in
	/// two places - and the whole difference is the ten seconds in between.
	/// </summary>
	private async Task<RefreshOutcome> ReplayAsync(
		RefreshToken token,
		DateTimeOffset now,
		CancellationToken cancellationToken)
	{
		bool insideWindow =
			token.UsedUtc is { } used && now - used < RefreshTokenGraceCache.Window;

		if (insideWindow && token.SuccessorId is { } successorId)
		{
			RefreshToken? successor = await database
				.Set<RefreshToken>()
				.SingleOrDefaultAsync(row => row.Id == successorId, cancellationToken);

			// An already-used successor means the chain moved on, so this is not the
			// duplicate of a single request - it is a third party holding an old token.
			if (successor is { UsedUtc: null, RevokedUtc: null }
				&& grace.TryGet(successorId, out string raw))
			{
				return RefreshOutcome.Rotated(token.UserId, token.DeviceId, raw);
			}

			if (successor is { UsedUtc: null, RevokedUtc: null })
			{
				// Inside the window but nothing cached - the process restarted. That is not
				// evidence of theft, so the family survives and the client re-authenticates.
				logger.LogInformation(
					"Refresh replay inside the grace window with no cached successor; " +
					"answering 401 without revoking family {FamilyId}.",
					token.FamilyId);

				return RefreshOutcome.Rejected();
			}
		}

		// Outside the window, a reused token really does mean the token exists in two places.
		// Revoking the whole family is the correct aggressive response: whichever copy is the
		// thief's, neither works now, and the rider signs in again once.
		logger.LogWarning(
			"Refresh token reuse detected outside the grace window. Revoking family {FamilyId}.",
			token.FamilyId);

		await RevokeFamilyAsync(token.FamilyId, RevocationReasons.ReuseDetected, cancellationToken);

		return RefreshOutcome.FamilyRevoked(token.UserId);
	}

	/// <summary>
	/// Adds an unsaved token to the chain. Returns the entity so the caller can point a
	/// predecessor's <c>successor_id</c> at it, and the raw value because this is the only
	/// moment it exists - after the save there is a hash and nothing else.
	/// </summary>
	private (RefreshToken Entity, string Raw) Issue(
		Guid userId,
		Guid deviceId,
		Guid familyId,
		DeviceKind kind)
	{
		string raw = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
		DateTimeOffset now = clock.GetUtcNow();

		RefreshToken token = new()
		{
			Id = Guid.NewGuid(),
			UserId = userId,
			DeviceId = deviceId,
			FamilyId = familyId,
			TokenHash = Hash(raw),
			IssuedUtc = now,

			// The one place §7.4's "never expires" and §7.5's thirty days are told apart. Both
			// are stored as an expiry rather than inferred at read time, so a blanket expiry
			// remains possible without a migration and the sweep in §7.11 needs no special case.
			ExpiresUtc = kind == DeviceKind.Web
				? now.AddDays(options.Value.WebSessionDays)
				: now.AddYears(options.Value.RefreshTokenYears),
		};

		database.Add(token);

		return (token, raw);
	}

	private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}

/// <summary>What redeeming a refresh token concluded.</summary>
/// <param name="Status">Which of the three §7.4 branches was taken.</param>
/// <param name="UserId">Whose session, when there was one.</param>
/// <param name="DeviceId">Which installation, when the token rotated.</param>
/// <param name="RefreshToken">The successor, when the token rotated.</param>
public readonly record struct RefreshOutcome(
	RefreshStatus Status,
	Guid UserId,
	Guid DeviceId,
	string? RefreshToken)
{
	/// <summary>Rotated, or replayed inside the grace window - the same answer either way.</summary>
	public static RefreshOutcome Rotated(Guid userId, Guid deviceId, string refreshToken) =>
		new(RefreshStatus.Rotated, userId, deviceId, refreshToken);

	/// <summary>Unknown, revoked, expired, or unanswerable.</summary>
	public static RefreshOutcome Rejected() => new(RefreshStatus.Rejected, default, default, null);

	/// <summary>The account this token belonged to was deleted by the §7.11 sweep.</summary>
	public static RefreshOutcome AccountDeleted() =>
		new(RefreshStatus.AccountDeleted, default, default, null);

	/// <summary>Reuse outside the window; every session on this chain is now dead.</summary>
	public static RefreshOutcome FamilyRevoked(Guid userId) =>
		new(RefreshStatus.FamilyRevoked, userId, default, null);
}

/// <summary>The three outcomes of §7.4's rotation flow.</summary>
public enum RefreshStatus
{
	/// <summary>The token is no good and no session state changed.</summary>
	Rejected = 0,

	/// <summary>A successor was issued, or re-issued idempotently.</summary>
	Rotated = 1,

	/// <summary>Reuse was detected and the family was revoked.</summary>
	FamilyRevoked = 2,

	/// <summary>
	/// The account was removed by the inactivity sweep (§7.11). Distinct from
	/// <see cref="Rejected"/> because the client's answer is different: sign in again is wrong
	/// advice for an account that no longer exists.
	/// </summary>
	AccountDeleted = 3,
}
