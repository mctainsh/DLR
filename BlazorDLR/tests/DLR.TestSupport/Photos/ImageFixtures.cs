using System.IO.Compression;
using SkiaSharp;

namespace DLR.TestSupport.Photos;

/// <summary>
/// Synthetic images for the §16.4 ingest tests, built in code rather than checked in (§15.3).
/// <para>
/// The rule that put the GPX corpus here applies with more force to photographs: a recorded
/// fixture for <c>Photo_ExifGpsTag_IsAbsentFromStoredImage</c> would be a real photograph with
/// real coordinates in it, committed to a repository that is going public. These are drawn with
/// SkiaSharp and their EXIF is assembled byte by byte, so the coordinates in them are a made-up
/// number and the test asserts on that exact number rather than on "some GPS tag".
/// </para>
/// </summary>
public static class ImageFixtures
{
	/// <summary>
	/// The latitude written into every EXIF fixture, in degrees, minutes and seconds.
	/// <para>
	/// A made-up position, and the point of it being a constant is that a test can look for
	/// <em>these bytes</em> in the re-encoded output. "No <c>Exif</c> marker" would pass against
	/// an implementation that moved the tags into a comment segment.
	/// </para>
	/// </summary>
	public static readonly (uint Degrees, uint Minutes, uint Seconds) Latitude = (33, 52, 7);

	/// <summary>The longitude written into every EXIF fixture.</summary>
	public static readonly (uint Degrees, uint Minutes, uint Seconds) Longitude = (151, 12, 33);

	/// <summary>
	/// The camera make written into every EXIF fixture. Distinctive on purpose - a re-encode that
	/// dropped GPS and kept the rest would still leak the device, and this is what catches that.
	/// </summary>
	public const string CameraMake = "DLR-TEST-CAMERA-SERIAL-7F3A";

	/// <summary>The capture timestamp written into every EXIF fixture.</summary>
	public const string CapturedAt = "2019:07:04 06:12:44";

	/// <summary>EXIF orientation 1 - stored the way it is displayed.</summary>
	public const ushort OrientationNormal = 1;

	/// <summary>
	/// EXIF orientation 6 - the stored top-left belongs at the top-right, so a correct reader
	/// rotates 90° clockwise. This is what every phone held in portrait writes.
	/// </summary>
	public const ushort OrientationRotate90Cw = 6;

	/// <summary>
	/// The exact 24 bytes the GPS latitude occupies inside a fixture's EXIF - three rationals,
	/// little-endian.
	/// <para>
	/// This is what <c>Photo_ExifGpsTag_IsAbsentFromStoredImage</c> looks for. Asserting merely
	/// that the output has no <c>Exif</c> marker would pass against an implementation that copied
	/// the tags into a comment segment or a maker note, and the rider's house would still be in
	/// the file. A 24-byte run does not occur by chance in JPEG entropy data.
	/// </para>
	/// </summary>
	public static byte[] LatitudeBytes()
	{
		byte[] bytes = new byte[24];

		Rationals(bytes, 0, Latitude);

		return bytes;
	}

	/// <summary>A JPEG with no metadata whatsoever.</summary>
	/// <param name="width">Pixels across.</param>
	/// <param name="height">Pixels down.</param>
	public static byte[] Jpeg(int width, int height) => Encode(Draw(width, height), SKEncodedImageFormat.Jpeg);

	/// <summary>A PNG with no metadata whatsoever.</summary>
	/// <param name="width">Pixels across.</param>
	/// <param name="height">Pixels down.</param>
	public static byte[] Png(int width, int height) => Encode(Draw(width, height), SKEncodedImageFormat.Png);

	/// <summary>
	/// A JPEG carrying a full EXIF block: GPS position, camera make, capture time and an
	/// orientation. The image itself is blue with a red square in its <em>stored</em> top-left,
	/// which is what makes a rotation observable rather than merely a dimension swap.
	/// </summary>
	/// <param name="width">Stored pixels across.</param>
	/// <param name="height">Stored pixels down.</param>
	/// <param name="orientation">The EXIF orientation tag, 1–8.</param>
	public static byte[] JpegWithExif(int width, int height, ushort orientation = OrientationNormal)
	{
		byte[] plain = Encode(Draw(width, height), SKEncodedImageFormat.Jpeg);
		byte[] app1 = App1(orientation);

		// Spliced in immediately after SOI, which is where a reader expects it. The encoder's own
		// APP0/JFIF segment then follows, and both being present is normal in a camera file.
		byte[] withExif = new byte[plain.Length + app1.Length];

		plain.AsSpan(0, 2).CopyTo(withExif);
		app1.CopyTo(withExif, 2);
		plain.AsSpan(2).CopyTo(withExif.AsSpan(2 + app1.Length));

		return withExif;
	}

	/// <summary>
	/// A PNG whose <c>IHDR</c> claims an enormous canvas in a few hundred bytes - a decompression
	/// bomb (§16.4).
	/// <para>
	/// <strong>Its image data is deliberately unusable.</strong> That is what makes the test able to
	/// tell the two implementations apart: code that reads the header and refuses answers with the
	/// pixel cap, and code that decodes first answers "not an image" - a different status and a
	/// different problem name. Without the broken stream, both orderings look identical from
	/// outside.
	/// </para>
	/// </summary>
	/// <param name="width">The width to declare.</param>
	/// <param name="height">The height to declare.</param>
	public static byte[] PngDeclaring(int width, int height)
	{
		using MemoryStream file = new();

		file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

		byte[] header = new byte[13];

		BigEndian(header, 0, (uint)width);
		BigEndian(header, 4, (uint)height);

		header[8] = 8;      // bit depth
		header[9] = 2;      // colour type: truecolour
		header[10] = 0;     // compression
		header[11] = 0;     // filter
		header[12] = 0;     // interlace

		Chunk(file, "IHDR", header);

		// A real zlib stream, but holding nothing like enough scanlines for the declared canvas.
		using (MemoryStream compressed = new())
		{
			using (ZLibStream deflate = new(compressed, CompressionLevel.Optimal, leaveOpen: true))
			{
				deflate.Write(new byte[256]);
			}

			Chunk(file, "IDAT", compressed.ToArray());
		}

		Chunk(file, "IEND", []);

		return file.ToArray();
	}

	/// <summary>Bytes that are not an image in any format, for the sniffing tests.</summary>
	public static byte[] NotAnImage() =>
		"﻿<?xml version=\"1.0\"?><gpx><trk><name>Not a photograph</name></trk></gpx>"u8
			.ToArray();

	/// <summary>
	/// Blue, with a red square filling the top-left quadrant of the <em>stored</em> pixels.
	/// Solid blocks survive JPEG quantisation, so a test can sample a quadrant's centre and know
	/// where that block ended up.
	/// </summary>
	private static SKBitmap Draw(int width, int height)
	{
		SKBitmap bitmap = new(width, height);

		using SKCanvas canvas = new(bitmap);

		canvas.Clear(new SKColor(0x20, 0x40, 0xC0));

		using SKPaint red = new() { Color = new SKColor(0xE0, 0x20, 0x20) };

		canvas.DrawRect(0, 0, width / 2f, height / 2f, red);

		return bitmap;
	}

	private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format)
	{
		using (bitmap)
		{
			using SKImage image = SKImage.FromBitmap(bitmap);
			using SKData encoded = image.Encode(format, 95);

			return encoded.ToArray();
		}
	}

	/// <summary>
	/// The <c>APP1</c> segment: <c>FFE1</c>, a length, the <c>Exif\0\0</c> signature and a TIFF
	/// structure holding IFD0 and a GPS IFD.
	/// </summary>
	private static byte[] App1(ushort orientation)
	{
		byte[] tiff = Tiff(orientation);

		// The length field counts itself but not the FFE1 marker.
		int length = 2 + 6 + tiff.Length;

		byte[] segment = new byte[2 + length];

		segment[0] = 0xFF;
		segment[1] = 0xE1;
		segment[2] = (byte)(length >> 8);
		segment[3] = (byte)length;

		"Exif\0\0"u8.CopyTo(segment.AsSpan(4));
		tiff.CopyTo(segment.AsSpan(10));

		return segment;
	}

	/// <summary>
	/// A little-endian TIFF holding four IFD0 tags and four GPS tags.
	/// <para>
	/// The layout is fixed rather than computed, because every offset in TIFF is absolute from the
	/// start of the header and a builder that got one wrong would produce a file readers disagree
	/// about - which is a fixture bug that looks exactly like the behaviour under test.
	/// </para>
	/// </summary>
	private static byte[] Tiff(ushort orientation)
	{
		byte[] make = Ascii(CameraMake);
		byte[] captured = Ascii(CapturedAt);

		const int Ifd0Offset = 8;
		const int EntrySize = 12;

		int ifd0Size = 2 + (4 * EntrySize) + 4;
		int gpsOffset = Ifd0Offset + ifd0Size;
		int gpsSize = 2 + (4 * EntrySize) + 4;

		int heap = gpsOffset + gpsSize;
		int makeOffset = heap;
		int capturedOffset = makeOffset + make.Length;
		int latitudeOffset = capturedOffset + captured.Length;
		int longitudeOffset = latitudeOffset + 24;

		byte[] tiff = new byte[longitudeOffset + 24];

		tiff[0] = (byte)'I';
		tiff[1] = (byte)'I';

		Little(tiff, 2, (ushort)42);
		Little(tiff, 4, (uint)Ifd0Offset);

		int at = Ifd0Offset;

		Little(tiff, at, (ushort)4);
		at += 2;

		at = Entry(tiff, at, tag: 0x010F, type: 2, count: (uint)make.Length, value: (uint)makeOffset);
		at = Entry(tiff, at, tag: 0x0112, type: 3, count: 1, value: orientation);
		at = Entry(tiff, at, tag: 0x0132, type: 2, count: (uint)captured.Length, value: (uint)capturedOffset);
		at = Entry(tiff, at, tag: 0x8825, type: 4, count: 1, value: (uint)gpsOffset);

		Little(tiff, at, 0u);

		at = gpsOffset;

		Little(tiff, at, (ushort)4);
		at += 2;

		// South and East, matching the made-up position above. The refs are two bytes, so they
		// sit inside the entry rather than out on the heap.
		at = Entry(tiff, at, tag: 0x0001, type: 2, count: 2, value: 'S');
		at = Entry(tiff, at, tag: 0x0002, type: 5, count: 3, value: (uint)latitudeOffset);
		at = Entry(tiff, at, tag: 0x0003, type: 2, count: 2, value: 'E');
		at = Entry(tiff, at, tag: 0x0004, type: 5, count: 3, value: (uint)longitudeOffset);

		Little(tiff, at, 0u);

		make.CopyTo(tiff, makeOffset);
		captured.CopyTo(tiff, capturedOffset);

		Rationals(tiff, latitudeOffset, Latitude);
		Rationals(tiff, longitudeOffset, Longitude);

		return tiff;
	}

	private static int Entry(byte[] buffer, int at, ushort tag, ushort type, uint count, uint value)
	{
		Little(buffer, at, tag);
		Little(buffer, at + 2, type);
		Little(buffer, at + 4, count);
		Little(buffer, at + 8, value);

		return at + 12;
	}

	/// <summary>Degrees, minutes and seconds as three TIFF rationals, each a numerator and a denominator.</summary>
	private static void Rationals(byte[] buffer, int at, (uint Degrees, uint Minutes, uint Seconds) angle)
	{
		Little(buffer, at, angle.Degrees);
		Little(buffer, at + 4, 1u);
		Little(buffer, at + 8, angle.Minutes);
		Little(buffer, at + 12, 1u);
		Little(buffer, at + 16, angle.Seconds);
		Little(buffer, at + 20, 1u);
	}

	private static byte[] Ascii(string text)
	{
		byte[] bytes = new byte[text.Length + 1];

		for (int i = 0; i < text.Length; i++)
		{
			bytes[i] = (byte)text[i];
		}

		return bytes;
	}

	private static void Little(byte[] buffer, int at, ushort value)
	{
		buffer[at] = (byte)value;
		buffer[at + 1] = (byte)(value >> 8);
	}

	private static void Little(byte[] buffer, int at, uint value)
	{
		buffer[at] = (byte)value;
		buffer[at + 1] = (byte)(value >> 8);
		buffer[at + 2] = (byte)(value >> 16);
		buffer[at + 3] = (byte)(value >> 24);
	}

	private static void BigEndian(byte[] buffer, int at, uint value)
	{
		buffer[at] = (byte)(value >> 24);
		buffer[at + 1] = (byte)(value >> 16);
		buffer[at + 2] = (byte)(value >> 8);
		buffer[at + 3] = (byte)value;
	}

	/// <summary>A PNG chunk: length, type, payload and the CRC over type and payload.</summary>
	private static void Chunk(Stream file, string type, byte[] payload)
	{
		byte[] length = new byte[4];

		BigEndian(length, 0, (uint)payload.Length);
		file.Write(length);

		byte[] body = new byte[4 + payload.Length];

		for (int i = 0; i < 4; i++)
		{
			body[i] = (byte)type[i];
		}

		payload.CopyTo(body, 4);
		file.Write(body);

		byte[] crc = new byte[4];

		BigEndian(crc, 0, Crc32(body));
		file.Write(crc);
	}

	private static uint Crc32(byte[] bytes)
	{
		uint crc = 0xFFFFFFFF;

		foreach (byte b in bytes)
		{
			crc ^= b;

			for (int bit = 0; bit < 8; bit++)
			{
				crc = (crc >> 1) ^ (0xEDB88320 & (uint)-(crc & 1));
			}
		}

		return crc ^ 0xFFFFFFFF;
	}
}
