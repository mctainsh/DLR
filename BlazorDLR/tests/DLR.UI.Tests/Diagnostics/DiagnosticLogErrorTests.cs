using BlazorDLR.Shared.Diagnostics;

namespace DLR.UI.Tests.Diagnostics;

/// <summary>
/// What the log writes when something throws (§14.6.2's log, read from Settings → Log).
/// <para>
/// This used to be one line of type and message, and the failure that proved it insufficient was
/// the map overlay's: the entire evidence a rider could send in was <em>"One or more errors
/// occurred. (Object reference not set to an instance of an object.)"</em> — a wrapper quoting a
/// null dereference, naming neither the component, the call nor the frame. The tests below pin the
/// three properties that make an entry worth reading: the wrapper is unwrapped, every exception in
/// the chain appears, and the stacks come with them.
/// </para>
/// <para>
/// Nothing here reads the log back, and nothing clears it: it is a process-wide ring that every
/// other suite writes to in parallel, and one of them empties it. Each test captures what it wrote
/// as it is written (<see cref="LogCapture"/>) and marks its own entries with a unique string, so
/// neither another suite's traffic nor its <c>Clear()</c> can decide the outcome.
/// </para>
/// </summary>
public sealed class DiagnosticLogErrorTests
{
	/// <summary>An exception with a real stack trace — one that was thrown, not one that was constructed.</summary>
	private static Exception Thrown(Func<Exception> build)
	{
		try
		{
			throw build();
		}
		catch (Exception caught)
		{
			return caught;
		}
	}

	/// <summary>
	/// Writes one error and hands back what the log recorded for it.
	/// <para>
	/// Captured as it is written rather than read back afterwards: the log is a process-wide ring
	/// that every suite in every other collection writes to, and one of them clears it outright.
	/// See <see cref="LogCapture"/>.
	/// </para>
	/// </summary>
	private static string WriteAndCapture(string context, Exception exception)
	{
		using LogCapture capture = new();

		DiagnosticLog.WriteError(context, exception);

		return capture.Text;
	}

	[Fact]
	public void WriteError_RecordsTheWrappedException_NotOnlyTheWrapper()
	{
		string marker = $"probe-{Guid.NewGuid():N}";
		Exception inner = Thrown(() => new NullReferenceException($"Object reference not set to an instance of an object. [{marker}]"));

		string entry = WriteAndCapture($"painting the map overlay [{marker}]", new AggregateException(inner));

		entry.Contains("System.NullReferenceException", StringComparison.Ordinal).ShouldBeTrue(
			"the type of what actually failed is the first thing a reader needs, and the wrapper does not carry it.");
		entry.Contains("System.AggregateException", StringComparison.Ordinal).ShouldBeTrue(
			"the wrapper is still named — it says how the failure travelled — but it is no longer the whole entry.");
		entry.Contains("at DLR.UI.Tests.Diagnostics.DiagnosticLogErrorTests", StringComparison.Ordinal).ShouldBeTrue(
			"and the stack, because 'which null?' is a question only frames answer.");
	}

	[Fact]
	public void WriteError_WalksTheWholeChain()
	{
		string marker = $"probe-{Guid.NewGuid():N}";
		Exception root = Thrown(() => new InvalidOperationException($"deepest [{marker}]"));
		Exception middle = new ApplicationException("middle", root);

		string entry = WriteAndCapture($"a nested failure [{marker}]", new AggregateException(middle));

		entry.Contains("System.ApplicationException", StringComparison.Ordinal).ShouldBeTrue();
		entry.Contains("System.InvalidOperationException", StringComparison.Ordinal).ShouldBeTrue(
			"a chain is only useful end to end — the answer is usually in the last one.");
		entry.Contains("← caused by", StringComparison.Ordinal).ShouldBeTrue(
			"and the entry has to read as a chain rather than as three unrelated exceptions.");
	}

	[Fact]
	public void WriteError_ListsEveryBranchOfAnAggregate()
	{
		string marker = $"probe-{Guid.NewGuid():N}";
		AggregateException both = new(
			new TimeoutException($"first [{marker}]"),
			new NotSupportedException($"second [{marker}]"));

		string entry = WriteAndCapture($"two failures at once [{marker}]", both);

		entry.Contains("System.TimeoutException", StringComparison.Ordinal).ShouldBeTrue();
		entry.Contains("System.NotSupportedException", StringComparison.Ordinal).ShouldBeTrue(
			"an aggregate that reported only its first inner exception is how the second one goes unfixed.");
	}

	[Fact]
	public void Summarise_NamesWhatFailed_NotTheWrapperThatCarriedIt()
	{
		AggregateException wrapped = new(new NullReferenceException("Object reference not set to an instance of an object."));

		string summary = DiagnosticLog.Summarise(wrapped);

		summary.ShouldBe("NullReferenceException: Object reference not set to an instance of an object.",
			"this is the line a banner on the map shows, and 'One or more errors occurred.' told a rider nothing at all.");
	}

	[Fact]
	public void Summarise_OfNothing_IsStillARenderableLine()
	{
		// The error-content path can be reached with no exception in hand on a host that recovers
		// between the throw and the render. A banner reading "" is a banner that looks broken.
		DiagnosticLog.Summarise(null).ShouldNotBeNullOrWhiteSpace();
	}
}
