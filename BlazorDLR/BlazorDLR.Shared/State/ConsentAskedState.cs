using BlazorDLR.Shared.Services;

namespace BlazorDLR.Shared.State;

/// <summary>
/// Which adventures this device has already put the sharing consent prompt up for (§5.6, §18.6).
/// <para>
/// Nothing on the server records that a rider was asked - <c>ShareLocation</c> says what they
/// answered, and <em>no</em> and <em>never asked</em> are the same false. Until v0.32 the
/// adventure's lifecycle carried the difference, because an adventure that had not started was
/// the only one that asked. With no lifecycle the fact has to live somewhere, and the two ways of
/// not storing it are both wrong: ask on every load of an adventure somebody declined, or stop
/// asking anybody.
/// </para>
/// <para>
/// <strong>Per device, deliberately.</strong> Declining on a laptop that has no GPS must not
/// suppress the prompt on the phone that would actually do the sharing, so this is
/// <see cref="IDeviceSettings"/> rather than a column.
/// </para>
/// </summary>
/// <param name="settings">Where the answer is remembered between launches.</param>
public sealed class ConsentAskedState(IDeviceSettings settings)
{
	/// <summary>
	/// The <see cref="IDeviceSettings"/> key. One key holding every adventure, not one key each:
	/// a key per adventure is unbounded in a store nothing sweeps - the argument
	/// <see cref="UnreadThreadState.StorageKey"/> already makes.
	/// </summary>
	public const string StorageKey = "dlr.consent-asked";

	/// <summary>
	/// How many adventures are remembered. Most recently asked first, so the oldest falls off -
	/// and a rider who comes back to an adventure they declined a hundred rides ago being asked
	/// once more is the right way for this to fail.
	/// </summary>
	public const int MaxTracked = 32;

	private readonly List<Guid> _asked = [];

	private bool _loaded;
	private Task? _reading;

	/// <summary>
	/// Reads the device store, once per app. A second caller joins the first read rather than
	/// being told it has already happened - <see cref="UnreadThreadState.LoadAsync"/>'s reasoning:
	/// answering "already loading" would let a caller decide nobody has been asked while the
	/// store's answer is still in flight, and this is a consent gate.
	/// </summary>
	/// <param name="cancellationToken">Abandons the read.</param>
	public Task LoadAsync(CancellationToken cancellationToken = default)
	{
		if (_loaded)
			return _reading ?? Task.CompletedTask;

		_loaded = true;

		return _reading = ReadAsync(cancellationToken);
	}

	/// <summary>Whether this device has already asked about an adventure.</summary>
	/// <param name="rideId">Which adventure.</param>
	/// <returns><c>false</c> before <see cref="LoadAsync"/> has completed - see the remarks.</returns>
	/// <remarks>
	/// Unread answers false, so a caller that forgets to load asks again rather than staying
	/// silent. Of the two ways to be wrong, asking twice is the one that does not quietly drop a
	/// consent prompt.
	/// </remarks>
	public bool WasAsked(Guid rideId) => _asked.Contains(rideId);

	/// <summary>Records that the prompt was answered - either way (§5.6).</summary>
	/// <param name="rideId">Which adventure.</param>
	/// <param name="cancellationToken">Abandons the write.</param>
	public ValueTask MarkAskedAsync(Guid rideId, CancellationToken cancellationToken = default)
	{
		_asked.Remove(rideId);
		_asked.Insert(0, rideId);

		if (_asked.Count > MaxTracked)
			_asked.RemoveRange(MaxTracked, _asked.Count - MaxTracked);

		return SaveAsync(cancellationToken);
	}

	/// <summary>
	/// Forgets an adventure, so a rider who rejoins one is asked again. Called where the app
	/// learns the adventure is gone, beside <see cref="CurrentRideState.ForgetAsync"/>.
	/// </summary>
	/// <param name="rideId">Which adventure.</param>
	/// <param name="cancellationToken">Abandons the write.</param>
	public ValueTask ForgetAsync(Guid rideId, CancellationToken cancellationToken = default) =>
		_asked.Remove(rideId) ? SaveAsync(cancellationToken) : ValueTask.CompletedTask;

	private async Task ReadAsync(CancellationToken cancellationToken)
	{
		_asked.Clear();
		_asked.AddRange(Decode(await settings.GetAsync(StorageKey, cancellationToken)));
	}

	/// <summary>
	/// Writes the set, or removes the key when it is empty - the same thing
	/// <see cref="CurrentRideState.ClearAsync"/> does, and for the same reason.
	/// </summary>
	private ValueTask SaveAsync(CancellationToken cancellationToken) =>
		_asked.Count == 0
			? settings.RemoveAsync(StorageKey, cancellationToken)
			: settings.SetAsync(StorageKey, "1|" + string.Join(',', _asked.Select(id => id.ToString("N"))), cancellationToken);

	/// <summary>
	/// Reads back what <see cref="SaveAsync"/> wrote. Anything else - a value from a format this
	/// version does not know, a half-written string - reads as "nobody has been asked", which is
	/// what a device with nothing stored answers and errs towards asking rather than towards
	/// silence.
	/// </summary>
	private static List<Guid> Decode(string? stored)
	{
		List<Guid> asked = [];

		if (stored is null || !stored.StartsWith("1|", StringComparison.Ordinal))
			return asked;

		foreach (string part in stored[2..].Split(',', StringSplitOptions.RemoveEmptyEntries))
		{
			if (Guid.TryParseExact(part, "N", out Guid rideId) && rideId != Guid.Empty && !asked.Contains(rideId))
			{
				asked.Add(rideId);
			}
		}

		if (asked.Count > MaxTracked)
			asked.RemoveRange(MaxTracked, asked.Count - MaxTracked);

		return asked;
	}
}
