using System.Runtime.CompilerServices;
using System.Threading.Channels;
using BlazorDLR.Shared.Services;

namespace DLR.UI.Tests.Fakes;

/// <summary>
/// A GPS a test drives by hand — §4.3's "third implementation used by tests", standing in for the
/// GPX replay harness the design names.
/// <para>
/// The platform providers cannot run here: one is a foreground service and the other is
/// <c>CLLocationManager</c>, and both need a phone. What <em>can</em> be tested without one is
/// everything above the seam — the private-area gate, the accuracy filter, the hub-then-REST
/// fallback and the start/stop lifetime — which is the half where the privacy rules live.
/// </para>
/// </summary>
public sealed class FakeLocationProvider : ILocationProvider
{
	private readonly Channel<LocationFix> _fixes = Channel.CreateUnbounded<LocationFix>();

	// Written on the pump's thread, read on the test's — see WatchCount.
	private int _watchCount;
	private int _permissionAsks;
	private bool _recording;
	private bool _stopped;

	/// <inheritdoc />
	public bool IsSupported { get; set; } = true;

	/// <inheritdoc />
	public bool IsRecording => Volatile.Read(ref _recording);

	/// <summary>What <see cref="EnsurePermissionsAsync"/> answers. Granted unless a test says otherwise.</summary>
	public LocationPermissionState Permission { get; set; } = LocationPermissionState.Granted;

	/// <summary>
	/// How many times the platform ladder has been climbed. On Android the first rung of it is the
	/// system location dialog, so a test asserting this is still zero is asserting that Play's
	/// prominent disclosure came first (§4.3).
	/// </summary>
	public int PermissionAsks => Volatile.Read(ref _permissionAsks);

	/// <summary>
	/// How many times a watch has been started — the receiver's lifetime, counted.
	/// <para>
	/// Written by the broadcaster's pump, which is a thread-pool task, and read by the test that
	/// is waiting for it. Interlocked and <see cref="Volatile"/> rather than a plain property for
	/// that reason alone: a test polling an ordinary field is polling a value the CPU is entitled
	/// to keep to itself.
	/// </para>
	/// </summary>
	public int WatchCount => Volatile.Read(ref _watchCount);

	/// <summary>The rate the last watch was started with (§4.2).</summary>
	public LocationUpdateRate? LastRate { get; private set; }

	/// <summary>Whether the receiver was released — the foreground service taken down.</summary>
	public bool Stopped => Volatile.Read(ref _stopped);

	/// <inheritdoc />
	public Task<LocationPermissionState> EnsurePermissionsAsync(CancellationToken cancellationToken = default)
	{
		Interlocked.Increment(ref _permissionAsks);
		return Task.FromResult(Permission);
	}

	/// <summary>Hands a fix to whoever is watching.</summary>
	/// <param name="fix">The fix to deliver.</param>
	public void Emit(LocationFix fix) => _fixes.Writer.TryWrite(fix);

	/// <inheritdoc />
	public async IAsyncEnumerable<LocationFix> WatchAsync(
		LocationUpdateRate rate,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		Interlocked.Increment(ref _watchCount);
		LastRate = rate;
		Volatile.Write(ref _recording, true);
		Volatile.Write(ref _stopped, false);

		try
		{
			await foreach (LocationFix fix in _fixes.Reader.ReadAllAsync(cancellationToken))
			{
				yield return fix;
			}
		}
		finally
		{
			// The real providers release a foreground service here. A test asserting Stopped is
			// asserting that the notification came down.
			Volatile.Write(ref _recording, false);
			Volatile.Write(ref _stopped, true);
		}
	}
}
