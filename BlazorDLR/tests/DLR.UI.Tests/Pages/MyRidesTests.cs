using BlazorDLR.Shared.Pages;
using BlazorDLR.Shared.Services;
using Bunit;
using DLR.Core.Contracts.Tracks;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// The signed-in tracks list. Three states matter: loading, empty (with a call to
/// import) and populated. §8's numbers-vs-null rule leans on this page — a null
/// ascent must render as "—", not as "0", because zero ascent is a real value.
/// </summary>
public sealed class MyRidesTests : BunitContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private FakeTrackRepository WireServices()
	{
		FakeTrackRepository repo = new();
		Services.AddSingleton<ITrackRepository>(repo);
		return repo;
	}

	[Fact]
	public void EmptyList_ShowsImportCallToAction()
	{
		WireServices();

		IRenderedComponent<MyRides> component = Render<MyRides>();

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("No rides yet", StringComparison.Ordinal).ShouldBeTrue(
				"an empty list must call out the import path — a blank table is not an answer.");
			component.FindAll("a[href='/import']").ShouldNotBeEmpty(
				"the Import GPX button is always visible, empty state or not — importing is the primary way rides land here in Phase 1.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void PopulatedList_RendersEachTrackRow()
	{
		FakeTrackRepository repo = WireServices();
		repo.Tracks.Add(new TrackSummary(
			Id: Guid.NewGuid(),
			Name: "Sunday morning gravel",
			CreatedUtc: FixedInstant,
			StartedUtc: null,
			EndedUtc: null,
			DistanceM: 25_000,
			DurationS: 3600,
			AscentM: 480,
			MaxSpeedMps: null,
			PointCount: 500,
			SegmentCount: 1,
			Source: TrackSourceDto.Imported,
			Version: 1));
		repo.Tracks.Add(new TrackSummary(
			Id: Guid.NewGuid(),
			Name: "Recorded loop",
			CreatedUtc: FixedInstant.AddDays(-1),
			StartedUtc: null,
			EndedUtc: null,
			DistanceM: 12_000,
			DurationS: 1800,
			AscentM: null,
			MaxSpeedMps: null,
			PointCount: 300,
			SegmentCount: 1,
			Source: TrackSourceDto.Recorded,
			Version: 1));

		IRenderedComponent<MyRides> component = Render<MyRides>();

		component.WaitForAssertion(() =>
		{
			string markup = component.Markup;
			markup.Contains("Sunday morning gravel", StringComparison.Ordinal).ShouldBeTrue();
			markup.Contains("Recorded loop", StringComparison.Ordinal).ShouldBeTrue();
			markup.Contains("Imported", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(
				"the source badge distinguishes imports from recordings — §15.4.");
			// §8: null ascent renders as an em dash, not as zero.
			markup.Contains("—", StringComparison.Ordinal).ShouldBeTrue(
				"§8: a null number renders as '—'. Zero ascent (dead flat) would render as '0 m'.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void ErrorState_ShowsMessageAndRetryButton()
	{
		FakeTrackRepository repo = WireServices();
		repo.ListException = new InvalidOperationException("network hiccup");

		IRenderedComponent<MyRides> component = Render<MyRides>();

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("network hiccup", StringComparison.Ordinal).ShouldBeTrue(
				"the exception message travels to the DOM — a bare 'could not load' is not diagnosable.");
			component.FindAll("button").Any(b => b.TextContent.Contains("Retry", StringComparison.Ordinal))
				.ShouldBeTrue("a transient error is a retryable event — the button offers the retry.");
		}, timeout: TimeSpan.FromSeconds(3));
	}
}
