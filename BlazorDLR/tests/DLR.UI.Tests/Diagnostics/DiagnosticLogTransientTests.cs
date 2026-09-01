using BlazorDLR.Shared.Diagnostics;

namespace DLR.UI.Tests.Diagnostics;

/// <summary>
/// The replaceable tail line (<see cref="DiagnosticLog.WriteTransient"/>), which is how a counter
/// that moves every couple of seconds for the length of a ride occupies one line instead of
/// thousands.
/// <para>
/// The rule has two halves and both are here: another transient line overwrites it, and anything
/// else settles it where it stands - so a finished log holds the totals as they were at each thing
/// that happened, and the current totals at the end.
/// </para>
/// </summary>
[Collection(DiagnosticLogCollection.Name)]
public sealed class DiagnosticLogTransientTests
{
	/// <summary>
	/// The lines this test wrote, in order. Marked rather than assumed: the ring is shared, and a
	/// background task belonging to a suite that has already finished may still write into it.
	/// </summary>
	private static string[] Mine(string marker) =>
	[
		.. DiagnosticLog.Snapshot().Select(line => line.Text).Where(text => text.Contains(marker)),
	];

	[Fact]
	public void ATransientLine_IsOverwrittenByTheNext_RatherThanFollowedByIt()
	{
		string marker = $"probe-{Guid.NewGuid():N}";

		DiagnosticLog.WriteTransient($"{marker} 1 fix");
		DiagnosticLog.WriteTransient($"{marker} 2 fix");
		DiagnosticLog.WriteTransient($"{marker} 3 fix");

		Mine(marker).ShouldBe([$"{marker} 3 fix"]);
	}

	[Fact]
	public void AnOrdinaryLine_SettlesTheTransientBeforeIt()
	{
		// The half that makes the log worth reading afterwards: what survives is the totals as they
		// stood at each thing that got its own line.
		string marker = $"probe-{Guid.NewGuid():N}";

		DiagnosticLog.WriteTransient($"{marker} 1 fix");
		DiagnosticLog.WriteTransient($"{marker} 2 fix");
		DiagnosticLog.Write($"{marker} GPS: Broadcasting -> Off");
		DiagnosticLog.WriteTransient($"{marker} 3 fix");

		Mine(marker).ShouldBe([
			$"{marker} 2 fix",
			$"{marker} GPS: Broadcasting -> Off",
			$"{marker} 3 fix",
		]);
	}

	[Fact]
	public void TheFile_IsTruncatedBackOverTheTransientLine_NotGrownByOne()
	{
		// The file is the artifact a rider actually mails in, and it is a different code path from
		// the ring: the offset the last transient line began at has to be tracked, because a
		// rendered line's length is not its byte length once an entry carries a stack trace.
		string marker = $"probe-{Guid.NewGuid():N}";

		// One stable name rather than one per run: the sink has no undo, so the file goes on being
		// appended to for the rest of the process and a unique name would leave one behind each time.
		string path = Path.Combine(Path.GetTempPath(), "dlr-log-tests.txt");

		// UseFile is process-wide and has no undo, which is why this lives in a collection that
		// runs alone. Everything written after it goes to a temp file nothing reads.
		DiagnosticLog.UseFile(path);

		DiagnosticLog.WriteTransient($"{marker} 1 fix");
		DiagnosticLog.WriteError($"{marker} something threw", new InvalidOperationException("with a stack"));
		DiagnosticLog.WriteTransient($"{marker} 2 fix");
		DiagnosticLog.WriteTransient($"{marker} 3 fix");

		string[] written = [.. File.ReadAllLines(path).Where(line => line.Contains($"{marker} ") && line.Contains("fix"))];

		written.Length.ShouldBe(2, "the settled one and the current one, and not the two it replaced.");
		written[0].ShouldEndWith($"{marker} 1 fix");
		written[1].ShouldEndWith($"{marker} 3 fix");
	}
}
