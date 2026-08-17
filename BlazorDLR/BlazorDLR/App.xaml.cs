using BlazorDLR.Shared.State;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorDLR;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		Window window = new(new MainPage()) { Title = "BlazorDLR" };

		TrackForeground(window);

		return window;
	}

	/// <summary>
	/// Reports whether the rider can see the app, for <see cref="CommentNotifier"/> (§17.6).
	/// <para>
	/// <strong><c>Resumed</c> and <c>Stopped</c> rather than <c>Activated</c> and
	/// <c>Deactivated</c>.</strong> The activation pair tracks <em>focus</em>, which is lost to a
	/// pulled-down notification shade, a permission dialog and a split-screen tap — none of which
	/// means the rider has stopped looking at the app, and every one of which would flip the flag
	/// twice for nothing. The resumed/stopped pair tracks visibility, which is the actual question:
	/// <c>onResume</c>/<c>onStop</c> on Android, and the scene's foreground transitions on iOS.
	/// </para>
	/// <para>
	/// Resolved from the platform provider rather than injected, because <see cref="App"/> is
	/// constructed by MAUI outside any scope — which is exactly why
	/// <see cref="AppForegroundState"/> is a singleton. Null-tolerant so a host that has not
	/// registered it still starts.
	/// </para>
	/// </summary>
	private static void TrackForeground(Window window)
	{
		if (IPlatformApplication.Current?.Services.GetService<AppForegroundState>() is not { } foreground)
			return;

		window.Resumed += (_, _) => foreground.Set(true);
		window.Stopped += (_, _) => foreground.Set(false);
	}
}
