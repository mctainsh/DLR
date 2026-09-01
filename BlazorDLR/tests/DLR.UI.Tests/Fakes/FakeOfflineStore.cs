using System.Collections.Concurrent;
using BlazorDLR.Shared.Services;

namespace DLR.UI.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IOfflineStore"/>. Stands in for the phone's file-backed store without
/// touching a disk, and - the part that matters for a cache - survives being read after the thing
/// that wrote it has gone, which is how a test spells "the app was restarted".
/// </summary>
public sealed class FakeOfflineStore : IOfflineStore
{
	private readonly ConcurrentDictionary<string, string> _entries = new(StringComparer.Ordinal);

	/// <summary>Whether this store keeps anything. Settable, so a test can play the browser (§18.6).</summary>
	public bool IsSupported { get; set; } = true;

	/// <summary>How many entries are held, so a test can assert a ride was forgotten.</summary>
	public int Count => _entries.Count;

	/// <summary>Whether an entry exists under <paramref name="name"/>.</summary>
	public bool Contains(string name) => _entries.ContainsKey(name);

	public ValueTask<string?> ReadAsync(string name, CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(_entries.TryGetValue(name, out string? value) ? value : null);

	public ValueTask WriteAsync(string name, string content, CancellationToken cancellationToken = default)
	{
		// Mirrors the real store, which drops writes it cannot land rather than throwing - a test
		// playing the browser must see the same silence a browser does.
		if (IsSupported)
		{
			_entries[name] = content;
		}

		return ValueTask.CompletedTask;
	}

	public ValueTask RemoveAsync(string name, CancellationToken cancellationToken = default)
	{
		_entries.TryRemove(name, out _);
		return ValueTask.CompletedTask;
	}
}
