using DLR.Architecture.Tests.Rules;

namespace DLR.Architecture.Tests;

/// <summary>
/// The two rules that keep the shared UI shared (§18.2, SharedFrontend.md §1).
/// <para>
/// A shared UI library that quietly stops compiling into WebAssembly is the failure mode
/// this whole architecture exists to prevent. These rules turn "we agreed not to do X" into
/// a build failure the first time somebody does it.
/// </para>
/// </summary>
public sealed class UiLayeringRules
{
	/// <summary>
	/// Assemblies whose presence in <c>BlazorDLR.Shared</c>'s reference graph would make it
	/// mobile-only. Matched by prefix, because the AndroidX and MAUI namespaces both split
	/// into many assemblies and enumerating them is a maintenance burden the rule does not
	/// need to carry.
	/// </summary>
	private static readonly string[] MobileOnlyAssemblyPrefixes =
	[
		"Microsoft.Maui",
		"Microsoft.AspNetCore.Components.WebView",
	];

	/// <summary>
	/// The one load-bearing rule of §18: the shared UI library must compile into a browser.
	/// <para>
	/// A MAUI reference here silently makes every screen mobile-only, and the failure surfaces
	/// as a WebAssembly build error a week later on the branch that thought it was only
	/// touching mobile - a MAUI reference in <c>BlazorDLR.Shared</c> is not a mistake we discover
	/// by rebuilding the phone app, because it still builds. This rule turns it into a red test.
	/// </para>
	/// </summary>
	[Fact]
	public void SharedUi_ReferencesNoMobileOnlyAssembly()
	{
		CompiledAssembly shared = CompiledAssembly.Named("BlazorDLR.Shared");

		IEnumerable<string> offenders = shared.AssemblyReferences.Where(IsMobileOnly);

		offenders.ShouldBeEmpty(
			"BlazorDLR.Shared is compiled into WebAssembly as well as into the MAUI Blazor Hybrid " +
			"host. A MAUI or WebView reference here makes the whole shared library mobile-only, " +
			"and the break is silent until something that is not a phone tries to build against it. " +
			"The correct move is always an interface with a per-host registration - see the pattern " +
			"in BlazorDLR.Shared/Services/, and IFormFactor as the canonical example.");
	}

	/// <summary>
	/// Platform preprocessor symbols inside a shared component are two libraries wearing one
	/// name (§18.2). Every place one is needed, an interface with per-host implementations is
	/// the right answer instead - the pattern <see cref="Shared.Services.IFormFactor"/>
	/// already demonstrates.
	/// </summary>
	[Fact]
	public void SharedUi_UsesNoPlatformConditionalCompilation()
	{
		string[] platformSymbols =
		[
			"#if ANDROID",
			"#if IOS",
			"#if MACCATALYST",
			"#if WINDOWS",
			"#if __ANDROID__",
			"#if __IOS__",
		];

		List<string> offenders = SourceTree.Under("src/BlazorDLR.Shared/")
			.SelectMany(file => file.Cite(file.LinesContaining(platformSymbols)))
			.ToList();

		offenders.ShouldBeEmpty(
			"BlazorDLR.Shared components render on every host: WebAssembly, MAUI Android and " +
			"MAUI iOS. #if ANDROID / #if IOS in a shared component compiles differently on " +
			"different hosts, which is two libraries wearing one name. The correct move is an " +
			"interface with two registrations - see IFormFactor in BlazorDLR.Shared/Services/.");
	}

	private static bool IsMobileOnly(string assemblyName) =>
		MobileOnlyAssemblyPrefixes.Any(prefix => assemblyName.StartsWith(prefix, StringComparison.Ordinal));

	/// <summary>
	/// §18.7's guarantee: a shared component is testable without a simulator, emulator or
	/// browser. The DLR.UI.Tests project renders every screen in a plain <c>dotnet test</c>
	/// pass, and it does that by linking BlazorDLR.Shared and bUnit - never any MAUI
	/// assembly. This rule catches the day somebody adds a MAUI reference to the UI test
	/// project because "just one test wants MediaPicker".
	/// </summary>
	[Fact]
	public void UiTests_ReferenceNoMobileOnlyAssembly()
	{
		CompiledAssembly uiTests = CompiledAssembly.Named("DLR.UI.Tests");

		IEnumerable<string> offenders = uiTests.AssemblyReferences.Where(IsMobileOnly);

		offenders.ShouldBeEmpty(
			"DLR.UI.Tests runs shared components under bUnit in a plain dotnet test pass (§18.7). " +
			"A MAUI reference here breaks that promise - the tests would need a simulator or an " +
			"emulator to run, and the point of the bUnit choice was that they never do.");
	}
}
