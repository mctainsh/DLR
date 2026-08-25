namespace DLR.Server.Diagnostics;

/// <summary>
/// The log-file's own health at startup, the markers either side of a shutdown, and the two
/// process-wide hooks an exception nobody was waiting for arrives through (§14.6).
/// <para>
/// What this server <em>is</em> — build, folders, database, roster — is <see cref="StartupBanner"/>'s
/// block, written before the first request. This adds the one fact that block cannot carry: the
/// writer only discovers a directory it may not create once it has tried, which is after the
/// banner was composed.
/// </para>
/// <para>
/// The shutdown pair is the marker an administrator reads a file backwards from. Without it a
/// restart is a gap in the timestamps, and a gap is indistinguishable from a quiet night.
/// </para>
/// </summary>
/// <param name="lifetime">Started, stopping, stopped.</param>
/// <param name="fileLog">Whether writing to the log file is working, and the flush on the way down.</param>
/// <param name="events">Where the lines go.</param>
/// <param name="started">When this run began — the same anchor the startup block stamps (§10.4).</param>
public sealed class ServerLifetimeLog(
	IHostApplicationLifetime lifetime,
	FileLoggerProvider fileLog,
	ServerEvents events,
	ServerStart started) : IHostedService, IDisposable
{
	private readonly List<IDisposable> _registrations = [];

	/// <inheritdoc />
	public Task StartAsync(CancellationToken cancellationToken)
	{
		// Guarded, all three: these run inside the host's own lifetime notification, and a
		// diagnostic that cannot describe the server must not be able to stop it from serving.
		// Reading a setting that is not there is exactly the sort of thing that throws here.
		_registrations.Add(lifetime.ApplicationStarted.Register(() => Safely("the startup lines", Started)));
		_registrations.Add(lifetime.ApplicationStopping.Register(() => Safely("the shutdown line", Stopping)));
		_registrations.Add(lifetime.ApplicationStopped.Register(() => Safely("the stopped line", Stopped)));

		AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
		TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

		return Task.CompletedTask;
	}

	/// <inheritdoc />
	public Task StopAsync(CancellationToken cancellationToken)
	{
		Unhook();
		return Task.CompletedTask;
	}

	/// <inheritdoc />
	public void Dispose()
	{
		Unhook();

		foreach (IDisposable registration in _registrations)
			registration.Dispose();

		_registrations.Clear();
	}

	/// <summary>
	/// Detaches the process-wide handlers.
	/// <para>
	/// Not merely tidy: <see cref="AppDomain"/> and <see cref="TaskScheduler"/> are per-process,
	/// and the integration tests build a host per test class. Left attached, the hundredth test
	/// would log one stray exception a hundred times through ninety-nine disposed containers.
	/// </para>
	/// <para>
	/// Unguarded, and safe to call twice: detaching a handler that is not attached is a no-op, so
	/// <see cref="StopAsync"/> and <see cref="Dispose"/> can both simply ask.
	/// </para>
	/// </summary>
	private void Unhook()
	{
		AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
		TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
	}

	/// <summary>
	/// Runs one of the lifetime callbacks, and turns a failure in it into a line rather than a
	/// failure of the thing it was describing.
	/// </summary>
	/// <param name="what">What was being written, for the line that says it was not.</param>
	/// <param name="write">The callback.</param>
	private void Safely(string what, Action write)
	{
		try
		{
			write();
		}
		catch (Exception exception)
		{
			// Deliberately every exception. The alternative to swallowing it here is a server that
			// refuses to finish starting because it could not write a sentence about itself.
			events.Failure(ServerEvents.Areas.Startup, $"Could not write {what}.", exception);
		}
	}

	/// <summary>Whether the log file this line is going into is actually being written.</summary>
	private void Started()
	{
		if (fileLog.Problem is { Length: > 0 } problem)
		{
			// This one will not reach the file — that is what the problem *is* — but the console
			// provider beside it still has it, and the administration screen reads the same property.
			events.Concern(ServerEvents.Areas.Startup, problem);
		}
		else
		{
			events.Note(ServerEvents.Areas.Startup, "No problems detected.");
		}
	}

	private void Stopping() =>
		events.Note(ServerEvents.Areas.Startup, "Shutting down — no longer accepting requests.");

	private void Stopped() =>
		events.Note(
			ServerEvents.Areas.Startup,
			$"Stopped after {StartupBanner.Age(started.Uptime)}. Anything after this line is a different run.");

	/// <summary>
	/// A background failure, and then — usually — the process ends. The flush is the point: the
	/// queue that keeps logging off the request path is also what loses the last line of a dying
	/// process.
	/// </summary>
	private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs args)
	{
		if (args.ExceptionObject is Exception exception)
		{
			events.Unobserved(
				args.IsTerminating ? "a thread, and the process is ending" : "a thread",
				exception);
		}

		// Only on the way out. This event also fires for failures the runtime goes on to survive,
		// and blocking one of those for up to two seconds buys a line the writer would have got to
		// on its own.
		if (args.IsTerminating)
			fileLog.Flush(TimeSpan.FromSeconds(2));
	}

	/// <summary>
	/// A task nobody awaited threw. Deliberately not marked observed: swallowing it here would
	/// change what the process does, and this type only watches.
	/// </summary>
	private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args) =>
		events.Unobserved("an unawaited task", args.Exception);
}
