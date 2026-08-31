using BlazorDLR.Shared.Services;

namespace DLR.UI.Tests.Fakes;

/// <summary>A <see cref="IFormFactor"/> that returns whatever the test needs.</summary>
public sealed class FakeFormFactor : IFormFactor
{
	public string FormFactor { get; set; } = "Test";
	public string Platform { get; set; } = "xunit";

	/// <summary>Null by default, which is what the browser hosts answer (§7.10).</summary>
	public string? DeviceName { get; set; }

	public string GetFormFactor() => FormFactor;
	public string GetPlatform() => Platform;
	public string? GetDeviceName() => DeviceName;
}
