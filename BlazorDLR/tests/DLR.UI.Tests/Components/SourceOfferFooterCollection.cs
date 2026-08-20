using System.Reflection;

using BlazorDLR.Shared.Components;

namespace DLR.UI.Tests.Components;

/// <summary>
/// The test classes that render <c>SourceOfferFooter</c>, run one at a time.
/// <para>
/// The component caches the About response in a private <em>static</em> so navigating between
/// screens does not refetch it — which is right for an app and wrong for a test host, where each
/// test brings its own fake. A test that renders the footer therefore has to clear that cache
/// first, and no <em>concurrently running</em> class may be allowed to fill it in again — xUnit
/// runs collections in parallel, and <c>Welcome</c>, the recovery pages and the introduction all
/// render the footer.
/// </para>
/// <para>
/// One collection closes the parallel half of that. Anything that renders the footer, directly or
/// through a page that contains it, belongs here — including pages whose tests never look at the
/// footer, because what matters is the write they cause, not the assertion they make.
/// </para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class SourceOfferFooterCollection
{
	/// <summary>The collection name, for <c>[Collection(...)]</c>.</summary>
	public const string Name = "source-offer-footer";
}

/// <summary>
/// Clears the footer's static About cache, so the next render fetches from the fake the test
/// wired rather than reusing whatever an earlier test left behind.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Call this in the test body, immediately before the render — never in a constructor.</strong>
/// That is not a style preference. xUnit builds a test class instance well ahead of running its
/// body: instrumenting the cache showed eight other footers rendering between one suite's
/// constructor and the render in its own test, all inside this serialized collection. A reset
/// written in a constructor is therefore undone by every footer that renders in the gap, and the
/// suite goes on to read another test's About — which surfaced as <c>WaitForAssertion</c> timing
/// out with a render count of one and markup carrying a commit nobody in that file had wired.
/// </para>
/// <para>
/// Adjacency is necessary and not sufficient, which is why there are three defences and not one.
/// The collection keeps other classes from running alongside; this keeps the reset next to the
/// render; and <c>FakeApiClient.AboutResult</c> is null by default, so a suite that merely happens
/// to render a footer cannot fill the cache at all — including from a render that lands after its
/// own test has finished, which is the one this pair could not have caught.
/// </para>
/// </remarks>
internal static class SourceOfferFooterCache
{
	private static readonly FieldInfo Cache =
		typeof(SourceOfferFooter).GetField("_cached", BindingFlags.NonPublic | BindingFlags.Static)!;

	/// <summary>Empties the cache. The next footer to render fetches About again.</summary>
	public static void Clear() => Cache.SetValue(null, null);
}
