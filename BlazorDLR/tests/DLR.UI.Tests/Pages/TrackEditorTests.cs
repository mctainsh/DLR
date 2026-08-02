using BlazorDLR.Shared.Pages;
using BlazorDLR.Shared.Services;
using Bunit;
using DLR.Core.Contracts.Tracks;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

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

	private static (FakeApiClient api, FakeFormFactor form) WireServices(BunitContext context, string formFactor)
	{
		FakeApiClient api = new()
		{
			TrackDetailResult = new TrackDetail(
				new TrackSummary(Guid.NewGuid(), "Test ride", FixedInstant, null, null, 3600, 25_000, 300, 100, 0, 1, TrackSourceDto.Recorded, 1),
				null,
				Array.Empty<DLR.Core.Tracks.TrackPoint>()),
			TrackPointsResult = new TrackPointsResponse(Version: 7, PointCount: 200, Polyline: "", TimeOffsets: null, ElevationDecimetres: null, SegmentStarts: new[] { 0 }),
		};
		FakeFormFactor form = new() { FormFactor = formFactor, Platform = formFactor };
		context.Services.AddSingleton<IApiClient>(api);
		context.Services.AddSingleton<IFormFactor>(form);
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
		sent.Version.ShouldBe(7, "the version quoted back is exactly the one the points response carried (§15.5's optimistic concurrency).");
		sent.Removals.Count.ShouldBe(1);
		sent.Removals[0].From.ShouldBe(12,
			"§15.5: the From index is passed through as a RAW point index, not into any simplified polyline.");
		sent.Removals[0].To.ShouldBe(40,
			"§15.5: the To index is likewise raw and half-open — the composer captures what the user typed and does not remap.");
	}
}
