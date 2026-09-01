using BlazorDLR.Shared.Services;

namespace DLR.UI.Tests.Services;

/// <summary>
/// The per-route colour map (§5.4, §18.6). Same job as <see cref="RouteStyleTests"/> and for the
/// same reason: this is a string on somebody's phone that a later build has to read back, and
/// every malformed shape it can meet is a shape a real device can hand it.
/// </summary>
public sealed class RouteColourMapTests
{
	[Fact]
	public void Encode_ThenDecode_RoundTripsEveryEntry()
	{
		Guid first = Guid.NewGuid();
		Guid second = Guid.NewGuid();

		Dictionary<Guid, string> colours = new()
		{
			[first] = "#ff8800",
			[second] = "#00ffcc",
		};

		IReadOnlyDictionary<Guid, string> read = RouteColourMap.Decode(RouteColourMap.Encode(colours));

		read.Count.ShouldBe(2);
		read[first].ShouldBe("#ff8800");
		read[second].ShouldBe("#00ffcc");
	}

	[Fact]
	public void Encode_AnEmptyMap_StillDecodesToAnEmptyMap()
	{
		// What a device stores after clearing its last override - it must not read back as
		// "unreadable", which would be indistinguishable but is a different thing.
		RouteColourMap.Decode(RouteColourMap.Encode(RouteColourMap.Empty)).ShouldBeEmpty();
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("nonsense")]
	[InlineData("2|0123456789abcdef0123456789abcdef=#ff8800")] // a version this build does not know
	public void Decode_AnythingItCannotTrust_KeepsNothing(string? stored) =>
		RouteColourMap.Decode(stored).ShouldBeEmpty();

	[Fact]
	public void Decode_OneBadEntry_DoesNotCostTheOthers()
	{
		Guid good = Guid.NewGuid();

		// A mangled id, a mangled colour, an entry with no separator - then a valid one.
		string stored = $"1|not-a-guid=#ff8800|{Guid.NewGuid():N}=purple|orphan|{good:N}=#00ffcc";

		IReadOnlyDictionary<Guid, string> read = RouteColourMap.Decode(stored);

		read.Count.ShouldBe(1, "one corrupt entry costs that route its colour, not every route.");
		read[good].ShouldBe("#00ffcc");
	}

	[Fact]
	public void Encode_DropsAColourTheCanvasCouldNotDraw()
	{
		// The canvas silently falls back to blue on a colour it cannot parse. Storing one would
		// leave the swatch and the line disagreeing, which is the failure the swatch exists to
		// prevent.
		string encoded = RouteColourMap.Encode(new Dictionary<Guid, string>
		{
			[Guid.NewGuid()] = "rgb(255, 136, 0)",
		});

		RouteColourMap.Decode(encoded).ShouldBeEmpty();
	}
}
