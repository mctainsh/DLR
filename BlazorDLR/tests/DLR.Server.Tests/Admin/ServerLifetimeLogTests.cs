using DLR.Core.Contracts.Admin;
using DLR.Server.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace DLR.Server.Tests.Admin;

/// <summary>
/// The startup lines, and the two recorders that put an exception in the file (§14.6).
/// <para>
/// End to end through the real provider and the real reader rather than against a mocked
/// <c>ILogger</c>: the thing worth pinning is that these lines reach the file an administrator
/// opens, and a mock proves only that a method was called.
/// </para>
/// </summary>
public sealed class ServerLifetimeLogTests : IDisposable
{
	private static readonly DateTimeOffset Start = new(2026, 3, 4, 9, 30, 0, TimeSpan.Zero);

	private readonly string _directory =
		Path.Combine(Path.GetTempPath(), $"dlr-startup-tests-{Guid.NewGuid():N}");

	private readonly CancellationTokenSource _started = new();

	private readonly CancellationTokenSource _stopping = new();

	private readonly CancellationTokenSource _stopped = new();

	public void Dispose()
	{
		_started.Dispose();
		_stopping.Dispose();
		_stopped.Dispose();

		if (Directory.Exists(_directory))
		{
			Directory.Delete(_directory, recursive: true);
		}
	}

	/// <summary>
	/// What the started callback has left to say once <see cref="StartupBanner"/> owns the
	/// description: whether the log file itself is working.
	/// <para>
	/// The build, the folders, the database and the roster are the block's, written before this
	/// callback can run and asserted in <c>StartupBannerTests</c>. This line is the one that cannot
	/// be in the block - the writer only discovers a directory it may not create once it has tried.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Starting_SaysWhetherTheLogFileIsWorking()
	{
		using Harness harness = Build();

		await harness.Log.StartAsync(CancellationToken.None);
		await harness.RaiseStartedAsync();

		IReadOnlyList<string> lines = await harness.LinesAsync(expected: 1);

		lines.ShouldContain(line => line.Contains("No problems detected", StringComparison.Ordinal));
	}

	/// <summary>
	/// A restart is the marker an administrator reads the file backwards from, so it has to be in
	/// the file rather than inferred from a gap in the timestamps.
	/// </summary>
	[Fact]
	public async Task Stopping_SaysSoAndAgesTheProcess()
	{
		using Harness harness = Build();

		await harness.Log.StartAsync(CancellationToken.None);
		await harness.RaiseStartedAsync();

		harness.Clock.Advance(TimeSpan.FromHours(3));

		await _stopping.CancelAsync();
		await _stopped.CancelAsync();

		// Three: started, stopping, stopped.
		IReadOnlyList<string> lines = await harness.LinesAsync(expected: 3);

		lines.ShouldContain(line => line.Contains("no longer accepting requests", StringComparison.Ordinal));
		lines.ShouldContain(line => line.Contains("Stopped after 0d 03:00:00", StringComparison.Ordinal));
	}

	/// <summary>
	/// The recorder itself: an exception has to arrive with its type and message, because the
	/// screen shows one line and a bare "an error occurred" sends somebody to the source anyway.
	/// </summary>
	[Fact]
	public async Task Failure_WritesTheExceptionBesideTheSentence()
	{
		using Harness harness = Build();

		harness.Events.Failure(
			ServerEvents.Areas.Photos,
			"Could not write the thumbnail.",
			new InvalidOperationException("The volume is full."));

		IReadOnlyList<string> lines = await harness.LinesAsync(expected: 1);

		string line = lines.Single();

		line.ShouldContain("Photos: Could not write the thumbnail.");
		line.ShouldContain("InvalidOperationException");
		line.ShouldContain("The volume is full.");
	}

	/// <summary>
	/// <see cref="UnhandledExceptionLogger"/> records and declines. Returning true would silently
	/// take over the response for every caller - see the note on the type.
	/// </summary>
	[Fact]
	public async Task TheRequestHandler_RecordsTheFailureAndLeavesTheResponseAlone()
	{
		using Harness harness = Build();

		UnhandledExceptionLogger handler = new(harness.Events);

		DefaultHttpContext context = new();

		context.Request.Method = "POST";
		context.Request.Path = "/api/v1/rides/42/join";
		context.TraceIdentifier = "0HN7ABC:00000003";

		bool handled = await handler.TryHandleAsync(
			context,
			new TimeoutException("The database did not answer."),
			CancellationToken.None);

		handled.ShouldBeFalse("the existing /Error handler owns the response");

		string line = (await harness.LinesAsync(expected: 1)).Single();

		line.ShouldContain("unhandled POST /api/v1/rides/42/join");
		line.ShouldContain("anonymous");
		line.ShouldContain("0HN7ABC:00000003");
		line.ShouldContain("TimeoutException");
	}

	/// <summary>
	/// The query string is deliberately absent: it carries tokens, share codes and whatever a rider
	/// typed into a search box, and none of that belongs in a file kept for a fortnight.
	/// </summary>
	[Fact]
	public async Task TheRequestHandler_DoesNotRecordTheQueryString()
	{
		using Harness harness = Build();

		UnhandledExceptionLogger handler = new(harness.Events);

		DefaultHttpContext context = new();

		context.Request.Path = "/api/v1/tracks";
		context.Request.QueryString = new QueryString("?token=sekrit-value");

		await handler.TryHandleAsync(context, new InvalidOperationException("No."), CancellationToken.None);

		string line = (await harness.LinesAsync(expected: 1)).Single();

		line.ShouldNotContain("sekrit-value");
	}

	private Harness Build()
	{
		FakeTimeProvider clock = new(Start);

		FileLogOptions settings = new()
		{
			Enabled = true,
			Directory = _directory,
			MinimumLevel = LogLevel.Information,
		};

		FileLoggerProvider provider = new(Options.Create(settings), clock);

		// A real factory holding the real provider, so what the test reads back is what the file
		// actually received rather than what a fake was told.
		ILoggerFactory factory = LoggerFactory.Create(logging =>
		{
			logging.SetMinimumLevel(LogLevel.Trace);
			logging.AddProvider(provider);
		});

		ServerEvents events = new(factory.CreateLogger<ServerEvents>());

		IHostApplicationLifetime lifetime = Substitute.For<IHostApplicationLifetime>();

		lifetime.ApplicationStarted.Returns(_started.Token);
		lifetime.ApplicationStopping.Returns(_stopping.Token);
		lifetime.ApplicationStopped.Returns(_stopped.Token);

		ServerLifetimeLog log = new(lifetime, provider, events, new ServerStart(clock));

		return new Harness(log, events, provider, factory, new ServerLogReader(provider, Options.Create(settings)), clock, _started);
	}

	/// <summary>Everything one test needs, disposed in the order the host would dispose it.</summary>
	private sealed record Harness(
		ServerLifetimeLog Log,
		ServerEvents Events,
		FileLoggerProvider Provider,
		ILoggerFactory Factory,
		ServerLogReader Reader,
		FakeTimeProvider Clock,
		CancellationTokenSource Started) : IDisposable
	{
		public void Dispose()
		{
			Log.Dispose();
			Factory.Dispose();
			Provider.Dispose();
		}

		/// <summary>Fires <c>ApplicationStarted</c> the way the host does.</summary>
		public Task RaiseStartedAsync() => Started.CancelAsync();

		/// <summary>The file's lines, once the writer has caught up. See <see cref="LogFile"/>.</summary>
		/// <param name="expected">How many entries to wait for.</param>
		/// <returns>Their messages, newest first.</returns>
		public async Task<IReadOnlyList<string>> LinesAsync(int expected)
		{
			AdminLogPage page = await LogFile.ReadWhenWrittenAsync(Reader, expected);

			return [.. page.Entries.Select(entry => entry.Message)];
		}
	}
}
