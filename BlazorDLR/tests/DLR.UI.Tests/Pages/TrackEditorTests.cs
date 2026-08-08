using BlazorDLR.Shared.Components;
using BlazorDLR.Shared.Pages;
using BlazorDLR.Shared.Services;
using Bunit;
using DLR.Core.Contracts.Tracks;
using DLR.Core.Tracks;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

// DLR.Core.Tracks carries a TrackEditor of its own — the domain operation that applies the
// removals. This file is about the page, so the bare name is pinned to it.
using TrackEditor = BlazorDLR.Shared.Pages.TrackEditor;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// §15.5's raw-versus-simplified rule. The editor speaks in indices into the raw
/// point list from <c>GET /tracks/{id}/points</c>, never into the simplified polyline
/// the map draws. Two behaviours are load-bearing and both are checked here:
/// <list type="bullet">
///   <item>on desktop the editor loads; on mobile the composer is hidden and a note
///     tells the reader where to go (§6.1);</item>
///   <item>ranges the user adds are passed through to <c>EditTrackAsync</c> verbatim as
///     raw indices — the point count on the point response is the domain the numbers
///     are in.</item>
/// </list>
/// </summary>
public sealed class TrackEditorTests : BunitContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private static (FakeApiClient api, FakeFormFactor form) WireServices(
		BunitContext context,
		string formFactor,
		TrackBounds? bounds = null,
		IReadOnlyList<TrackPoint>? polyline = null)
	{
		FakeApiClient api = new()
		{
			TrackDetailResult = new TrackDetail(
				new TrackSummary(Guid.NewGuid(), "Test ride", FixedInstant, null, null, 3600, 25_000, 300, 100, 0, 1, TrackSourceDto.Recorded, 1),
				bounds,
				polyline ?? Array.Empty<TrackPoint>()),
			TrackPointsResult = new TrackPointsResponse(Version: 7, PointCount: 200, Polyline: "", TimeOffsets: null, ElevationDecimetres: null, SegmentStarts: new[] { 0 }),
		};
		FakeFormFactor form = new() { FormFactor = formFactor, Platform = formFactor };
		context.Services.AddSingleton<IApiClient>(api);
		context.Services.AddSingleton<IFormFactor>(form);

		// The editor draws the track it is trimming. InitAsync throws so RideMap takes its
		// stated-error branch and never reports a viewport — SkiaMapOverlay's SKCanvasView is
		// browser-only and would throw on render.
		context.Services.AddSingleton<IMapInterop>(new FakeMapInterop
		{
			InitException = new InvalidOperationException("Test host — map interop is stubbed."),
		});
		return (api, form);
	}

	[Fact]
	public void OnMobile_ShowsBigScreenNote_AndDoesNotLoadPoints()
	{
		(FakeApiClient api, _) = WireServices(this, formFactor: "Phone");

		IRenderedComponent<TrackEditor> component = Render<TrackEditor>(parameters => parameters
			.Add(p => p.TrackId, Guid.NewGuid()));

		component.Markup.Contains("Track editing lives on the web", StringComparison.Ordinal).ShouldBeTrue(
			"§6.1: on a phone the editor renders the redirect note, not the composer.");
		api.Calls.ShouldNotContain("GetTrackPointsAsync",
			"loading the point stream on a device that cannot edit is wasted bytes over a mobile connection.");
	}

	[Fact]
	public void OnDesktop_DrawsTheTrackAndFramesTheMapOnIt()
	{
		TrackPoint[] polyline =
		[
			new(-33.8688, 151.2093),
			new(-33.8700, 151.2110),
			new(-33.8725, 151.2148),
		];
		WireServices(this, formFactor: "Desktop",
			bounds: new TrackBounds(-33.8725, 151.2093, -33.8688, 151.2148),
			polyline: polyline);

		// The map's own logic is real; only SkiaMapOverlay's browser-only canvas is dropped.
		ComponentFactories.Add<RideMap, StubRideMap>();

		IRenderedComponent<TrackEditor> component = Render<TrackEditor>(parameters => parameters
			.Add(p => p.TrackId, Guid.NewGuid()));

		component.WaitForAssertion(() =>
		{
			RideMap map = component.FindComponent<StubRideMap>().Instance;

			// §15.5: the line the editor draws is the simplified polyline off the detail
			// endpoint, encoded by the one codec — the overlay decodes with the same one, so a
			// precision mismatch cannot put the track a continent away and leave the map blank.
			map.Route.ShouldNotBeNull("a track editor that draws no track is the bug this asserts against.");
			PolylineCodec.DecodePoints(map.Route!.EncodedPolyline)
				.Select(point => point.Latitude)
				.ShouldBe(polyline.Select(point => point.Latitude), tolerance: 1e-6);

			// Framed on the bounding box, so the line opens in view rather than at null island.
			map.Camera.Latitude.ShouldBe(-33.87065, tolerance: 1e-6);
			map.Camera.Longitude.ShouldBe(151.21205, tolerance: 1e-6);
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task CommitAsync_PassesUserAddedRangesAsRawIndicesToTheApi()
	{
		(FakeApiClient api, _) = WireServices(this, formFactor: "Desktop");

		IRenderedComponent<TrackEditor> component = Render<TrackEditor>(parameters => parameters
			.Add(p => p.TrackId, Guid.NewGuid()));

		// Wait for the async OnParametersSetAsync to load track + points and re-render.
		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("Point count:", StringComparison.Ordinal).ShouldBeTrue();
		}, timeout: TimeSpan.FromSeconds(3));

		// Fill From/To and Add. bUnit v2 requires a fresh FindAll + InvokeAsync per interaction
		// so event handler IDs stay valid across the re-renders each change triggers.
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement[] inputs = component.FindAll("input[type=number]").ToArray();
			inputs.Length.ShouldBeGreaterThanOrEqualTo(2);
			inputs[0].Change("12");
		});
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement[] inputs = component.FindAll("input[type=number]").ToArray();
			inputs[1].Change("40");
		});
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement addRange = component
				.FindAll("button")
				.First(b => b.TextContent.Contains("Add range", StringComparison.Ordinal));
			addRange.Click();
		});
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement apply = component
				.FindAll("button")
				.First(b => b.TextContent.Contains("Apply", StringComparison.Ordinal));
			apply.Click();
		});

		component.WaitForAssertion(() => api.LastEditTrackRequest.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		EditTrackRequest sent = api.LastEditTrackRequest!;
		sent.Version.ShouldBe(7, "the version quoted back is exactly the one the points response carried optimistic concurrency).");
		sent.Removals.Count.ShouldBe(1);
		sent.Removals[0].From.ShouldBe(12,
			"§15.5: the From index is passed through as a RAW point index, not into any simplified polyline.");
		sent.Removals[0].To.ShouldBe(40,
			"§15.5: the To index is likewise raw and half-open — the composer captures what the user typed and does not remap.");
	}
}
