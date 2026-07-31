using System.Security.Cryptography;
using DLR.Server.Data.Identity;
using Microsoft.AspNetCore.Identity;

namespace DLR.Server.Identity;

/// <summary>
/// Burns the same work on an unknown username as on a real one (§7.8).
/// <para>
/// Password hashing is deliberately slow — that is the point of it — which makes "no such
/// user" the fastest path through the login endpoint by an enormous margin. Returning the same
/// message in a tenth of the time tells an attacker exactly what the message was written to
/// hide, and does so through a channel nobody thinks to look at.
/// </para>
/// <para>
/// The hash is of a random password nobody holds, computed once with the same hasher and the
/// same work factor real accounts use. Verifying against it always fails, which is the correct
/// answer, and takes as long as failing against a real account, which is the reason it exists.
/// </para>
/// </summary>
/// <param name="scopes">
/// A scope factory rather than the hasher itself. Identity registers
/// <see cref="IPasswordHasher{TUser}"/> as <em>scoped</em>, so a singleton cannot hold one —
/// and this has to be a singleton, because computing that target hash on every unknown-username
/// login would turn a timing defence into a denial-of-service amplifier. Resolving per call
/// costs microseconds on a path whose whole purpose is to cost milliseconds, and it keeps the
/// dummy verification on whatever hasher the application actually registered: a locally
/// constructed one would silently stop matching the moment somebody changed the work factor.
/// </param>
public sealed class DummyPasswordVerifier(IServiceScopeFactory scopes)
{
	private readonly AppUser _nobody = new() { UserName = "nobody" };

	private readonly Lazy<string> _hashOfNothing = new(() =>
	{
		using IServiceScope scope = scopes.CreateScope();

		IPasswordHasher<AppUser> hasher =
			scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>();

		return hasher.HashPassword(
			new AppUser(),
			Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
	});

	/// <summary>Verifies <paramref name="password"/> against a hash that cannot match.</summary>
	/// <param name="password">Whatever the caller submitted.</param>
	public void BurnTime(string? password)
	{
		using IServiceScope scope = scopes.CreateScope();

		IPasswordHasher<AppUser> hasher =
			scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>();

		hasher.VerifyHashedPassword(_nobody, _hashOfNothing.Value, password ?? string.Empty);
	}
}
