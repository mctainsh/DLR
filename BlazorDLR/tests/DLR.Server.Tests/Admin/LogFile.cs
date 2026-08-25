using DLR.Core.Contracts.Admin;
using DLR.Server.Diagnostics;

namespace DLR.Server.Tests.Admin;

/// <summary>
/// Waiting on the log writer, for every test that reads back what it wrote (§14.6).
/// <para>
/// Shared because the writer drains on its own thread, so a test has to wait for a line rather
/// than assume it — and a polling loop copied per test class is a loop that only some of the
/// copies get fixed when the reader's signature or the drain timing moves.
/// </para>
/// </summary>
internal static class LogFile
{
	/// <summary>How long to wait, in total, for the writer to catch up.</summary>
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(2);

	private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(20);

	/// <summary>
	/// Reads the day once <paramref name="expected"/> entries have arrived, or once patience runs
	/// out — a line that never comes fails the assertion that follows rather than hanging.
	/// </summary>
	/// <param name="reader">The reader under test.</param>
	/// <param name="expected">How many entries to wait for.</param>
	/// <param name="day">
	/// Which day to read, or null for whatever is newest. A test about the midnight roll has to
	/// name it: "newest" is satisfied by the day before the roll, which already has its lines.
	/// </param>
	/// <returns>The page as it read at the moment the wait ended.</returns>
	public static async Task<AdminLogPage> ReadWhenWrittenAsync(
		ServerLogReader reader,
		int expected,
		DateOnly? day = null)
	{
		for (TimeSpan waited = TimeSpan.Zero; ; waited += Interval)
		{
			AdminLogPage page = await reader.ReadAsync(day, 100, null, databaseCommands: true, CancellationToken.None);

			if (page.Entries.Count >= expected || waited >= Patience)
			{
				return page;
			}

			await Task.Delay(Interval);
		}
	}
}
