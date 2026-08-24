using System.Globalization;
using System.Text;
using DLR.Core.Contracts.Admin;
using Microsoft.Extensions.Options;

namespace DLR.Server.Diagnostics;

/// <summary>
/// Reads the tail of a day's log file back for the administration screen (§14.6).
/// <para>
/// <strong>No path ever comes off the wire.</strong> The caller names a <em>date</em>; this builds
/// the filename from it through <see cref="FileLoggerProvider.FileFor"/> and refuses anything that
/// does not resolve inside the configured directory. A <c>file</c> parameter — even one that
/// looked safe — would make an endpoint that returns the contents of an arbitrary file to whoever
/// the admin roster happens to contain, and the roster is a list of usernames in a config file.
/// </para>
/// <para>
/// The tail, not the head, and read from the end: the file is append-only and the interesting
/// entries are the newest, so reading it forwards would mean holding a day of logging in memory to
/// throw nearly all of it away.
/// </para>
/// </summary>
/// <param name="provider">Owns the resolved directory — the only directory this will open.</param>
/// <param name="options">The read cap.</param>
public sealed class ServerLogReader(FileLoggerProvider provider, IOptions<FileLogOptions> options)
{
	/// <summary>
	/// The newest <paramref name="take"/> entries of a day's log, newest first.
	/// </summary>
	/// <param name="day">Which day, in UTC. Null reads the newest day a file exists for.</param>
	/// <param name="take">How many lines, clamped to <see cref="FileLogOptions.MaxLinesPerRead"/>.</param>
	/// <param name="minimum">Drop entries below this level, or null for all of them.</param>
	/// <param name="cancellationToken">Abandons the read.</param>
	/// <returns>The page, empty when that day has no file.</returns>
	public async Task<AdminLogPage> ReadAsync(
		DateOnly? day,
		int take,
		string? minimum,
		CancellationToken cancellationToken)
	{
		IReadOnlyList<DateOnly> days = AvailableDays();

		// Null means "whatever is newest", which is what the screen opens on. A day the caller
		// named that has no file is not an error — it is an empty page for that day, which is the
		// honest answer and keeps the picker from having to be right about the past.
		DateOnly target = day ?? (days.Count > 0 ? days[0] : DateOnly.FromDateTime(provider.Now.UtcDateTime));

		int limit = Math.Clamp(take, 1, options.Value.MaxLinesPerRead);
		string path = FileLoggerProvider.FileFor(provider.Directory, target);

		if (!IsInsideLogDirectory(path) || !File.Exists(path))
		{
			return new AdminLogPage([], target, days, Truncated: false);
		}

		List<AdminLogEntry> entries = new(limit);
		bool truncated = false;

		// Ranked once for the read rather than per line: Rank trims and upper-cases, and the floor is
		// the same string for every one of the (up to) MaxLinesPerRead lines about to be parsed.
		int floor = minimum is { Length: > 0 } ? Rank(minimum) : int.MinValue;

		await foreach (string line in TailAsync(path, cancellationToken))
		{
			AdminLogEntry entry = Parse(line);

			if (Rank(entry.Level) < floor)
			{
				continue;
			}

			if (entries.Count == limit)
			{
				// One line past the cap is enough to know there is more; reading the rest of the
				// file to count it would be the exact cost the cap exists to avoid.
				truncated = true;

				break;
			}

			entries.Add(entry);
		}

		return new AdminLogPage(entries, target, days, truncated);
	}

	/// <summary>Every day the directory currently holds a file for, newest first.</summary>
	/// <returns>The picker's options. Empty when logging to file is off or nothing has been written.</returns>
	public IReadOnlyList<DateOnly> AvailableDays()
	{
		if (!Directory.Exists(provider.Directory))
		{
			return [];
		}

		List<DateOnly> days = [];

		foreach (string path in Directory.EnumerateFiles(provider.Directory, "dlr-*.log"))
		{
			string name = Path.GetFileNameWithoutExtension(path);

			// Parsed from the name rather than trusted: a file dropped into the directory by hand
			// is skipped rather than offered as a day that cannot then be opened.
			if (name.Length == "dlr-yyyyMMdd".Length
				&& DateOnly.TryParseExact(
					name["dlr-".Length..],
					"yyyyMMdd",
					CultureInfo.InvariantCulture,
					DateTimeStyles.None,
					out DateOnly day))
			{
				days.Add(day);
			}
		}

		days.Sort(static (left, right) => right.CompareTo(left));

		return days;
	}

	/// <summary>
	/// Deletes every daily file older than <paramref name="cutoff"/> (§14.6).
	/// </summary>
	/// <param name="cutoff">The oldest day to keep. Files for earlier days go.</param>
	/// <param name="dryRun">Counts what would be deleted without deleting it.</param>
	/// <returns>How many files were deleted, or would have been.</returns>
	/// <remarks>
	/// Here rather than in the nightly job, so the <c>dlr-yyyyMMdd.log</c> naming is known to the one
	/// component that owns it. A sweep that composed the name itself would keep working after a
	/// change to the format by silently matching nothing — reporting zero deleted forever while the
	/// disk filled, which is the outcome retention exists to prevent.
	/// <para>
	/// Only files this reader would have offered to read are candidates: something else left in the
	/// directory is not this job's to remove. A file held open or newly unreadable is skipped rather
	/// than thrown out of — the next run tries it again.
	/// </para>
	/// </remarks>
	public int Prune(DateOnly cutoff, bool dryRun)
	{
		int deleted = 0;

		foreach (DateOnly day in AvailableDays().Where(day => day < cutoff))
		{
			if (dryRun)
			{
				deleted++;

				continue;
			}

			try
			{
				File.Delete(FileLoggerProvider.FileFor(provider.Directory, day));
				deleted++;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// A file still held open, or a permission that changed under us.
			}
		}

		return deleted;
	}

	/// <summary>
	/// Whether a resolved path really sits in the log directory.
	/// <para>
	/// Belt and braces — the only caller builds the name from a <see cref="DateOnly"/>, which
	/// cannot carry a separator. It is here so that the guarantee survives somebody later adding an
	/// overload that takes a string.
	/// </para>
	/// </summary>
	private bool IsInsideLogDirectory(string path)
	{
		string root = Path.GetFullPath(provider.Directory);
		string full = Path.GetFullPath(path);

		return full.StartsWith(
			root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar,
			StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// The file's lines, last one first, without reading the whole file.
	/// <para>
	/// Reads fixed blocks backwards from the end and splits them, so the cost is the size of what
	/// was asked for rather than the size of the day. <c>FileShare.ReadWrite</c> because the writer
	/// has the same file open and appending — a reader that locked it would silence the log while
	/// somebody was reading it.
	/// </para>
	/// </summary>
	private static async IAsyncEnumerable<string> TailAsync(
		string path,
		[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
	{
		const int BlockSize = 64 * 1024;

		await using FileStream stream = new(
			path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BlockSize, useAsync: true);

		long position = stream.Length;
		byte[] block = new byte[BlockSize];

		// Whatever was at the front of the last block read and did not end in a newline — the tail
		// of a line whose beginning is in the block before it.
		string remainder = string.Empty;

		while (position > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();

			int size = (int)Math.Min(BlockSize, position);
			position -= size;

			stream.Seek(position, SeekOrigin.Begin);
			await stream.ReadExactlyAsync(block.AsMemory(0, size), cancellationToken);

			string text = Encoding.UTF8.GetString(block, 0, size) + remainder;
			string[] lines = text.Split('\n');

			// The first element is only a whole line if this block started at the file's beginning;
			// otherwise it is carried into the next read.
			remainder = position > 0 ? lines[0] : string.Empty;

			for (int index = lines.Length - 1; index >= (position > 0 ? 1 : 0); index--)
			{
				string line = lines[index].TrimEnd('\r');

				if (line.Length > 0)
				{
					yield return line;
				}
			}
		}
	}

	/// <summary>
	/// Splits a written line back into its fields — three separators, then the rest verbatim.
	/// </summary>
	/// <param name="line">One line as <see cref="FileLogger"/> wrote it.</param>
	/// <returns>The parsed entry, or the whole line as the message when it is not one of ours.</returns>
	private static AdminLogEntry Parse(string line)
	{
		string[] parts = line.Split('\t', 4);

		if (parts.Length < 4
			|| !DateTimeOffset.TryParse(
				parts[0],
				CultureInfo.InvariantCulture,
				DateTimeStyles.RoundtripKind,
				out DateTimeOffset stamp))
		{
			// Not a line this provider wrote — a crash dump, or something else appending to the
			// same file. It is still shown, because a log that hides what it did not recognise is
			// hiding the entries most worth seeing.
			return new AdminLogEntry(null, string.Empty, string.Empty, Unflatten(line));
		}

		return new AdminLogEntry(stamp, parts[1].Trim(), parts[2], Unflatten(parts[3]));
	}

	/// <summary>Puts the newlines back that <see cref="FileLogger"/> folded out, for display.</summary>
	private static string Unflatten(string text) => text.Replace('¶', '\n');

	/// <summary>
	/// Severity order, by prefix so that both the file's fixed-width names and the framework's own
	/// spelling match — "INFO", "INFO " and "Information" are one level.
	/// </summary>
	private static int Rank(string level) => level.Trim().ToUpperInvariant() switch
	{
		"TRACE" => 0,
		"DEBUG" => 1,
		"INFO" or "INFORMATION" => 2,
		"WARN" or "WARNING" => 3,
		"ERROR" => 4,
		"CRIT" or "CRITICAL" => 5,

		// An unrecognised level sorts above everything, so a filter never hides a line whose
		// severity could not be established.
		_ => int.MaxValue,
	};
}
