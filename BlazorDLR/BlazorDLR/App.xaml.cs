using BlazorDLR.Shared.Diagnostics;

namespace BlazorDLR;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();

		DiagnosticLog.Write("Startup: App constructed.");
	}

	/// <summary>
	/// The app's one window.
	/// <para>
	/// Nothing is hung off <c>Resumed</c> / <c>Stopped</c> any more. Until v0.27 those events fed an
	/// <c>AppForegroundState</c> that <c>CommentNotifier</c> consulted before deciding to stay
	/// quiet; there is no such decision left to make (§17.6) — a post notifies whether the rider is
	/// looking at the app or not — so the tracking went with it rather than sitting here as a
	/// singleton nothing reads.
	/// </para>
	/// </summary>
	protected override Window CreateWindow(IActivationState? activationState)
	{
		DiagnosticLog.Write("Startup: creating the window.");

		return new Window(new MainPage()) { Title = "BlazorDLR" };
	}
}
