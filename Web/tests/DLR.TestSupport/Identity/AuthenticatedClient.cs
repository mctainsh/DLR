using System.Net.Http.Headers;
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
}
