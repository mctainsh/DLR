using BlazorDLR.Shared.Services;

namespace DLR.UI.Tests.Fakes;

/// <summary>A <see cref="IFormFactor"/> that returns whatever the test needs.</summary>
public sealed class FakeFormFactor : IFormFactor
{
	public string FormFactor { get; set; } = "Test";
	public string Platform { get; set; } = "xunit";

	public string GetFormFactor() => FormFactor;
	public string GetPlatform() => Platform;
}
