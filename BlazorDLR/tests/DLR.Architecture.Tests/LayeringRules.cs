using DLR.Architecture.Tests.Rules;

namespace DLR.Architecture.Tests;

/// <summary>
/// Layering rules from §10.4. These pass trivially today, and that is the point:
/// each one fails the first time somebody adds the reference it forbids.
/// </summary>
public sealed class LayeringRules
{
	private static readonly string[] MauiAssemblyPrefixes =
	[
		"Microsoft.Maui",
		"Microsoft.AspNetCore.Components.WebView.Maui",
	];

	[Fact]
	public void Core_ReferencesNoMauiAssembly()
	{
		CompiledAssembly core = CompiledAssembly.Named("DLR.Core");

		IEnumerable<string> maui = core.AssemblyReferences.Where(IsMaui);

		maui.ShouldBeEmpty(
			"DLR.Core is compiled into the WASM client and the server as well as the phone app. " +
			"A MAUI reference here makes the shared core mobile-only, and the break is silent " +
			"until something that is not a phone tries to build.");
	}

	[Fact]
	public void NoInspectableAssemblyReferencesMaui()
	{
		Dictionary<string, string[]> offenders = CompiledAssembly.All
			.Select(assembly => (assembly.Name, Maui: assembly.AssemblyReferences.Where(IsMaui).ToArray()))
			.Where(x => x.Maui.Length > 0)
			.ToDictionary(x => x.Name, x => x.Maui);

		offenders.ShouldBeEmpty(
			"Only DLR.App may reference MAUI, and DLR.App is not one of the assemblies this " +
			"project inspects. Anything else reaching for MAUI is a layering mistake: " +
			$"{Describe(offenders)}");
	}

	private static bool IsMaui(string assemblyName) =>
		MauiAssemblyPrefixes.Any(prefix => assemblyName.StartsWith(prefix, StringComparison.Ordinal));

	private static string Describe(Dictionary<string, string[]> offenders) =>
		string.Join("; ", offenders.Select(pair => $"{pair.Key} → {string.Join(", ", pair.Value)}"));
}
