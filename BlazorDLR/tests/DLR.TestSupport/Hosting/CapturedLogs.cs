using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace DLR.TestSupport.Hosting;

/// <summary>
/// Everything the server logged, for the one place where the log <em>is</em> the feature.
/// <para>
/// Asserting on log output is usually a way to pin an implementation detail, and this project does
/// not do it anywhere else. §7.11's dry run is the exception: the entire purpose of running the
/// nightly job with <c>DryRun</c> on for a week is that somebody reads what it says it would have
/// deleted. A dry run that quietly logged nothing would satisfy every other assertion available -
/// the accounts are still there either way - and would be useless.
/// </para>
/// </summary>
public sealed class CapturedLogs : ILoggerProvider
{
	private readonly ConcurrentQueue<string> _lines = new();

	/// <summary>Every message logged so far, formatted.</summary>
	public IReadOnlyList<string> Lines => [.. _lines];

	/// <summary>Whether anything logged so far contains a fragment.</summary>
	/// <param name="fragment">What to look for.</param>
	public bool Mentions(string fragment) =>
		_lines.Any(line => line.Contains(fragment, StringComparison.Ordinal));

	/// <inheritdoc />
	public ILogger CreateLogger(string categoryName) => new Sink(_lines);

	/// <inheritdoc />
	public void Dispose() => _lines.Clear();

	private sealed class Sink(ConcurrentQueue<string> lines) : ILogger
	{
		public IDisposable? BeginScope<TState>(TState state)
			where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(
			LogLevel logLevel,
			EventId eventId,
			TState state,
			Exception? exception,
			Func<TState, Exception?, string> formatter) =>
			lines.Enqueue(formatter(state, exception));
	}
}
