using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Rides;
using DLR.Core.Contracts.Tracks;
using DLR.Server.Data.Markers;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using DLR.TestSupport.Tracks;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Markers;

/// <summary>
/// The GPX waypoint round trip (§16.6).
/// <para>
/// v0.12's "waypoints are ignored" rule is retired: §15.3 dropped <c>&lt;wpt&gt;</c> because
/// nothing in the model could hold one, and markers are exactly what they are.
/// </para>
/// </summary>
public sealed class GpxMarkerTests(PostgresFixture postgres)
{
	private const string ImportUrl = "/api/v1/tracks/import";

	[Fact]
	public async Task Gpx_WaypointsImportAsMarkers()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient client = await SignedInAsync(app, "DaveSmith");

		TrackImportResult result = await ImportAsync(client, GpxFixtures.TrackWithWaypoints(3));

		Guid trackId = result.Tracks.Single().TrackId!.Value;

		List<Marker> markers = await app.WithDatabaseAsync(database =>
			database.Set<Marker>()
				.Where(marker => marker.TrackId == trackId)
				.OrderBy(marker => marker.Title)
				.ToListAsync());

		markers.Count.ShouldBe(3);

		markers[0].Title.ShouldBe("Water stop 0");
		markers[0].Note.ShouldBe("Tap on the wall");

		// <sym>Drinking Water</sym> maps onto the curated key rather than being stored raw.
		markers[0].Icon.ShouldBe("water");

		markers.ShouldAllBe(marker => marker.GroupRideId == null, "a track marker has no adventure parent");
	}

	/// <summary>
	/// An external URL is not a photo attachment, and fetching one server-side would be an SSRF
	/// hole — a file's author would get a request from this server to any address they chose
	/// (§16.6).
	/// </summary>
	[Fact]
	public async Task Gpx_WaypointLinkElement_IsIgnoredAndMakesNoRequest()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient client = await SignedInAsync(app, "DaveSmith");

		// The fixture's <link href> points at example.invalid, which by RFC 2606 cannot resolve.
		// If anything tried to fetch it the import would stall or throw rather than return.
		TrackImportResult result = await ImportAsync(client, GpxFixtures.TrackWithWaypoints(1));

		Guid trackId = result.Tracks.Single().TrackId!.Value;

		Marker marker = await app.WithDatabaseAsync(database =>
			database.Set<Marker>().SingleAsync(row => row.TrackId == trackId));

		// And the URL is nowhere in what was stored — not silently parked in the note, which
		// would put it back in front of a user as something to tap.
		string stored = $"{marker.Title} {marker.Note} {marker.Icon}";

		stored.Contains("example.invalid", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
		stored.Contains("http", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
	}

	/// <summary>
	/// A file exported from this app and re-imported produces the same markers. This is the test
	/// that says the mapping is honest rather than merely present (§16.6).
	/// </summary>
	[Fact]
	public async Task Gpx_MarkerRoundTrip_IsLossless()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient client = await SignedInAsync(app, "DaveSmith");

		TrackImportResult imported = await ImportAsync(client, GpxFixtures.SingleTrack(points: 6));

		Guid trackId = imported.Tracks.Single().TrackId!.Value;

		// A marker of each interesting shape: a bearing, no bearing, an icon this version knows,
		// and one it does not.
		await CreateAsync(client, Marker(trackId, "hazard", "Blind crest", "Gravel on exit", 275));
		await CreateAsync(client, Marker(trackId, "fuel", "Last servo", note: null, direction: null));
		await CreateAsync(client, Marker(trackId, "ferry", "Punt crossing", "Cash only", 0));

		string exported = await client.GetStringAsync($"/api/v1/tracks/{trackId}/gpx");

		TrackImportResult reimported = await ImportAsync(client, exported);

		Guid roundTripped = reimported.Tracks.Single().TrackId!.Value;

		roundTripped.ShouldNotBe(trackId, "re-importing makes a new track; the markers are the point");

		List<Marker> before = await MarkersOfAsync(app, trackId);
		List<Marker> after = await MarkersOfAsync(app, roundTripped);

		after.Count.ShouldBe(before.Count);

		foreach ((Marker original, Marker copy) in before.Zip(after))
		{
			copy.Title.ShouldBe(original.Title);
			copy.Note.ShouldBe(original.Note);
			copy.Lat.ShouldBe(original.Lat);
			copy.Lon.ShouldBe(original.Lon);

			// The one that matters most: null must survive as null. Writing a zero for "no
			// bearing" would invent a due-north direction on every fuel stop in the file.
			copy.DirectionDeg.ShouldBe(
				original.DirectionDeg,
				$"'{original.Title}' lost its direction across the round trip");

			copy.Icon.ShouldBe(
				original.Icon,
				$"'{original.Title}' lost its icon — including the one this version cannot draw");
		}
	}

	/// <summary>
	/// A name longer than the title column is split rather than truncated. Discarding text
	/// somebody typed is damaging the file, not importing it (§16.6).
	/// </summary>
	[Fact]
	public async Task Gpx_OverlongWaypointName_KeepsTheRemainderInTheNote()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient client = await SignedInAsync(app, "DaveSmith");

		const string LongName =
			"The cafe on the corner past the roundabout with the good bacon sandwiches";

		string gpx = $"""
			<?xml version="1.0" encoding="UTF-8"?>
			<gpx version="1.1" creator="test" xmlns="http://www.topografix.com/GPX/1/1">
			  <wpt lat="-33.86" lon="151.20">
			    <name>{LongName}</name>
			    <desc>Open from six</desc>
			  </wpt>
			  <trk><name>Ride</name><trkseg>
			    <trkpt lat="-33.86" lon="151.20" />
			    <trkpt lat="-33.87" lon="151.21" />
			  </trkseg></trk>
			</gpx>
			""";

		TrackImportResult result = await ImportAsync(client, gpx);

		Marker marker = await app.WithDatabaseAsync(database =>
			database.Set<Marker>().SingleAsync(row => row.TrackId == result.Tracks.Single().TrackId));

		marker.Title.Length.ShouldBeLessThanOrEqualTo(40);

		LongName.ShouldStartWith(marker.Title);

		// Everything that did not fit is still there, and so is the description.
		marker.Note.ShouldNotBeNull();
		marker.Note.ShouldContain("bacon sandwiches");
		marker.Note.ShouldContain("Open from six");
	}

	private static CreateMarkerRequest Marker(
		Guid trackId,
		string icon,
		string title,
		string? note,
		int? direction) => new(
			trackId,
			GroupRideId: null,
			PositionScale.FromDegrees(-33.865),
			PositionScale.FromDegrees(151.205),
			icon,
			title,
			note,
			(short?)direction);

	private static Task<List<Marker>> MarkersOfAsync(DlrWebApplicationFactory app, Guid trackId) =>
		app.WithDatabaseAsync(database => database.Set<Marker>()
			.Where(marker => marker.TrackId == trackId)
			.OrderBy(marker => marker.CreatedUtc)
			.ThenBy(marker => marker.Title)
			.ToListAsync());

	private static async Task CreateAsync(HttpClient client, CreateMarkerRequest request)
	{
		using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/markers", request);

		response.StatusCode.ShouldBe(
			HttpStatusCode.Created,
			await response.Content.ReadAsStringAsync());
	}

	private static async Task<TrackImportResult> ImportAsync(HttpClient client, string gpx)
	{
		using MultipartFormDataContent form = [];

		ByteArrayContent file = new(Encoding.UTF8.GetBytes(gpx));

		file.Headers.ContentType = new MediaTypeHeaderValue("application/gpx+xml");

		form.Add(file, "file", "ride.gpx");

		using HttpResponseMessage response = await client.PostAsync($"{ImportUrl}?dryRun=false", form);

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<TrackImportResult>())!;
	}

	private static async Task<HttpClient> SignedInAsync(DlrWebApplicationFactory app, string userName)
	{
		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync(userName);

		return app.CreateClient().Authenticated(session);
	}
}
