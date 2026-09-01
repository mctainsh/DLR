namespace DLR.UI.Tests.Diagnostics;

/// <summary>
/// Holds the tests that assert on the shape of the log itself, and stops them running beside
/// anything else.
/// <para>
/// <see cref="BlazorDLR.Shared.Diagnostics.DiagnosticLog"/> is a process-wide static, and every
/// other suite writes to it - which is why <see cref="LogCapture"/> exists and why the tests that
/// only care about <em>their own</em> entries need nothing more. These ones care about what is
/// <em>next to</em> what: the replaceable tail line is defined by the write that follows it, so a
/// line from another collection landing in the middle is not noise, it is a different scenario.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DiagnosticLogCollection
{
	/// <summary>The collection name, for <c>[Collection(...)]</c>.</summary>
	public const string Name = "diagnostic-log";
}
