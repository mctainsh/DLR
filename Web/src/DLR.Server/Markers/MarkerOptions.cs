namespace DLR.Server.Markers;

/// <summary>The §16.5 caps, as settings (§14.5).</summary>
public sealed class MarkerOptions
{
	/// <summary>Configuration section name.</summary>
	public const string Section = "Markers";

	/// <summary>How many markers one track may carry.</summary>
	public int MaxPerTrack { get; set; } = 200;

	/// <summary>How many one ride may carry, across everybody.</summary>
	public int MaxPerGroupRide { get; set; } = 500;

	/// <summary>
	/// How many one member may add to one ride.
	/// <para>
	/// The per-ride cap alone would let one enthusiastic member use the whole allowance, so the
	/// two caps do different jobs: one bounds the ride, the other keeps it civil.
	/// </para>
	/// </summary>
	public int MaxPerMemberPerRide { get; set; } = 50;

	/// <summary>Title length. A rendering constraint before it is a database one (§16.2).</summary>
	public int TitleMaxChars { get; set; } = 40;

	/// <summary>Note length.</summary>
	public int NoteMaxChars { get; set; } = 500;

	/// <summary>Creations per hour per user.</summary>
	public int CreatesPerHourPerUser { get; set; } = 60;
}
