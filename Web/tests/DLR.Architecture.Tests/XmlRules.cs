using DLR.Architecture.Tests.Rules;

namespace DLR.Architecture.Tests;

/// <summary>
/// GPX import is the only place the server parses a file a stranger chose the bytes of
/// (§15.3). These two rules close XXE permanently, in the sense that closing it is a
/// build failure to reopen rather than a review comment somebody might miss.
/// </summary>
public sealed class XmlRules
{
	[Fact]
	public void NoAssemblyReferencesXmlDocument()
	{
		List<string> offenders = CompiledAssembly.All
			.Where(assembly => assembly.TypeReferences.Contains("System.Xml.XmlDocument", StringComparer.Ordinal))
			.Select(assembly => assembly.Name)
			.ToList();

		offenders.ShouldBeEmpty(
			"The DOM loads the whole document and resolves entities by default. GPX is read " +
			"with a streaming XmlReader configured to prohibit DTDs, which is also what lets " +
			"the point cap abort mid-stream instead of after buffering a million points.");
	}

	[Fact]
	public void NoSourceFileUsesXmlDocument()
	{
		List<string> offenders = SourceTree.Files
			.SelectMany(file => file.Cite(file.LinesContaining("XmlDocument", "XPathDocument")))
			.ToList();

		offenders.ShouldBeEmpty("Use XmlReader with DtdProcessing.Prohibit and a null XmlResolver.");
	}

	[Fact]
	public void EveryDtdProcessingSettingIsProhibit()
	{
		List<string> offenders = SourceTree.Files
			.SelectMany(file => file.Cite(file
				.LinesContaining("DtdProcessing")
				.Where(line => !file.CodeLines[line - 1].Contains("DtdProcessing.Prohibit", StringComparison.Ordinal))))
			.ToList();

		offenders.ShouldBeEmpty(
			"DtdProcessing.Parse resolves external entities; DtdProcessing.Ignore still walks " +
			"an internal subset and remains vulnerable to nested entity expansion. Prohibit is " +
			"the only setting this project accepts, and it is why " +
			"Import_ExternalEntityReference_MakesNoNetworkCall can be a test rather than a hope.");
	}
}
