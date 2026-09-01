using DLR.Server.Identity;
using Microsoft.AspNetCore.Diagnostics;

namespace DLR.Server.Diagnostics;

/// <summary>
/// Records an exception that reached the top of a request, and lets the existing error page
/// handle the response (§14.6).
/// <para>
/// <strong>It records and returns <c>false</c>.</strong> Handling the response is
/// <c>UseExceptionHandler("/Error")</c>'s job and it already does it; a handler that returned
/// <c>true</c> here would silently take that over and change what every caller sees. This exists
/// because the framework's own log line for an unhandled exception is written under a
/// <c>Microsoft.AspNetCore.Diagnostics</c> category - which the server's configuration filters to
/// Warning and above, so it survives, but it says nothing about who was signed in or which
/// reference the caller was shown.
/// </para>
/// <para>
/// Registered as a singleton by <c>AddExceptionHandler</c>, so it may only depend on singletons.
/// </para>
/// </summary>
/// <param name="events">Where the line goes.</param>
public sealed class UnhandledExceptionLogger(ServerEvents events) : IExceptionHandler
{
	/// <inheritdoc />
	public ValueTask<bool> TryHandleAsync(
		HttpContext httpContext,
		Exception exception,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(httpContext);

		// The username claim rather than Identity.Name: MapInboundClaims is off and the token
		// spells the name "unm" (§7.4), so the framework's own lookup finds nothing here.
		string user = httpContext.User.FindFirst(DlrClaims.UserName)?.Value ?? "anonymous";

		events.Unhandled(
			httpContext.Request.Method,
			httpContext.Request.Path.Value ?? "/",
			user,
			httpContext.TraceIdentifier,
			exception);

		// Not handled. The status code, the error page and the API's own response are unchanged by
		// this type existing, which is the whole of its contract.
		return ValueTask.FromResult(false);
	}
}
