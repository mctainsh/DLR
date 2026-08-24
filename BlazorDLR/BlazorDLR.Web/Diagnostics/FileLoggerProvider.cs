using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DLR.Server.Diagnostics;

/// <summary>
/// Writes the server's log to one file per day (§14.6).
/// <para>
/// Hand-written rather than a logging framework, and that is a small amount of code for a reason
/// worth stating: the alternative is a dependency whose licence has to be reconciled with the
/// AGPL, whose configuration becomes a second place logging is described, and most of which is
/// sinks this service will never use. What is actually needed is "append a line, roll at midnight,
/// delete the old ones" — which is this file and one branch of the nightly job.
/// </para>
/// <para>
/// <strong>One background writer, never the calling thread.</strong> Logging happens on the
/// position path and inside the hub, so a caller that had to wait on a file handle would turn disk
/// latency into request latency. Entries queue and a single consumer drains them, which also means
/// exactly one thread ever holds the file open and nothing has to lock around it.
/// </para>
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
	/// <summary>
	/// The queue between the callers and the writer.
	/// <para>
	/// Bounded, and it drops rather than blocking when full. A log that can apply back-pressure to
	/// the thing it is logging turns a burst of errors into an outage, which is the one failure
	/// mode a diagnostic must not have.
	/// </para>
	/// </summary>
	private readonly BlockingCollection<string> _queue = new(boundedCapacity: 10_000);

	/// <summary>
	/// UTF-8 with no byte-order mark.
	/// <para>
	/// <c>Encoding.UTF8</c> emits one, and it lands at the start of the <em>first</em> line of each
	/// new day's file — where the reader is expecting a timestamp. The line then parses as
	/// unrecognised and the first entry of every morning comes back as an undated blob. A log's
	/// encoding is not in doubt; the mark buys nothing and costs that.
	/// </para>
	/// </summary>
	private static readonly UTF8Encoding FileEncoding = new(encoderShouldEmitUTF8Identifier: false);

	private readonly FileLogOptions _settings;

	private readonly TimeProvider _clock;

	private readonly Task? _writer;

	/// <summary>
	/// Guards <see cref="Dispose"/> against its second call.
	/// <para>
	/// It gets one: the provider is registered twice on purpose — once as itself so the reader can
	/// be handed the resolved directory, and once as <see cref="ILoggerProvider"/> so the logging
	/// factory finds it — and both the factory and the container dispose what they own. Without
	/// this the second call throws <see cref="ObjectDisposedException"/> out of host shutdown,
	/// which surfaces as every integration test failing on the way out rather than on the way in.
	/// </para>
	/// </summary>
	private bool _disposed;

	/// <summary>Builds the provider, and starts the writer only if a file was actually asked for.</summary>
	/// <param name="options">Where to write, and from which level.</param>
	/// <param name="clock">Names the file and stamps each line (§10.4).</param>
	public FileLoggerProvider(IOptions<FileLogOptions> options, TimeProvider clock)
	{
		_settings = options.Value;
		_clock = clock;
		Directory = ResolveDirectory(_settings.Directory);

		if (!_settings.Enabled)
		{
			// No thread, no directory, no handle. A deployment that did not ask for a log file
			// should not be able to tell this provider is registered.
			return;
		}

		_writer = Task.Factory.StartNew(
			WriteLoop,
			CancellationToken.None,
			TaskCreationOptions.LongRunning,
			TaskScheduler.Default);
	}

	/// <summary>
	/// The directory in use, already resolved to an absolute path.
	/// <para>
	/// The reader is handed this and nothing else. No filename ever comes off the wire — see
	/// <see cref="ServerLogReader"/>, which builds every path it opens from a date.
	/// </para>
	/// </summary>
	public string Directory { get; }

	/// <inheritdoc />
	public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

	/// <inheritdoc />
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;

		_queue.CompleteAdding();

		// Bounded, because this runs on the shutdown path: a writer stuck on a full disk must not
		// hold the process open, and the lines it has not written are lost either way.
		_ = _writer?.Wait(TimeSpan.FromSeconds(5));

		_queue.Dispose();
	}

	/// <summary>Whether a level reaches the file. Checked before formatting, which is the costly part.</summary>
	/// <param name="level">The candidate level.</param>
	/// <returns><c>true</c> when the line should be written.</returns>
	internal bool IsEnabled(LogLevel level) =>
		_settings.Enabled && level != LogLevel.None && level >= _settings.MinimumLevel;

	/// <summary>The clock, so <see cref="FileLogger"/> can stamp a line without its own injection.</summary>
	internal DateTimeOffset Now => _clock.GetUtcNow();

	/// <summary>Queues one formatted line. Never blocks, and never throws.</summary>
	/// <param name="line">The line, without its terminator.</param>
	internal void Enqueue(string line)
	{
		if (!_queue.IsAddingCompleted)
		{
			// TryAdd, not Add: see the queue's own note. A full queue drops the line.
			_ = _queue.TryAdd(line);
		}
	}

	/// <summary>The file a given day's entries belong in. Also the only thing the reader opens.</summary>
	/// <param name="directory">The resolved log directory.</param>
	/// <param name="day">Which day, in UTC.</param>
	/// <returns>An absolute path whose name carries the date and nothing a caller supplied.</returns>
	public static string FileFor(string directory, DateOnly day) =>
		Path.Combine(directory, $"dlr-{day:yyyyMMdd}.log");

	/// <summary>Drains the queue onto disk until the provider is disposed.</summary>
	/// <remarks>
	/// Holds the day's file open and drains in batches: <c>File.AppendAllText</c> per line would open,
	/// encode, flush and close for every entry, which on a server that logs from the position path is
	/// four syscalls per line forever. One handle per day and one flush per batch instead — and the
	/// midnight roll stays free, because the day is part of the name and a new day simply swaps the
	/// writer.
	/// </remarks>
	private void WriteLoop()
	{
		try
		{
			System.IO.Directory.CreateDirectory(Directory);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Nowhere to write. The console provider beside this one still has everything.
			return;
		}

		StreamWriter? file = null;
		DateOnly open = default;

		try
		{
			foreach (string first in _queue.GetConsumingEnumerable())
			{
				try
				{
					DateOnly day = DateOnly.FromDateTime(Now.UtcDateTime);

					if (file is null || day != open)
					{
						file?.Dispose();

						// FileShare.ReadWrite explicitly: the handle is now held for the whole day rather
						// than reopened per line, and StreamWriter's own default would let the reader open
						// today's file only by accident. A log nobody can read while it is being written is
						// the one the administration screen exists to show.
						file = new StreamWriter(
							new FileStream(
								FileFor(Directory, day),
								FileMode.Append,
								FileAccess.Write,
								FileShare.ReadWrite),
							FileEncoding);
						open = day;
					}

					file.WriteLine(first);

					// Whatever else is already queued goes out under the same handle and the same flush.
					// A burst of logging is one write to the disk rather than one per line.
					while (_queue.TryTake(out string? next))
					{
						file.WriteLine(next);
					}

					// Flushed every batch rather than left to the buffer: a log read minutes later must
					// not be missing the entry that explains what has just gone wrong.
					file.Flush();
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
					// A log that throws takes down the thing it is logging. A full disk or a revoked
					// permission is a reason to stop writing, not a reason to stop the server — and it
					// is still visible in the console log this one sits beside.
					try
					{
						file?.Dispose();
					}
					catch (Exception disposal) when (disposal is IOException or UnauthorizedAccessException)
					{
						// The handle was already unusable; that is what brought us here.
					}

					// Dropped rather than kept: the next line reopens, so a disk that comes back does
					// not need the process restarted.
					file = null;
				}
			}
		}
		finally
		{
			try
			{
				file?.Dispose();
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// Shutting down on a disk that has already failed. Nothing left to do about it.
			}
		}
	}

	/// <summary>Absolute, so nothing downstream depends on the process's working directory.</summary>
	private static string ResolveDirectory(string configured) =>
		Path.IsPathRooted(configured)
			? Path.GetFullPath(configured)
			: Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured));
}
