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
	/// </summary>
	/// <param name="path">Full path to the log file. Its directory must already exist.</param>
	public static void UseFile(string path)
	{
		_filePath = path;

		// A banner per run, because the file is append-only across launches: without it, working
		// out where the run being investigated starts means counting backwards through timestamps.
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
				// One generation back, not many: the previous run is usually the one being asked
				// about, and a phone is not the place to keep a log archive.
				if (new FileInfo(path) is { Exists: true, Length: > MaxFileBytes })
					File.Move(path, path + ".1", overwrite: true);

				File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
			}
		}
		catch (Exception)
		{
			// See the remarks. Nothing here is retryable and nothing is watching.
		}
	}
}
