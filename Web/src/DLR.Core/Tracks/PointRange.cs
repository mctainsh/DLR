namespace DLR.Core.Tracks;

/// <summary>
/// A half-open span of raw point indices, <c>[From, To)</c> (§15.5).
/// <para>
/// <strong>Raw</strong> is the load-bearing word. Indices address the full-resolution track,
/// never the simplified polyline the map draws. A browser sending "delete the 412th point I am
/// displaying" would delete a different point on the server — plausibly hundreds of metres
/// away, invisibly, and only on tracks dense enough for simplification to have done anything.
/// One index space, no mapping layer, no class of bug.
/// </para>
/// </summary>
/// <param name="From">First index removed.</param>
/// <param name="To">First index <em>kept</em> after the span.</param>
public readonly record struct PointRange(int From, int To)
{
	/// <summary>How many points the range covers.</summary>
	public int Length => To - From;

	/// <summary>Whether the range covers at least one point.</summary>
	public bool IsEmpty => To <= From;

	/// <summary>Whether an index falls inside the range.</summary>
	/// <param name="index">The index to test.</param>
	public bool Contains(int index) => index >= From && index < To;

	/// <inheritdoc />
	public override string ToString() => $"[{From}, {To})";
}
