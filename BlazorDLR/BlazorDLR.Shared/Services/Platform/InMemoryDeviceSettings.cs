namespace BlazorDLR.Shared.Services.Platform;

/// <summary>
/// An <see cref="IDeviceSettings"/> that forgets everything when the process does.
/// <para>
/// Bound by the SSR pass, which has no device to store anything on - the prerender renders
/// with the shipped defaults and the WASM client re-resolves against browser
/// <c>localStorage</c> the moment it boots. Also what bUnit tests get, so a test that sets a
/// preference can read it back without a browser.
/// </para>
/// </summary>
public sealed class InMemoryDeviceSettings : IDeviceSettings
{
	private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

	/// <inheritdoc />
	public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(_values.TryGetValue(key, out string? value) ? value : null);

	/// <inheritdoc />
	public ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default)
	{
		_values[key] = value;
		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
	{
		_values.Remove(key);
		return ValueTask.CompletedTask;
	}
}
