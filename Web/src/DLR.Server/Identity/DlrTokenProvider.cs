using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using DLR.Server.Data.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;

namespace DLR.Server.Identity;

/// <summary>
/// A stateless, time-limited token for one purpose (§7.7).
/// <para>
/// Identity ships <c>DataProtectorTokenProvider</c>, and §7.7's first draft subclassed it. That
/// type reads <c>DateTimeOffset.UtcNow</c> directly — ASP.NET Core Identity 10 takes no
/// <c>TimeProvider</c> anywhere — so the two lifespans could be asserted as configuration but
/// never as behaviour. <c>ConfirmEmail_TokenJustUnder24Hours_IsAccepted</c> and its opposite are
/// boundary tests; against the framework provider they could only have been written as a
/// sleeping test or not at all.
/// </para>
/// <para>
/// So the provider is ours and the clock is the project's (§10.4). Nothing cryptographic is
/// reinvented: the payload is sealed by <see cref="IDataProtector"/>, which is the same vetted
/// primitive Identity uses, and the security stamp is carried inside it so that a completed
/// reset or a password change invalidates every outstanding token early.
/// </para>
/// <para>
/// Tokens remain <em>stateless</em>. Validity derives from the protected payload and the
/// security stamp, not from a row — so there is no "used" flag to inspect and no way to expire
/// one on demand.
/// </para>
/// </summary>
/// <param name="protection">Where the protector comes from.</param>
/// <param name="clock">The project's clock.</param>
/// <param name="purpose">What this token is for; a token minted for one purpose fails another.</param>
/// <param name="lifespan">How long it lasts.</param>
public abstract class DlrTokenProvider(
	IDataProtectionProvider protection,
	TimeProvider clock,
	string purpose,
	TimeSpan lifespan) : IUserTwoFactorTokenProvider<AppUser>
{
	// The purpose is in the protector's own chain *and* inside the payload, and each stops a
	// cross-purpose token on its own — both were checked by removing the other. The chain is
	// the stronger of the two, because a token minted for another purpose fails to decrypt at
	// all rather than being decrypted and then judged; the payload check is what still holds
	// if somebody later "simplifies" the protector to a single purpose string.
	private readonly IDataProtector _protector = protection.CreateProtector("DLR.Identity.Tokens", purpose);

	/// <summary>How long a token from this provider is valid for.</summary>
	public TimeSpan Lifespan { get; } = lifespan;

	/// <summary>What this provider's tokens are for.</summary>
	public string Purpose { get; } = purpose;

	/// <inheritdoc />
	public Task<string> GenerateAsync(string purpose, UserManager<AppUser> manager, AppUser user) =>
		GenerateAsync(manager, user);

	/// <inheritdoc />
	public Task<bool> ValidateAsync(
		string purpose,
		string token,
		UserManager<AppUser> manager,
		AppUser user) =>
		ValidateAsync(token, manager, user);

	/// <summary>
	/// Whether this provider can mint a token for the user. Not a two-factor provider in any
	/// real sense — the interface is simply how Identity names a token provider.
	/// </summary>
	public Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<AppUser> manager, AppUser user) =>
		Task.FromResult(false);

	/// <summary>Mints a token for this provider's purpose.</summary>
	/// <param name="manager">For the security stamp.</param>
	/// <param name="user">Who the token is about.</param>
	public async Task<string> GenerateAsync(UserManager<AppUser> manager, AppUser user)
	{
		string stamp = await manager.GetSecurityStampAsync(user) ?? string.Empty;

		using MemoryStream buffer = new();
		using (BinaryWriter writer = new(buffer, Encoding.UTF8, leaveOpen: true))
		{
			writer.Write(clock.GetUtcNow().UtcTicks);
			writer.Write(user.Id.ToString());
			writer.Write(Purpose);
			writer.Write(stamp);
		}

		return Base64Url.EncodeToString(_protector.Protect(buffer.ToArray()));
	}

	/// <summary>Checks a token against this provider's purpose, lifespan and the user's stamp.</summary>
	/// <param name="token">What the caller presented.</param>
	/// <param name="manager">For the security stamp.</param>
	/// <param name="user">Who the token should be about.</param>
	public async Task<bool> ValidateAsync(string token, UserManager<AppUser> manager, AppUser user)
	{
		byte[] payload;

		try
		{
			payload = _protector.Unprotect(Base64Url.DecodeFromChars(token));
		}
		catch (Exception exception) when (exception is CryptographicException or FormatException)
		{
			// Tampered, truncated, minted for another purpose, or from before a key rotation.
			// None of those is worth distinguishing to a caller holding a bad link.
			return false;
		}

		using MemoryStream buffer = new(payload);
		using BinaryReader reader = new(buffer, Encoding.UTF8, leaveOpen: true);

		DateTimeOffset issued;
		string userId;
		string purpose;
		string stamp;

		try
		{
			issued = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
			userId = reader.ReadString();
			purpose = reader.ReadString();
			stamp = reader.ReadString();
		}
		catch (Exception exception) when (exception is EndOfStreamException or IOException)
		{
			return false;
		}

		if (!string.Equals(purpose, Purpose, StringComparison.Ordinal)
			|| !string.Equals(userId, user.Id.ToString(), StringComparison.Ordinal))
		{
			return false;
		}

		// The lifespan is this provider's own, which is the whole point of there being two of
		// them: a global setting would make one number govern both a 24-hour confirmation link
		// and a 1-hour reset, and nothing would warn you (§7.7).
		if (clock.GetUtcNow() >= issued + Lifespan)
		{
			return false;
		}

		string current = await manager.GetSecurityStampAsync(user) ?? string.Empty;

		return string.Equals(stamp, current, StringComparison.Ordinal);
	}
}

/// <summary>
/// Email confirmation links, valid 24 hours (§7.7).
/// </summary>
/// <param name="protection">Where the protector comes from.</param>
/// <param name="clock">The project's clock.</param>
public sealed class EmailConfirmationTokenProvider(
	IDataProtectionProvider protection,
	TimeProvider clock)
	: DlrTokenProvider(protection, clock, ProviderName, TimeSpan.FromHours(24))
{
	/// <summary>The name this provider is registered and selected under.</summary>
	public const string ProviderName = "DlrEmailConfirmation";
}

/// <summary>
/// Password reset links, valid 1 hour (§7.7).
/// <para>
/// Separate from confirmation, and that separation is the point rather than an implementation
/// detail. A reset link is a live credential for whoever holds the mailbox; a confirmation link
/// proves an address exists. They do not deserve the same hour.
/// </para>
/// </summary>
/// <param name="protection">Where the protector comes from.</param>
/// <param name="clock">The project's clock.</param>
public sealed class PasswordResetTokenProvider(
	IDataProtectionProvider protection,
	TimeProvider clock)
	: DlrTokenProvider(protection, clock, ProviderName, TimeSpan.FromHours(1))
{
	/// <summary>The name this provider is registered and selected under.</summary>
	public const string ProviderName = "DlrPasswordReset";
}
