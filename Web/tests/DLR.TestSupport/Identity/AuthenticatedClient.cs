using System.Net.Http.Headers;
using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;

namespace DLR.TestSupport.Identity;

/// <summary>Attaching a bearer token to a client, for the endpoints that need one.</summary>
public static class AuthenticatedClient
{
	/// <summary>Sets the <c>Authorization</c> header from a session.</summary>
	/// <param name="client">The client to authenticate.</param>
	/// <param name="session">The session whose access token to present.</param>
	public static HttpClient Authenticated(this HttpClient client, TokenResponse session)
	{
		client.DefaultRequestHeaders.Authorization =
			new AuthenticationHeaderValue("Bearer", session.AccessToken);

		return client;
	}

	/// <summary>
	/// A fresh token for an account that already exists — the §7.4 password grant.
	/// <para>
	/// Needed more often than it looks. An access token lives fifteen minutes, so any test that
	/// advances the clock past that and then calls an authed endpoint gets a <c>401</c> for a
	/// reason it is not investigating. Signing in again is also what the rider would do.
	/// </para>
	/// </summary>
	/// <param name="anonymous">A client with no token, used to make the request.</param>
	/// <param name="userName">Who to sign in as.</param>
	/// <param name="password">Defaults to <see cref="TestRegistration.ValidPassword"/>.</param>
	public static async Task<TokenResponse> SignInAsync(
		this HttpClient anonymous,
		string userName,
		string? password = null)
	{
		using HttpResponseMessage response = await anonymous.PostAsJsonAsync(
			"/api/v1/auth/token",
			new TokenRequest(
				GrantTypes.Password,
				userName,
				password ?? TestRegistration.ValidPassword));

		if (!response.IsSuccessStatusCode)
		{
			throw new InvalidOperationException(
				$"Signing in as '{userName}' returned {(int)response.StatusCode}: " +
				await response.Content.ReadAsStringAsync());
		}

		return await response.Content.ReadFromJsonAsync<TokenResponse>()
			?? throw new InvalidOperationException("The token endpoint returned an empty body.");
	}
}
