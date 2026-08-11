using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace BlazorDLR.Shared.State;

/// <summary>
/// How deep into the app the rider has navigated since this session started, so a back
/// button can tell "there is a previous page inside the app" from "this tab opened here".
/// <para>
/// One instance per app (scoped in each host), and the count is deliberately in C# rather
/// than read from the browser: <c>window.history.length</c> counts entries this app did not
/// create — a tab that visited three other sites before landing on a deep link reports a
/// history to go back into, and stepping into it would walk the rider out of the app.
/// </para>
/// <para>
/// A rider who opens a shared link lands with <see cref="Depth"/> at zero, so
/// <c>PageNav</c> follows the page's declared parent route instead of calling
/// <c>history.back()</c> — which on that first entry would leave the app entirely, or do
/// nothing at all.
/// </para>
/// <para>
/// Counting starts when the first <c>PageNav</c> injects this, so navigations made before
/// any page with a title bar — Home and Welcome are the ones that have none — are not in
/// the count. That is the answer we want rather than a gap to close: the first page with an
/// arrow reports no history and therefore follows its declared parent, and for every such
/// page that parent is the rail destination the rider actually arrived from.
/// </para>
/// </summary>
public sealed class NavigationHistory : IDisposable
{
	private readonly NavigationManager _navigation;
	private bool _steppingBack;

	public NavigationHistory(NavigationManager navigation)
	{
		_navigation = navigation;
		_navigation.LocationChanged += OnLocationChanged;
	}

	/// <summary>
	/// In-app navigations still behind the current page. Zero on the entry the session
	/// opened at, whether that is the app root or a deep link.
	/// </summary>
	public int Depth { get; private set; }

	/// <summary>Whether stepping back would land on a page this app put there.</summary>
	public bool CanGoBack => Depth > 0;

	/// <summary>
	/// Called by <c>PageNav</c> immediately before it steps back, so the
	/// <see cref="NavigationManager.LocationChanged"/> that follows is counted as
	/// unwinding the stack rather than pushing onto it.
	/// <para>
	/// This covers the parent-route fallback too. Walking up to a parent because there was
	/// no history to step into must not leave the rider one level deeper than they started:
	/// without this, a deep link to a child page would go child → parent → child forever,
	/// because the fallback navigation would itself become the history the button steps
	/// back into.
	/// </para>
	/// </summary>
	public void NotifySteppingBack() => _steppingBack = true;

	private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
	{
		if (_steppingBack)
		{
			_steppingBack = false;

			if (Depth > 0)
			{
				Depth--;
			}

			return;
		}

		Depth++;
	}

	public void Dispose() => _navigation.LocationChanged -= OnLocationChanged;
}
