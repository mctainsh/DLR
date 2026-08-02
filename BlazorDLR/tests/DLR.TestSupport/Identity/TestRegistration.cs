using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;

namespace DLR.TestSupport.Identity;

/// <summary>
/// Registering an account, for the tests of everything that needs one to exist.
/// </summary>
public static class TestRegistration
{
	/// <summary>Where accounts are created.</summary>
	public const string RegisterUrl = "/api/v1/auth/register";

	/// <summary>
	/// A password every test can use without thinking about it.
	/// <para>
	/// Chosen to satisfy both password regimes this project passes through: ASP.NET Core
	/// Identity's default composition rules, which are in force until SRV-07, and §7.2's
	/// actual policy of ten characters and no composition rules, which replaces them. A
	/// constant here means SRV-07 changes the policy without editing a single existing test.
	/// </para>
	/// </summary>
	public const string ValidPassword = "Correct-Horse-Battery-Staple-9";

	/// <summary>Posts a registration and hands back the raw response.</summary>
	/// <param name="client">A client for the server under test.</param>
	/// <param name="userName">The username to register.</param>
	/// <param name="password">Defaults to <see cref="ValidPassword"/>.</param>
	/// <param name="email">Optional recovery address (§7.2).</param>
	public static Task<HttpResponseMessage> PostRegisterAsync(
		this HttpClient client,
		string userName,
		string? password = null,
		string? email = null) =>
		client.PostAsJsonAsync(
			RegisterUrl,
			new RegisterRequest(userName, password ?? ValidPassword, email));

	/// <summary>Registers an account, failing the calling test if the server refused.</summary>
	/// <param name="client">A client for the server under test.</param>
	/// <param name="userName">The username to register.</param>
	/// <param name="password">Defaults to <see cref="ValidPassword"/>.</param>
	/// <param name="email">Optional recovery address (§7.2).</param>
	public static async Task<TokenResponse> RegisterAsync(
		this HttpClient client,
		string userName,
		string? password = null,
		string? email = null)
	{
		using HttpResponseMessage response =
			await client.PostRegisterAsync(userName, password, email);

		if (!response.IsSuccessStatusCode)
		{
			throw new InvalidOperationException(
				$"Registering '{userName}' returned {(int)response.StatusCode}: " +
				await response.Content.ReadAsStringAsync());
		}

		return await response.Content.ReadFromJsonAsync<TokenResponse>()
			?? throw new InvalidOperationException("Registration returned an empty body.");
	}
}
