using System.Collections.Concurrent;
using System.Text;

namespace BlazorDLR.Shared.Diagnostics;

/// <summary>
/// The app's own log: a ring in memory for <c>Pages/Settings/DiagnosticsLog.razor</c>, and a file
/// for everything the screen is too small or too late to answer.
/// <para>
/// <strong>Two sinks, and they used to be five.</strong> <c>Debug.WriteLine</c> needs a paired IDE
/// and vanishes entirely from a Release build; <c>Console</c> output is redirected to the system
/// log by some configurations of the iOS runtime and not others; <c>NSLog</c> needs Console.app
/// and a cable. On a phone in a tank bag all three are a round trip through a laptop, and on the
/// build that mattered none of them produced a single line. What survives is the pair that works
/// without anything attached: the ring the rider reads on the device, and a file that outlives the
/// process so a crash still leaves evidence.
/// </para>
/// <para>
/// <strong>Static, deliberately, and the one thing here that is.</strong> Callers include the iOS
/// <c>Program.Main</c>, which runs before <c>MauiProgram</c> has built a service provider, and
/// <c>AppDelegate.FinishedLaunching</c>, which runs before there is a scope to resolve from. A DI
/// registration would be unreachable from exactly the two moments most worth tracing.
/// </para>
/// <para>
/// <strong>The file is opt-in per host.</strong> <see cref="UseFile"/> is called from a host that
/// has somewhere to write - the MAUI heads - and never from the browsers, which have no filesystem
/// worth the name and a rider sitting in front of a real console anyway. Until it is called this
/// is memory only, which is what every test gets.
/// </para>
/// </summary>
public static class DiagnosticLog
{
	/// <summary>How many lines are held in memory. Roughly a ride's worth of traffic.</summary>
	public const int Capacity = 1000;

	/// <summary>
	/// How large the file may grow before it is rolled. Two megabytes is tens of thousands of
	/// lines - long enough to hold the ride that went wrong, small enough that a rider mailing
	/// it in does not need to think about it.
	/// </summary>
	public const long MaxFileBytes = 2L * 1024 * 1024;

	/// <summary>
	/// Wall-clock, and <em>not</em> an injected <c>TimeProvider</c> - the one deliberate exception
	/// to §10.4 rather than a hole in it.
	/// <para>
	/// The rule exists so timing logic can be tested by advancing a fake clock. A log line has no
	/// timing logic; it has a timestamp a human reads against their own watch to line the app up
	/// with something that happened outside it. Under a <c>FakeTimeProvider</c> anchored to
	/// 2026-01-01 this log would confidently report the wrong time for everything, which is worse
	/// than useless in the one job it has. <c>ClockRules</c> is satisfied on the letter - there is
	/// no ambient property read anywhere here - and this says plainly why it is not resolving one.
	/// </para>
	/// </summary>
	private static readonly TimeProvider Clock = TimeProvider.System;

	/// <summary>
	/// What the previous run's file is called: this run's path with a suffix. One generation, kept
	/// deliberately - see <see cref="UseFile"/>.
	/// </summary>
	private const string PreviousSuffix = ".1";

	/// <summary>
	/// How many lines <see cref="ReadPreviousFile"/> will hand back. A rolled file is two megabytes
	/// - tens of thousands of lines - and every one of them would go into a <c>textarea</c> in a
	/// WebView on a phone. The tail is what is kept: a run that ended badly ended at the bottom.
	/// </summary>
	private const int MaxReadLines = 5000;

	private static readonly ConcurrentQueue<LogLine> Lines = new();
	private static readonly Lock FileGate = new();

	/// <summary>Guards the replaceable tail line, which both sinks and every reader share.</summary>
	private static readonly Lock TransientGate = new();

	private static string? _filePath;

	/// <summary>
	/// The replaceable line standing at the tail, or null when the last thing written was an
	/// ordinary one. Held outside <see cref="Lines"/> because a queue cannot replace its own end.
	/// </summary>
	private static LogLine? _transient;

	/// <summary>
	/// Where that line begins in the file, so the next one can be written over it, or -1 when
	/// there is nothing there to overwrite.
	/// </summary>
	private static long _transientStart = -1;

	/// <summary>
	/// Raised after every write, so an open viewer follows along without polling. Subscribers are
	/// called on whatever thread wrote - a hub callback, the main thread, a background service -
	/// so a component handling this must marshal with <c>InvokeAsync</c> before touching state.
	/// </summary>
	public static event Action? Changed;

	/// <summary>Where the file sink writes, or null when this host has none.</summary>
	public static string? FilePath => _filePath;

	/// <summary>
	/// Where the run before this one was written, or null when this host has no file sink. The
	/// file may not exist - a first install has no previous run. See <see cref="HasPreviousFile"/>.
	/// </summary>
	public static string? PreviousFilePath => _filePath is null ? null : _filePath + PreviousSuffix;

	/// <summary>Whether there is a previous run on disk to read.</summary>
	public static bool HasPreviousFile => PreviousFilePath is { } path && File.Exists(path);

	/// <summary>One line: when, and what.</summary>
	/// <param name="At">The instant, in UTC. Rendered local, because a person comparing this
	/// against something that happened in front of them is reading their own watch.</param>
	/// <param name="Text">What happened.</param>
	public record LogLine(DateTimeOffset At, string Text)
	{
		/// <summary>Renders the line the way the viewer and a copied transcript both show it.</summary>
		/// <returns><c>HH:mm:ss.fff  text</c>.</returns>
		public override string ToString() => $"{At.LocalDateTime:HH:mm:ss.fff}  {Text}";
	}

	/// <summary>
	/// Points the file sink at <paramref name="path"/> and starts a run in it. Call once, from a
	/// host with a filesystem, as early as there is one.
	/// <para>
	/// <strong>The previous run is moved aside rather than appended to.</strong> One file per run,
	/// one generation back, and that pair is chosen for the question this log is usually asked:
	/// "why did the last launch not come up?" - which cannot be answered from the run that is doing
	/// the asking. Appending across launches, which is what this used to do, left the evidence in
	/// one growing file with no boundary in it, and the ring in memory covers only the run reading
	/// it. <c>Pages/Settings/DiagnosticsLog.razor</c> reads the moved-aside copy back.
	/// </para>
	/// </summary>
	/// <param name="path">Full path to the log file. Its directory must already exist.</param>
	public static void UseFile(string path)
	{
		_filePath = path;

		RotateForNewRun(path);

		// A banner per run. Still worth writing now that each run has its own file: a process the
		// OS restarted on its own - a sticky service coming back without an app - opens a file
		// exactly like a launch the rider made, and the timestamp on this line is the only tell.
		Write($"===== Log file opened: {path} =====");
	}

	/// <summary>Appends one line to both sinks, dropping the oldest in memory if the ring is full.</summary>
	/// <param name="message">What happened. Null is written as an empty line rather than skipped.</param>
	public static void Write(string message)
	{
		LogLine line = new(Clock.GetUtcNow(), message ?? string.Empty);

		lock (TransientGate)
		{
			// A replaceable line is only ever replaced by another of its own kind. Anything else
			// being said settles it where it stands - see WriteTransient - which is what makes
			// the totals in the log the ones each stretch of activity actually ended on.
			if (_transient is { } settled)
			{
				Lines.Enqueue(settled);
				_transient = null;
			}

			Lines.Enqueue(line);
		}

		Trim();
		AppendToFile(line, replacing: false);
		Changed?.Invoke();
	}

	/// <summary>
	/// Appends a line the <em>next</em> such line overwrites: a running total rather than an event.
	/// <para>
	/// For a counter that moves every couple of seconds for the length of a ride. Through
	/// <see cref="Write"/> it would be thousands of lines saying almost nothing; left out
	/// altogether it is the question a log cannot answer - how many fixes the receiver produced,
	/// and how many of them reached the ride. Replacing rather than appending keeps the current
	/// answer at the tail and costs one line.
	/// </para>
	/// <para>
	/// The line survives permanently once anything else is written after it, so what a finished log
	/// holds is the totals as they stood at each thing that happened.
	/// </para>
	/// </summary>
	/// <param name="message">The totals as they now stand.</param>
	public static void WriteTransient(string message)
	{
		LogLine line = new(Clock.GetUtcNow(), message ?? string.Empty);

		lock (TransientGate)
		{
			_transient = line;
		}

		AppendToFile(line, replacing: true);
		Changed?.Invoke();
	}

	/// <summary>
	/// Drops the oldest lines once the ring is over <see cref="Capacity"/>.
	/// </summary>
	/// <remarks>
	/// After enqueueing rather than before, so a burst from several threads converges on the cap
	/// instead of racing to make room and then all writing.
	/// </remarks>
	private static void Trim()
	{
		while (Lines.Count > Capacity && Lines.TryDequeue(out _))
		{
			// Dropping the oldest is the whole of it.
		}
	}

	/// <summary>
	/// Records an exception in full: what was being attempted, then the type, message and stack of
	/// every exception in the chain.
	/// <para>
	/// <strong>The whole chain, and the stacks with it.</strong> This used to write one line of
	/// type and message, which is enough for a failure that names itself and useless for one that
	/// does not - "One or more errors occurred. (Object reference not set to an instance of an
	/// object.)" is a wrapper's message quoting a null dereference with no hint of whose. The
	/// wrapper is unwrapped, the inner exceptions are listed and every frame is kept, because the
	/// device this most often happens on is a phone in a mount with no debugger anywhere near it,
	/// and the log is the only evidence there will ever be.
	/// </para>
	/// </summary>
	/// <param name="context">What was being attempted, in the app's own words.</param>
	/// <param name="exception">What went wrong.</param>
	public static void WriteError(string context, Exception exception) =>
		Write($"ERROR - {context}: {Describe(exception)}");

	/// <summary>
	/// The whole of an exception as the log renders it: type, message and stack for it and for
	/// everything it wraps.
	/// <para>
	/// <see cref="AggregateException"/> is flattened rather than quoted - its own message says
	/// only how many there were, and the answer is always in what it carries. Every inner
	/// exception is listed, indented under the one that wrapped it.
	/// </para>
	/// </summary>
	/// <param name="exception">What went wrong.</param>
	/// <returns>A multi-line description. One log entry, however many lines it runs to.</returns>
	public static string Describe(Exception? exception)
	{
		if (exception is null)
		{
			return "<no exception>";
		}

		StringBuilder description = new();
		Describe(exception, depth: 0, description);
		return description.ToString().TrimEnd();
	}

	/// <summary>
	/// A one-line summary for a screen: the innermost thing that actually went wrong.
	/// <para>
	/// What a rider or a developer standing over the phone reads off the banner. The full
	/// description belongs in the log (<see cref="Describe(Exception?)"/>); this is what fits
	/// under a heading, and it names the type because "Object reference not set to an instance of
	/// an object." on its own has been the whole of the evidence more than once.
	/// </para>
	/// </summary>
	/// <param name="exception">What went wrong.</param>
	/// <returns>A single line, safe to render.</returns>
	public static string Summarise(Exception? exception)
	{
		if (exception is null)
		{
			return "Unknown error.";
		}

		Exception innermost = exception is AggregateException aggregate
			? aggregate.Flatten().InnerExceptions.FirstOrDefault() ?? aggregate
			: exception;

		while (innermost.InnerException is { } inner)
		{
			innermost = inner;
		}

		return $"{innermost.GetType().Name}: {innermost.Message}";
	}

	/// <summary>How deep the inner-exception walk goes before it stops describing.</summary>
	/// <remarks>
	/// A chain this long is a cycle or a wrapper gone mad, and either way the answer is in the
	/// first few. The cap is here so one malformed exception cannot fill the ring on its own.
	/// </remarks>
	private const int MaxExceptionDepth = 8;

	private static void Describe(Exception exception, int depth, StringBuilder description)
	{
		if (depth > MaxExceptionDepth)
		{
			description.AppendLine("  … further inner exceptions not shown.");
			return;
		}

		string indent = new(' ', depth * 2);

		description.Append(indent)
			.Append(depth == 0 ? string.Empty : "← caused by ")
			.Append(exception.GetType().FullName)
			.Append(": ")
			.AppendLine(exception.Message);

		if (exception.StackTrace is { Length: > 0 } stack)
		{
			// Re-indented frame by frame rather than appended whole, so an inner exception's
			// stack is visibly under the exception it belongs to when three of them are stacked
			// up in one entry.
			foreach (string frame in stack.ReplaceLineEndings("\n").Split('\n'))
			{
				description.Append(indent).AppendLine(frame.TrimEnd());
			}
		}

		// Flattened, so a wrapper around a wrapper does not cost two levels of indent for one
		// piece of information - and so every branch of a multi-error aggregate is listed rather
		// than only whichever one happened to be first.
		if (exception is AggregateException aggregate)
		{
			foreach (Exception inner in aggregate.Flatten().InnerExceptions)
			{
				Describe(inner, depth + 1, description);
			}

			return;
		}

		if (exception.InnerException is { } single)
		{
			Describe(single, depth + 1, description);
		}
	}

	/// <summary>Everything held in memory, oldest first, the replaceable tail line last.</summary>
	/// <returns>A snapshot that will not change underneath a render.</returns>
	public static IReadOnlyList<LogLine> Snapshot()
	{
		lock (TransientGate)
		{
			return _transient is { } pending ? [.. Lines, pending] : Lines.ToArray();
		}
	}

	/// <summary>Empties the ring, so the next attempt at reproducing something starts clean.</summary>
	/// <remarks>Leaves the file alone: that is the copy worth keeping precisely because nothing
	/// in the UI can throw it away by accident.</remarks>
	public static void Clear()
	{
		lock (TransientGate)
		{
			while (Lines.TryDequeue(out _))
			{
				// Nothing to do per line.
			}

			_transient = null;
		}

		Changed?.Invoke();
	}

	/// <summary>
	/// Reads the previous run's file back, newest lines last, so the Log screen can show a launch
	/// that is already over.
	/// </summary>
	/// <returns>
	/// The lines as they were written, at most <see cref="MaxReadLines"/> of them with a marker in
	/// place of what was dropped. Empty when this host has no file sink or there is no previous
	/// run; a single explanatory line when the file is there and could not be read - a diagnostic
	/// screen that fails silently is worse than one that says what stopped it.
	/// </returns>
	public static IReadOnlyList<string> ReadPreviousFile()
	{
		if (PreviousFilePath is not { } path)
			return [];

		try
		{
			// Under the same gate as the writes: nothing appends to this file after the rotation
			// that made it, but the rotation itself can land while a viewer is reading.
			lock (FileGate)
			{
				if (!File.Exists(path))
					return [];

				string[] lines = File.ReadAllLines(path);

				return lines.Length <= MaxReadLines
					? lines
					: [$"===== {lines.Length - MaxReadLines} earlier line(s) not shown =====", .. lines[^MaxReadLines..]];
			}
		}
		catch (Exception exception)
		{
			return [$"Could not read {path}: {exception.GetType().Name}: {exception.Message}"];
		}
	}

	/// <summary>
	/// Moves this run's file aside so the run starts on an empty one, keeping exactly one
	/// generation. Failure is swallowed for the reason given on <see cref="AppendToFile"/> - the
	/// worst outcome allowed here is a log that is harder to read, never an app that will not
	/// start.
	/// </summary>
	/// <param name="path">The file the sink is about to write to.</param>
	private static void RotateForNewRun(string path)
	{
		try
		{
			lock (FileGate)
			{
				_transientStart = -1;

				if (File.Exists(path))
					File.Move(path, path + PreviousSuffix, overwrite: true);
			}
		}
		catch (Exception)
		{
			// Nowhere to report it: this runs before the first line of the run is written, and the
			// sink it would be reported to is the one that just failed.
		}
	}

	/// <summary>
	/// Appends to the file, rolling it once it passes <see cref="MaxFileBytes"/>.
	/// <para>
	/// Every failure is swallowed, and deliberately: a log that can take the app down with it is a
	/// worse bug than whatever it was added to find. There is also nowhere left to report it -
	/// this <em>is</em> the reporting.
	/// </para>
	/// </summary>
	/// <param name="line">The line to write.</param>
	/// <param name="replacing">
	/// Whether this line overwrites the replaceable one already at the end of the file
	/// (<see cref="WriteTransient"/>) rather than following it. The file is truncated back to
	/// where that line began, which is why the offset is tracked rather than recomputed: the
	/// rendered length is not the byte length once a message carries a stack trace.
	/// </param>
	private static void AppendToFile(LogLine line, bool replacing)
	{
		if (_filePath is not { } path)
			return;

		try
		{
			lock (FileGate)
			{
				long length = new FileInfo(path) is { Exists: true } info ? info.Length : 0;

				// This run's file starts again rather than being moved over the previous run's
				// copy, which is what this used to do. That copy is the one the Log screen's
				// "Previous run" view reads, and a single long ride must not be able to overwrite
				// the launch somebody is trying to diagnose. Losing the early part of a run that
				// has already written two megabytes is the cheaper half of that trade.
				if (length > MaxFileBytes)
				{
					File.WriteAllText(
						path,
						$"===== Rolled past {MaxFileBytes} bytes; earlier lines of this run are gone ====={Environment.NewLine}",
						Encoding.UTF8);

					_transientStart = -1;
					length = new FileInfo(path).Length;
				}

				if (!replacing)
				{
					_transientStart = -1;
				}
				else if (_transientStart >= 0 && _transientStart <= length)
				{
					// Cut the previous totals off the end rather than writing another set beside
					// them. The bound is belt and braces: an external truncation between two of
					// these would otherwise have this lengthen the file with nulls.
					using FileStream file = new(path, FileMode.Open, FileAccess.Write, FileShare.Read);
					file.SetLength(_transientStart);
				}
				else
				{
					_transientStart = length;
				}

				File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
			}
		}
		catch (Exception)
		{
			// See the remarks. Nothing here is retryable and nothing is watching.
		}
	}
}
