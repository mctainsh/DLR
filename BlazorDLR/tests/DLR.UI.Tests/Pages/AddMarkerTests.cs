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
/// The map is out of scope: this test renders the composer against no map at all, which
/// is the whole point of the "editor is not the map" checkbox in SharedFrontend.md §7
/// Phase 4.
/// </summary>
public sealed class AddMarkerTests : BunitContext
{
	private static FakeApiClient WireServices(BunitContext context)
	{
		FakeApiClient api = new();
		context.Services.AddSingleton<IApiClient>(api);
		return api;
	}

	[Fact]
	public void EveryCuratedIcon_IsRenderedAsARadioOption()
	{
		WireServices(this);

		IRenderedComponent<AddMarker> component = Render<AddMarker>(parameters => parameters
			.Add(p => p.RideId, Guid.NewGuid()));

		// §16.2's curated set is authoritative — each key must appear as a radio.
		foreach (string key in DLR.Core.Markers.MarkerIcons.Known)
		{
			component.Markup.Contains($"value=\"{key}\"", StringComparison.Ordinal).ShouldBeTrue(
				$"the icon picker must expose the curated key '{key}' as a radio value.");
		}
	}

	[Fact]
	public void DirectionSwitch_IsOffByDefault_AndBearingFieldIsHidden()
	{
		WireServices(this);

		IRenderedComponent<AddMarker> component = Render<AddMarker>(parameters => parameters
			.Add(p => p.RideId, Guid.NewGuid()));

		// The bearing input is inside an @if(_hasDirection) block; when off, it must not exist.
		int bearingLabels = component.Markup.IndexOf("Bearing", StringComparison.Ordinal);
		bearingLabels.ShouldBe(-1,
			"§16.2: null-not-zero. With the switch off the bearing field must not exist — a hidden but present <input> would still submit a value.");
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

	[Fact]
	public async Task SaveWithDirection_SendsBearingAsAShort()
	{
		FakeApiClient api = WireServices(this);

		IRenderedComponent<AddMarker> component = Render<AddMarker>(parameters => parameters
			.Add(p => p.RideId, Guid.NewGuid()));

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement titleInput = component.FindAll("input[placeholder='Gravel on the corner']").Single();
			titleInput.Change("Crest");
		});

		// Flip the "has direction" switch.
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement hasDir = component.FindAll("input[type=checkbox]").Single();
			hasDir.Change(true);
		});

		// Type a bearing.
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement bearing = component
				.FindAll("input[type=number]")
				.Last(); // last number input is the bearing (lat/lon come first).
			bearing.Change("270");
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
		sent.DirectionDeg.ShouldBe((short)270,
			"§16.2: with the switch on and 270 typed, the request carries 270 as a short — the DTO shape.");
	}
}
