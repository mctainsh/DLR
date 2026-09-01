using BlazorDLR.Shared.Diagnostics;
using BlazorDLR.Shared.Services;
using DLR.Core.Contracts.Announcements;

namespace BlazorDLR.Shared.State;

/// <summary>
/// Keeps this app reachable by whoever runs the server (§20.3).
/// <para>
/// <strong>It holds the hub connection for as long as the app is open</strong>, and that is the
/// point of it. Until now nothing connected unless the rider was on a ride or a thread screen
/// (<c>RideSession</c>, <c>LocationBroadcastState</c>, <c>CommentThreadView</c>), so a rider sitting
/// on the adventure list could not be told anything at all. An announcement belongs to the server
/// rather than to a ride, so the connection it arrives on cannot belong to a ride either.
/// </para>
/// <para>
/// <strong>Lives for as long as the app does</strong>, by <see cref="CommentNotifier"/>'s trick:
/// <c>MainLayout</c> injects it, and injecting it is what constructs it. It also means that
/// notifier now sees posts on every screen rather than only when some other screen had happened to
/// connect.
/// </para>
/// <para>
/// <strong>An announcement reaches a running app, not a closed one.</strong> This project owns no
/// push infrastructure and this feature adds none - the same trade §17.6 already documents. A rider
/// whose phone has the app suspended sees the message when they next open it, from the launch
/// check.
/// </para>
/// </summary>
public sealed class AnnouncementNotifier : IDisposable
{
	private readonly IRideHubClient _hub;
	private readonly StartupCheckState _startup;
	private readonly AuthState _auth;

	private bool _connected;
	private bool _disposed;

	/// <summary>Starts watching the hub for announcements.</summary>
	/// <param name="hub">Where they arrive.</param>
	/// <param name="startup">What holds them, and what asks the server at launch.</param>
	/// <param name="auth">Whether there is a session to connect with - the hub is authenticated (§7.6).</param>
	public AnnouncementNotifier(IRideHubClient hub, StartupCheckState startup, AuthState auth)
	{
		_hub = hub;
		_startup = startup;
		_auth = auth;

		_hub.AnnouncementPosted += OnAnnouncementPosted;
		_hub.ConnectionChanged += OnConnectionChanged;
	}

	/// <summary>
	/// The launch rung: ask the server, then hold a connection so anything published afterwards
	/// arrives without another launch.
	/// </summary>
	/// <param name="cancellationToken">Abandons the call.</param>
	public async Task StartAsync(CancellationToken cancellationToken = default)
	{
		await _startup.CheckAsync(cancellationToken);

		await ConnectAsync(cancellationToken);
	}

	/// <summary>
	/// Opens the connection, if there is a session to open one with.
	/// </summary>
	/// <remarks>
	/// Failure is swallowed: a rider with no signal is a rider who gets their announcements at the
	/// next launch, and a launch rung that threw here would be one that took the rungs after it
	/// down with it.
	/// </remarks>
	private async Task ConnectAsync(CancellationToken cancellationToken)
	{
		if (_auth.UserId is null) return;

		try
		{
			// Set BEFORE the await, not after. ConnectAsync raises ConnectionChanged from inside
			// itself on success, and a handler that still saw `false` here would read the first
			// connect as a re-connect and fetch the launch check a second time on every launch.
			_connected = true;

			await _hub.ConnectAsync(cancellationToken);

			_connected = _hub.IsConnected;
		}
		catch (Exception failure) when (failure is not OperationCanceledException)
		{
			// Back to what the hub actually says, so a later reconnect is still seen as one.
			_connected = _hub.IsConnected;

			DiagnosticLog.Write($"Announcement: the hub would not open ({failure.GetType().Name}).");
		}
	}

	private void OnAnnouncementPosted(AnnouncementDto announcement) => _startup.Receive(announcement);

	/// <summary>
	/// Re-asks the server when the connection comes back.
	/// <para>
	/// §5.3's standing rule: reconnect refetches state and never replays history. Nothing is queued
	/// for a connection that was away, so this is what closes the gap for a rider who was in a
	/// tunnel when the sweep ran.
	/// </para>
	/// </summary>
	private void OnConnectionChanged()
	{
		bool up = _hub.IsConnected;

		// Only the transition back up. The event is raised on every change, including the drop.
		if (up && !_connected)
		{
			// Forget() rather than a bare discard: this is a hub callback with nowhere to report a
			// failure, and an unobserved exception is not the way to find that out (§17.6).
			_startup.CheckAsync().Forget();
		}

		_connected = up;
	}

	/// <summary>
	/// Releases the subscriptions. The hub client is not disposed here: it is a scoped service the
	/// container owns, and disposing an injected service from the class that happened to ask for it
	/// would take the connection down while the app is still running.
	/// </summary>
	public void Dispose()
	{
		if (_disposed) return;

		_disposed = true;

		_hub.AnnouncementPosted -= OnAnnouncementPosted;
		_hub.ConnectionChanged -= OnConnectionChanged;
	}
}
