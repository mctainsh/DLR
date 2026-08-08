using BlazorDLR.Shared.Components;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.State;
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
/// The trim composer end to end (§15.5). The index arithmetic is asserted in
/// <see cref="Tracks.TrackTrimSessionTests"/>; what this file covers is the wiring around it:
/// <list type="bullet">
///   <item>the composer loads on every form factor — §6.1's web-only gate was removed, and a
///     test that renders it as a phone is what stops the gate coming back;</item>
///   <item>a tap on the map places the cursor, and the trim buttons appear only once it has;</item>
///   <item>nothing reaches the API until Apply, and Apply asks first;</item>
///   <item>what Apply sends is raw half-open index ranges quoting the points response's version.</item>
/// </list>
/// </summary>
public sealed class TrackEditorTests : BunitContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	/// <summary>A degree either way across a 1000 px canvas, so a tap's pixel distance is readable.</summary>
	private static readonly MapViewport UnitViewport = new(
		TopLeftLatitude: 1, TopLeftLongitude: -1,
		BottomRightLatitude: -1, BottomRightLongitude: 1,
		ZoomLevel: 12, HeadingDeg: 0,
		CanvasWidthPx: 1000, CanvasHeightPx: 1000, DevicePixelRatio: 1);

	/// <summary>Eleven points along the equator, 0.1° apart — index 5 sits on the origin.</summary>
	private static readonly TrackPoint[] Line =
	[
		.. Enumerable.Range(0, 11).Select(index => new TrackPoint(0, -0.5 + (index * 0.1))),
	];

	private (FakeApiClient api, FakeMapInterop map) WireServices(string formFactor = "Desktop")
	{
		FakeApiClient api = new()
		{
			TrackDetailResult = new TrackDetail(
				new TrackSummary(Guid.NewGuid(), "Test ride", FixedInstant, null, null, 25_000, 3600, 300, null, 11, 1, TrackSourceDto.Recorded, 1),
				new TrackBounds(0, -0.5, 0, 0.5),
				Line),
			TrackPointsResult = new TrackPointsResponse(
				Version: 7,
				PointCount: Line.Length,
				Polyline: PolylineCodec.EncodePoints(Line),
				TimeOffsets: null,
				ElevationDecimetres: null,
				SegmentStarts: [0]),
		};

		// InitAsync succeeds but reports no viewport of its own: the tests raise the frames they
		// want. SkiaMapOverlay's SKCanvasView is browser-only, so StubRideMap draws nothing.
		FakeMapInterop map = new();

		Services.AddSingleton<IApiClient>(api);
		Services.AddSingleton<IFormFactor>(new FakeFormFactor { FormFactor = formFactor, Platform = formFactor });
		Services.AddSingleton<IMapInterop>(map);
		Services.AddSingleton(new ConfirmService());
		ComponentFactories.Add<RideMap, StubRideMap>();

		return (api, map);
	}

	private IRenderedComponent<TrackEditor> RenderEditor()
	{
		IRenderedComponent<TrackEditor> component = Render<TrackEditor>(parameters => parameters
			.Add(p => p.TrackId, Guid.NewGuid()));

		component.WaitForAssertion(
			() => component.Markup.Contains("points remain", StringComparison.Ordinal).ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		return component;
	}

	/// <summary>Answers the confirm dialog the way a rider would, without rendering it.</summary>
	private async Task AnswerConfirmAsync(IRenderedComponent<TrackEditor> component, bool accept)
	{
		ConfirmService confirm = Services.GetRequiredService<ConfirmService>();

		component.WaitForAssertion(() => confirm.Current.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => confirm.Respond(accept));
	}

	/// <summary>
	/// A trim button, by its exact face. Exact and not Contains: "◀ 1" is a prefix of
	/// "◀ 10" and sits after it in the DOM, so a substring match clicks the wrong one.
	/// </summary>
	private static AngleSharp.Dom.IElement Button(IRenderedComponent<TrackEditor> component, string face) =>
		component.FindAll("button").First(b => string.Equals(b.TextContent.Trim(), face, StringComparison.Ordinal));

	/// <summary>A button whose face carries a changing count — Undo and Apply.</summary>
	private static AngleSharp.Dom.IElement ButtonStarting(IRenderedComponent<TrackEditor> component, string prefix) =>
		component.FindAll("button").First(b => b.TextContent.Trim().StartsWith(prefix, StringComparison.Ordinal));

	/// <summary>
	/// The survivor count line as a human reads it. Asserted against text rather than markup
	/// because the numbers render inside <c>&lt;strong&gt;</c> — "9 of 11" is one phrase on
	/// screen and three nodes in the DOM.
	/// </summary>
	private static string Lead(IRenderedComponent<TrackEditor> component) =>
		component.Find("p.lead").TextContent;

	/// <summary>The cursor marker the page hands the map. There is only ever one.</summary>
	private static MapMarker Pin(IRenderedComponent<TrackEditor> component) =>
		component.FindComponent<StubRideMap>().Instance.Markers!.Values.Single();

	[Fact]
	public void OnAPhone_LoadsTheComposer_RatherThanSendingTheRiderToADesktop()
	{
		(FakeApiClient api, _) = WireServices(formFactor: "Phone");

		IRenderedComponent<TrackEditor> component = RenderEditor();

		component.Markup.Contains("Track editing lives on the web", StringComparison.Ordinal).ShouldBeFalse(
			"the note that sent a phone to a desktop browser is gone.");
		api.Calls.ShouldContain("GetTrackPointsAsync",
			"the raw points are the index space the cursor lands in — a host that can edit must fetch them.");
	}

	[Fact]
	public void DrawsTheTrackAndFramesTheMapOnIt()
	{
		WireServices();

		IRenderedComponent<TrackEditor> component = RenderEditor();

		RideMap drawn = component.FindComponent<StubRideMap>().Instance;

		// §15.5: the line is encoded by the one codec, and the overlay decodes with the same
		// one — a precision mismatch here is what once drew every track a continent away and
		// left the map looking empty rather than wrong.
		drawn.Route.ShouldNotBeNull("a track editor that draws no track cannot be tapped.");
		PolylineCodec.DecodePoints(drawn.Route!.EncodedPolyline)
			.Select(point => point.Longitude)
			.ShouldBe(Line.Select(point => point.Longitude), tolerance: 1e-6);

		drawn.Camera.Longitude.ShouldBe(0, tolerance: 1e-9, "framed on the bounding box.");
	}

	[Fact]
	public async Task TheCursorPin_TravelsWithTheCut()
	{
		(_, FakeMapInterop map) = WireServices();

		IRenderedComponent<TrackEditor> component = RenderEditor();

		await component.InvokeAsync(() => map.RaiseViewport(UnitViewport));
		await component.InvokeAsync(() => map.RaiseClick(0, 0));

		component.WaitForAssertion(() => Pin(component).Longitude.ShouldBe(0, tolerance: 1e-9),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => Button(component, "◀ 1").Click());

		component.WaitForAssertion(
			() => Pin(component).Longitude.ShouldBe(-0.1, tolerance: 1e-9,
				"the pin is drawn from the session cursor, so a back-trim walks it one point down the line."),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task TrimButtons_AppearOnlyOnceATapHasPlacedTheCursor()
	{
		(_, FakeMapInterop map) = WireServices();

		IRenderedComponent<TrackEditor> component = RenderEditor();

		component.Markup.Contains("Tap a point on the track", StringComparison.Ordinal).ShouldBeTrue(
			"before a cursor there is no 'before' or 'after' to trim from, so the page asks for one.");
		component.FindAll("button").Any(b => b.TextContent.Contains("◀ 10", StringComparison.Ordinal))
			.ShouldBeFalse();

		await component.InvokeAsync(() => map.RaiseViewport(UnitViewport));
		await component.InvokeAsync(() => map.RaiseClick(0, 0));

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("Cursor on point <strong>5</strong>", StringComparison.Ordinal).ShouldBeTrue(
				"the tap at the origin is on the sixth point of the line.");
			component.FindAll("button").Any(b => b.TextContent.Contains("◀ 10", StringComparison.Ordinal))
				.ShouldBeTrue();
			component.Markup.Contains("◀ Back", StringComparison.Ordinal).ShouldBeTrue();
			component.Markup.Contains("Forward ▶", StringComparison.Ordinal).ShouldBeTrue();
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task ATapThatMissesTheLine_LeavesTheCursorAlone()
	{
		(_, FakeMapInterop map) = WireServices();

		IRenderedComponent<TrackEditor> component = RenderEditor();

		await component.InvokeAsync(() => map.RaiseViewport(UnitViewport));
		await component.InvokeAsync(() => map.RaiseClick(0.5, 0));

		component.WaitForAssertion(
			() => component.Markup.Contains("missed the track", StringComparison.Ordinal).ShouldBeTrue(
				"a tap on bare map must not drag the cursor to whichever end of the ride was nearest."),
			timeout: TimeSpan.FromSeconds(3));

		component.Markup.Contains("Cursor on point", StringComparison.Ordinal).ShouldBeFalse();
	}

	[Fact]
	public async Task TrimsAccumulateOnTheClient_AndSendNothingUntilApply()
	{
		(FakeApiClient api, FakeMapInterop map) = WireServices();

		IRenderedComponent<TrackEditor> component = RenderEditor();

		await component.InvokeAsync(() => map.RaiseViewport(UnitViewport));
		await component.InvokeAsync(() => map.RaiseClick(0, 0));
		await component.InvokeAsync(() => Button(component, "◀ 1").Click());
		await component.InvokeAsync(() => Button(component, "◀ 1").Click());

		component.WaitForAssertion(
			() => component.Markup.Contains("remove 2 point(s)", StringComparison.Ordinal).ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		api.LastEditTrackRequest.ShouldBeNull(
			"§15.5 rule 2: trims live on the client until Apply. Nothing has been sent.");
		Lead(component).ShouldContain("9 of 11 points remain");
		component.Markup.Contains("Cursor on point <strong>3</strong>", StringComparison.Ordinal).ShouldBeTrue(
			"the cursor travels with the cut: two back-trims from 5 eat 5 then 4 and leave it on 3.");
	}

	[Fact]
	public async Task Undo_WalksTheClientHistoryBack_OneTrimAtATime()
	{
		(_, FakeMapInterop map) = WireServices();

		IRenderedComponent<TrackEditor> component = RenderEditor();

		await component.InvokeAsync(() => map.RaiseViewport(UnitViewport));
		await component.InvokeAsync(() => map.RaiseClick(0, 0));
		await component.InvokeAsync(() => Button(component, "◀ 1").Click());
		await component.InvokeAsync(() => Button(component, "◀ 1").Click());

		component.WaitForAssertion(
			() => component.Markup.Contains("Undo (2)", StringComparison.Ordinal).ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => ButtonStarting(component, "Undo").Click());

		component.WaitForAssertion(
			() => Lead(component).ShouldContain("10 of 11 points remain"),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => ButtonStarting(component, "Undo").Click());

		component.WaitForAssertion(() =>
		{
			Lead(component).ShouldContain("11 of 11 points remain",
				customMessage: "undo reaches all the way back to the track as loaded.");
			ButtonStarting(component, "Apply").HasAttribute("disabled").ShouldBeTrue(
				"with nothing struck out there is nothing to apply.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Apply_SendsRawHalfOpenRangesQuotingThePointsVersion()
	{
		(FakeApiClient api, FakeMapInterop map) = WireServices();

		IRenderedComponent<TrackEditor> component = RenderEditor();

		await component.InvokeAsync(() => map.RaiseViewport(UnitViewport));
		await component.InvokeAsync(() => map.RaiseClick(0, 0));
		await component.InvokeAsync(() => Button(component, "◀ 1").Click());
		await component.InvokeAsync(() => Button(component, "◀ 1").Click());
		await component.InvokeAsync(() => ButtonStarting(component, "Apply").Click());

		await AnswerConfirmAsync(component, accept: true);

		component.WaitForAssertion(() => api.LastEditTrackRequest.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		EditTrackRequest sent = api.LastEditTrackRequest!;

		sent.Version.ShouldBe(7,
			"the version quoted back is the one the points response carried (optimistic concurrency).");
		sent.Removals.ShouldBe([new IndexRange(4, 6)],
			"§15.5: two −1 back-trims from the cursor at 5 eat 5 then 4, as one half-open range.");
	}

	[Fact]
	public async Task Apply_AsksFirst_AndCancellingSendsNothing()
	{
		(FakeApiClient api, FakeMapInterop map) = WireServices();

		IRenderedComponent<TrackEditor> component = RenderEditor();

		await component.InvokeAsync(() => map.RaiseViewport(UnitViewport));
		await component.InvokeAsync(() => map.RaiseClick(0, 0));
		await component.InvokeAsync(() => Button(component, "◀ 1").Click());
		await component.InvokeAsync(() => ButtonStarting(component, "Apply").Click());

		await AnswerConfirmAsync(component, accept: false);

		api.LastEditTrackRequest.ShouldBeNull(
			"the commit is the point of no return, so the safe answer has to be the accidental one.");
		Lead(component).ShouldContain("10 of 11 points remain",
			customMessage: "declining leaves the working copy exactly as it was.");
	}

	[Fact]
	public async Task AfterApply_TheHistoryIsGone_AndNoServerSideUndoIsOffered()
	{
		(_, FakeMapInterop map) = WireServices();

		IRenderedComponent<TrackEditor> component = RenderEditor();

		await component.InvokeAsync(() => map.RaiseViewport(UnitViewport));
		await component.InvokeAsync(() => map.RaiseClick(0, 0));
		await component.InvokeAsync(() => Button(component, "◀ 1").Click());
		await component.InvokeAsync(() => ButtonStarting(component, "Apply").Click());

		await AnswerConfirmAsync(component, accept: true);

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("Applied.", StringComparison.Ordinal).ShouldBeTrue();

			component.FindAll("button")
				.Where(b => b.TextContent.Contains("Undo", StringComparison.Ordinal))
				.ShouldBeEmpty(
					"the client history does not survive a commit. The page reloads into a fresh session "
					+ "with no cursor and no steps, so the trim block — Undo included — is not even drawn.");

			component.Markup.Contains("retained until", StringComparison.Ordinal).ShouldBeFalse(
				"an undo offered after the point of no return is one that gets trusted at the wrong moment.");
			component.Markup.Contains("Remove the original now", StringComparison.Ordinal).ShouldBeFalse();
		}, timeout: TimeSpan.FromSeconds(3));
	}
}
