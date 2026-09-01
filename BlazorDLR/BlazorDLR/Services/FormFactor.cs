using BlazorDLR.Shared.Services;

namespace BlazorDLR.Services;

public class FormFactor : IFormFactor
{
	public string GetFormFactor()
	{
		return DeviceInfo.Idiom.ToString();
	}

	public string GetPlatform()
	{
		return DeviceInfo.Platform.ToString() + " - " + DeviceInfo.VersionString;
	}

	/// <inheritdoc />
	/// <remarks>
	/// The handset's own name first - "John's iPhone" is what a rider recognises. Android often
	/// has none, so the model stands in; both are what the phone says about itself and neither is
	/// verified server-side (§7.10).
	/// </remarks>
	public string? GetDeviceName()
	{
		if (!string.IsNullOrWhiteSpace(DeviceInfo.Name)) return DeviceInfo.Name;

		string model = $"{DeviceInfo.Manufacturer} {DeviceInfo.Model}".Trim();

		return string.IsNullOrWhiteSpace(model) ? null : model;
	}
}
