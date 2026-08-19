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
/// has somewhere to write — the MAUI heads — and never from the browsers, which have no filesystem
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
	/// lines — long enough to hold the ride that went wrong, small enough that a rider mailing
	/// it in does not need to think about it.
	/// </summary>
	public const long MaxFileBytes = 2L * 1024 * 1024;

	/// <summary>
	/// Wall-clock, and <em>not</em> an injected <c>TimeProvider</c> — the one deliberate exception
	/// to §10.4 rather than a hole in it.
	/// <para>
	/// The rule exists so timing logic can be tested by advancing a fake clock. A log line has no
	/// timing logic; it has a timestamp a human reads against their own watch to line the app up
	/// with something that happened outside it. Under a <c>FakeTimeProvider</c> anchored to
	/// 2026-01-01 this log would confidently report the wrong time for everything, which is worse
	/// than useless in the one job it has. <c>ClockRules</c> is satisfied on the letter — there is
	/// no ambient property read anywhere here — and this says plainly why it is not resolving one.
	/// </para>
	/// </summary>
	private static readonly TimeProvider Clock = TimeProvider.System;

	/// <summary>
	/// What the previous run's file is called: this run's path with a suffix. One generation, kept
	/// deliberately — see <see cref="UseFile"/>.
	/// </summary>
	private const string PreviousSuffix = ".1";

	/// <summary>
	/// How many lines <see cref="ReadPreviousFile"/> will hand back. A rolled file is two megabytes
	/// — tens of thousands of lines — and every one of them would go into a <c>textarea</c> in a
	/// WebView on a phone. The tail is what is kept: a run that ended badly ended at the bottom.
	/// </summary>
	private const int MaxReadLines = 5000;

	private static readonly ConcurrentQueue<LogLine> Lines = new();
	private static readonly Lock FileGate = new();

	private static string? _filePath;

	/// <summary>
	/// Raised after every write, so an open viewer follows along without polling. Subscribers are
	/// called on whatever thread wrote — a hub callback, the main thread, a background service —
	/// so a component handling this must marshal with <c>InvokeAsync</c> before touching state.
	/// </summary>
	public static event Action? Changed;

	/// <summary>Where the file sink writes, or null when this host has none.</summary>
	public static string? FilePath => _filePath;

	/// <summary>
	/// Where the run before this one was written, or null when this host has no file sink. The
	/// file may not exist — a first install has no previous run. See <see cref="HasPreviousFile"/>.
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
	/// "why did the last launch not come up?" — which cannot be answered from the run that is doing
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
		// OS restarted on its own — a sticky service coming back without an app — opens a file
		// exactly like a launch the rider made, and the timestamp on this line is the only tell.
		Write($"===== Log file opened: {path} =====");
	}

	/// <summary>Appends one line to both sinks, dropping the oldest in memory if the ring is full.</summary>
	/// <param name="message">What happened. Null is written as an empty line rather than skipped.</param>
	public static void Write(string message)
	{
		LogLine line = new(Clock.GetUtcNow(), message ?? string.Empty);
		Lines.Enqueue(line);

		// Trim after enqueueing rather than before, so a burst from several threads converges on
		// the cap instead of racing to make room and then all writing.
		while (Lines.Count > Capacity && Lines.TryDequeue(out _))
		{
			// Dropping the oldest is the whole of it.
		}

		AppendToFile(line);
		Changed?.Invoke();
	}

	/// <summary>Records an exception with its type, message and where it came from.</summary>
	/// <param name="context">What was being attempted, in the app's own words.</param>
	/// <param name="exception">What went wrong.</param>
	public static void WriteError(string context, Exception exception) =>
		Write($"ERROR — {context}: {exception.GetType().Name}: {exception.Message}");

	/// <summary>Everything held in memory, oldest first.</summary>
	/// <returns>A snapshot that will not change underneath a render.</returns>
	public static IReadOnlyList<LogLine> Snapshot() => Lines.ToArray();

	/// <summary>Empties the ring, so the next attempt at reproducing something starts clean.</summary>
	/// <remarks>Leaves the file alone: that is the copy worth keeping precisely because nothing
	/// in the UI can throw it away by accident.</remarks>
	public static void Clear()
	{
		while (Lines.TryDequeue(out _))
		{
			// Nothing to do per line.
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
	/// run; a single explanatory line when the file is there and could not be read — a diagnostic
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
	/// generation. Failure is swallowed for the reason given on <see cref="AppendToFile"/> — the
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
	/// worse bug than whatever it was added to find. There is also nowhere left to report it —
	/// this <em>is</em> the reporting.
	/// </para>
	/// </summary>
	private static void AppendToFile(LogLine line)
	{
		if (_filePath is not { } path)
			return;

		try
		{
			lock (FileGate)
			{
				// This run's file starts again rather than being moved over the previous run's
				// copy, which is what this used to do. That copy is the one the Log screen's
				// "Previous run" view reads, and a single long ride must not be able to overwrite
				// the launch somebody is trying to diagnose. Losing the early part of a run that
				// has already written two megabytes is the cheaper half of that trade.
				if (new FileInfo(path) is { Exists: true, Length: > MaxFileBytes })
					File.WriteAllText(
						path,
						$"===== Rolled past {MaxFileBytes} bytes; earlier lines of this run are gone ====={Environment.NewLine}",
						Encoding.UTF8);

				File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
			}
		}
		catch (Exception)
		{
			// See the remarks. Nothing here is retryable and nothing is watching.
		}
	}
}
