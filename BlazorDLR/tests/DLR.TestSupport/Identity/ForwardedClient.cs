namespace DLR.TestSupport.Identity;

/// <summary>
/// Presenting as a client behind the reverse proxy (§7.8).
/// <para>
/// The test host connects over loopback, so without a forwarded header every test in the suite
/// shares one address - which is exactly the production failure this guards against, and would
/// make every per-address rule untestable in the same breath.
/// </para>
/// </summary>
public static class ForwardedClient
{
	/// <summary>The header Caddy sets and <c>ForwardedHeadersMiddleware</c> reads.</summary>
	public const string Header = "X-Forwarded-For";

	/// <summary>Makes every request from this client appear to come from an address.</summary>
	/// <param name="client">The client to label.</param>
	/// <param name="address">The address to claim.</param>
	public static HttpClient From(this HttpClient client, string address)
	{
		client.DefaultRequestHeaders.Remove(Header);
		client.DefaultRequestHeaders.Add(Header, address);

		return client;
	}
}
