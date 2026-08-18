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
/// The composer does <em>not</em> own the point: §16.1 places a marker by tapping the live
/// ride map, and that tap hands the point over on the query string. This screen takes it as
/// an input — so almost every test here renders at a point, and the one that does not is
/// asserting what the screen does when nobody supplied one.
/// </summary>
public sealed class AddMarkerTests : PageTestContext
{
	private const double Lat = -37.81402;
	private const double Lon = 144.96328;

	private static FakeApiClient WireServices(BunitContext context)
	{
		FakeApiClient api = new();
		context.Services.AddSingleton<IApiClient>(api);

		// Still bound although this screen no longer draws a map: the composer's own picker is
		// gone, but leaving the seam registered keeps an accidental re-add failing here as the
		// behaviour change it would be rather than as a missing service.
		context.Services.AddSingleton<IMapInterop>(new FakeMapInterop());
		return api;
	}

	/// <summary>
	/// Renders the composer the way the live map opens it — with the chosen point on the query
	/// string, which is the only route into this screen.
	/// </summary>
	private IRenderedComponent<AddMarker> RenderAt(Guid rideId, double lat = Lat, double lon = Lon)
	{
		Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
			.NavigateTo(FormattableString.Invariant(
				$"/group-rides/{rideId}/markers/new?lat={lat}&lon={lon}"));

		return Render<AddMarker>(parameters => parameters.Add(p => p.RideId, rideId));
	}

	private static async Task Save(IRenderedComponent<AddMarker> component) =>
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement save = component
				.FindAll("button.primary")
				.First(b => b.TextContent.Contains("Save marker", StringComparison.Ordinal));
			save.Click();
		});

	/// <summary>
	/// The picker opens shut: one row showing the icon that is chosen and its name. The grid of
	/// twenty-nine pictures is the right way to answer "which icon" and the wrong thing to leave
	/// standing above the title box once it is answered.
	/// <para>
	/// Shut means hidden, not unrendered — the radios stay in the DOM so the checked state and
	/// the group live in one place. That is why this asserts on the class rather than on the
	/// cells being gone.
	/// </para>
	/// </summary>
	[Fact]
	public void ThePicker_OpensCollapsed_ShowingOnlyTheChosenIcon()
	{
		WireServices(this);

		IRenderedComponent<AddMarker> component = RenderAt(Guid.NewGuid());

		component.FindAll(".icon-grid.shut").ShouldNotBeEmpty(
			"the grid starts collapsed — the choice is one tap, and the cells that were not it cost the screen below them.");
		component.Find(".icon-current").GetAttribute("aria-expanded").ShouldBe("false");
		component.Find(".icon-current-name").TextContent.Trim().ShouldBe(
			MarkerIconGlyphs.Label(DLR.Core.Markers.MarkerIcons.Fallback),
			"the collapsed row names the marker that is currently chosen.");
	}

	/// <summary>
	/// Tapping the collapsed row brings the whole list back, which is the only way to change the
	/// choice once it is made.
	/// </summary>
	[Fact]
	public async Task TappingTheChosenIcon_ShowsTheFullListAgain()
	{
		WireServices(this);

		IRenderedComponent<AddMarker> component = RenderAt(Guid.NewGuid());

		await component.InvokeAsync(() => component.Find(".icon-current").Click());

		component.FindAll(".icon-grid.open").ShouldNotBeEmpty("tapping the row opens the grid.");
		component.Find(".icon-current").GetAttribute("aria-expanded").ShouldBe("true");
	}

	/// <summary>
	/// And picking from the open list closes it again, on the icon that was picked — the row is
	/// then the answer, not the question.
	/// </summary>
	[Fact]
	public async Task ChoosingAnIcon_CollapsesTheListOntoThatIcon()
	{
		WireServices(this);

		IRenderedComponent<AddMarker> component = RenderAt(Guid.NewGuid());

		await component.InvokeAsync(() => component.Find(".icon-current").Click());
		await component.InvokeAsync(() =>
			component.FindAll("input[type=radio][value=water-crossing]").Single().Change("water-crossing"));

		component.FindAll(".icon-grid.shut").ShouldNotBeEmpty("picking an icon answers the question and closes the list.");
		component.Find(".icon-current-name").TextContent.Trim().ShouldBe("Water crossing",
			"and the row that is left shows what was picked, by name.");
	}

	/// <summary>
	/// The composer's one radio group is still a radio group when it is collapsed: every curated
	/// key stays in the DOM so the checked state, the arrow keys and this file's other assertions
	/// all keep working without opening anything first.
	/// </summary>
	[Fact]
	public void EveryCuratedIcon_IsRenderedAsAPickerChoice()
	{
		WireServices(this);

		IRenderedComponent<AddMarker> component = RenderAt(Guid.NewGuid());

		// §16.2's curated set is authoritative — each key must be reachable in the picker.
		foreach (string key in DLR.Core.Markers.MarkerIcons.Known)
		{
			component.Markup.Contains($"value=\"{key}\"", StringComparison.Ordinal).ShouldBeTrue(
				$"the icon picker must expose the curated key '{key}' as a choice value.");
		}
	}

	[Fact]
	public void IconOptions_CarryBothTheIconAndTheLabel()
	{
		WireServices(this);

		IRenderedComponent<AddMarker> component = RenderAt(Guid.NewGuid());

		// The key is what travels on the wire; the human picks by picture and words. A bare
		// key list ("water-crossing") is what this replaced, so assert both halves render.
		component.Markup.Contains("Water crossing", StringComparison.Ordinal).ShouldBeTrue(
			"choices are labelled in words, not with the raw curated key.");
		component.Markup.Contains(MarkerIconGlyphs.AssetPath("hazard"), StringComparison.Ordinal).ShouldBeTrue(
			"§16.2: the client owns the drawing, and the picker's drawing is the icon PNG.");
	}

	[Fact]
	public void EveryCuratedIcon_HasItsOwnArtwork_AndUnknownKeysDegrade()
	{
		// A key added to MarkerIcons.Known without an entry here would silently render as
		// the note icon — reachable, but wrong and hard to spot by eye.
		foreach (string key in DLR.Core.Markers.MarkerIcons.Known)
		{
			if (key == DLR.Core.Markers.MarkerIcons.Fallback)
			{
				continue;
			}

			MarkerIconGlyphs.AssetPath(key).ShouldNotBe(
				MarkerIconGlyphs.AssetPath(DLR.Core.Markers.MarkerIcons.Fallback),
				$"'{key}' is curated, so it needs its own artwork rather than falling through to the note icon.");
			MarkerIconGlyphs.Label(key).ShouldNotBe(key,
				$"'{key}' is curated, so it needs a human label rather than showing its raw key.");
		}

		// §16.2's forward-compatibility rule: a key from a newer client still draws. It must
		// resolve to the note icon rather than to markers/ferry.png, which would 404 and leave
		// a broken-image box on the map.
		MarkerIconGlyphs.AssetPath("ferry").ShouldBe(
			MarkerIconGlyphs.AssetPath(DLR.Core.Markers.MarkerIcons.Fallback),
			"an unknown key degrades to the note icon rather than to a URL that does not exist.");
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

		IRenderedComponent<AddMarker> component = RenderAt(Guid.NewGuid());

		component.Markup.IndexOf("Bearing", StringComparison.Ordinal).ShouldBe(-1,
			"the bearing box went with the switch — a hidden but present <input> would still submit.");
		component.Markup.IndexOf("direction", StringComparison.OrdinalIgnoreCase).ShouldBe(-1,
			"and so did the switch that revealed it.");
	}

	/// <summary>
	/// Picking an icon actually changes what is sent.
	/// <para>
	/// The picker is a radio group rather than a <c>&lt;select&gt;</c>, because an option element
	/// may hold text and nothing else and the icons are pictures. Radios put the checked state in
	/// the DOM where the browser also mutates it, which is the classic way for a rebuild of this
	/// control to look right and bind nothing — every other test here would still pass, because
	/// they all save on the default key.
	/// </para>
	/// </summary>
	[Fact]
	public async Task ChoosingAnIcon_SendsThatKey()
	{
		FakeApiClient api = WireServices(this);

		IRenderedComponent<AddMarker> component = RenderAt(Guid.NewGuid());

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement choice = component.FindAll("input[type=radio][value=water-crossing]").Single();
			choice.Change("water-crossing");
		});

		await Save(component);

		component.WaitForAssertion(() => api.LastCreateMarkerRequest.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		api.LastCreateMarkerRequest!.Icon.ShouldBe("water-crossing",
			"the key the traveller picked is the key that travels on the wire (§16.2).");
	}

	[Fact]
	public async Task SaveWithoutDirection_SendsNullDirection_NotZero()
	{
		FakeApiClient api = WireServices(this);

		IRenderedComponent<AddMarker> component = RenderAt(Guid.NewGuid());

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement titleInput = component.FindAll("input[placeholder='Gravel on the corner']").Single();
			titleInput.Change("Gravel");
		});

		await Save(component);

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

		IRenderedComponent<AddMarker> component = RenderAt(Guid.NewGuid());

		component.FindAll("input[placeholder='Gravel on the corner'][required]").ShouldBeEmpty(
			"a required attribute would have the browser refuse the submit before any of this runs.");

		// Nothing typed anywhere. The icon is the whole marker.
		await Save(component);

		component.WaitForAssertion(() => api.LastCreateMarkerRequest.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		api.LastCreateMarkerRequest!.Title.ShouldBeEmpty(
			"an untitled marker travels as an empty title — the overlay draws the plate alone.");
	}

	/// <summary>
	/// The point is an input to this screen, not something it collects (§16.1). The composer
	/// used to re-show it in a 22 rem map with coordinate boxes under it, asking the rider for
	/// an answer they had just given on a full screen of map.
	/// </summary>
	[Fact]
	public void TheComposer_OffersNoPlacePicker()
	{
		WireServices(this);

		IRenderedComponent<AddMarker> component = RenderAt(Guid.NewGuid(), lat: -37.81402, lon: 144.96328);

		component.FindAll(".picker").ShouldBeEmpty("the second map is gone — the point arrives decided.");
		component.FindAll("input[type=number]").ShouldBeEmpty(
			"and so did the coordinate boxes that were its other half.");
		component.Markup.IndexOf("Tap the map", StringComparison.OrdinalIgnoreCase).ShouldBe(-1,
			"nothing on this screen asks for a point any more.");
	}

	/// <summary>
	/// The live map hands the point over on the query string (§16.1), and that is the point
	/// that gets filed — no second tap, and no default standing in for one.
	/// </summary>
	[Fact]
	public async Task ThePointHandedOverByTheLiveMap_IsWhatGetsSaved()
	{
		FakeApiClient api = WireServices(this);

		IRenderedComponent<AddMarker> component = RenderAt(Guid.NewGuid(), lat: -37.81402, lon: 144.96328);

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement titleInput = component.FindAll("input[placeholder='Gravel on the corner']").Single();
			titleInput.Change("Tram tracks");
		});

		await Save(component);

		component.WaitForAssertion(() => api.LastCreateMarkerRequest.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		CreateMarkerRequest sent = api.LastCreateMarkerRequest!;
		sent.Lat.ShouldBe(DLR.Core.Contracts.Rides.PositionScale.FromDegrees(-37.81402),
			"the point chosen on the live map is what reaches the server, without a second tap.");
		sent.Lon.ShouldBe(DLR.Core.Contracts.Rides.PositionScale.FromDegrees(144.96328));
		sent.Title.ShouldBe("Tram tracks");
	}

	/// <summary>
	/// Reached without a point — a bookmark, or a hand-typed URL. There is nothing to compose,
	/// and the screen has to say so: a composer that rendered its form here would file a
	/// plausible-looking marker at whatever the fields defaulted to, onto a ride people are
	/// following.
	/// </summary>
	[Fact]
	public void WithNoPointOnTheQueryString_TheFormIsNotOffered()
	{
		FakeApiClient api = WireServices(this);

		IRenderedComponent<AddMarker> component = Render<AddMarker>(parameters => parameters
			.Add(p => p.RideId, Guid.NewGuid()));

		component.FindAll("form").ShouldBeEmpty(
			"with no point there is no marker to compose — offering the form invites a pin in the wrong place.");
		component.Markup.Contains("point you have already chosen", StringComparison.Ordinal).ShouldBeTrue(
			"and the screen says where the point is meant to come from rather than failing silently.");
		component.FindAll("a.button").ShouldNotBeEmpty("with a way back to the adventure to go and choose one.");

		api.Calls.ShouldBeEmpty(
			"and it asks the server nothing: the adventure read only ever existed to centre the picker that is gone.");
	}
}
