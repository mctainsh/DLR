using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Photos;
using DLR.Core.Contracts.Rides;
using DLR.Server.Data.Photos;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using DLR.TestSupport.Photos;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace DLR.Server.Tests.Photos;

/// <summary>
/// The one image ingest path (§16.4).
/// <para>
/// The first test here is a privacy guarantee rather than a feature. §15.6 lets a rider trim the
/// first 400 m off a track so a ride does not start at their house; if they then attach a photo
/// taken in the driveway, an EXIF GPS tag puts the house straight back — in a file handed to every
/// member of the ride. The two features are one decision, and getting one right without the other
/// is worth nothing.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PhotoTests(PostgresFixture postgres)
{
	private const string PhotosUrl = "/api/v1/photos";

	/// <summary>
	/// The whole reason this feature re-encodes rather than passing bytes through.
	/// <para>
	/// Asserted against the made-up coordinates the fixture wrote, not merely against the absence
	/// of an <c>Exif</c> marker — an implementation that moved the tags into a comment segment
	/// would satisfy the weaker assertion and still ship the rider's house.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Photo_ExifGpsTag_IsAbsentFromStoredImage()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient rider = await SignedInAsync(app, "DaveSmith");

		byte[] uploaded = ImageFixtures.JpegWithExif(800, 600);

		// The fixture is only worth anything if it actually carries what the test says it does.
		Contains(uploaded, "Exif\0\0"u8.ToArray())
			.ShouldBeTrue("the fixture must carry an EXIF block for its removal to mean something");

		Contains(uploaded, ImageFixtures.LatitudeBytes())
			.ShouldBeTrue("the fixture must carry the GPS latitude for its removal to mean something");

		PhotoUploaded photo = await UploadAsync(rider, uploaded);

		byte[] stored = await ContentAsync(rider, photo.PhotoId);

		Contains(stored, "Exif\0\0"u8.ToArray()).ShouldBeFalse("the stored image carries no EXIF block");

		Contains(stored, ImageFixtures.LatitudeBytes())
			.ShouldBeFalse("the coordinates the photograph was taken at are not in the file anybody downloads");
	}

	/// <summary>
	/// Both halves matter, and each catches a different mistake (§16.4).
	/// <para>
	/// The dimensions swap only if the orientation was <em>applied</em> — code that stripped the
	/// tag first has nothing left to rotate by and leaves the image sideways forever. The absent
	/// EXIF only holds if it was then <em>discarded</em> — code that passed the tag through would
	/// also look upright in a viewer, and would double-rotate in one that trusts the tag.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Photo_ExifOrientation_IsAppliedBeforeStripping()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient rider = await SignedInAsync(app, "DaveSmith");

		// Stored 400 wide by 200 tall, with a red square in the stored top-left, and a tag saying
		// the stored top-left belongs at the top-right. This is what a phone held in portrait
		// writes, and it is why photographs arrive sideways from servers that ignore it.
		PhotoUploaded photo = await UploadAsync(
			rider,
			ImageFixtures.JpegWithExif(400, 200, ImageFixtures.OrientationRotate90Cw));

		photo.WidthPx.ShouldBe(200, "a 90° turn swaps the axes");
		photo.HeightPx.ShouldBe(400);

		byte[] stored = await ContentAsync(rider, photo.PhotoId);

		using SKBitmap image = SKBitmap.Decode(stored);

		image.Width.ShouldBe(200);
		image.Height.ShouldBe(400);

		// And it turned the right way. A 90° turn anticlockwise would swap the axes just as
		// neatly and put the red square at the bottom-left.
		IsRed(image, x: 0.75, y: 0.25).ShouldBeTrue("the stored top-left belongs at the displayed top-right");
		IsRed(image, x: 0.25, y: 0.25).ShouldBeFalse("the displayed top-left is the blue background");

		Contains(stored, "Exif\0\0"u8.ToArray())
			.ShouldBeFalse("the orientation was applied and then discarded, not passed on");
	}

	/// <summary>
	/// GPS is the tag that matters most here, but it is not the only one that identifies somebody.
	/// A re-encode that dropped the coordinates and kept the device serial and the capture time
	/// would still leak, so this asserts on the whole segment structure rather than on three tags.
	/// </summary>
	[Fact]
	public async Task Photo_AllMetadata_IsAbsentAfterReEncode()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient rider = await SignedInAsync(app, "DaveSmith");

		PhotoUploaded photo = await UploadAsync(rider, ImageFixtures.JpegWithExif(600, 400));

		byte[] stored = await ContentAsync(rider, photo.PhotoId);

		Contains(stored, Encoding.ASCII.GetBytes(ImageFixtures.CameraMake))
			.ShouldBeFalse("the camera and its serial number are not the rider's to publish");

		Contains(stored, Encoding.ASCII.GetBytes(ImageFixtures.CapturedAt))
			.ShouldBeFalse("when the photograph was taken is not in the file either");

		// The structural assertion, and the one that keeps holding as formats gain new segments:
		// an application segment other than APP0/JFIF, or a comment, is somewhere metadata can be.
		// A stripper removes the tags it knows; an encoder handed a pixel buffer has none to write.
		List<byte> carriers = Markers(stored)
			.Where(marker => (marker is >= 0xE1 and <= 0xEF) || marker == 0xFE)
			.ToList();

		carriers.ShouldBeEmpty(
			"the only application segment in a re-encoded file is APP0/JFIF; anything else is a " +
			$"place metadata survives. Found: {string.Join(", ", carriers.Select(m => $"FF{m:X2}"))}");
	}

	/// <summary>
	/// A 69-byte PNG declaring a 30000 × 30000 canvas — 900 megapixels, some 3.6 GB of bitmap.
	/// <para>
	/// <strong>How this test tells the two orderings apart.</strong> The fixture's image data is
	/// deliberately unusable, so an implementation that decoded first would fail in the decoder and
	/// answer <em>400, DecodeFailed</em>. Only an implementation that reads the declared dimensions
	/// out of the header and refuses before allocating can answer <em>413, TooManyPixels</em>.
	/// Without the broken stream both orderings look identical from outside, which is exactly how
	/// a cap that runs too late gets written and passes review.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Photo_DecompressionBomb_IsRejectedBeforeAllocating()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient rider = await SignedInAsync(app, "DaveSmith");

		byte[] bomb = ImageFixtures.PngDeclaring(30_000, 30_000);

		bomb.Length.ShouldBeLessThan(4096, "a decompression bomb is small — that is the whole trick");

		using HttpResponseMessage response = await PostAsync(rider, bomb, "bomb.png", "image/png");

		response.StatusCode.ShouldBe(
			HttpStatusCode.RequestEntityTooLarge,
			await response.Content.ReadAsStringAsync());

		(await ProblemNameAsync(response)).ShouldBe(
			"TooManyPixels",
			"DecodeFailed here would mean the bitmap was allocated before the cap was consulted");

		// And nothing was kept.
		int rows = await app.WithDatabaseAsync(database => database.Set<Photo>().CountAsync());

		rows.ShouldBe(0);

		BlobCount(app).ShouldBe(0, "a refused upload leaves nothing on a 40 GB disk");
	}

	[Fact]
	public async Task Photo_ExceedsByteCap_Returns413()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: new Dictionary<string, string?> { ["Photos:MaxUploadBytes"] = "4000" });

		using HttpClient rider = await SignedInAsync(app, "DaveSmith");

		// The cap is lowered to meet the fixture rather than the other way round. These images are
		// two flat colours, so a 1600 x 1200 one is about 12 KB — a fixture big enough to exceed a
		// realistic 12 MB cap would have to be noise, and noise compresses to megabytes of nothing.
		byte[] large = ImageFixtures.Jpeg(1600, 1200);

		large.Length.ShouldBeGreaterThan(4000, "the fixture has to actually exceed the cap");

		using HttpResponseMessage response = await PostAsync(rider, large, "big.jpg", "image/jpeg");

		response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);

		BlobCount(app).ShouldBe(0);

		// The cap is a cap, not a ban on photographs: a file inside it still works.
		PhotoUploaded small = await UploadAsync(rider, ImageFixtures.Jpeg(120, 90));

		small.PhotoId.ShouldNotBe(Guid.Empty);
	}

	[Fact]
	public async Task Photo_NotAnImage_ReturnsProblemDetails()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient rider = await SignedInAsync(app, "DaveSmith");

		using HttpResponseMessage response = await PostAsync(
			rider,
			ImageFixtures.NotAnImage(),
			"ride.gpx",
			"application/gpx+xml");

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

		// Problem Details, not an unhandled decoder exception (§16.4).
		response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

		(await ProblemNameAsync(response)).ShouldBe("NotAnImage");
	}

	/// <summary>
	/// Two halves, because the lie can point either way and only one of them is dangerous.
	/// </summary>
	[Fact]
	public async Task Photo_ContentTypeLies_IsDetectedBySniffing()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient rider = await SignedInAsync(app, "DaveSmith");

		// Claiming to be a JPEG does not make it one. This is the half that matters: believing the
		// header is how bytes nobody parsed reach a decoder.
		using (HttpResponseMessage lied = await PostAsync(
			rider,
			ImageFixtures.NotAnImage(),
			"holiday.jpg",
			"image/jpeg"))
		{
			lied.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

			(await ProblemNameAsync(lied)).ShouldBe("NotAnImage");
		}

		// And the harmless direction: a real PNG mislabelled as a JPEG is still a real image, so
		// it is accepted on its content and comes back re-encoded as JPEG like everything else.
		using HttpResponseMessage honest = await PostAsync(
			rider,
			ImageFixtures.Png(300, 200),
			"holiday.jpg",
			"image/jpeg");

		honest.StatusCode.ShouldBe(HttpStatusCode.Created, await honest.Content.ReadAsStringAsync());

		PhotoUploaded photo = (await honest.Content.ReadFromJsonAsync<PhotoUploaded>())!;

		byte[] stored = await ContentAsync(rider, photo.PhotoId);

		stored[0].ShouldBe((byte)0xFF, "everything is re-encoded to JPEG, whatever arrived");
		stored[1].ShouldBe((byte)0xD8);
	}

	[Fact]
	public async Task Photo_LargeImage_IsDownscaledAndThumbnailed()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient rider = await SignedInAsync(app, "DaveSmith");

		PhotoUploaded photo = await UploadAsync(rider, ImageFixtures.Jpeg(3000, 1500));

		// The long edge lands on the cap and the aspect ratio survives.
		photo.WidthPx.ShouldBe(2048);
		photo.HeightPx.ShouldBe(1024);

		using (SKBitmap full = SKBitmap.Decode(await ContentAsync(rider, photo.PhotoId)))
		{
			full.Width.ShouldBe(2048);
			full.Height.ShouldBe(1024);
		}

		byte[] thumbnailBytes = await ContentAsync(rider, photo.PhotoId, thumbnail: true);

		using (SKBitmap thumbnail = SKBitmap.Decode(thumbnailBytes))
		{
			thumbnail.Width.ShouldBe(320);
			thumbnail.Height.ShouldBe(160);
		}

		// Two objects, and the small one is genuinely small — a map callout that pulled the full
		// image would spend a rider's data allowance drawing pins (§16.4).
		thumbnailBytes.Length.ShouldBeLessThan(photo.ByteSize / 4);

		BlobCount(app).ShouldBe(2);

		(string blobRef, string thumbRef) = await app.WithDatabaseAsync(database =>
			database.Set<Photo>()
				.Where(row => row.Id == photo.PhotoId)
				.Select(row => new ValueTuple<string, string>(row.BlobRef, row.ThumbBlobRef))
				.SingleAsync());

		blobRef.ShouldNotBe(thumbRef);
	}

	/// <summary>
	/// <c>Marker.PhotoId</c>, deferred out of SRV-26 because it was a foreign key to a table that
	/// did not exist. Attaching is its own request for the reason §16.4 gives: the photograph is
	/// taken at the top of the hill, which is exactly where there is no signal.
	/// </summary>
	[Fact]
	public async Task Photo_AttachedToMarker_IsVisibleOnIt()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient rider = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(rider);

		MarkerDto marker = await CreateMarkerAsync(rider, ride.Id);

		marker.PhotoId.ShouldBeNull("a marker appears on the map before its photograph arrives");

		PhotoUploaded photo = await UploadAsync(rider, ImageFixtures.Jpeg(640, 480));

		using HttpResponseMessage attached = await rider.PatchAsJsonAsync(
			$"/api/v1/markers/{marker.Id}/photo",
			new AttachPhotoRequest(photo.PhotoId));

		attached.StatusCode.ShouldBe(HttpStatusCode.OK, await attached.Content.ReadAsStringAsync());

		MarkerDto withPhoto =
			(await attached.Content.ReadFromJsonAsync<MarkerDto>())!;

		withPhoto.PhotoId.ShouldBe(photo.PhotoId);
	}

	/// <summary>
	/// A photo identifier is a guid somebody else's client generated, so attaching has to check
	/// ownership rather than existence — otherwise a guessed identifier republishes a stranger's
	/// photograph under a marker of the caller's choosing.
	/// </summary>
	[Fact]
	public async Task Photo_AttachingSomebodyElsesUpload_IsRefused()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient other = await SignedInAsync(app, "SamJones");

		PhotoUploaded theirs = await UploadAsync(owner, ImageFixtures.Jpeg(320, 240));

		RideDetail ride = await CreateRideAsync(other);

		MarkerDto marker = await CreateMarkerAsync(other, ride.Id);

		using HttpResponseMessage response = await other.PatchAsJsonAsync(
			$"/api/v1/markers/{marker.Id}/photo",
			new AttachPhotoRequest(theirs.PhotoId));

		response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
	}

	private static async Task<PhotoUploaded> UploadAsync(HttpClient client, byte[] bytes)
	{
		using HttpResponseMessage response = await PostAsync(client, bytes, "photo.jpg", "image/jpeg");

		response.StatusCode.ShouldBe(
			HttpStatusCode.Created,
			await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<PhotoUploaded>())!;
	}

	private static async Task<HttpResponseMessage> PostAsync(
		HttpClient client,
		byte[] bytes,
		string fileName,
		string contentType)
	{
		using MultipartFormDataContent form = [];
		using ByteArrayContent file = new(bytes);

		file.Headers.ContentType = new MediaTypeHeaderValue(contentType);

		form.Add(file, "file", fileName);

		return await client.PostAsync(PhotosUrl, form);
	}

	private static async Task<byte[]> ContentAsync(HttpClient client, Guid photoId, bool thumbnail = false)
	{
		string url = thumbnail
			? $"{PhotosUrl}/{photoId}/thumbnail"
			: $"{PhotosUrl}/{photoId}";

		using HttpResponseMessage response = await client.GetAsync(url);

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return await response.Content.ReadAsByteArrayAsync();
	}

	/// <summary>The <c>problem</c> extension the endpoint puts on its Problem Details.</summary>
	private static async Task<string?> ProblemNameAsync(HttpResponseMessage response)
	{
		JsonDocument body =
			JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		return body.RootElement.TryGetProperty("problem", out JsonElement problem)
			? problem.GetString()
			: null;
	}

	/// <summary>Every blob on this server's throwaway volume.</summary>
	private static int BlobCount(DlrWebApplicationFactory app) =>
		Directory.EnumerateFiles(app.BlobRoot, "*", SearchOption.AllDirectories).Count();

	/// <summary>Whether the pixel at the given fractional position is the fixture's red.</summary>
	private static bool IsRed(SKBitmap image, double x, double y)
	{
		SKColor pixel = image.GetPixel((int)(image.Width * x), (int)(image.Height * y));

		return pixel.Red > 150 && pixel.Blue < 100;
	}

	/// <summary>The JPEG segment markers in a file, up to the start of scan.</summary>
	private static List<byte> Markers(byte[] jpeg)
	{
		List<byte> found = [];

		int at = 2;

		while (at + 4 < jpeg.Length && jpeg[at] == 0xFF)
		{
			byte marker = jpeg[at + 1];

			found.Add(marker);

			// Everything after SOS is entropy-coded data, where a 0xFF is a pixel and not a marker.
			if (marker == 0xDA)
			{
				break;
			}

			at += 2 + ((jpeg[at + 2] << 8) | jpeg[at + 3]);
		}

		return found;
	}

	private static bool Contains(byte[] haystack, byte[] needle)
	{
		for (int i = 0; i + needle.Length <= haystack.Length; i++)
		{
			if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
			{
				return true;
			}
		}

		return false;
	}

	private static async Task<RideDetail> CreateRideAsync(HttpClient organiser)
	{
		using HttpResponseMessage response = await organiser.PostAsJsonAsync(
			"/api/v1/group-rides",
			new CreateRideRequest(
				"Saturday hills",
				DlrWebApplicationFactory.DefaultStart.AddDays(3),
				JoinPolicy: JoinPolicyDto.Open));

		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<RideDetail>())!;
	}

	private static async Task<MarkerDto> CreateMarkerAsync(
		HttpClient client,
		Guid rideId)
	{
		using HttpResponseMessage response = await client.PostAsJsonAsync(
			"/api/v1/markers",
			new CreateMarkerRequest(
				TrackId: null,
				GroupRideId: rideId,
				PositionScale.FromDegrees(-33.86),
				PositionScale.FromDegrees(151.20),
				"hazard",
				"Gravel on the corner"));

		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<MarkerDto>())!;
	}

	private static async Task<HttpClient> SignedInAsync(DlrWebApplicationFactory app, string userName)
	{
		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync(userName);

		return app.CreateClient().Authenticated(session);
	}
}
