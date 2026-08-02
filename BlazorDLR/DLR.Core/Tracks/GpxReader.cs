using System.Globalization;
using System.Xml;

namespace DLR.Core.Tracks;

/// <summary>
/// The only GPX reader in the project (§15.3, §15.7).
/// <para>
/// The app parses with it offline, the server re-parses everything the app sends with it, and
/// the editor re-stats with it. One implementation is what makes
/// <c>Import_AppAndServerParsers_ProduceIdenticalTracks</c> a real guarantee rather than a
/// tautology — and what stops a track's ascent depending on which door it came in through.
/// </para>
/// <para>
/// This is the first user-supplied file format the project reads, and GPX is XML, so the
/// failure modes are the classic ones rather than anything cycling-specific.
/// </para>
/// </summary>
public static class GpxReader
{
	/// <summary>
	/// How the reader is configured, and every line of it is load-bearing (§15.3).
	/// </summary>
	private static XmlReaderSettings HardenedSettings() => new()
	{
		// XXE and billion-laughs, both, and neither is a theoretical attack against a service
		// that accepts files from strangers. Prohibit rather than Ignore: a document that
		// declares a DTD is refused rather than quietly stripped, because a caller sending one
		// is telling us something about what they expected to happen.
		DtdProcessing = DtdProcessing.Prohibit,

		// Nothing external ever resolves. With the line above this is belt and braces, and it
		// stays because the two are removed by different mistakes.
		XmlResolver = null,

		// Entity expansion is the other half of billion-laughs. Zero is not "a small budget",
		// it is none.
		MaxCharactersFromEntities = 0,

		IgnoreComments = true,
		IgnoreWhitespace = true,
		IgnoreProcessingInstructions = true,

		// Streaming. Not Async, because every caller here is synchronous over a buffered
		// request body and an async reader would add a state machine per element.
		CloseInput = false,
	};

	/// <summary>Reads a GPX file.</summary>
	/// <param name="stream">
	/// The file, read forwards and never buffered whole. Buffering first is what turns a 25 MB
	/// upload into several hundred megabytes of allocation on a 4 GB VPS.
	/// </param>
	/// <param name="limits">The caps to enforce; defaults to §15.8's.</param>
	/// <exception cref="GpxFormatException">The file is not usable, and the message says why.</exception>
	public static GpxDocument Read(Stream stream, GpxLimits? limits = null)
	{
		GpxLimits caps = limits ?? GpxLimits.Default;

		List<GpxTrack> tracks = [];
		List<GpxWaypoint> waypoints = [];

		bool sawGpxElement = false;
		bool truncated = false;
		int totalPoints = 0;

		using XmlReader reader = XmlReader.Create(stream, HardenedSettings());

		try
		{
			while (reader.Read())
			{
				if (reader.NodeType != XmlNodeType.Element)
				{
					continue;
				}

				switch (reader.LocalName)
				{
					case "gpx":
						sawGpxElement = true;

						break;

					case "trk" or "rte":
						if (tracks.Count >= caps.MaxTracksPerFile)
						{
							// Reported, not thrown. The preview lists what was read and says
							// what was left over, which is more use to somebody with a
							// twenty-one-track file than a refusal is.
							truncated = true;

							reader.Skip();

							continue;
						}

						tracks.Add(ReadTrack(reader, caps, ref totalPoints));

						break;

					case "wpt":
						waypoints.Add(ReadWaypoint(reader));

						break;
				}
			}
		}
		catch (XmlException exception)
		{
			throw Translate(exception);
		}

		if (!sawGpxElement)
		{
			throw new GpxFormatException(
				GpxProblem.NotGpx,
				"This is well-formed XML but not a GPX file: there is no <gpx> element.");
		}

		return new GpxDocument(tracks, waypoints, truncated);
	}

	private static GpxTrack ReadTrack(XmlReader reader, GpxLimits caps, ref int totalPoints)
	{
		bool isRoute = reader.LocalName == "rte";
		string element = reader.LocalName;

		string? name = null;
		List<TrackPoint> points = [];
		List<int> segmentStarts = [];

		if (reader.IsEmptyElement)
		{
			return new GpxTrack(null, new TrackGeometry(points), Source(isRoute));
		}

		int depth = reader.Depth;

		while (reader.Read())
		{
			if (reader.NodeType == XmlNodeType.EndElement
				&& reader.Depth == depth
				&& reader.LocalName == element)
			{
				break;
			}

			if (reader.NodeType != XmlNodeType.Element)
			{
				continue;
			}

			switch (reader.LocalName)
			{
				case "name" when name is null:
					name = ElementText(reader);

					break;

				// A pause or a signal gap. Recorded as a break so that nothing later draws a
				// straight line through a tunnel and calls it distance ridden (§15.3).
				case "trkseg" when points.Count > 0:
					segmentStarts.Add(points.Count);

					break;

				case "trkpt" or "rtept":
					if (++totalPoints > caps.MaxPointsPerFile)
					{
						throw new GpxFormatException(
							GpxProblem.TooManyPoints,
							$"This file holds more than {caps.MaxPointsPerFile:N0} points, " +
							"which is more than can be imported in one go.",
							Line(reader),
							Position(reader));
					}

					points.Add(ReadPoint(reader, isRoute));

					break;
			}
		}

		return new GpxTrack(name, new TrackGeometry(points, segmentStarts), Source(isRoute));
	}

	private static TrackPoint ReadPoint(XmlReader reader, bool isRoute)
	{
		double latitude = Coordinate(reader, "lat");
		double longitude = Coordinate(reader, "lon");

		TrackPoint point = new(latitude, longitude);

		if (!point.HasUsableCoordinates)
		{
			// Malformed rather than merely odd, so the file is refused rather than repaired.
			throw new GpxFormatException(
				GpxProblem.InvalidCoordinate,
				$"A point has coordinates outside the possible range: {latitude}, {longitude}.",
				Line(reader),
				Position(reader));
		}

		if (reader.IsEmptyElement)
		{
			return point;
		}

		double? elevation = null;
		DateTimeOffset? time = null;
		int depth = reader.Depth;

		while (reader.Read())
		{
			if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
			{
				break;
			}

			if (reader.NodeType != XmlNodeType.Element)
			{
				continue;
			}

			switch (reader.LocalName)
			{
				case "ele":
					elevation = ParseDouble(ElementText(reader));

					break;

				// A route has no time, whatever the file says: §15.3 imports <rte> as a track
				// without timestamps, and honouring a stray one would give a planned route a
				// duration and let it into "distance ridden this month" (§15.1).
				case "time" when !isRoute:
					time = ParseTime(ElementText(reader));

					break;
			}
		}

		return point with { ElevationM = elevation, TimeUtc = time };
	}

	private static GpxWaypoint ReadWaypoint(XmlReader reader)
	{
		double latitude = Coordinate(reader, "lat");
		double longitude = Coordinate(reader, "lon");

		if (!new TrackPoint(latitude, longitude).HasUsableCoordinates)
		{
			throw new GpxFormatException(
				GpxProblem.InvalidCoordinate,
				$"A waypoint has coordinates outside the possible range: {latitude}, {longitude}.",
				Line(reader),
				Position(reader));
		}

		string? name = null;
		string? description = null;
		string? symbol = null;
		short? direction = null;

		if (reader.IsEmptyElement)
		{
			return new GpxWaypoint(latitude, longitude, name, description, symbol, direction);
		}

		int depth = reader.Depth;

		while (reader.Read())
		{
			if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
			{
				break;
			}

			if (reader.NodeType != XmlNodeType.Element)
			{
				continue;
			}

			switch (reader.LocalName)
			{
				case "name":
					name = ElementText(reader);

					break;

				case "desc":
					description = ElementText(reader);

					break;

				case "cmt":
					// §16.6 maps <desc> *or* <cmt> onto the note. Whichever arrives first wins,
					// so a file carrying both does not lose the one it listed first.
					description ??= ElementText(reader);

					break;

				case "sym":
					symbol = ElementText(reader);

					break;

				case "direction":
					// Our own extension, read back (§16.6). Namespace-checked, because "direction"
					// is a plausible enough element name for another writer to have used it for
					// something else entirely.
					if (reader.NamespaceURI == DlrGpx.Namespace
						&& short.TryParse(
							ElementText(reader),
							NumberStyles.Integer,
							CultureInfo.InvariantCulture,
							out short parsed)
						&& parsed is >= 0 and <= 359)
					{
						direction = parsed;
					}

					break;

					// <link> is read past deliberately and never followed. A waypoint that names a
					// URL is still just text; fetching one would hand a file's author a request
					// from this server (§16.6).
			}
		}

		return new GpxWaypoint(latitude, longitude, name, description, symbol, direction);
	}

	private static double Coordinate(XmlReader reader, string attribute)
	{
		string? raw = reader.GetAttribute(attribute);

		if (raw is null)
		{
			throw new GpxFormatException(
				GpxProblem.InvalidCoordinate,
				$"A <{reader.LocalName}> is missing its {attribute} attribute.",
				Line(reader),
				Position(reader));
		}

		return ParseDouble(raw)
			?? throw new GpxFormatException(
				GpxProblem.InvalidCoordinate,
				$"'{raw}' is not a usable {attribute}.",
				Line(reader),
				Position(reader));
	}

	/// <summary>
	/// Invariant culture, always. A GPX file uses a full stop whatever the server's locale is,
	/// and parsing one on a machine set to a comma decimal separator is a bug that only appears
	/// on somebody else's laptop.
	/// </summary>
	/// <summary>
	/// The text of the element the reader is on, leaving it positioned on that element's
	/// <em>end</em> tag.
	/// <para>
	/// Deliberately not <c>ReadElementContentAsString</c>, which advances past the end tag —
	/// so a caller looping on <c>Read()</c> then skips the following sibling. That is a
	/// silent bug rather than a loud one: it reads every other child, which for a
	/// <c>&lt;trkpt&gt;</c> means the elevation arrives and the timestamp does not.
	/// </para>
	/// </summary>
	private static string? ElementText(XmlReader reader)
	{
		if (reader.IsEmptyElement)
		{
			return null;
		}

		string? text = null;
		int depth = reader.Depth;

		while (reader.Read())
		{
			if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
			{
				break;
			}

			if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
			{
				text += reader.Value;
			}
		}

		return text;
	}

	private static double? ParseDouble(string? raw) =>
		double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
			? value
			: null;

	private static DateTimeOffset? ParseTime(string? raw) =>
		DateTimeOffset.TryParse(
			raw,
			CultureInfo.InvariantCulture,
			DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
			out DateTimeOffset value)
			? value
			: null;

	private static GpxTrackSource Source(bool isRoute) =>
		isRoute ? GpxTrackSource.Route : GpxTrackSource.Track;

	private static int? Line(XmlReader reader) =>
		reader is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : null;

	private static int? Position(XmlReader reader) =>
		reader is IXmlLineInfo info && info.HasLineInfo() ? info.LinePosition : null;

	/// <summary>
	/// Turns the parser's own failure into one that names the problem. The DTD case is
	/// separated because it is a refusal rather than a malformed file, and the caller may want
	/// to say so differently.
	/// </summary>
	private static GpxFormatException Translate(XmlException exception)
	{
		bool isDtd = exception.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase);

		bool isTruncated =
			exception.Message.Contains("Unexpected end of file", StringComparison.OrdinalIgnoreCase)
			|| exception.Message.Contains("unclosed", StringComparison.OrdinalIgnoreCase);

		(GpxProblem problem, string message) = isDtd
			? (GpxProblem.DtdNotAllowed,
				"This file declares a document type (DTD), which is not accepted. GPX does not " +
				"need one, and processing it would allow a file to reference things outside itself.")
			: isTruncated
				? (GpxProblem.Truncated,
					"This file ends in the middle of an element — it looks truncated. Check the " +
					"upload completed, then try again.")
				: (GpxProblem.NotXml, $"This file is not valid XML: {exception.Message}");

		return new GpxFormatException(
			problem,
			message,
			exception.LineNumber == 0 ? null : exception.LineNumber,
			exception.LinePosition == 0 ? null : exception.LinePosition,
			exception);
	}
}
