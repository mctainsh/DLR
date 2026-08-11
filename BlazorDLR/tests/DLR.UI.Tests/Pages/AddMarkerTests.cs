using BlazorDLR.Shared.Markers;
using BlazorDLR.Shared.Pages.GroupRides;
using BlazorDLR.Shared.Services;
using Bunit;
using DLR.Core.Contracts.Markers;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// §16.2's marker composer. Two rules that the DTO leans on:
/// <list type="bullet">
///   <item><em>Direction is nullable-not-zero.</em> Zero is due north — a real bearing.
///     Null means "no direction". The switch is off by default; only when it is on is
///     the bearing field visible, and only then does it reach the API as a non-null
///     value.</item>
///   <item><em>Icon is a curated string.</em> Every key in <see cref="MarkerIcons.Known"/>
///     appears as a radio option so authors cannot type a key the server doesn't
///     recognise.</item>
/// </list>
/// The composer also owns the point itself: §16.1 places a marker by tapping the map, and
/// the coordinate boxes are the second view of the same two numbers. The map here is the
/// shared <c>RideMap</c> against <see cref="FakeMapInterop"/> with its init forced to fail,
/// so <c>SkiaMapOverlay</c> never mounts (its <c>SKCanvasView</c> is browser-only) while the
/// click seam still reaches the page — which is the part with logic in it.
/// </summary>
public sealed class AddMarkerTests : PageTestContext
{
	private static FakeApiClient WireServices(BunitContext context) => WireServices(context, out _);

	private static FakeApiClient WireServices(BunitContext context, out FakeMapInterop map)
	{
		FakeApiClient api = new();
		context.Services.AddSingleton<IApiClient>(api);

		map = new FakeMapInterop
		{
			// Forces RideMap's stated-error branch so the Skia overlay stays unmounted.
			// Clicked is raised by the fake directly, so the picker seam is still live.
			InitException = new InvalidOperationException("No base map in this test host."),
		};
		context.Services.AddSingleton<IMapInterop>(map);
		return api;
	}

	[Fact]
	public void EveryCuratedIcon_IsRenderedAsADropDownOption()
	{
		WireServices(this);

		IRenderedComponent<AddMarker> component = Render<AddMarker>(parameters => parameters
			.Add(p => p.RideId, Guid.NewGuid()));

		// §16.2's curated set is authoritative — each key must be reachable in the picker.
		foreach (string key in DLR.Core.Markers.MarkerIcons.Known)
		{
			component.Markup.Contains($"value=\"{key}\"", StringComparison.Ordinal).ShouldBeTrue(
				$"the icon picker must expose the curated key '{key}' as an option value.");
		}
	}

	[Fact]
	public void IconOptions_CarryBothTheEmojiAndTheLabel()
	{
		WireServices(this);

		IRenderedComponent<AddMarker> component = Render<AddMarker>(parameters => parameters
			.Add(p => p.RideId, Guid.NewGuid()));

		// The key is what travels on the wire; the human picks by emoji and words. A bare
		// key list ("water-crossing") is what this replaced, so assert both halves render.
		component.Markup.Contains("Water crossing", StringComparison.Ordinal).ShouldBeTrue(
			"options are labelled in words, not with the raw curated key.");
		component.Markup.Contains(MarkerIconGlyphs.Emoji("hazard"), StringComparison.Ordinal).ShouldBeTrue(
			"§16.2: the client owns the drawing, and the picker's drawing is the colour emoji.");
	}

	[Fact]
	public void EveryCuratedIcon_HasItsOwnGlyph_AndUnknownKeysDegrade()
	{
		// A key added to MarkerIcons.Known without a glyph here would silently render as
		// the note emoji — reachable, but wrong and hard to spot by eye.
		foreach (string key in DLR.Core.Markers.MarkerIcons.Known)
		{
			if (key == DLR.Core.Markers.MarkerIcons.Fallback)
			{
				continue;
			}

			MarkerIconGlyphs.Emoji(key).ShouldNotBe(
				MarkerIconGlyphs.Emoji(DLR.Core.Markers.MarkerIcons.Fallback),
				$"'{key}' is curated, so it needs its own emoji rather than falling through to the note glyph.");
			MarkerIconGlyphs.Label(key).ShouldNotBe(key,
				$"'{key}' is curated, so it needs a human label rather than showing its raw key.");
		}

		// §16.2's forward-compatibility rule: a key from a newer client still draws.
		MarkerIconGlyphs.Emoji("ferry").ShouldBe(MarkerIconGlyphs.Emoji(DLR.Core.Markers.MarkerIcons.Fallback),
			"an unknown key degrades to the note glyph rather than throwing or drawing nothing.");
	}

	/// <summary>
	/// The composer offers no direction at all. The field is still on the marker — it is stored,
	/// it round-trips through GPX and the overlay rotates a pin that has one — but typing a
	/// bearing in degrees at the side of a road is not how anybody says which way a hazard faces,
	/// so this screen no longer asks. What it must never do is send a zero instead of a null:
	/// zero is due north (§16.2).
	/// </summary>
	[Fact]
	public void TheComposer_OffersNoDirectionControl()
	{
		WireServices(this);

		IRenderedComponent<AddMarker> component = Render<AddMarker>(parameters => parameters
			.Add(p => p.RideId, Guid.NewGuid()));

		component.Markup.IndexOf("Bearing", StringComparison.Ordinal).ShouldBe(-1,
			"the bearing box went with the switch — a hidden but present <input> would still submit.");
		component.Markup.IndexOf("direction", StringComparison.OrdinalIgnoreCase).ShouldBe(-1,
			"and so did the switch that revealed it.");
	}

	[Fact]
	public async Task SaveWithoutDirection_SendsNullDirection_NotZero()
	{
		FakeApiClient api = WireServices(this);

		IRenderedComponent<AddMarker> component = Render<AddMarker>(parameters => parameters
			.Add(p => p.RideId, Guid.NewGuid()));

		// Fill required fields: title. Lat/Lon default to the Sydney coords in _latDeg/_lonDeg.
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement titleInput = component.FindAll("input[placeholder='Gravel on the corner']").Single();
			titleInput.Change("Gravel");
		});

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement save = component
				.FindAll("button.primary")
				.First(b => b.TextContent.Contains("Save marker", StringComparison.Ordinal));
			save.Click();
		});

		component.WaitForAssertion(() => api.LastCreateMarkerRequest.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		CreateMarkerRequest sent = api.LastCreateMarkerRequest!;
		sent.DirectionDeg.ShouldBeNull(
			"§16.2: with the direction switch off, the request must carry null — never a default zero, which is a real bearing.");
		sent.Title.ShouldBe("Gravel");
		sent.Icon.ShouldBe("note", "the composer starts on the 'note' icon; assert it survives the round trip.");
	}

	/// <summary>
	/// A title is optional (§16.2): the icon is what carries the meaning of most pins, and
	/// "gravel" typed under a gravel pin is the word twice. An empty box has to reach the server
	/// as an untitled marker rather than being caught by a required field.
	/// </summary>
	[Fact]
	public async Task SaveWithNoTitle_IsAllowed_AndSendsAnEmptyOne()
	{
		FakeApiClient api = WireServices(this);

		IRenderedComponent<AddMarker> component = Render<AddMarker>(parameters => parameters
			.Add(p => p.RideId, Guid.NewGuid()));

		component.FindAll("input[placeholder='Gravel on the corner'][required]").ShouldBeEmpty(
			"a required attribute would have the browser refuse the submit before any of this runs.");

		// Nothing typed anywhere. The icon is the whole marker.
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement save = component
				.FindAll("button.primary")
				.First(b => b.TextContent.Contains("Save marker", StringComparison.Ordinal));
			save.Click();
		});

		component.WaitForAssertion(() => api.LastCreateMarkerRequest.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		api.LastCreateMarkerRequest!.Title.ShouldBeEmpty(
			"an untitled marker travels as an empty title — the overlay draws the plate alone.");
	}

	[Fact]
	public async Task TappingTheMap_SetsThePoint_AndItIsWhatGetsSaved()
	{
		FakeApiClient api = WireServices(this, out FakeMapInterop map);

		IRenderedComponent<AddMarker> component = Render<AddMarker>(parameters => parameters
			.Add(p => p.RideId, Guid.NewGuid()));

		// §16.1: the author points at the place instead of typing two decimal numbers.
		await component.InvokeAsync(() => map.RaiseClick(-37.81402, 144.96328));

		// The coordinate boxes are the second view of the same numbers — they must follow.
		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("-37.81402", StringComparison.Ordinal).ShouldBeTrue(
				"a tap must write the latitude into the coordinate box; the boxes and the map are one value, not two.");
			component.Markup.Contains("144.96328", StringComparison.Ordinal).ShouldBeTrue(
				"a tap must write the longitude into the coordinate box.");
		}, timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement titleInput = component.FindAll("input[placeholder='Gravel on the corner']").Single();
			titleInput.Change("Tram tracks");
		});

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement save = component
				.FindAll("button.primary")
				.First(b => b.TextContent.Contains("Save marker", StringComparison.Ordinal));
			save.Click();
		});

		component.WaitForAssertion(() => api.LastCreateMarkerRequest.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		CreateMarkerRequest sent = api.LastCreateMarkerRequest!;
		sent.Lat.ShouldBe(DLR.Core.Contracts.Rides.PositionScale.FromDegrees(-37.81402),
			"the tapped point — not the composer's default — is what reaches the server.");
		sent.Lon.ShouldBe(DLR.Core.Contracts.Rides.PositionScale.FromDegrees(144.96328));
	}

	[Fact]
	public void WithNoMapClickYet_ThePromptAsksForOne()
	{
		WireServices(this);

		IRenderedComponent<AddMarker> component = Render<AddMarker>(parameters => parameters
			.Add(p => p.RideId, Guid.NewGuid()));

		component.Markup.Contains("Tap the map to place the pin", StringComparison.Ordinal).ShouldBeTrue(
			"§16.1: tapping is the primary way to place a marker, so the composer has to say so before anything is placed.");
	}

	/// <summary>
	/// The live map hands the point over on the query string (§16.1). It arrives <em>placed</em>:
	/// the rider chose it on a full screen of map, and a composer that opened unplaced would be
	/// asking for the same answer a second time, in a 22 rem box.
	/// </summary>
	[Fact]
	public async Task APointHandedOverByTheLiveMap_OpensAlreadyPlaced_AndIsWhatGetsSaved()
	{
		FakeApiClient api = WireServices(this);
		Guid rideId = Guid.NewGuid();

		Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
			.NavigateTo($"/group-rides/{rideId}/markers/new?lat=-37.81402&lon=144.96328&zoom=16");

		IRenderedComponent<AddMarker> component = Render<AddMarker>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("-37.81402", StringComparison.Ordinal).ShouldBeTrue(
				"the handed-over point fills the coordinate boxes, which are the second view of it.");
			component.Markup.Contains("144.96328", StringComparison.Ordinal).ShouldBeTrue();
			component.Markup.Contains("Tap the map again to move the pin", StringComparison.Ordinal).ShouldBeTrue(
				"the pin is already down, so the prompt offers to move it rather than asking for a point " +
				"the rider has just given.");
		}, timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement titleInput = component.FindAll("input[placeholder='Gravel on the corner']").Single();
			titleInput.Change("Tram tracks");
		});

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement save = component
				.FindAll("button.primary")
				.First(b => b.TextContent.Contains("Save marker", StringComparison.Ordinal));
			save.Click();
		});

		component.WaitForAssertion(() => api.LastCreateMarkerRequest.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		CreateMarkerRequest sent = api.LastCreateMarkerRequest!;
		sent.Lat.ShouldBe(DLR.Core.Contracts.Rides.PositionScale.FromDegrees(-37.81402),
			"the point chosen on the live map is what reaches the server, without a second tap.");
		sent.Lon.ShouldBe(DLR.Core.Contracts.Rides.PositionScale.FromDegrees(144.96328));
	}
}
