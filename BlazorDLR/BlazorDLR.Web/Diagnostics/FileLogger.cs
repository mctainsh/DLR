using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DLR.Server.Diagnostics;

/// <summary>
/// One category's view of <see cref="FileLoggerProvider"/> - formats an entry and hands it to the
/// queue (§14.6).
/// </summary>
/// <param name="provider">Owns the queue, the clock and the level.</param>
/// <param name="category">The logger's category, usually a type name.</param>
internal sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
{
	/// <summary>
	/// The field separator.
	/// <para>
	/// A tab, and the message is the last field on purpose: a reader can split three times from the
	/// left and take the rest verbatim, so a message containing tabs, pipes or brackets - a SQL
	/// statement, a stack trace, a rider's own text - cannot be mistaken for another column.
	/// </para>
	/// </summary>
	private const char Separator = '\t';

	/// <inheritdoc />
	public IDisposable? BeginScope<TState>(TState state)
		where TState : notnull => null;

	/// <inheritdoc />
	public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

	/// <inheritdoc />
	public void Log<TState>(
		LogLevel logLevel,
		EventId eventId,
		TState state,
		Exception? exception,
		Func<TState, Exception?, string> formatter)
	{
		if (!IsEnabled(logLevel))
		{
			return;
		}

		ArgumentNullException.ThrowIfNull(formatter);

		provider.Enqueue(Compose(provider.Now, logLevel, category, formatter(state, exception), exception));
	}

	/// <summary>
	/// One entry as it appears on disk: stamp, level, category, message, tab-separated.
	/// <para>
	/// Shared with <see cref="FileLoggerProvider"/>, which writes the startup block at the head of
	/// each new day's file without going through a logger - the header has to be the file's first
	/// line, and anything queued would land behind whatever line caused the roll.
	/// </para>
	/// </summary>
	/// <param name="moment">When the entry happened.</param>
	/// <param name="level">Its level.</param>
	/// <param name="category">Usually a type name.</param>
	/// <param name="message">The formatted message.</param>
	/// <param name="exception">The exception to append, if there is one.</param>
	/// <returns>The line, without its terminator.</returns>
	internal static string Compose(
		DateTimeOffset moment,
		LogLevel level,
		string category,
		string message,
		Exception? exception = null)
	{
		StringBuilder line = new();

		// Round-trip format: sorts lexically, carries the offset, and parses back without a culture
		// - which matters because InvariantGlobalization is on solution-wide.
		line.Append(moment.ToString("O", CultureInfo.InvariantCulture))
			.Append(Separator)
			.Append(Level(level))
			.Append(Separator)
			.Append(category)
			.Append(Separator)
			.Append(Flatten(message));

		if (exception is not null)
		{
			// On the same line, flattened. A stack trace spread over forty lines makes every other
			// entry in the file unfindable, and the reader would have to guess which of those lines
			// were continuations. The screen unflattens it again for display.
			line.Append(" | ").Append(Flatten(exception.ToString()));
		}

		return line.ToString();
	}

	/// <summary>The short level name, fixed width so a file reads as columns.</summary>
	private static string Level(LogLevel level) => level switch
	{
		LogLevel.Trace => "TRACE",
		LogLevel.Debug => "DEBUG",
		LogLevel.Information => "INFO ",
		LogLevel.Warning => "WARN ",
		LogLevel.Error => "ERROR",
		LogLevel.Critical => "CRIT ",
		_ => "NONE ",
	};

	/// <summary>
	/// One entry, one line. Newlines become <c>¶</c> and tabs become spaces, so neither can forge a
	/// record separator or a column boundary in the file.
	/// </summary>
	private static string Flatten(string text) => text
		.Replace("\r\n", "¶", StringComparison.Ordinal)
		.Replace('\r', '¶')
		.Replace('\n', '¶')
		.Replace(Separator, ' ');
}
