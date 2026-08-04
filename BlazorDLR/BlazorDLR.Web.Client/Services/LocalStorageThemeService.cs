using BlazorDLR.Shared.Services;
using Microsoft.JSInterop;

namespace BlazorDLR.Web.Client.Services;

/// <summary>
/// The web binding for <see cref="IThemeService"/> — reads/writes browser
/// <c>localStorage</c> under the key <c>dlr.theme</c>. Runs entirely on the client,
/// so the setting survives a page reload but never travels over the wire.
/// </summary>
public sealed class LocalStorageThemeService : IThemeService
{
	private const string Key = "dlr.theme";
	private readonly IJSRuntime _js;

	public LocalStorageThemeService(IJSRuntime js) => _js = js;

	public async Task<AppTheme> GetAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			string? value = await _js.InvokeAsync<string?>("localStorage.getItem", cancellationToken, Key);
			return value switch
			{
				"light" => AppTheme.Light,
				"dark" => AppTheme.Dark,
				"system" => AppTheme.System,
				_ => AppTheme.Dark,
			};
		}
		catch
		{
			// localStorage can throw if the user has blocked it. The design default is dark.
			return AppTheme.Dark;
		}
	}

	public async Task SetAsync(AppTheme theme, CancellationToken cancellationToken = default)
	{
		string encoded = theme switch
		{
			AppTheme.Light => "light",
			AppTheme.System => "system",
			_ => "dark",
		};
		try
		{
			await _js.InvokeVoidAsync("localStorage.setItem", cancellationToken, Key, encoded);
		}
		catch
		{
			// Ignore write failures — the in-memory state has already updated.
		}
	}
}
