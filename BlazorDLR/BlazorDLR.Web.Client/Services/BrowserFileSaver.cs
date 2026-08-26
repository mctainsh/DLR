using BlazorDLR.Shared.Services;
using Microsoft.JSInterop;

namespace BlazorDLR.Web.Client.Services;

/// <summary>
/// The web's <see cref="IFileSaver"/>: a Blob URL on a synthetic <c>&lt;a download&gt;</c>
/// click (see <c>_content/BlazorDLR.Shared/download.js</c>). The browser takes it from
/// there — its own download UI is the confirmation, so nothing is reported back.
/// </summary>
public sealed class BrowserFileSaver : IFileSaver, IAsyncDisposable
{
	private const string ModulePath = "./_content/BlazorDLR.Shared/download.js";

	private readonly IJSRuntime _js;

	private IJSObjectReference? _module;

	/// <param name="js">The runtime the module is imported into.</param>
	public BrowserFileSaver(IJSRuntime js) => _js = js;

	/// <inheritdoc />
	public async Task<FileSaveResult> SaveAsync(
		string fileName,
		string contentType,
		byte[] content,
		CancellationToken cancellationToken = default)
	{
		try
		{
			_module ??= await _js.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath);

			// byte[] crosses to JS as a Uint8Array without a base64 round trip.
			await _module.InvokeVoidAsync("save", cancellationToken, fileName, contentType, content);
			return FileSaveResult.Saved();
		}
		catch (JSException exception)
		{
			return FileSaveResult.Failed(exception.Message);
		}
	}

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		if (_module is null)
			return;

		try
		{
			await _module.DisposeAsync();
		}
		catch (JSDisconnectedException)
		{
			// The circuit went away before the module did. Nothing left to dispose.
		}

		_module = null;
	}
}
