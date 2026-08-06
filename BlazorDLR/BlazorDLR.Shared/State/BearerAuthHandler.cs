using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorDLR.Shared.State;

/// <summary>
/// A <see cref="DelegatingHandler"/> that attaches the current access token to every
/// request and retries once on a 401 with a freshly refreshed one (§7.4). Wired on the
/// MAUI host and the WASM host; the SSR host uses <c>InProcessAboutApiClient</c> and does
/// not go through this pipeline.
/// <para>
/// <strong>Lazy <see cref="AuthState"/>.</strong> The constructor takes
/// <see cref="IServiceProvider"/> rather than <see cref="AuthState"/> itself, because
/// <c>AuthState</c> depends on <c>IApiClient</c>, which depends on <see cref="HttpClient"/>,
/// which depends on this handler. Injecting <c>AuthState</c> at construction time is a
/// closed graph the DI container refuses to resolve. Resolving it inside
/// <see cref="SendAsync"/> defers the lookup to a point where the scope has already
/// cached <c>HttpClient</c>, so the graph closes without cycling.
/// </para>
/// <para>
/// <strong>One retry, ever.</strong> A second 401 after a fresh token means the token
/// endpoint returned a value the resource endpoint refuses — a bug the client cannot fix,
/// and looping would turn it into a self-inflicted denial of service. The 401 propagates
/// and the caller decides what to do.
/// </para>
/// </summary>
public sealed class BearerAuthHandler : DelegatingHandler
{
	private readonly IServiceProvider _services;

	public BearerAuthHandler(IServiceProvider services)
	{
		_services = services;
	}

	/// <inheritdoc />
	protected override async Task<HttpResponseMessage> SendAsync(
		HttpRequestMessage request,
		CancellationToken cancellationToken)
	{
		// Never attach a bearer to the token endpoint itself — it authenticates by the
		// refresh grant, and adding a stale access token would surface as a 401 on the
		// call the app depends on to un-401 itself.
		bool isTokenEndpoint = request.RequestUri is { AbsolutePath: string path }
			&& (path.Equals("/api/v1/auth/token", StringComparison.OrdinalIgnoreCase)
				|| path.Equals("/api/v1/auth/register", StringComparison.OrdinalIgnoreCase)
				|| path.Equals("/api/v1/auth/forgot-password", StringComparison.OrdinalIgnoreCase)
				|| path.Equals("/api/v1/auth/reset-password", StringComparison.OrdinalIgnoreCase)
				|| path.Equals("/api/v1/auth/web/token", StringComparison.OrdinalIgnoreCase));

		AuthState auth = _services.GetRequiredService<AuthState>();

		if (!isTokenEndpoint)
		{
			string? token = await auth.GetOrRefreshAccessTokenAsync(cancellationToken);
			if (token is not null)
			{
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
			}
		}

		HttpResponseMessage response = await base.SendAsync(request, cancellationToken);

		if (response.StatusCode != HttpStatusCode.Unauthorized || isTokenEndpoint)
		{
			return response;
		}

		// The one retry. The server's view is authoritative — it refused a token we thought
		// was valid — so we refresh unconditionally rather than trusting our own cache.
		response.Dispose();

		string? refreshed = await auth.RefreshNowAsync(cancellationToken);
		if (refreshed is null)
		{
			// The refresh failed and AuthState signed us out. There is nothing to try — hand
			// back a 401 the caller can surface. Cloning is not needed because the original
			// response is already disposed.
			return new HttpResponseMessage(HttpStatusCode.Unauthorized);
		}

		HttpRequestMessage retry = await CloneRequestAsync(request, cancellationToken);
		retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshed);

		return await base.SendAsync(retry, cancellationToken);
	}

	/// <summary>
	/// <see cref="HttpRequestMessage"/> cannot be re-sent — <see cref="HttpClient"/>
	/// consumes the content stream on the first send. Clone the parts a retry cares
	/// about and drop the parts it does not.
	/// </summary>
	private static async Task<HttpRequestMessage> CloneRequestAsync(
		HttpRequestMessage original,
		CancellationToken cancellationToken)
	{
		HttpRequestMessage clone = new(original.Method, original.RequestUri);

		foreach (var header in original.Headers)
		{
			clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
		}

		if (original.Content is not null)
		{
			// Read once, in memory, and hand that back. The requests this handler sees are
			// small — token requests, profile puts, GPX previews are handled by a separate
			// upload path — so materialising is fine and lets a retry succeed.
			byte[] bytes = await original.Content.ReadAsByteArrayAsync(cancellationToken);
			clone.Content = new ByteArrayContent(bytes);
			foreach (var header in original.Content.Headers)
			{
				clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
			}
		}

		return clone;
	}
}
