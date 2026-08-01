using DLR.Core.Tracks;
using DLR.TestSupport.Tracks;

namespace DLR.Core.Tests.Tracks;

/// <summary>
/// The hostile half of §15.3, and it comes first because GPX is XML and this is the first
/// user-supplied file format the project reads.
/// <para>
/// None of these is cycling-specific. They are the classic XML attacks, which is precisely what
/// makes them easy to leave out — nothing about a bike ride suggests you need to think about
/// entity expansion.
/// </para>
/// </summary>
public sealed class GpxReaderHostileInputTests
{
	[Fact]
	public void Import_DtdDeclaration_IsRejectedWithoutResolvingIt()
	{
		GpxFormatException refused = Should.Throw<GpxFormatException>(
			() => GpxReader.Read(GpxFixtures.AsStream(GpxFixtures.WithDtd())));

		refused.Problem.ShouldBe(GpxProblem.DtdNotAllowed);

		refused.Message.ShouldContain("DTD",
			Case.Insensitive,
			"somebody whose exporter emits a doctype needs to know that is the reason");
	}

	/// <summary>
	/// Billion laughs. A few hundred bytes expand to gigabytes in a parser that processes
	/// entities — so the assertion is not only that it fails, but that it fails <em>fast</em>
	/// and without allocating.
	/// </summary>
	[Fact]
	public void Import_NestedEntityExpansion_IsRejected()
	{
		long before = GC.GetTotalAllocatedBytes(precise: true);

		GpxFormatException refused = Should.Throw<GpxFormatException>(
			() => GpxReader.Read(GpxFixtures.AsStream(GpxFixtures.NestedEntityExpansion())));

		long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

		refused.Problem.ShouldBe(GpxProblem.DtdNotAllowed);

		allocated.ShouldBeLessThan(
			10 * 1024 * 1024,
			$"the read allocated {allocated / 1024:N0} KB — an expansion that gets anywhere near " +
			"running is the attack succeeding slowly rather than failing");
	}

	/// <summary>
	/// XXE: the entity that reads a file off the server and posts it back inside a track name.
	/// The fixture points at a real file with known content, so resolving it would visibly
	/// succeed — which is the only way to tell "did not resolve" from "resolved and found
	/// nothing".
	/// </summary>
	[Fact]
	public void Import_ExternalEntityReference_MakesNoNetworkCall()
	{
		string path = Path.Combine(Path.GetTempPath(), $"dlr-xxe-{Guid.NewGuid():N}.txt");
		const string secret = "this-must-never-appear-in-a-parsed-track";

		File.WriteAllText(path, secret);

		try
		{
			GpxDocument? document = null;

			Exception? failure = Record.Exception(
				() => document = GpxReader.Read(
					GpxFixtures.AsStream(GpxFixtures.ExternalEntityReference(path))));

			// Checked before the exception type, and deliberately: a test that only asserts
			// "it threw" passes for any reason at all, including reasons that have nothing to
			// do with the entity. What actually matters is that the file's contents are
			// nowhere in the result, however the read ended.
			string parsed = document is null
				? string.Empty
				: string.Join("|", document.Tracks.Select(track => track.Name));

			parsed.Contains(secret, StringComparison.Ordinal).ShouldBeFalse(
				"the entity resolved and put the contents of a local file inside a track name — " +
				"which is XXE working exactly as intended by whoever sent the file");

			failure.ShouldBeOfType<GpxFormatException>().Problem.ShouldBe(GpxProblem.DtdNotAllowed);
		}
		finally
		{
			File.Delete(path);
		}
	}

	/// <summary>
	/// A file can be small and still be pathological, so a byte cap does not bound the point
	/// count. The read has to stop <em>at</em> the cap rather than after finishing (§15.3).
	/// </summary>
	[Fact]
	public void Import_ExceedsPointCap_AbortsMidStreamWithoutBufferingAll()
	{
		const int cap = 100;

		CountingStream stream = new(
			GpxFixtures.AsStream(GpxFixtures.ManyPoints(points: 20_000)));

		GpxFormatException refused = Should.Throw<GpxFormatException>(
			() => GpxReader.Read(stream, new GpxLimits { MaxPointsPerFile = cap }));

		refused.Problem.ShouldBe(GpxProblem.TooManyPoints);

		refused.Message.ShouldContain(cap.ToString("N0", System.Globalization.CultureInfo.CurrentCulture));

		// The whole file is ~20 000 points; stopping at 100 must not have required reading it.
		// A generous bound, because XmlReader buffers in blocks — the point is the order of
		// magnitude, not the byte.
		stream.BytesRead.ShouldBeLessThan(
			stream.TotalBytes / 2,
			$"read {stream.BytesRead:N0} of {stream.TotalBytes:N0} bytes — the cap is supposed " +
			"to abort the parse, not audit it afterwards");
	}

	[Fact]
	public void Import_NotXml_ReturnsProblemDetailsNamingTheProblem()
	{
		GpxFormatException refused = Should.Throw<GpxFormatException>(
			() => GpxReader.Read(GpxFixtures.AsStream(GpxFixtures.NotXml())));

		refused.Problem.ShouldBe(GpxProblem.NotXml);

		// "Invalid file" is useless to somebody whose exporter emits something slightly
		// unusual, and this is a feature people meet with files from a dozen tools.
		refused.Describe().ShouldContain("line 1");
	}

	[Fact]
	public void Import_WellFormedXmlThatIsNotGpx_SaysSo()
	{
		GpxFormatException refused = Should.Throw<GpxFormatException>(
			() => GpxReader.Read(GpxFixtures.AsStream(GpxFixtures.NotGpx())));

		refused.Problem.ShouldBe(GpxProblem.NotGpx);
		refused.Message.ShouldContain("<gpx>");
	}

	[Fact]
	public void Import_TruncatedFile_ReturnsProblemDetailsNamingTheProblem()
	{
		GpxFormatException refused = Should.Throw<GpxFormatException>(
			() => GpxReader.Read(GpxFixtures.AsStream(GpxFixtures.Truncated())));

		refused.Problem.ShouldBe(GpxProblem.Truncated);

		refused.Message.ShouldContain("truncated",
			Case.Insensitive,
			"an interrupted upload is worth telling somebody to retry, not just refusing");
	}

	[Theory]
	[InlineData("91.5", "north of the north pole")]
	[InlineData("-90.001", "south of the south pole")]
	[InlineData("NaN", "not a number at all")]
	[InlineData("", "empty")]
	[InlineData("north", "not a number")]
	public void Import_OutOfRangeCoordinates_IsRejected(string latitude, string why)
	{
		GpxFormatException refused = Should.Throw<GpxFormatException>(
			() => GpxReader.Read(
				GpxFixtures.AsStream(GpxFixtures.OutOfRangeCoordinate(latitude))));

		refused.Problem.ShouldBe(GpxProblem.InvalidCoordinate, why);
	}

	/// <summary>Counts what was actually pulled off the stream, to tell aborting from finishing.</summary>
	private sealed class CountingStream(Stream inner) : Stream
	{
		public long BytesRead { get; private set; }

		public long TotalBytes => inner.Length;

		public override bool CanRead => true;

		public override bool CanSeek => false;

		public override bool CanWrite => false;

		public override long Length => inner.Length;

		public override long Position
		{
			get => inner.Position;
			set => throw new NotSupportedException();
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			int read = inner.Read(buffer, offset, count);

			BytesRead += read;

			return read;
		}

		public override void Flush() => inner.Flush();

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		public override void SetLength(long value) => throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				inner.Dispose();
			}

			base.Dispose(disposing);
		}
	}
}
