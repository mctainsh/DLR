using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;

namespace DLR.UI.Tests.State;

/// <summary>
/// The in-memory half of the route-style preference (§18.6): what the map reads on every
/// repaint, and the broadcast that tells it to repaint at all.
/// <para>
/// The persistence itself is a one-line call into whichever platform store the host bound —
/// what is worth testing is the behaviour around it: that the read happens once, that a change
/// reaches the canvas without waiting on a JS round trip, and that "reset" forgets rather than
/// pins today's defaults.
/// </para>
/// </summary>
public sealed class RouteStyleStateTests
{
	/// <summary>An <see cref="IDeviceSettings"/> that counts reads, so "idempotent" can be asserted rather than assumed.</summary>
	private sealed class CountingSettings : IDeviceSettings
	{
		private readonly InMemoryDeviceSettings _inner = new();

		public int Reads { get; private set; }

		public IReadOnlyList<string> Removed => _removed;

		private readonly List<string> _removed = [];

		public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default)
		{
			Reads++;
			return _inner.GetAsync(key, cancellationToken);
		}

		public ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default) =>
			_inner.SetAsync(key, value, cancellationToken);

		public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
		{
			_removed.Add(key);
			return _inner.RemoveAsync(key, cancellationToken);
		}
	}

	[Fact]
	public void BeforeLoad_TheStyleIsTheShippedDefault()
	{
		RouteStyleState state = new(new CountingSettings());

		// The prerender pass renders against this: it has no device to read from, and a screen
		// that waited for one would render nothing at all.
		state.Style.ShouldBe(RouteStyle.Default);
		state.IsCustomised.ShouldBeFalse();
	}

	[Fact]
	public async Task LoadAsync_ReadsTheDeviceOnce_HoweverManyComponentsAsk()
	{
		CountingSettings settings = new();
		await settings.SetAsync(RouteStyleState.StorageKey, (RouteStyle.Default with { LineWidthPx = 9 }).Encode());

		RouteStyleState state = new(settings);

		// The map overlay and the settings panel both call this without coordinating.
		await state.LoadAsync();
		await state.LoadAsync();
		await state.LoadAsync();

		// Three keys — the style, the per-route colours and the reversed routes — read once
		// between them, not once per caller. On the web each of these is a JS interop round trip.
		settings.Reads.ShouldBe(3);
		state.Style.LineWidthPx.ShouldBe(9);
	}

	[Fact]
	public async Task LoadAsync_BroadcastsOnce_SoAMapAlreadyOnScreenRepaints()
	{
		CountingSettings settings = new();
		await settings.SetAsync(RouteStyleState.StorageKey, (RouteStyle.Default with { ArrowColour = "#ff8800" }).Encode());

		RouteStyleState state = new(settings);

		int changes = 0;
		state.Changed += () => changes++;

		await state.LoadAsync();
		await state.LoadAsync();

		// The canvas paints its first frame from the defaults, because the read cannot happen
		// until after first render. This event is what corrects it.
		changes.ShouldBe(1);
		state.Style.ArrowColour.ShouldBe("#ff8800");
	}

	[Fact]
	public async Task SetAsync_AppliesInMemoryBeforeItPersists()
	{
		CountingSettings settings = new();
		RouteStyleState state = new(settings);

		RouteStyle? seenByTheMap = null;
		state.Changed += () => seenByTheMap = state.Style;

		await state.SetAsync(RouteStyle.Default with { ShowDirectionArrows = false });

		// The repaint is what the rider is waiting to see; it must not queue behind storage.
		seenByTheMap.ShouldNotBeNull();
		seenByTheMap.ShowDirectionArrows.ShouldBeFalse();

		RouteStyleState reopened = new(settings);
		await reopened.LoadAsync();
		reopened.Style.ShowDirectionArrows.ShouldBeFalse("the point of the setting is that it survives a restart.");
	}

	[Fact]
	public async Task SetAsync_NormalisesWhatAControlHandsIt()
	{
		RouteStyleState state = new(new CountingSettings());

		await state.SetAsync(RouteStyle.Default with { LineWidthPx = 999, OutlineColour = "rgb(1,2,3)" });

		state.Style.LineWidthPx.ShouldBe(RouteStyle.MaxLineWidthPx);
		state.Style.OutlineColour.ShouldBe(RouteStyle.Default.OutlineColour);
	}

	[Fact]
	public void ColourFor_ResolvesMostSpecificFirst()
	{
		RouteStyleState state = new(new CountingSettings());
		Guid trackId = Guid.NewGuid();

		// Nothing set: the palette's answer, which is what makes a three-route ride readable.
		state.ColourFor(trackId, "#16a34a").ShouldBe("#16a34a");
	}

	[Fact]
	public async Task ColourFor_APinnedColour_BeatsTheAllRoutesColour()
	{
		RouteStyleState state = new(new CountingSettings());
		Guid pinned = Guid.NewGuid();
		Guid plain = Guid.NewGuid();

		await state.SetAsync(RouteStyle.Default with { FillColour = "#000000" });
		await state.SetRouteColourAsync(pinned, "#ff8800");

		// If the blanket colour won, the per-route picker would silently do nothing — which is
		// the one ordering that makes the pair of controls unusable.
		state.ColourFor(pinned, "#16a34a").ShouldBe("#ff8800");
		state.ColourFor(plain, "#16a34a").ShouldBe("#000000");

		// A line that is not a saved track cannot be pinned, and falls through to the rest.
		state.ColourFor(null, "#16a34a").ShouldBe("#000000");
	}

	[Fact]
	public async Task SetRouteColourAsync_SurvivesARestart_AndClearingRemovesTheKey()
	{
		CountingSettings settings = new();
		RouteStyleState state = new(settings);
		Guid trackId = Guid.NewGuid();

		await state.SetRouteColourAsync(trackId, "#FF8800");
		state.HasRouteColour(trackId).ShouldBeTrue();
		state.RouteColours[trackId].ShouldBe("#ff8800", "colours are normalised before they are stored.");

		RouteStyleState reopened = new(settings);
		await reopened.LoadAsync();
		reopened.ColourFor(trackId, "#16a34a").ShouldBe("#ff8800");

		await reopened.ClearRouteColourAsync(trackId);
		reopened.ColourFor(trackId, "#16a34a").ShouldBe("#16a34a");

		// Back where it started, so nothing is left behind for it.
		settings.Removed.ShouldContain(RouteStyleState.ColoursStorageKey);
	}

	[Fact]
	public async Task SetRouteColourAsync_IgnoresAColourTheCanvasCouldNotDraw()
	{
		RouteStyleState state = new(new CountingSettings());
		Guid trackId = Guid.NewGuid();

		await state.SetRouteColourAsync(trackId, "#ff8800");
		await state.SetRouteColourAsync(trackId, "not a colour");

		state.ColourFor(trackId, "#16a34a").ShouldBe("#ff8800",
			"losing the colour you had is worse than ignoring an input the canvas cannot parse.");
	}

	[Fact]
	public async Task LoadAsync_ReadsBothKeys_AndBroadcastsOnce()
	{
		CountingSettings settings = new();
		Guid trackId = Guid.NewGuid();

		await settings.SetAsync(RouteStyleState.StorageKey, (RouteStyle.Default with { LineWidthPx = 9 }).Encode());
		await settings.SetAsync(
			RouteStyleState.ColoursStorageKey,
			RouteColourMap.Encode(new Dictionary<Guid, string> { [trackId] = "#ff8800" }));

		RouteStyleState state = new(settings);

		int changes = 0;
		state.Changed += () => changes++;

		await state.LoadAsync();

		state.Style.LineWidthPx.ShouldBe(9);
		state.ColourFor(trackId, "#16a34a").ShouldBe("#ff8800");
		changes.ShouldBe(1, "two reads, one repaint — the canvas draws from whatever is in memory when it runs.");
	}

	// -- Reversed routes (§5.4) ---------------------------------------------------------------

	[Fact]
	public void BeforeLoad_NothingIsReversed()
	{
		RouteStyleState state = new(new CountingSettings());

		// The prerender pass renders against this, and so does every route on a device that has
		// never touched the control: a track is drawn the way its GPX recorded it.
		state.IsReversed(Guid.NewGuid()).ShouldBeFalse();

		// A line that is not a saved track — the editor's working copy — cannot be reversed and
		// must not throw for asking.
		state.IsReversed(null).ShouldBeFalse();
	}

	[Fact]
	public async Task SetReversedAsync_SurvivesARestart_AndTurningItBackRemovesTheKey()
	{
		CountingSettings settings = new();
		RouteStyleState state = new(settings);
		Guid trackId = Guid.NewGuid();

		await state.SetReversedAsync(trackId, true);
		state.IsReversed(trackId).ShouldBeTrue();

		// The direction belongs to the track, so it is still reversed after a relaunch — and on
		// the next ride the same GPX is attached to.
		RouteStyleState reopened = new(settings);
		await reopened.LoadAsync();
		reopened.IsReversed(trackId).ShouldBeTrue();

		await reopened.SetReversedAsync(trackId, false);
		reopened.IsReversed(trackId).ShouldBeFalse();

		// Back where it started, so nothing is left behind for it.
		settings.Removed.ShouldContain(RouteStyleState.ReversedStorageKey);
	}

	[Fact]
	public async Task ToggleReversedAsync_FlipsOneRoute_AndLeavesTheOthersAlone()
	{
		RouteStyleState state = new(new CountingSettings());
		Guid reversed = Guid.NewGuid();
		Guid untouched = Guid.NewGuid();

		await state.ToggleReversedAsync(reversed);

		state.IsReversed(reversed).ShouldBeTrue();
		state.IsReversed(untouched).ShouldBeFalse(
			"an adventure carries several routes, and turning one round is not a statement about the rest.");

		await state.ToggleReversedAsync(reversed);
		state.IsReversed(reversed).ShouldBeFalse("the same button undoes it.");
	}

	[Fact]
	public async Task SetReversedAsync_AskingForTheWayItAlreadyIs_CostsNoRepaint()
	{
		RouteStyleState state = new(new CountingSettings());
		Guid trackId = Guid.NewGuid();

		await state.SetReversedAsync(trackId, true);

		int changes = 0;
		state.Changed += () => changes++;

		await state.SetReversedAsync(trackId, true);
		await state.SetReversedAsync(Guid.NewGuid(), false);

		// Every Changed is a full Skia repaint of whatever maps are on screen — see
		// SkiaMapOverlay.OnRouteStyleChanged — and neither call changes a frame.
		changes.ShouldBe(0);
	}

	[Fact]
	public async Task AReversedRoute_CountsAsCustomised_SoResetIsOffered()
	{
		RouteStyleState state = new(new CountingSettings());

		state.IsCustomised.ShouldBeFalse();

		await state.SetReversedAsync(Guid.NewGuid(), true);

		// The reset button is disabled on !IsCustomised. A rider who has only reversed a route
		// would otherwise have no way back to stock but to find the row again.
		state.IsCustomised.ShouldBeTrue();
	}

	[Fact]
	public async Task ResetAsync_ForgetsTheKey_RatherThanStoringTodaysDefaults()
	{
		CountingSettings settings = new();
		RouteStyleState state = new(settings);
		Guid reversed = Guid.NewGuid();

		await state.SetAsync(RouteStyle.Default with { FillColour = "#ff8800" });
		await state.SetRouteColourAsync(Guid.NewGuid(), "#00ffcc");
		await state.SetReversedAsync(reversed, true);
		state.IsCustomised.ShouldBeTrue();

		await state.ResetAsync();

		state.Style.ShouldBe(RouteStyle.Default);
		state.RouteColours.ShouldBeEmpty("one button, everything this device chose.");
		state.ReversedRoutes.ShouldBeEmpty("including which way round the routes are read.");
		state.IsReversed(reversed).ShouldBeFalse();
		state.IsCustomised.ShouldBeFalse();

		// Storing the current defaults would pin them: a later build that shipped a different
		// default would never reach a device that had once pressed reset.
		settings.Removed.ShouldContain(RouteStyleState.StorageKey);
		settings.Removed.ShouldContain(RouteStyleState.ColoursStorageKey);
		settings.Removed.ShouldContain(RouteStyleState.ReversedStorageKey);
		(await settings.GetAsync(RouteStyleState.StorageKey)).ShouldBeNull();
		(await settings.GetAsync(RouteStyleState.ColoursStorageKey)).ShouldBeNull();
		(await settings.GetAsync(RouteStyleState.ReversedStorageKey)).ShouldBeNull();
	}
}
