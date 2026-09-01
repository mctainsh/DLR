using System.Globalization;
using System.Text;
using System.Xml;

namespace DLR.Core.Tracks;

/// <summary>One waypoint on its way out (§16.6).</summary>
/// <param name="Latitude">Degrees.</param>
/// <param name="Longitude">Degrees.</param>
/// <param name="Name">The marker title.</param>
/// <param name="Description">The marker note.</param>
/// <param name="Symbol">The GPX symbol name for the marker's icon.</param>
/// <param name="DirectionDeg">Written under the <c>dlr:</c> namespace, or omitted when null.</param>
public sealed record GpxWaypointOut(
	double Latitude,
	double Longitude,
	string Name,
	string? Description,
	string Symbol,
	short? DirectionDeg);

/// <summary>
/// The only GPX writer in the project - the mirror of <see cref="GpxReader"/> (§15.3, §16.6).
/// <para>
/// A file this writes and that reader reads produces the same markers, which is the test that says
/// the mapping is honest rather than merely present. Escaping is <see cref="XmlWriter"/>'s job and
/// is never done by string concatenation: title and note are user text, and the one place they
/// leave this system as markup is here.
/// </para>
/// </summary>
public static class GpxWriter
{
	/// <summary>The creator string written into the file.</summary>
	public const string Creator = "Dumb Luck Routes";

	private const string GpxNamespace = "http://www.topografix.com/GPX/1/1";

	/// <summary>Writes a track and its waypoints.</summary>
	/// <param name="name">The track name.</param>
	/// <param name="points">The geometry, in order.</param>
	/// <param name="waypoints">The markers.</param>
	/// <returns>The GPX document, UTF-8.</returns>
	public static string Write(
		string name,
		IReadOnlyList<TrackPoint> points,
		IReadOnlyList<GpxWaypointOut> waypoints)
	{
		// A plain StringWriter reports UTF-16, so XmlWriter emits
		// <?xml version="1.0" encoding="utf-16"?> - and the bytes this is then encoded into are
		// UTF-8, which makes the declaration a lie every strict reader rejects. Including ours.
		using Utf8StringWriter output = new();

		XmlWriterSettings settings = new()
		{
			Indent = true,
			IndentChars = "\t",
			Encoding = Encoding.UTF8,
		};

		using (XmlWriter writer = XmlWriter.Create(output, settings))
		{
			writer.WriteStartDocument();
			writer.WriteStartElement("gpx", GpxNamespace);
			writer.WriteAttributeString("version", "1.1");
			writer.WriteAttributeString("creator", Creator);
			writer.WriteAttributeString("xmlns", DlrGpx.Prefix, null, DlrGpx.Namespace);

			// Waypoints before the track, which is the order every reader expects and the order
			// the schema requires.
			foreach (GpxWaypointOut waypoint in waypoints)
			{
				WriteWaypoint(writer, waypoint);
			}

			writer.WriteStartElement("trk", GpxNamespace);
			writer.WriteElementString("name", GpxNamespace, name);
			writer.WriteStartElement("trkseg", GpxNamespace);

			foreach (TrackPoint point in points)
			{
				writer.WriteStartElement("trkpt", GpxNamespace);
				WriteCoordinates(writer, point.Latitude, point.Longitude);

				if (point.ElevationM is { } elevation)
				{
					writer.WriteElementString("ele", GpxNamespace, Number(elevation));
				}

				if (point.TimeUtc is { } timestamp)
				{
					writer.WriteElementString(
						"time",
						GpxNamespace,
						timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
				}

				writer.WriteEndElement();
			}

			writer.WriteEndElement();
			writer.WriteEndElement();
			writer.WriteEndElement();
			writer.WriteEndDocument();
		}

		return output.ToString();
	}

	private static void WriteWaypoint(XmlWriter writer, GpxWaypointOut waypoint)
	{
		writer.WriteStartElement("wpt", GpxNamespace);

		WriteCoordinates(writer, waypoint.Latitude, waypoint.Longitude);

		writer.WriteElementString("name", GpxNamespace, waypoint.Name);

		if (!string.IsNullOrWhiteSpace(waypoint.Description))
		{
			writer.WriteElementString("desc", GpxNamespace, waypoint.Description);
		}

		writer.WriteElementString("sym", GpxNamespace, waypoint.Symbol);

		// Omitted rather than written as zero when there is none - zero is due north (§16.2), so
		// writing it would invent a bearing the marker never had.
		if (waypoint.DirectionDeg is { } direction)
		{
			writer.WriteStartElement("extensions", GpxNamespace);
			writer.WriteElementString(
				DlrGpx.Prefix,
				"direction",
				DlrGpx.Namespace,
				direction.ToString(CultureInfo.InvariantCulture));
			writer.WriteEndElement();
		}

		writer.WriteEndElement();
	}

	private static void WriteCoordinates(XmlWriter writer, double latitude, double longitude)
	{
		writer.WriteAttributeString("lat", Number(latitude));
		writer.WriteAttributeString("lon", Number(longitude));
	}

	/// <summary>Invariant culture, always - a comma decimal separator is an unreadable file.</summary>
	private static string Number(double value) =>
		value.ToString("0.#######", CultureInfo.InvariantCulture);

	/// <summary>A <see cref="StringWriter"/> that admits to being UTF-8.</summary>
	private sealed class Utf8StringWriter : StringWriter
	{
		public Utf8StringWriter()
			: base(CultureInfo.InvariantCulture)
		{
		}

		public override Encoding Encoding => Encoding.UTF8;
	}
}
