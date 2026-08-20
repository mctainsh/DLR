using BlazorDLR.Shared.Diagnostics;

namespace DLR.UI.Tests;

/// <summary>
/// Records everything written to <see cref="DiagnosticLog"/> while it is alive, so a test can
/// assert on what was logged without reading the log itself.
/// <para>
/// <strong>Why not just call <c>DiagnosticLog.Snapshot()</c>.</strong> The log is a process-wide
/// static with a bounded ring, and the suite runs its collections in parallel: by the time a test
/// looks, another suite may have written past its entry — or called <c>Clear()</c> and dropped it
/// outright. Both are real; the second is a test suite of its own. Subscribing takes a copy as
/// each line is written, which nothing afterwards can take away.
/// </para>
/// <para>
/// Everything written by every thread lands here, so a test looks for its own entry by a marker
/// nothing else shares rather than expecting the log to itself.
/// </para>
/// </summary>
internal sealed class LogCapture : IDisposable
{
	private readonly List<string> _lines = [];
	private readonly HashSet<DiagnosticLog.LogLine> _seen = [];

	public LogCapture() => DiagnosticLog.Changed += OnChanged;

	/// <summary>Everything captured so far, newest last, one entry per line.</summary>
	/// <remarks>An entry may itself run to several lines — an exception is written with its stacks.</remarks>
	public string Text
	{
		get
		{
			lock (_lines)
			{
				return string.Join("\n", _lines);
			}
		}
	}

	public void Dispose() => DiagnosticLog.Changed -= OnChanged;

	/// <summary>
	/// Copies whatever is new. Walks the ring backwards and stops at the first line already held,
	/// so the usual cost is one entry however long the log is.
	/// </summary>
	private void OnChanged()
	{
		IReadOnlyList<DiagnosticLog.LogLine> snapshot = DiagnosticLog.Snapshot();

		lock (_lines)
		{
			int firstNew = snapshot.Count;
			while (firstNew > 0 && !_seen.Contains(snapshot[firstNew - 1]))
			{
				firstNew--;
			}

			for (int index = firstNew; index < snapshot.Count; index++)
			{
				_seen.Add(snapshot[index]);
				_lines.Add(snapshot[index].Text);
			}
		}
	}
}
