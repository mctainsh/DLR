using System.Globalization;
using System.Text;

namespace DLR.TestSupport.Tracks;

/// <summary>
/// GPX files built in code, both the ordinary and the hostile (§15.3, §14.2).
/// <para>
/// <strong>Generated, never recorded.</strong> A real trace starts at somebody's house and ends
/// there, and this repository is public - so there are no captured files here and there is no
/// procedure for adding one. It is also better for testing: a tunnel gap, a GPS spike or a
/// clock that runs backwards can be constructed exactly, where getting one by riding into it
/// is a matter of luck.
/// </para>
/// <para>
/// The hostile fixtures are the classic XML attacks rather than anything cycling-specific,
/// because that is what GPX being XML actually costs.
/// </para>
/// </summary>
public static class GpxFixtures
{
	/// <summary>Somewhere in the Brisbane hinterland, and nowhere anybody lives.</summary>
	public const double BaseLatitude = -27.4700;

	/// <summary>Paired with <see cref="BaseLatitude"/>.</summary>
	public const double BaseLongitude = 153.0250;

	/// <summary>A fixed start instant, so a fixture's boundary conditions do not move daily.</summary>
	public static readonly DateTimeOffset Start = new(2026, 3, 1, 6, 0, 0, TimeSpan.Zero);

	/// <summary>A file with one track: points spaced along a line, timed and with elevation.</summary>
	/// <param name="points">How many points.</param>
	/// <param name="name">The track name.</param>
	/// <param name="withTime">Whether to emit <c>&lt;time&gt;</c>.</param>
	/// <param name="withElevation">Whether to emit <c>&lt;ele&gt;</c>.</param>
	/// <param name="secondsApart">Interval between points.</param>
	/// <param name="metresApart">Roughly, how far apart along a meridian.</param>
	public static string SingleTrack(
		int points = 5,
		string name = "Morning loop",
		bool withTime = true,
		bool withElevation = true,
		int secondsApart = 10,
		double metresApart = 20)
	{
		StringBuilder gpx = new();

		gpx.Append(Header());
		gpx.Append(CultureInfo.InvariantCulture, $"  <trk><name>{name}</name><trkseg>\n");

		for (int index = 0; index < points; index++)
		{
			gpx.Append(Point(
				"trkpt",
				BaseLatitude + (index * MetresToDegreesLatitude(metresApart)),
				BaseLongitude,
				withElevation ? 50 + index : null,
				withTime ? Start.AddSeconds(index * secondsApart) : null));
		}

		gpx.Append("  </trkseg></trk>\n");
		gpx.Append(Footer());

		return gpx.ToString();
	}

	/// <summary>One track in two segments - a tunnel, a pause, a lost signal.</summary>
	/// <param name="pointsPerSegment">Points in each segment.</param>
	/// <param name="gapMetres">How far the second segment starts from where the first ended.</param>
	public static string TrackWithSegmentBreak(int pointsPerSegment = 3, double gapMetres = 5_000)
	{
		StringBuilder gpx = new();

		gpx.Append(Header());
		gpx.Append("  <trk><name>Through the tunnel</name>\n");

		for (int segment = 0; segment < 2; segment++)
		{
			gpx.Append("    <trkseg>\n");

			for (int index = 0; index < pointsPerSegment; index++)
			{
				double offset = (segment * gapMetres) + (index * 20);

				gpx.Append(Point(
					"trkpt",
					BaseLatitude + MetresToDegreesLatitude(offset),
					BaseLongitude,
					50,
					Start.AddSeconds(((segment * pointsPerSegment) + index) * 10)));
			}

			gpx.Append("    </trkseg>\n");
		}

		gpx.Append("  </trk>\n");
		gpx.Append(Footer());

		return gpx.ToString();
	}

	/// <summary>Several <c>&lt;trk&gt;</c> in one file.</summary>
	/// <param name="tracks">How many.</param>
	public static string ManyTracks(int tracks)
	{
		StringBuilder gpx = new();

		gpx.Append(Header());

		for (int track = 0; track < tracks; track++)
		{
			gpx.Append(CultureInfo.InvariantCulture, $"  <trk><name>Track {track}</name><trkseg>\n");

			for (int index = 0; index < 3; index++)
			{
				gpx.Append(Point(
					"trkpt",
					BaseLatitude + MetresToDegreesLatitude((track * 100) + (index * 20)),
					BaseLongitude,
					null,
					null));
			}

			gpx.Append("  </trkseg></trk>\n");
		}

		gpx.Append(Footer());

		return gpx.ToString();
	}

	/// <summary>A planning tool's output: <c>&lt;rte&gt;</c> with no times.</summary>
	/// <param name="points">How many route points.</param>
	/// <param name="withStrayTime">
	/// Whether to emit a <c>&lt;time&gt;</c> anyway. Some exporters do, and §15.3 says a route
	/// is imported without timestamps regardless.
	/// </param>
	public static string Route(int points = 4, bool withStrayTime = false)
	{
		StringBuilder gpx = new();

		gpx.Append(Header());
		gpx.Append("  <rte><name>Planned route</name>\n");

		for (int index = 0; index < points; index++)
		{
			gpx.Append(Point(
				"rtept",
				BaseLatitude + (index * MetresToDegreesLatitude(100)),
				BaseLongitude,
				null,
				withStrayTime ? Start.AddSeconds(index * 60) : null));
		}

		gpx.Append("  </rte>\n");
		gpx.Append(Footer());

		return gpx.ToString();
	}

	/// <summary>A track whose clock goes backwards partway through.</summary>
	public static string NonMonotonicTimestamps()
	{
		StringBuilder gpx = new();

		gpx.Append(Header());
		gpx.Append("  <trk><name>Clock trouble</name><trkseg>\n");

		int[] offsets = [0, 10, 20, 5, 40];

		for (int index = 0; index < offsets.Length; index++)
		{
			gpx.Append(Point(
				"trkpt",
				BaseLatitude + (index * MetresToDegreesLatitude(20)),
				BaseLongitude,
				50 + index,
				Start.AddSeconds(offsets[index])));
		}

		gpx.Append("  </trkseg></trk>\n");
		gpx.Append(Footer());

		return gpx.ToString();
	}

	/// <summary>A track with a coordinate that cannot exist.</summary>
	/// <param name="latitude">Something outside −90…90, or <c>NaN</c>.</param>
	public static string OutOfRangeCoordinate(string latitude = "91.5")
	{
		return Header()
			+ "  <trk><trkseg>\n"
			+ $"    <trkpt lat=\"{latitude}\" lon=\"153.0\"></trkpt>\n"
			+ "  </trkseg></trk>\n"
			+ Footer();
	}

	/// <summary>
	/// A track <em>and</em> waypoints - the ordinary shape of a file somebody exports from a
	/// mapping tool, and the only shape whose waypoints have a parent to attach to (§16.6).
	/// </summary>
	/// <param name="waypoints">How many waypoints.</param>
	/// <param name="points">How many track points.</param>
	public static string TrackWithWaypoints(int waypoints = 2, int points = 5)
	{
		string track = SingleTrack(points);

		// Waypoints go before the <trk>, which is where the schema wants them and where every
		// reader expects to find them.
		return track.Replace("  <trk>", Waypoints(waypoints) + "  <trk>", StringComparison.Ordinal);
	}

	/// <summary>Waypoints alone, with no track for them to hang off.</summary>
	/// <param name="count">How many.</param>
	public static string WithWaypoints(int count = 2) => Header() + Waypoints(count) + Footer();

	private static string Waypoints(int count)
	{
		StringBuilder gpx = new();

		for (int index = 0; index < count; index++)
		{
			gpx.Append(CultureInfo.InvariantCulture, $"""
				  <wpt lat="{BaseLatitude + (index * 0.001)}" lon="{BaseLongitude}">
				    <name>Water stop {index}</name>
				    <desc>Tap on the wall</desc>
				    <sym>Drinking Water</sym>
				    <link href="https://example.invalid/should-never-be-fetched" />
				  </wpt>

				""");
		}

		return gpx.ToString();
	}

	/// <summary>
	/// A document type declaration. Both XXE and billion-laughs arrive through this door, which
	/// is why it is refused rather than ignored (§15.3).
	/// </summary>
	public static string WithDtd() =>
		"""
		<?xml version="1.0" encoding="UTF-8"?>
		<!DOCTYPE gpx [ <!ELEMENT gpx ANY> ]>
		<gpx version="1.1" creator="test"><trk><trkseg></trkseg></trk></gpx>
		""";

	/// <summary>
	/// Billion laughs: entities nested so that expansion is exponential. A parser that
	/// processes this allocates gigabytes from a file of a few hundred bytes.
	/// </summary>
	public static string NestedEntityExpansion() =>
		"""
		<?xml version="1.0" encoding="UTF-8"?>
		<!DOCTYPE gpx [
		  <!ENTITY a "aaaaaaaaaa">
		  <!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">
		  <!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;">
		  <!ENTITY d "&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;">
		  <!ENTITY e "&d;&d;&d;&d;&d;&d;&d;&d;&d;&d;">
		]>
		<gpx version="1.1" creator="test"><trk><name>&e;</name></trk></gpx>
		""";

	/// <summary>
	/// An external entity pointing at a local file - the XXE that reads
	/// <c>/etc/passwd</c> and posts it back inside a track name.
	/// </summary>
	/// <param name="path">A file that exists, so resolving it would visibly succeed.</param>
	public static string ExternalEntityReference(string path) =>
		$"""
		<?xml version="1.0" encoding="UTF-8"?>
		<!DOCTYPE gpx [ <!ENTITY secret SYSTEM "file://{path.Replace('\\', '/')}"> ]>
		<gpx version="1.1" creator="test"><trk><name>&secret;</name></trk></gpx>
		""";

	/// <summary>Not XML at all. People upload the wrong file.</summary>
	public static string NotXml() => "this is a photo of a bicycle, not a gpx file";

	/// <summary>Well-formed XML that is not GPX - an exported spreadsheet, say.</summary>
	public static string NotGpx() =>
		"""<?xml version="1.0" encoding="UTF-8"?><workbook><sheet name="rides" /></workbook>""";

	/// <summary>A file that stops mid-element, as an interrupted upload does.</summary>
	public static string Truncated()
	{
		string whole = SingleTrack(points: 20);

		return whole[..(whole.Length / 2)];
	}

	/// <summary>A file whose point count is meant to blow past the cap.</summary>
	/// <param name="points">How many points to emit.</param>
	public static string ManyPoints(int points) => SingleTrack(points, withElevation: false);

	/// <summary>The fixture as a stream, which is how the reader takes it.</summary>
	/// <param name="gpx">The document.</param>
	public static Stream AsStream(string gpx) => new MemoryStream(Encoding.UTF8.GetBytes(gpx));

	/// <summary>
	/// Metres of northward travel as degrees of latitude. Near enough for a fixture, and it
	/// keeps the expected distances in a test something a person can check by hand.
	/// </summary>
	/// <param name="metres">Distance north.</param>
	public static double MetresToDegreesLatitude(double metres) => metres / 111_320.0;

	private static string Header() =>
		"""
		<?xml version="1.0" encoding="UTF-8"?>
		<gpx version="1.1" creator="DLR test fixtures" xmlns="http://www.topografix.com/GPX/1/1">

		""";

	private static string Footer() => "</gpx>\n";

	private static string Point(
		string element,
		double latitude,
		double longitude,
		double? elevation,
		DateTimeOffset? time)
	{
		StringBuilder point = new();

		point.Append(CultureInfo.InvariantCulture,
			$"    <{element} lat=\"{latitude:F7}\" lon=\"{longitude:F7}\">");

		if (elevation is { } metres)
		{
			point.Append(CultureInfo.InvariantCulture, $"<ele>{metres:F1}</ele>");
		}

		if (time is { } instant)
		{
			point.Append(CultureInfo.InvariantCulture, $"<time>{instant:yyyy-MM-ddTHH:mm:ssZ}</time>");
		}

		point.Append(CultureInfo.InvariantCulture, $"</{element}>\n");

		return point.ToString();
	}
}
