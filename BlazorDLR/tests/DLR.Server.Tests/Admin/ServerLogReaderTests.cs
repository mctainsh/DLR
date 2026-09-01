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

	/// <summary>The day after <see cref="Start"/> - the file a roll at midnight opens.</summary>
	private static readonly DateOnly Tomorrow = DateOnly.FromDateTime(Start.UtcDateTime).AddDays(1);

	/// <summary>A startup block as <c>StartupBanner</c> builds one: ruled top and bottom.</summary>
	private static readonly string Banner =
		$"{StartupBanner.Rule}\n    Application       : Dumb Luck Routes 8.0.0.28\n{StartupBanner.Rule}";

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

	/// <summary>Waits for the writer, then reads. See <see cref="LogFile"/>.</summary>
	/// <param name="reader">The reader under test.</param>
	/// <param name="expected">How many entries to wait for.</param>
	/// <param name="day">Which day, or null for whatever is newest.</param>
	private static Task<AdminLogPage> ReadWhenWrittenAsync(
		ServerLogReader reader,
		int expected,
		DateOnly? day = null) =>
		LogFile.ReadWhenWrittenAsync(reader, expected, day);

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
	/// The writer is the only thing that puts a block in a file, so a run gets exactly one - at the
	/// top, ahead of the entry whose arrival opened the file.
	/// </summary>
	[Fact]
	public async Task AtStartup_TheBlockOpensTheFile_Once()
	{
		(FileLoggerProvider provider, ServerLogReader reader, _) = Build();

		using (provider)
		{
			provider.Header = () => Banner;

			ILogger logger = provider.CreateLogger("DLR.Server");

			// The ordering that actually happens: the framework is logging before Program reaches
			// the line that hands the writer its header.
			logger.LogInformation("User profile is available.");
			logger.LogInformation("Now listening on: http://localhost:5005");

			AdminLogPage page = await ReadWhenWrittenAsync(reader, 3);

			// Newest first, so the oldest entry - the top of the file - is the last of them.
			page.Entries.Count.ShouldBe(3);
			page.Entries[^1].Message.ShouldContain(StartupBanner.Rule);
			page.Entries.Count(entry => entry.Message.Contains(StartupBanner.Rule, StringComparison.Ordinal))
				.ShouldBe(1, "one producer, so there is nothing to write a second copy.");
		}
	}

	/// <summary>
	/// A restart writes its own block, into the same day's file, under the last run's.
	/// <para>
	/// Two blocks in one file are not a duplicate - they are where one run ended and the next
	/// began, which is the marker an administrator reads the file backwards from.
	/// </para>
	/// </summary>
	[Fact]
	public async Task ARestart_WritesItsOwnBlockIntoTheDaysExistingFile()
	{
		(FileLoggerProvider first, ServerLogReader reader, _) = Build();

		using (first)
		{
			first.Header = () => Banner;

			first.CreateLogger("DLR.Server").LogInformation("Now listening on: http://localhost:5005");

			await ReadWhenWrittenAsync(reader, 2);
		}

		// The process ends and comes back the same day: same directory, so the same file, appended.
		(FileLoggerProvider second, ServerLogReader reopened, _) = Build();

		using (second)
		{
			second.Header = () => Banner;

			second.CreateLogger("DLR.Server").LogInformation("Now listening on: http://localhost:5005");

			AdminLogPage page = await ReadWhenWrittenAsync(reopened, 4);

			page.Entries.Count(entry => entry.Message.Contains(StartupBanner.Rule, StringComparison.Ordinal))
				.ShouldBe(2, "every run says what it is, even into a file another run started.");
		}
	}

	/// <summary>
	/// Midnight rolls the file, and the new one opens with the startup block. Without it, every day
	/// after the one the server came up on begins midway through a sentence and cannot say what is
	/// running - which is the question the file is opened to answer.
	/// </summary>
	[Fact]
	public async Task WhenTheDayRolls_TheNewFileOpensWithTheHeader()
	{
		(FileLoggerProvider provider, ServerLogReader reader, FakeTimeProvider clock) = Build();

		using (provider)
		{
			provider.Header = () => Banner;

			ILogger logger = provider.CreateLogger("Test");

			logger.LogInformation("yesterday");

			// Waited for on purpose: the header is written as a file is opened, so the writer has to
			// have opened the first day's file before the clock moves, or there is no roll to test.
			await ReadWhenWrittenAsync(reader, 1);

			clock.Advance(TimeSpan.FromDays(1));

			logger.LogInformation("today");

			AdminLogPage page = await ReadWhenWrittenAsync(reader, 2, Tomorrow);

			// Newest first, so the header is the older of the two: it went in ahead of the entry
			// whose arrival opened the file, rather than behind it as a queued line would have.
			page.Day.ShouldBe(Tomorrow);
			page.Entries.Count.ShouldBe(2);
			page.Entries[0].Message.ShouldBe("today");
			page.Entries[1].Message.ShouldContain("Dumb Luck Routes 8.0.0.28");
			page.Entries[1].Level.ShouldBe("CRIT");
			page.Entries[1].Category.ShouldBe("DLR.Server.Diagnostics.StartupBanner");
		}
	}

	/// <summary>
	/// A header that throws is a banner that could not read a setting, at midnight, on a server that
	/// has been up for a week. It says so in the file rather than taking the writer down with it.
	/// </summary>
	[Fact]
	public async Task AHeaderThatThrows_LeavesANoteAndKeepsWriting()
	{
		(FileLoggerProvider provider, ServerLogReader reader, FakeTimeProvider clock) = Build();

		using (provider)
		{
			provider.Header = () => throw new InvalidOperationException("no idea what is running");

			ILogger logger = provider.CreateLogger("Test");

			logger.LogInformation("yesterday");

			await ReadWhenWrittenAsync(reader, 1);

			clock.Advance(TimeSpan.FromDays(1));

			logger.LogInformation("today");

			AdminLogPage page = await ReadWhenWrittenAsync(reader, 2, Tomorrow);

			page.Entries[0].Message.ShouldBe("today");
			page.Entries[1].Message.ShouldContain("no idea what is running");
			page.Problem.ShouldBeNull();
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
	/// A message carrying a tab must not be able to invent a column - the reader splits three times
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

			AdminLogPage page = await reader.ReadAsync(
				null, 100, "Warning", databaseCommands: true, CancellationToken.None);

			// Somebody filtering to warnings is looking for problems, and an error is a worse
			// problem than a warning. Excluding it would be the opposite of what was asked.
			page.Entries.Select(entry => entry.Message).ShouldBe(["an error", "a warning"]);
		}
	}

	/// <summary>
	/// The statement filter is applied while reading, so <c>take</c> is spent on lines the reader
	/// will actually be shown.
	/// <para>
	/// This is the whole reason it is not done on the screen. Here a cap of two over a file whose
	/// newest three lines are SQL comes back with the two ride lines beneath them; filtered after
	/// the fact it would have come back with nothing at all, and an administrator would have
	/// concluded the day was empty.
	/// </para>
	/// </summary>
	[Fact]
	public async Task StatementLines_AreSteppedOverWithoutSpendingTheCap()
	{
		(FileLoggerProvider provider, ServerLogReader reader, _) = Build();

		using (provider)
		{
			ILogger rides = provider.CreateLogger("DLR.Server.Rides.RideController");
			ILogger sql = provider.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command");

			rides.LogInformation("first ride");
			rides.LogInformation("second ride");
			sql.LogInformation("SELECT 1");
			sql.LogInformation("SELECT 2");
			sql.LogInformation("SELECT 3");

			await ReadWhenWrittenAsync(reader, 5);

			AdminLogPage page = await reader.ReadAsync(
				null, 2, null, databaseCommands: false, CancellationToken.None);

			page.Entries.Select(entry => entry.Message).ShouldBe(["second ride", "first ride"]);
			page.DatabaseCommandsHidden.ShouldBe(3);
		}
	}

	/// <summary>Asked for, they come back - and nothing is reported hidden.</summary>
	[Fact]
	public async Task StatementLines_ComeBackWhenTheyAreAskedFor()
	{
		(FileLoggerProvider provider, ServerLogReader reader, _) = Build();

		using (provider)
		{
			provider
				.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command")
				.LogInformation("SELECT 1");

			AdminLogPage page = await ReadWhenWrittenAsync(reader, 1);

			page.Entries.Single().Message.ShouldBe("SELECT 1");
			page.DatabaseCommandsHidden.ShouldBe(0);
		}
	}

	[Fact]
	public async Task ADayWithNoFile_IsAnEmptyPage_NotAFailure()
	{
		(FileLoggerProvider provider, ServerLogReader reader, _) = Build();

		using (provider)
		{
			AdminLogPage page = await reader.ReadAsync(
				new DateOnly(2020, 1, 1), 100, null, databaseCommands: true, CancellationToken.None);

			page.Entries.ShouldBeEmpty();
			page.Day.ShouldBe(new DateOnly(2020, 1, 1));
		}
	}

	/// <summary>
	/// A disabled provider must not create the directory or start a writer - a deployment that did
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
	/// The provider is registered twice - as itself and as <c>ILoggerProvider</c> - and the logging
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

	/// <summary>
	/// An empty page says why it is empty. Switched off is the one the screen used to assume, and
	/// it is only one of three.
	/// </summary>
	[Fact]
	public async Task WhenSwitchedOff_ThePageSaysSoAndNamesTheDirectory()
	{
		FileLogOptions settings = new() { Enabled = false, Directory = _directory };

		using FileLoggerProvider provider = new(Options.Create(settings), new FakeTimeProvider(Start));

		ServerLogReader reader = new(provider, Options.Create(settings));

		AdminLogPage page = await reader.ReadAsync(null, 100, null, databaseCommands: true, CancellationToken.None);

		page.Enabled.ShouldBeFalse();
		page.Problem.ShouldBeNull();
		page.Directory.ShouldBe(_directory);
	}

	/// <summary>A writer that is working reports no problem, so a stale complaint cannot linger.</summary>
	[Fact]
	public async Task WhenWriting_ThePageReportsNoProblem()
	{
		(FileLoggerProvider provider, ServerLogReader reader, _) = Build();

		using (provider)
		{
			provider.CreateLogger("DLR.Server").LogInformation("Up.");

			AdminLogPage page = await ReadWhenWrittenAsync(reader, 1);

			page.Enabled.ShouldBeTrue();
			page.Problem.ShouldBeNull();
			page.Directory.ShouldBe(_directory);
		}
	}

	/// <summary>
	/// The failure this exists for: switched on, but the directory cannot be made - which in a
	/// deployment is a service account without permission, and which used to be indistinguishable
	/// on screen from never having switched it on.
	/// </summary>
	[Fact]
	public async Task WhenTheDirectoryCannotBeCreated_ThePageSaysWhy()
	{
		// A file where the directory would go. Portable, and CreateDirectory fails on it the same
		// way a denied permission does - the provider swallows both and must report both.
		Directory.CreateDirectory(_directory);

		string blocked = Path.Combine(_directory, "blocked");

		await File.WriteAllTextAsync(blocked, "in the way");

		FileLogOptions settings = new() { Enabled = true, Directory = blocked };

		using FileLoggerProvider provider = new(Options.Create(settings), new FakeTimeProvider(Start));

		ServerLogReader reader = new(provider, Options.Create(settings));

		for (int attempt = 0; attempt < 100 && provider.Problem is null; attempt++)
		{
			await Task.Delay(20);
		}

		AdminLogPage page = await reader.ReadAsync(null, 100, null, databaseCommands: true, CancellationToken.None);

		page.Enabled.ShouldBeTrue();
		page.Entries.ShouldBeEmpty();
		page.Problem.ShouldNotBeNull().ShouldContain(blocked);
	}
}
