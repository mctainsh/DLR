using DLR.Architecture.Tests.Rules;

namespace DLR.Architecture.Tests;

/// <summary>
/// What each host's HTML shell has to link for the shared UI to actually look like itself.
/// <para>
/// These are the failures nothing else catches. A missing stylesheet is not a compile error,
/// not a bUnit failure — bUnit asserts on markup and never loads CSS — and not something a
/// server-side integration test can see. It is a page that renders every element in the right
/// order with none of the layout, which is exactly what the web host was doing.
/// </para>
/// </summary>
public sealed class HostShellRules
{
	/// <summary>The Blazor Web App's SSR shell.</summary>
	private const string WebShell = "BlazorDLR.Web/Components/App.razor";

	/// <summary>The MAUI BlazorWebView's shell.</summary>
	private const string MauiShell = "BlazorDLR/wwwroot/index.html";

	/// <summary>Where the icon font is vendored, relative to the shared RCL's wwwroot.</summary>
	private const string IconFontCss = "lib/fontawesome/css/all.min.css";

	private static string Read(string relativePath)
	{
		string path = Path.Combine(SourceTree.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

		File.Exists(path).ShouldBeTrue($"{relativePath} is a host shell and must exist.");

		return File.ReadAllText(path);
	}

	/// <summary>
	/// The nav is drawn with Font Awesome classes, so every host has to link the font — and
	/// link the vendored copy, not a CDN. The phone is the host most likely to be offline and
	/// the one whose whole navigation is icons; a CDN turns a tunnel into a row of empty
	/// squares with no way back to the ride list.
	/// </summary>
	[Fact]
	public void EveryHostShell_LinksTheVendoredIconFont()
	{
		foreach (string shell in new[] { WebShell, MauiShell })
		{
			string markup = Read(shell);

			markup.ShouldContain(IconFontCss,
				customMessage: $"{shell} must link the vendored Font Awesome stylesheet — NavMenu draws "
					+ "every destination with `fa-` classes and renders blank without it.");

			markup.Contains("fontawesome", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
			markup.Contains("cdn.jsdelivr.net/npm/@fortawesome", StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
				$"{shell} must not pull the icon font from a CDN — see the vendoring rule above.");
			markup.Contains("use.fontawesome.com", StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
				$"{shell} must not pull the icon font from a CDN — see the vendoring rule above.");
		}
	}

	/// <summary>
	/// The vendored font has to be on disk, with the licence that lets us ship it. Font Awesome
	/// Free is CC BY 4.0 for the icons and SIL OFL 1.1 for the font itself; §14.6.3's licence
	/// gate applies to a woff2 in wwwroot exactly as it applies to a NuGet package.
	/// </summary>
	[Fact]
	public void TheIconFont_IsVendored_WithItsLicence()
	{
		string root = Path.Combine(SourceTree.Root, "BlazorDLR.Shared", "wwwroot", "lib", "fontawesome");

		foreach (string asset in new[] { "css/all.min.css", "webfonts/fa-solid-900.woff2", "LICENSE.txt" })
		{
			string path = Path.Combine(root, asset.Replace('/', Path.DirectorySeparatorChar));

			File.Exists(path).ShouldBeTrue(
				$"{asset} is missing from the vendored icon font. The stylesheet alone renders nothing: "
					+ "`fa` resolves to the solid weight, so fa-solid-900.woff2 is the file the glyphs "
					+ "come out of, and LICENSE.txt is what lets us redistribute it.");
		}
	}

	/// <summary>
	/// The scoped-component bundle is named for the <em>assembly</em>, and this project's
	/// assembly name is <c>DLR.Server</c> rather than <c>BlazorDLR.Web</c> (§ the .csproj, kept
	/// that way so <c>WebApplicationFactory&lt;Program&gt;</c> binds). Linking the project name
	/// gives a 404 and silently drops every <c>.razor.css</c> in the shared RCL — the layout,
	/// the nav rail, the lot.
	/// </summary>
	[Fact]
	public void TheWebShell_LinksTheScopedBundle_UnderTheAssemblyName()
	{
		string markup = Read(WebShell);

		markup.ShouldContain("DLR.Server.styles.css",
			customMessage: "the scoped-CSS bundle is served as {AssemblyName}.styles.css. Without this link "
				+ "MainLayout.razor.css and NavMenu.razor.css do not reach the browser, and the nav has "
				+ "no rail — on the one host where a window gets resized across the breakpoint.");
	}
}
