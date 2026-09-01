namespace BlazorDLR.Shared.Services;

/// <summary>
/// The two decisions behind a rider's marker on the live map (§5.3, §16.3): whether it is drawn
/// as an arrow or as a stopped dot, and how much of a long name the label carries.
/// <para>
/// Here rather than inside <c>SkiaMapOverlay</c> for the reason <see cref="MapGeometry"/> gives:
/// the overlay's <c>SKCanvasView</c> cannot render in bUnit, so anything with a rule in it is
/// worth extracting rather than eyeballing on a screenshot.
/// </para>
/// </summary>
public static class RiderMarker
{
	/// <summary>
	/// At or above this, in metres per second, a rider is travelling and gets the arrow.
	/// <para>
	/// 1 m/s is 3.6 km/h - walking pace, and comfortably above the drift a phone reports while
	/// standing at a junction. Positions travel as whole metres per second
	/// (<c>RiderPositionDto.SpeedMps</c> is a <c>short</c>), so every threshold between zero and
	/// one is the same threshold; this is the one that says what it means.
	/// </para>
	/// </summary>
	public const double MovingAtLeastMps = 0.5;

	/// <summary>
	/// Whether the marker points somewhere: the fix has to say the rider is moving <em>and</em>
	/// which way.
	/// <para>
	/// Both, not either. A heading with no speed is the compass reading of somebody parked at a
	/// café, and an arrow drawn from it sends whoever is looking for them down a road nobody is
	/// on; a speed with no heading has nothing to point along. Either way the answer is the dot,
	/// which claims only "here".
	/// </para>
	/// </summary>
	/// <param name="speedMps">Ground speed from the fix, or null when it carried none.</param>
	/// <param name="headingDeg">Degrees clockwise from true north, or null for "no direction" (§16.2).</param>
	/// <returns><c>true</c> when the arrow should be drawn.</returns>
	public static bool IsMoving(double? speedMps, double? headingDeg) =>
		headingDeg is not null && speedMps >= MovingAtLeastMps;

	/// <summary>
	/// The longest label the overlay will draw. Usernames are capped at 20 characters on the way
	/// in, so this only ever bites on a name from somewhere else - but a label is drawn to the
	/// right of the rider and an unbounded one is a banner across the map at the exact moment
	/// somebody is trying to read the road under it.
	/// </summary>
	public const int LabelMaxChars = 20;

	/// <summary>
	/// The label text for a rider: their name, cropped to <see cref="LabelMaxChars"/>, or a
	/// question mark when the position arrived before the member list did.
	/// </summary>
	/// <param name="name">The rider's username, or null / blank when it is not known yet.</param>
	/// <returns>Text that is never empty.</returns>
	public static string Label(string? name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return "?";
		}

		string trimmed = name.Trim();

		// One character of ellipsis in place of the last kept character, so a cropped label is
		// visibly cropped rather than looking like a different rider with a shorter name.
		return trimmed.Length <= LabelMaxChars
			? trimmed
			: string.Concat(trimmed.AsSpan(0, LabelMaxChars - 1), "…");
	}
}
