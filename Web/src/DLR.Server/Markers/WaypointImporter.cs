using DLR.Core.Contracts.Rides;
using DLR.Core.Markers;
using DLR.Core.Tracks;
using DLR.Server.Data;
using DLR.Server.Data.Markers;

namespace DLR.Server.Markers;

/// <summary>
/// Turning GPX waypoints into markers (§16.6).
/// <para>
/// v0.12's "waypoints are ignored" rule is retired here. §15.3 dropped <c>&lt;wpt&gt;</c> because
/// nothing in the model could hold one; markers are exactly what they are.
/// </para>
/// </summary>
public static class WaypointImporter
{
	/// <summary>Stages a marker per waypoint against a freshly imported track.</summary>
	/// <param name="database">The context to add to; the caller saves.</param>
	/// <param name="waypoints">What the file carried.</param>
	/// <param name="trackId">The track they annotate.</param>
	/// <param name="ownerId">Whose track it is.</param>
	/// <param name="limits">The caps and field lengths.</param>
	/// <param name="now">Creation time.</param>
	/// <returns>How many were staged.</returns>
	public static int Stage(
		DlrDbContext database,
		IReadOnlyList<GpxWaypoint> waypoints,
		Guid trackId,
		Guid ownerId,
		MarkerOptions limits,
		DateTimeOffset now)
	{
		int staged = 0;

		foreach (GpxWaypoint waypoint in waypoints)
		{
			// The per-track cap applies to an import exactly as it does to a tap. A file with
			// nine hundred waypoints is not a reason to make an exception to the number that
			// keeps a map readable.
			if (staged >= limits.MaxPerTrack)
			{
				break;
			}

			// A name longer than the title column is split rather than truncated: the overflow
			// goes on the front of the note, because discarding text somebody typed is damaging
			// the file rather than importing it (§16.6).
			(string title, string? overflow) = MarkerText.SplitTitle(waypoint.Name, limits.TitleMaxChars);

			string? note = Combine(overflow, MarkerText.Clean(waypoint.Description), limits.NoteMaxChars);

			database.Add(new Marker
			{
				Id = Guid.NewGuid(),
				TrackId = trackId,
				CreatedByUserId = ownerId,
				Lat = PositionScale.FromDegrees(waypoint.Latitude),
				Lon = PositionScale.FromDegrees(waypoint.Longitude),
				DirectionDeg = waypoint.DirectionDeg,
				Icon = MarkerIcons.ForSymbol(waypoint.Symbol),
				Title = title,
				Note = note,
				CreatedUtc = now,
				UpdatedUtc = now,
			});

			staged++;
		}

		return staged;
	}

	private static string? Combine(string? overflow, string? description, int noteMaxChars)
	{
		string combined = (overflow, description) switch
		{
			(null, null) => string.Empty,
			(null, { } only) => only,
			({ } only, null) => only,
			var (first, second) => $"{first}\n{second}",
		};

		if (combined.Length == 0)
		{
			return null;
		}

		return combined.Length <= noteMaxChars ? combined : combined[..noteMaxChars];
	}
}
