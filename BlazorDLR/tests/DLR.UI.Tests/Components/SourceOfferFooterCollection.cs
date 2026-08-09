namespace DLR.UI.Tests.Components;

/// <summary>
/// The test classes that render <c>SourceOfferFooter</c>, run one at a time.
/// <para>
/// The component caches the About response in a private <em>static</em> so navigating between
/// screens does not refetch it — which is right for an app and wrong for a test host, where each
/// test brings its own fake. Both footer test classes already null that field in their
/// constructors; what they cannot do is stop a <em>concurrently running</em> class from filling it
/// in again, and xUnit runs collections in parallel. <c>Welcome</c> renders the footer, so
/// <c>WelcomeTests</c> racing <c>SourceOfferFooterTests</c> is a real failure that appears and
/// disappears with unrelated changes to how many other tests there are.
/// </para>
/// <para>
/// One collection is the smallest fix that actually closes it. Anything that renders the footer,
/// directly or through a page that contains it, belongs here.
/// </para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class SourceOfferFooterCollection
{
	/// <summary>The collection name, for <c>[Collection(...)]</c>.</summary>
	public const string Name = "source-offer-footer";
}
