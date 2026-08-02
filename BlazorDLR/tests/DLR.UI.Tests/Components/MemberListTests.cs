using BlazorDLR.Shared.Components;
using Bunit;
using DLR.Core.Contracts.Rides;

namespace DLR.UI.Tests.Components;

/// <summary>
/// §5.6 leans hard on <em>three</em> distinct member states — sharing / not sharing /
/// no signal — never collapsed into two. Collapsing "not sharing" and "no signal" is
/// exactly the ambiguity that gets someone left behind, so the assertion here is on
/// the label text: three different words in the rendered DOM.
/// </summary>
public sealed class MemberListTests : BunitContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private static RideMemberSummary Member(string userName, bool sharing, bool hasPosition) =>
		new(Guid.NewGuid(), userName, "Rider", FixedInstant, sharing, hasPosition);

	[Fact]
	public void ThreeStates_AreRenderedWithDistinctLabels()
	{
		IRenderedComponent<MemberList> component = Render<MemberList>(parameters => parameters
			.Add(p => p.Members, new[]
			{
				Member("Alice", sharing: true, hasPosition: true),
				Member("Bob", sharing: false, hasPosition: false),
				Member("Cass", sharing: true, hasPosition: false),
			}));

		string markup = component.Markup;
		markup.Contains("sharing", StringComparison.Ordinal).ShouldBeTrue(
			"a rider broadcasting to the ride shows 'sharing'.");
		markup.Contains("not sharing", StringComparison.Ordinal).ShouldBeTrue(
			"§5.6: a rider who is in the ride but not broadcasting is 'not sharing', not 'no signal'.");
		markup.Contains("no signal", StringComparison.Ordinal).ShouldBeTrue(
			"§5.6: a rider who is broadcasting but has no fresh fix is 'no signal', not 'not sharing'.");
	}

	[Fact]
	public void MemberList_RendersMemberCount()
	{
		IRenderedComponent<MemberList> component = Render<MemberList>(parameters => parameters
			.Add(p => p.Members, new[]
			{
				Member("Alice", true, true),
				Member("Bob", false, false),
			}));

		component.Markup.Contains("Members (2)", StringComparison.Ordinal).ShouldBeTrue();
	}

	[Fact]
	public void EmptyList_RendersZero()
	{
		IRenderedComponent<MemberList> component = Render<MemberList>(parameters => parameters
			.Add(p => p.Members, Array.Empty<RideMemberSummary>()));

		component.Markup.Contains("Members (0)", StringComparison.Ordinal).ShouldBeTrue();
	}
}
