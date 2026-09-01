using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace DLR.TestSupport.Hosting;

/// <summary>
/// Gives the test host a connection address, because <c>TestServer</c> does not have one.
/// <para>
/// Without this <c>HttpContext.Connection.RemoteIpAddress</c> is null, and
/// <c>ForwardedHeadersMiddleware</c> then skips its <c>KnownProxies</c> check entirely - every
/// <c>X-Forwarded-For</c> is honoured no matter what the configuration says. Every per-address
/// test would still pass, including the ones asserting the header <em>is</em> read, so the
/// suite would look like it covered §7.8's forwarded-header rule while covering only half of
/// it.
/// </para>
/// <para>
/// Loopback, because that is what a real deployment sees: Caddy on the same host.
/// </para>
/// </summary>
public sealed class LoopbackConnection : IStartupFilter
{
	/// <inheritdoc />
	public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
		builder =>
		{
			// First in the pipeline, so it is set before UseForwardedHeaders looks at it.
			builder.Use(async (context, following) =>
			{
				context.Connection.RemoteIpAddress ??= IPAddress.Loopback;
				context.Connection.RemotePort = 40_000;

				await following(context);
			});

			next(builder);
		};
}
