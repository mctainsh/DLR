using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DLR.Server.Identity;

/// <summary>
/// The Pwned Passwords range API, queried under k-anonymity (§7.2).
/// <para>
/// Only the first five hexadecimal characters of the password's SHA-1 ever leave the server.
/// The service answers with every suffix sharing that prefix — some hundreds of hashes — and
/// the comparison happens here. So the operator of that service learns that somebody asked
/// about one of roughly 800 passwords, and cannot learn which, or whose.
/// </para>
/// </summary>
/// <param name="client">Configured by <c>AddHttpClient</c>; the base address is the API.</param>
/// <param name="logger">Where an outage is recorded.</param>
public sealed class PwnedPasswordsClient(
	HttpClient client,
	ILogger<PwnedPasswordsClient> logger) : IBreachedPasswordCheck
{
	/// <summary>The named client, so the base address and timeout are configured in one place.</summary>
	public const string HttpClientName = "pwned-passwords";

	/// <summary>Where the range API lives.</summary>
	public const string BaseAddress = "https://api.pwnedpasswords.com/";

	private const int PrefixLength = 5;

	/// <inheritdoc />
	public async Task<BreachCheckResult> CheckAsync(
		string password,
		CancellationToken cancellationToken = default)
	{
		// SHA-1 is not a security choice here and is not protecting anything: it is the
		// corpus's index, and the plaintext is already in hand. The password's own hash at
		// rest is Identity's, which is not this.
		string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));

		string prefix = hash[..PrefixLength];
		string suffix = hash[PrefixLength..];

		string body;

		try
		{
			body = await client.GetStringAsync($"range/{prefix}", cancellationToken);
		}
		catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
		{
			// A third-party outage must not stop signups (§7.2). Logged at warning rather
			// than swallowed, because the interesting failure is not one lookup — it is
			// this line appearing on every registration for a week.
			logger.LogWarning(
				exception,
				"Pwned Passwords was unreachable; registration proceeds without a breach check.");

			return BreachCheckResult.Unavailable;
		}

		return ContainsSuffix(body, suffix)
			? BreachCheckResult.Breached
			: BreachCheckResult.NotBreached;
	}

	/// <summary>
	/// Scans the range response for our suffix. Lines are <c>SUFFIX:COUNT</c>, uppercase.
	/// </summary>
	private static bool ContainsSuffix(string body, string suffix)
	{
		foreach (ReadOnlySpan<char> line in body.AsSpan().EnumerateLines())
		{
			int separator = line.IndexOf(':');

			ReadOnlySpan<char> candidate = separator < 0 ? line : line[..separator];

			if (candidate.Trim().Equals(suffix, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Registers the client. Padding is requested so that response *size* does not narrow
	/// the prefix down for an observer who can see the traffic but not the URL.
	/// </summary>
	/// <param name="services">The container.</param>
	public static IServiceCollection AddPwnedPasswords(IServiceCollection services)
	{
		services
			.AddHttpClient<IBreachedPasswordCheck, PwnedPasswordsClient>(HttpClientName, http =>
			{
				http.BaseAddress = new Uri(BaseAddress, UriKind.Absolute);
				http.DefaultRequestHeaders.Add("Add-Padding", "true");
				http.DefaultRequestHeaders.UserAgent.ParseAdd(
					string.Create(CultureInfo.InvariantCulture, $"DumbLuckRides/1.0"));

				// Short on purpose. This call sits in the registration path, and §7.2's
				// answer to the service being slow is the same as to it being down.
				http.Timeout = TimeSpan.FromSeconds(3);
			});

		return services;
	}
}
