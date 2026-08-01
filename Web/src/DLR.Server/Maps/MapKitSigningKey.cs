using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DLR.Server.Maps;

/// <summary>
/// The MapKit private key, read once and held for the life of the process (§4.5).
/// <para>
/// <strong>Built once because it must be, not because it is faster.</strong> The obvious shape —
/// <c>using ECDsa key = ECDsa.Create()</c> inside the request — works exactly once: IdentityModel
/// caches the signature provider it builds around a <see cref="SecurityKey"/>, so the second
/// request reaches for a provider whose <c>ECDsa</c> the first request already disposed and throws
/// <see cref="ObjectDisposedException"/>. It is a first-call-succeeds bug, which is the kind that
/// reaches production because the smoke test passes.
/// </para>
/// </summary>
/// <param name="options">Where the key and its identifier come from.</param>
public sealed class MapKitSigningKey(IOptions<MapKitOptions> options) : IDisposable
{
	private readonly Lock _gate = new();

	private ECDsa? _key;
	private ECDsaSecurityKey? _securityKey;
	private bool _attempted;

	/// <summary>
	/// The signing key, or null when this deployment has none — or has one it cannot read.
	/// <para>
	/// Both answers are the same to a caller, and deliberately: from a client's point of view a
	/// server with no key and a server with a broken key are the same situation, and §4.5 asks for
	/// a stated error in both.
	/// </para>
	/// </summary>
	/// <exception cref="CryptographicException">The configured PEM is not a key.</exception>
	public SecurityKey? Resolve()
	{
		MapKitOptions settings = options.Value;

		if (!settings.IsConfigured)
		{
			return null;
		}

		lock (_gate)
		{
			// Attempted once, however it went. A malformed key is a deployment mistake that will
			// not fix itself, and re-parsing it on every request would turn one misconfiguration
			// into a throw on the hot path of every map load.
			if (_attempted)
			{
				return _securityKey;
			}

			_attempted = true;

			ECDsa key = ECDsa.Create();

			key.ImportFromPem(settings.PrivateKeyPem);

			_key = key;

			// The `kid` goes in the *header*, and this is what puts it there. Apple picks which of
			// a team's keys to verify against by it; a token without one is rejected wholesale.
			_securityKey = new ECDsaSecurityKey(key) { KeyId = settings.KeyId };

			return _securityKey;
		}
	}

	/// <inheritdoc />
	public void Dispose()
	{
		lock (_gate)
		{
			_key?.Dispose();
			_key = null;
			_securityKey = null;
		}
	}
}
