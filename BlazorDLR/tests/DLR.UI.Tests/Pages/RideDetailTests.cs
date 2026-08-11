using BlazorDLR.Shared.Pages;
using BlazorDLR.Shared.Services;
using Bunit;
using DLR.Core.Contracts.Tracks;
using DLR.Core.Tracks;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// The signed-in track detail page. Two properties:
/// <list type="bullet">
///   <item>The header exposes both the Edit link (route to <c>TrackEditor</c>) and the
///     GPX download button — losing either would silently strand the two operations
///     the design outline names for a stored track (§15.5, §15.7).</item>
///   <item>The stats row honours the null-vs-zero rule: a null ascent renders as "—",
///     a zero-second duration renders as an em dash from the null formatter, and a
///     zero-metre distance renders as "0.0 km" — because zero is a real number for
///     distance (§8).</item>
/// </list>
/// </summary>
public sealed class RideDetailTests : PageTestContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private FakeTrackRepository WireServices(TrackDetail? detail)
	{
		FakeTrackRepository repo = new() { DetailResult = detail };
		Services.AddSingleton<ITrackRepository>(repo);
		Services.AddSingleton<IApiClient>(new FakeApiClient());
		Services.AddSingleton<IMapInterop>(new FakeMapInterop
		{
			InitException = new InvalidOperationException("Test host — map interop is stubbed."),
		});
		return repo;
	}

	private static TrackSummary Sample(double? ascent = 480, double? duration = 3600) =>
		new(
			Id: Guid.NewGuid(),
			Name: "Sample ride",
			CreatedUtc: FixedInstant,
			StartedUtc: null,
			EndedUtc: null,
			DistanceM: 25_000,
			DurationS: duration,
			AscentM: ascent,
			MaxSpeedMps: null,
			PointCount: 500,
			SegmentCount: 1,
			Source: TrackSourceDto.Recorded,
			Version: 1);

	[Fact]
	public void Header_HasEditLinkAndDownloadButton()
	{
		Guid id = Guid.NewGuid();
		WireServices(new TrackDetail(
			Track: Sample() with { Id = id },
			Bounds: null,
			Polyline: Array.Empty<TrackPoint>()));

		IRenderedComponent<RideDetail> component = Render<RideDetail>(parameters => parameters
			.Add(p => p.TrackId, id));

		component.WaitForAssertion(() =>
		{
			component.FindAll($"a[href='/rides/{id}/edit']").ShouldNotBeEmpty(
				"§15.5: the Edit link routes to the composer. Missing it strands the trimming path.");
			component.FindAll("button").Any(b => b.TextContent.Contains("Download GPX", StringComparison.Ordinal))
				.ShouldBeTrue("§15.7: the raw GPX download button is on this page — the design outline names it.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void Stats_NullAscentRendersAsEmDash_NotZero()
	{
		WireServices(new TrackDetail(
			Track: Sample(ascent: null),
			Bounds: null,
			Polyline: Array.Empty<TrackPoint>()));

		IRenderedComponent<RideDetail> component = Render<RideDetail>(parameters => parameters
			.Add(p => p.TrackId, Guid.NewGuid()));

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("—", StringComparison.Ordinal).ShouldBeTrue(
				"§8: null ascent must render as '—'. Zero would be a real value (a flat coastal ride) and must not collide with 'unknown'.");
			// A dead flat ride would show "0 m", which contains "0" — assert with the unit to be specific.
			component.Markup.Contains("0 m ascent", StringComparison.Ordinal).ShouldBeFalse();
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void NotFound_RendersFriendlyMessage()
	{
		WireServices(detail: null);

		IRenderedComponent<RideDetail> component = Render<RideDetail>(parameters => parameters
			.Add(p => p.TrackId, Guid.NewGuid()));

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("Not found", StringComparison.Ordinal).ShouldBeTrue(
				"a page reached with a bad id must say so — silently rendering an empty shell would be a bug that looks like a bug.");
		}, timeout: TimeSpan.FromSeconds(3));
	}
}
