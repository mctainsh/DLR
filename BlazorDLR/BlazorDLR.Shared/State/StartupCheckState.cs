using System.Globalization;
using BlazorDLR.Shared.Diagnostics;
using BlazorDLR.Shared.Services;
using DLR.Core.Client;
using DLR.Core.Contracts.Announcements;

namespace BlazorDLR.Shared.State;

/// <summary>
/// What the server had to say to this app: whether it is still a client the server will serve, and
/// which announcements this device has not cleared yet (§20).
/// <para>
/// <strong>Dismissals are device-local.</strong> Same reasoning as <see cref="IntroTourState"/>: no
/// table, no round trip, works with no signal, and nothing about what a rider has read reaches the
/// database. The cost is that a rider on a phone and on the web clears the same message twice,
/// which the introduction deck already trades away for the same reasons.
/// </para>
/// <para>
/// <strong>Every failure is silent.</strong> No signal, or a server too old to have the endpoint,
/// leaves the app exactly as it was. A wall a flat tunnel could raise would be worse than no wall.
/// </para>
/// </summary>
public sealed class StartupCheckState
{
	/// <summary>
	/// The <see cref="IDeviceSettings"/> key holding what has been cleared, as
	/// <c>id:unix-seconds</c> pairs joined by <c>,</c> behind the house version marker.
	/// <para>
	/// Multi-field, so the value carries the leading <c>1|</c> every other multi-field setting in
	/// the app does (<see cref="UnreadThreadState"/>, <c>RouteStyle</c>, <c>PrivateArea</c>). This
	/// is the one stored value that cannot be re-shaped later without every device re-showing
	/// every notice it had cleared, which is exactly what the marker buys.
	/// </para>
	/// <para>
	/// The expiry travels with the id so the list prunes itself against the clock alone. Pruning
	/// against the server's live list instead would drop - and so re-show - anything past the
	/// <see cref="AnnouncementLimits.MaxLive"/> cap.
	/// </para>
	/// </summary>
	public const string DismissedKey = "dlr.notices-seen";

	/// <summary>The <see cref="IDeviceSettings"/> key holding the version whose update offer was waved away.</summary>
	public const string UpdateOfferKey = "dlr.update-offered";

	private readonly IApiClient _api;
	private readonly IDeviceSettings _settings;
	private readonly TimeProvider _clock;
	private readonly IFormFactor _formFactor;

	/// <summary>Cleared announcements, onto when they stop being worth remembering.</summary>
	private readonly Dictionary<Guid, DateTimeOffset> _dismissed = [];

	/// <summary>Waiting to be shown, oldest first. Fed by the launch check and by the hub.</summary>
	private readonly List<AnnouncementDto> _pending = [];

	/// <summary>The last answer the server gave, or null before one has landed.</summary>
	private StartupCheck? _server;

	private bool _loaded;
	private string? _updateOffered;

	/// <param name="api">Where the check is made.</param>
	/// <param name="settings">Where dismissals live.</param>
	/// <param name="clock">Prunes the dismissal list (§10.4).</param>
	/// <param name="formFactor">Which store this host sends a rider to for a newer build.</param>
	public StartupCheckState(
		IApiClient api,
		IDeviceSettings settings,
		TimeProvider clock,
		IFormFactor formFactor)
	{
		_api = api;
		_settings = settings;
		_clock = clock;
		_formFactor = formFactor;
	}

	/// <summary>Something on this state changed and whatever is drawing it should look again.</summary>
	public event Action? Changed;

	/// <summary>
	/// Whether this build is too old for the server to serve, as answered at launch.
	/// <para>
	/// <strong>Latched at the first check, deliberately.</strong> A later answer updates nothing
	/// visible: walling the app off mid-session would take the map away from a rider who is out on
	/// a road, and the verdict cannot change for a reason that is urgent - the binary is the same
	/// binary it was when the app opened. It takes effect at the next launch.
	/// </para>
	/// </summary>
	public bool IsUnsupported { get; private set; }

	/// <summary>The most recent verdict, or null before the first answer arrives.</summary>
	public ClientSupport? Support => _server?.Support;

	/// <summary>Where this rider gets a newer build, when the host has a store to send them to.</summary>
	public string? UpdateUrl => ClientRelease.UpdateUrlFor(_formFactor.GetPlatform());

	/// <summary>What the wall says the floor is.</summary>
	public string MinimumVersion => _server?.MinimumVersion ?? ClientRelease.Minimum.ToString();

	/// <summary>The announcement to show now, or null when there is nothing waiting.</summary>
	public AnnouncementDto? Current => _pending.Count > 0 ? _pending[0] : null;

	/// <summary>
	/// Whether the rider should be offered an update they have not already waved away. Only ever
	/// true while <see cref="Current"/> is null - one thing at a time, and a message from a person
	/// outranks a message from a version number.
	/// </summary>
	public bool UpdateOffered =>
		Current is null
		&& Support is ClientSupport.UpdateAvailable
		&& !string.Equals(_updateOffered, RecommendedVersion, StringComparison.Ordinal);

	/// <summary>What an update would take this rider to.</summary>
	public string RecommendedVersion => _server?.RecommendedVersion ?? ClientRelease.Recommended.ToString();

	/// <summary>
	/// The version this build is, as the server is told it. The shared library's assembly version,
	/// which is what Settings already shows a rider.
	/// </summary>
	public static string? ClientVersion =>
		typeof(StartupCheckState).Assembly.GetName().Version?.ToString();

	/// <summary>
	/// Asks the server, and queues whatever this device has not already cleared.
	/// </summary>
	/// <param name="cancellationToken">Abandons the call.</param>
	public async Task CheckAsync(CancellationToken cancellationToken = default)
	{
		await LoadAsync(cancellationToken);

		StartupCheck check;

		try
		{
			check = await _api.StartupCheckAsync(ClientVersion, cancellationToken);
		}
		catch (Exception failure) when (failure is not OperationCanceledException)
		{
			// A server that has never heard of this endpoint answers 404 and lands here, which is
			// the same silence as no signal. Both are "carry on".
			DiagnosticLog.Write($"Startup: the check could not be made ({failure.GetType().Name}).");
			return;
		}

		// Latched on the first answer. See IsUnsupported for why a later check never raises the wall.
		if (_server is null) IsUnsupported = check.Support is ClientSupport.Unsupported;

		_server = check;

		foreach (AnnouncementDto announcement in check.Live)
		{
			Queue(announcement);
		}

		DiagnosticLog.Write(
			$"Startup: the server says {check.Support}, with {check.Live.Count} announcement(s) live.");

		Changed?.Invoke();
	}

	/// <summary>Takes one that arrived over the hub rather than in the launch check (§20.3).</summary>
	/// <param name="announcement">What arrived.</param>
	public void Receive(AnnouncementDto announcement)
	{
		if (!Queue(announcement)) return;

		DiagnosticLog.Write($"Announcement: \"{announcement.Title}\" arrived over the hub.");

		Changed?.Invoke();
	}

	/// <summary>
	/// Clears what is on screen and remembers that this device has seen it.
	/// </summary>
	/// <param name="cancellationToken">Cancels the write. The message closes either way.</param>
	public async Task DismissAsync(CancellationToken cancellationToken = default)
	{
		if (Current is not { } announcement)
		{
			// The update offer, which is remembered by version rather than by id: a later release
			// that raises the recommendation is a different offer and gets asked again.
			_updateOffered = RecommendedVersion;
			Changed?.Invoke();
			await WriteAsync(UpdateOfferKey, RecommendedVersion, cancellationToken);
			return;
		}

		_pending.RemoveAt(0);
		_dismissed[announcement.Id] = announcement.ExpiresUtc;

		Changed?.Invoke();

		await WriteAsync(DismissedKey, Encode(), cancellationToken);
	}

	/// <summary>
	/// Queues an announcement this device has not cleared and is not already holding.
	/// </summary>
	/// <param name="announcement">The message.</param>
	/// <returns>Whether it was added.</returns>
	/// <remarks>
	/// The de-duplication is what lets the hub and the launch check both deliver freely: a rider
	/// who was connected when the sweep ran and then relaunched has been sent the same message
	/// twice, and neither path knows about the other.
	/// </remarks>
	private bool Queue(AnnouncementDto announcement)
	{
		if (_dismissed.ContainsKey(announcement.Id)) return false;

		if (_pending.Any(waiting => waiting.Id == announcement.Id)) return false;

		if (announcement.ExpiresUtc <= _clock.GetUtcNow()) return false;

		_pending.Add(announcement);

		return true;
	}

	/// <summary>Reads the device's dismissal list once, dropping anything that has expired.</summary>
	private async Task LoadAsync(CancellationToken cancellationToken)
	{
		if (_loaded) return;

		_loaded = true;

		_updateOffered = await ReadAsync(UpdateOfferKey, cancellationToken);

		string? stored = await ReadAsync(DismissedKey, cancellationToken);

		// Anything without the marker was written by a format this version does not know, and
		// reads as "nothing cleared" - the answer a device that has never run the app gives, and
		// never worse than wrong.
		if (stored is null || !stored.StartsWith(Format, StringComparison.Ordinal)) return;

		DateTimeOffset now = _clock.GetUtcNow();

		foreach (string entry in stored[Format.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries))
		{
			string[] parts = entry.Split(':');

			if (parts.Length != 2
				|| !Guid.TryParseExact(parts[0], "N", out Guid id)
				|| !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds))
			{
				// A half-written pair. Skipped rather than failed: the cost is one message shown a
				// second time.
				continue;
			}

			DateTimeOffset expires = DateTimeOffset.FromUnixTimeSeconds(seconds);

			if (expires > now) _dismissed[id] = expires;
		}
	}

	/// <summary>The version marker every multi-field device setting in this app carries.</summary>
	private const string Format = "1|";

	private string Encode() => Format + string.Join(
		',',
		_dismissed.Select(entry => string.Create(
			CultureInfo.InvariantCulture,
			$"{entry.Key:N}:{entry.Value.ToUnixTimeSeconds()}")));

	private async Task<string?> ReadAsync(string key, CancellationToken cancellationToken)
	{
		try
		{
			return await _settings.GetAsync(key, cancellationToken);
		}
		catch (Exception failure) when (failure is not OperationCanceledException)
		{
			return null;
		}
	}

	/// <summary>
	/// Persists a value, treating a store that refused as "not remembered". A browser with site
	/// data blocked shows the message again next launch, which is the harmless direction.
	/// </summary>
	private async Task WriteAsync(string key, string value, CancellationToken cancellationToken)
	{
		try
		{
			await _settings.SetAsync(key, value, cancellationToken);
		}
		catch (Exception failure) when (failure is not OperationCanceledException)
		{
			DiagnosticLog.Write($"Announcement: the dismissal could not be stored ({failure.GetType().Name}).");
		}
	}
}
