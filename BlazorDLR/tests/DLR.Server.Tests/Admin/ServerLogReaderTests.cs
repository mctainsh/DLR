using DLR.Core.Contracts.Admin;
using DLR.Server.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DLR.Server.Tests.Admin;

/// <summary>
/// Writing the log file and reading it back (§14.6).
/// <para>
/// Round trips rather than assertions about the format: the writer folds newlines out of a stack
/// trace and the reader folds them back, and the only thing worth pinning is that what an
/// administrator sees is what was logged.
/// </para>
/// </summary>
public sealed class ServerLogReaderTests : IDisposable
{
	private static readonly DateTimeOffset Start = new(2026, 3, 4, 9, 30, 0, TimeSpan.Zero);

	private readonly string _directory =
		Path.Combine(Path.GetTempPath(), $"dlr-log-tests-{Guid.NewGuid():N}");

	public void Dispose()
	{
		if (Directory.Exists(_directory))
		{
			Directory.Delete(_directory, recursive: true);
		}
	}

	private (FileLoggerProvider Provider, ServerLogReader Reader, FakeTimeProvider Clock) Build(
		LogLevel minimum = LogLevel.Information)
	{
		FakeTimeProvider clock = new(Start);

		FileLogOptions settings = new()
		{
			Enabled = true,
			Directory = _directory,
			MinimumLevel = minimum,
		};

		FileLoggerProvider provider = new(Options.Create(settings), clock);

		return (provider, new ServerLogReader(provider, Options.Create(settings)), clock);
	}

	/// <summary>
	/// The writer drains on its own thread, so a test has to wait for the line rather than assume
	/// it. Bounded, and a failure to appear fails the assertion that follows rather than hanging.
	/// </summary>
	private async Task<AdminLogPage> ReadWhenWrittenAsync(ServerLogReader reader, int expected)
	{
		for (int attempt = 0; attempt < 100; attempt++)
		{
			AdminLogPage page = await reader.ReadAsync(null, 100, null, CancellationToken.None);

			if (page.Entries.Count >= expected)
			{
				return page;
			}

			await Task.Delay(20);
		}

		return await reader.ReadAsync(null, 100, null, CancellationToken.None);
	}

	[Fact]
	public async Task AWrittenEntry_ComesBackWithItsLevelCategoryAndMessage()
	{
		(FileLoggerProvider provider, ServerLogReader reader, _) = Build();

		using (provider)
		{
			ILogger logger = provider.CreateLogger("DLR.Server.Rides.RideController");

			logger.LogWarning("A ride went missing.");

			AdminLogPage page = await ReadWhenWrittenAsync(reader, 1);

			AdminLogEntry entry = page.Entries.Single();

			entry.Level.ShouldBe("WARN");
			entry.Category.ShouldBe("DLR.Server.Rides.RideController");
			entry.Message.ShouldBe("A ride went missing.");
			entry.Utc.ShouldBe(Start);
		}
	}

	[Fact]
	public async Task Entries_ComeBackNewestFirst()
	{
		(FileLoggerProvider provider, ServerLogReader reader, FakeTimeProvider clock) = Build();

		using (provider)
		{
			ILogger logger = provider.CreateLogger("Test");

			logger.LogInformation("first");
			clock.Advance(TimeSpan.FromSeconds(1));
			logger.LogInformation("second");
			clock.Advance(TimeSpan.FromSeconds(1));
			logger.LogInformation("third");

			AdminLogPage page = await ReadWhenWrittenAsync(reader, 3);

			// A log is read from the end, which is the whole reason the reader seeks backwards
			// rather than reading the file forwards and reversing it.
			page.Entries.Select(entry => entry.Message).ShouldBe(["third", "second", "first"]);
		}
	}

	/// <summary>
	/// A stack trace is written flattened so it cannot forge a record separator, and unflattened
	/// on the way out so it is readable. Both halves, or the file is unparseable or the screen is.
	/// </summary>
	[Fact]
	public async Task AnExceptionSurvivesTheRoundTrip_AsOneEntryWithItsNewlinesBack()
	{
		(FileLoggerProvider provider, ServerLogReader reader, _) = Build();

		using (provider)
		{
			ILogger logger = provider.CreateLogger("Test");

			logger.LogError(new InvalidOperationException("it broke"), "Could not finish.");

			AdminLogPage page = await ReadWhenWrittenAsync(reader, 1);

			AdminLogEntry entry = page.Entries.Single();

			entry.Level.ShouldBe("ERROR");
			entry.Message.ShouldContain("Could not finish.");
			entry.Message.ShouldContain("it broke");
			entry.Message.ShouldNotContain("¶", customMessage:
				"the folded newline is an on-disk detail and must not reach the screen.");
		}
	}

	/// <summary>
	/// A message carrying a tab must not be able to invent a column — the reader splits three times
	/// and takes the rest verbatim, and the writer replaces tabs so that stays true.
	/// </summary>
	[Fact]
	public async Task AMessageContainingTabs_CannotForgeTheColumns()
	{
		(FileLoggerProvider provider, ServerLogReader reader, _) = Build();

		using (provider)
		{
			ILogger logger = provider.CreateLogger("Test");

			logger.LogInformation("SELECT\t1\tFROM\tusers");

			AdminLogPage page = await ReadWhenWrittenAsync(reader, 1);

			AdminLogEntry entry = page.Entries.Single();

			entry.Category.ShouldBe("Test");
			entry.Message.ShouldBe("SELECT 1 FROM users");
		}
	}

	[Fact]
	public async Task TheLevelFilter_IsAFloor_NotAnExactMatch()
	{
		(FileLoggerProvider provider, ServerLogReader reader, _) = Build(LogLevel.Debug);

		using (provider)
		{
			ILogger logger = provider.CreateLogger("Test");

			logger.LogDebug("chatter");
			logger.LogWarning("a warning");
			logger.LogError("an error");

			await ReadWhenWrittenAsync(reader, 3);

			AdminLogPage page = await reader.ReadAsync(null, 100, "Warning", CancellationToken.None);

			// Somebody filtering to warnings is looking for problems, and an error is a worse
			// problem than a warning. Excluding it would be the opposite of what was asked.
			page.Entries.Select(entry => entry.Message).ShouldBe(["an error", "a warning"]);
		}
	}

	[Fact]
	public async Task ADayWithNoFile_IsAnEmptyPage_NotAFailure()
	{
		(FileLoggerProvider provider, ServerLogReader reader, _) = Build();

		using (provider)
		{
			AdminLogPage page = await reader.ReadAsync(
				new DateOnly(2020, 1, 1), 100, null, CancellationToken.None);

			page.Entries.ShouldBeEmpty();
			page.Day.ShouldBe(new DateOnly(2020, 1, 1));
		}
	}

	/// <summary>
	/// A disabled provider must not create the directory or start a writer — a deployment that did
	/// not ask for a log file should not be able to tell the provider is registered.
	/// </summary>
	[Fact]
	public void WhenDisabled_NothingIsWrittenAndNoDirectoryAppears()
	{
		FileLogOptions settings = new() { Enabled = false, Directory = _directory };

		using FileLoggerProvider provider = new(Options.Create(settings), new FakeTimeProvider(Start));

		ILogger logger = provider.CreateLogger("Test");

		logger.IsEnabled(LogLevel.Error).ShouldBeFalse();

		logger.LogError("this goes nowhere");

		Directory.Exists(_directory).ShouldBeFalse();
	}

	/// <summary>
	/// The provider is registered twice — as itself and as <c>ILoggerProvider</c> — and the logging
	/// factory and the container each dispose what they own. The second call must be a no-op, or
	/// host shutdown throws.
	/// </summary>
	[Fact]
	public void DisposingTwice_IsHarmless()
	{
		(FileLoggerProvider provider, _, _) = Build();

		provider.Dispose();

		Should.NotThrow(provider.Dispose);
	}
}
