namespace BlazorDLR.Shared.Services;

public interface IFormFactor
{
	public string GetFormFactor();
	public string GetPlatform();

	/// <summary>
	/// What this installation should be called in Settings → Signed-in devices - "Pixel 8", say
	/// (§7.10). <c>null</c> when the host has nothing to offer, which leaves the row unnamed
	/// rather than inventing a label nobody would recognise.
	/// </summary>
	/// <remarks>
	/// The browser hosts answer <c>null</c> on purpose: a browser signs in through a form post the
	/// server handles, and the server names those rows from the user agent, which is the only side
	/// that can see one.
	/// </remarks>
	public string? GetDeviceName() => null;
}
