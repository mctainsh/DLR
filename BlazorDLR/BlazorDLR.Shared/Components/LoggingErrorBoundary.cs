using BlazorDLR.Shared.Diagnostics;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace BlazorDLR.Shared.Components;

/// <summary>
/// An <see cref="ErrorBoundary"/> that writes what it caught to <see cref="DiagnosticLog"/> before
/// it renders its error content.
/// <para>
/// <strong>Why this exists.</strong> The stock boundary hands the exception to
/// <c>ErrorContent</c> and nowhere else, so the whole of the evidence was whatever that markup
/// chose to print — in practice <c>exception.Message</c>, one line, on a phone screen. When the
/// map overlay failed on device, that line read "One or more errors occurred. (Object reference
/// not set to an instance of an object.)": a wrapper quoting a null dereference, naming neither
/// the component nor the frame it came from, and nothing was written to the log at all. The
/// person holding the phone could see that something had broken and could not find out what.
/// </para>
/// <para>
/// A boundary is also the last place an exception is seen — recovering is the whole point of one,
/// so nothing downstream will ever report it. That makes logging here obligatory rather than
/// convenient.
/// </para>
/// <para>
/// Everything else is the base component's behaviour, deliberately: the same
/// <c>ChildContent</c> / <c>ErrorContent</c> parameters, the same <c>MaximumErrorCount</c>, and
/// the same rethrow once that count is exceeded.
/// </para>
/// </summary>
public sealed class LoggingErrorBoundary : ErrorBoundary
{
	/// <summary>
	/// What is being guarded, in the app's own words — "the map overlay". Written into the log
	/// line so an entry says which part of the app went down rather than only what threw.
	/// </summary>
	[Parameter]
	public string Context { get; set; } = "a component";

	/// <inheritdoc />
	protected override Task OnErrorAsync(Exception exception)
	{
		DiagnosticLog.WriteError($"{Context} failed and was unmounted", exception);
		return base.OnErrorAsync(exception);
	}
}
