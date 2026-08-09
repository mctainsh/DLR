using BlazorDLR.Shared.Services;
using Microsoft.JSInterop;

namespace BlazorDLR.Web.Client.Services;

/// <summary>
/// The web binding for <see cref="IDeviceSettings"/> — browser <c>localStorage</c>, which is
/// per-origin and per-browser-profile and therefore exactly the "this device" the interface
/// promises. Nothing written here travels over the wire.
/// </summary>
public sealed class LocalStorageDeviceSettings : IDeviceSettings
{
	private readonly IJSRuntime _js;

	/// <summary>Creates the binding over the client's JS runtime.</summary>
	/// <param name="js">Used for the three <c>localStorage</c> calls.</param>
	public LocalStorageDeviceSettings(IJSRuntime js) => _js = js;

	/// <inheritdoc />
	public async ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default)
	{
		try
		{
			return await _js.InvokeAsync<string?>("localStorage.getItem", cancellationToken, key);
		}
		catch
		{
			// localStorage throws outright when the user has blocked site data, and the
			// interop call itself throws if this runs before WASM is interactive. Both mean
			// "no stored value", which is what a first run means too.
			return null;
		}
	}

	/// <inheritdoc />
	public async ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default)
	{
		try
		{
			await _js.InvokeVoidAsync("localStorage.setItem", cancellationToken, key, value);
		}
		catch
		{
			// Ignored: the caller has already applied the value in memory, so the setting
			// works for this session and simply does not survive a reload.
		}
	}

	/// <inheritdoc />
	public async ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
	{
		try
		{
			await _js.InvokeVoidAsync("localStorage.removeItem", cancellationToken, key);
		}
		catch
		{
			// Ignored, for the same reason as SetAsync.
		}
	}
}
