using BlazorDLR.Shared.Components;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Tracks;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Components;

/// <summary>
/// Choosing which offline map pack to download by pointing at the ground it covers (§4.2).
/// <para>
/// What the settings page's own tests cover is the outcome - an area selected, the form filled in,
/// nothing downloaded. What is here is the part of the window that is not visible from outside it:
/// which boxes get drawn, and which tiles they get drawn over. Neither can be asserted through the
/// rendered markup, because the boxes are pixels on a Skia canvas that does not exist in bUnit -
/// so the assertion is on what the component handed the map.
/// </para>
/// </summary>
public sealed class MapAreaPickerTests : BunitContext
{
	private static readonly TrackBounds NswBounds = new(-37.52, 140.99, -28.15, 153.65);
	private static readonly TrackBounds AustraliaBounds = new(-43.64, 112.92, -10.06, 153.64);

	private static MapPackOffer Offer(string id, string name, TrackBounds? bounds) =>
		new(id, name, "Australia", 1, 1024, null, new Uri($"https://packs.example.com/{id}.pmtiles"), bounds);

	private readonly FakeMapInterop _map = new();

	/// <summary>The packs this device holds, which is what decides whether an offline source is real.</summary>
	private readonly FakeMapPackStore _packs = new();

	private IRenderedComponent<MapAreaPicker> RenderPicker(
		IReadOnlyList<MapPackOffer> offers,
		Action<MapPackOffer>? chosen = null)
	{
		Services.AddSingleton<IMapInterop>(_map);
		Services.AddRideMapServices();

		// Over the browser-shaped store AddRideMapServices binds: an offline source is only real on
		// a host that can hold a pack (§18.6), and one of these tests turns on exactly that.
		Services.AddScoped<IMapPackStore>(_ => _packs);

		return Render<MapAreaPicker>(parameters => parameters
			.Add(p => p.Offers, offers)
			.Add(p => p.OnChosen, offer => chosen?.Invoke(offer)));
	}

	/// <summary>The boxes the picker handed the map, which is what a rider sees to point at.</summary>
	private static IReadOnlyList<MapBox> DrawnBoxes(IRenderedComponent<MapAreaPicker> picker) =>
		picker.FindComponent<RideMap>().Instance.Boxes ?? [];

	[Fact]
	public void EveryOfferWithAPublishedBox_IsDrawn()
	{
		IRenderedComponent<MapAreaPicker> picker = RenderPicker([
			Offer("au-nsw", "New South Wales", NswBounds),
			Offer("au-all", "Australia", AustraliaBounds),
		]);

		DrawnBoxes(picker).Select(box => box.Bounds).ShouldBe([NswBounds, AustraliaBounds]);
	}

	/// <summary>
	/// An offer that published no bounds cannot be drawn and cannot be pointed at. It is left out
	/// rather than guessed at - the alternative is a box somewhere it does not belong, which a
	/// rider would tap and download the wrong several hundred megabytes.
	/// </summary>
	[Fact]
	public void AnOfferWithNoBox_IsNotDrawn()
	{
		IRenderedComponent<MapAreaPicker> picker = RenderPicker([
			Offer("au-nsw", "New South Wales", NswBounds),
			Offer("au-vic", "Victoria", null),
		]);

		DrawnBoxes(picker).Count.ShouldBe(1);
		DrawnBoxes(picker).Single().Bounds.ShouldBe(NswBounds);
	}

	/// <summary>
	/// While the rider is choosing between overlapping areas, the ones in question are redrawn
	/// emphasised - a list of names cannot say <em>which</em> of the shapes on screen each one is,
	/// and they may know neither by name.
	/// </summary>
	[Fact]
	public void WhileChoosingBetweenOverlappingAreas_ThoseBoxesArePickedOut()
	{
		IRenderedComponent<MapAreaPicker> picker = RenderPicker([
			Offer("au-nsw", "New South Wales", NswBounds),
			Offer("au-all", "Australia", AustraliaBounds),
			Offer("au-tas", "Tasmania", new TrackBounds(-43.70, 143.80, -39.50, 148.50)),
		]);

		DrawnBoxes(picker).ShouldAllBe(box => !box.Emphasised, "nothing is being pointed at yet.");

		// Sydney, which is inside New South Wales and inside Australia.
		picker.InvokeAsync(() => _map.RaiseClick(-33.87, 151.21));

		picker.WaitForAssertion(() =>
		{
			DrawnBoxes(picker).Count(box => box.Emphasised).ShouldBe(2);
			DrawnBoxes(picker).Single(box => box.Bounds == NswBounds).Emphasised.ShouldBeTrue();
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// Putting the list away leaves the map up and nothing chosen - a rider who tapped the wrong
	/// place should not have to reopen the window to try again.
	/// </summary>
	[Fact]
	public void TheListOfOverlappingAreasCanBeDismissed_WithoutChoosing()
	{
		List<MapPackOffer> chosen = [];

		IRenderedComponent<MapAreaPicker> picker = RenderPicker(
			[Offer("au-nsw", "New South Wales", NswBounds), Offer("au-all", "Australia", AustraliaBounds)],
			chosen.Add);

		picker.InvokeAsync(() => _map.RaiseClick(-33.87, 151.21));

		picker.WaitForAssertion(
			() => picker.FindAll(".choices li button").Count.ShouldBe(2),
			timeout: TimeSpan.FromSeconds(3));

		picker.Find(".choices .dismiss").Click();

		picker.FindAll(".choices").ShouldBeEmpty();
		picker.FindAll(".area-picker-map").ShouldNotBeEmpty("the map stays up.");
		chosen.ShouldBeEmpty();
		DrawnBoxes(picker).ShouldAllBe(box => !box.Emphasised);
	}

	/// <summary>
	/// The one map in the app that cannot be drawn from an offline pack. A pack is a single region,
	/// so a world map made of one is that region and then nothing - and this is the screen where the
	/// rest of the world is the whole point.
	/// </summary>
	[Fact]
	public async Task WithAnOfflinePackAsTheDevicesSource_ItDrawsOpenStreetMapInstead()
	{
		Services.AddSingleton<IMapInterop>(_map);
		Services.AddRideMapServices();

		// Over the browser-shaped store AddRideMapServices binds: an offline source is only real on
		// a host that can hold a pack (§18.6), and one of these tests turns on exactly that.
		Services.AddScoped<IMapPackStore>(_ => _packs);

		_packs.Add("au-nsw", [1, 2, 3, 4]);
		await Services.GetRequiredService<MapSourceState>().SetAsync(
			MapSource.OfflinePack("au-nsw", MapTheme.Light));

		IRenderedComponent<MapAreaPicker> picker = Render<MapAreaPicker>(parameters => parameters
			.Add(p => p.Offers, new[] { Offer("au-nsw", "New South Wales", NswBounds) }));

		picker.WaitForAssertion(
			() => _map.LastOptions!.EffectiveSource.ShouldBe(MapSource.Default),
			timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// A tile server of the rider's own is left alone - it is a world map and probably works. If it
	/// does not, this is the answer, and it has to be: the commonest way to arrive here with a
	/// broken one is to have just typed it in on the screen behind.
	/// </summary>
	[Fact]
	public async Task WhenTheRidersOwnTilesWillNotDraw_ItFallsBackToOpenStreetMap()
	{
		Services.AddSingleton<IMapInterop>(_map);
		Services.AddRideMapServices();

		// Over the browser-shaped store AddRideMapServices binds: an offline source is only real on
		// a host that can hold a pack (§18.6), and one of these tests turns on exactly that.
		Services.AddScoped<IMapPackStore>(_ => _packs);

		MapSource custom = MapSource.Custom("https://tiles.example.com/{z}/{x}/{y}.png", "© Example");
		await Services.GetRequiredService<MapSourceState>().SetAsync(custom);

		IRenderedComponent<MapAreaPicker> picker = Render<MapAreaPicker>(parameters => parameters
			.Add(p => p.Offers, new[] { Offer("au-nsw", "New South Wales", NswBounds) }));

		picker.WaitForAssertion(
			() => _map.LastOptions!.EffectiveSource.ShouldBe(custom, "their own choice, tried first."),
			timeout: TimeSpan.FromSeconds(3));

		_map.RaiseError("Failed to fetch https://tiles.example.com/3/1/2.png");

		picker.WaitForAssertion(() =>
		{
			_map.Sources.ShouldContain(MapSource.Default);
			picker.Find(".area-picker .status").TextContent.ShouldContain("OpenStreetMap");
		}, timeout: TimeSpan.FromSeconds(3));
	}
}
