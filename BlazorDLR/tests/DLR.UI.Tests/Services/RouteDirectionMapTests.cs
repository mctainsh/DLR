using BlazorDLR.Shared.Services;

namespace DLR.UI.Tests.Services;

/// <summary>
/// The reversed-route set (§5.4, §18.6). Same job as <see cref="RouteColourMapTests"/> and for the
/// same reason: this is a string on somebody's phone that a later build has to read back, and
/// every malformed shape it can meet is a shape a real device can hand it.
/// </summary>
public sealed class RouteDirectionMapTests
{
	[Fact]
	public void Encode_ThenDecode_RoundTripsEveryEntry()
	{
		Guid first = Guid.NewGuid();
		Guid second = Guid.NewGuid();

		IReadOnlySet<Guid> read = RouteDirectionMap.Decode(
			RouteDirectionMap.Encode(new HashSet<Guid> { first, second }));

		read.Count.ShouldBe(2);
		read.ShouldContain(first);
		read.ShouldContain(second);
	}

	[Fact]
	public void Encode_AnEmptySet_StillDecodesToAnEmptySet()
	{
		// What a device stores after turning its last reversed route back - it must not read
		// back as "unreadable", which would be indistinguishable but is a different thing.
		RouteDirectionMap.Decode(RouteDirectionMap.Encode(RouteDirectionMap.Empty)).ShouldBeEmpty();
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("nonsense")]
	[InlineData("2|0123456789abcdef0123456789abcdef")] // a version this build does not know
	public void Decode_AnythingItCannotTrust_KeepsNothing(string? stored) =>
		RouteDirectionMap.Decode(stored).ShouldBeEmpty();

	[Fact]
	public void Decode_OneBadEntry_DoesNotCostTheOthers()
	{
		Guid good = Guid.NewGuid();

		// A route that loses its entry is drawn the way its GPX was recorded, which looks stock.
		// Dropping the whole set for one bad id would silently un-reverse every other route.
		IReadOnlySet<Guid> read = RouteDirectionMap.Decode($"1|not-a-guid|{good:N}");

		read.Count.ShouldBe(1);
		read.ShouldContain(good);
	}

	[Fact]
	public void Decode_ToleratesTheColourMapsSeparator_WithoutInventingIds()
	{
		// The two keys are written by the same build and never crossed, but a device is not a
		// place we control: a value from the wrong key must decode to nothing rather than to a
		// track id parsed out of half an entry.
		RouteDirectionMap.Decode("1|0123456789abcdef0123456789abcdef=#ff8800").ShouldBeEmpty();
	}
}
