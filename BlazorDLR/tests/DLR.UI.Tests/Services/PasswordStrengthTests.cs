using BlazorDLR.Shared.Services;

namespace DLR.UI.Tests.Services;

/// <summary>
/// The sign-up meter's arithmetic (§7.2).
/// <para>
/// Worth pinning down because the meter is the only feedback a rider gets while choosing a
/// password now that v0.23 removed the breach lookup, and a meter that flatters is worse than
/// none: it converts "nobody checked" into "we checked and it is fine".
/// </para>
/// </summary>
public sealed class PasswordStrengthTests
{
	[Theory]
	[InlineData(null)]
	[InlineData("")]
	public void NothingTyped_IsEmpty_SoTheMeterIsNotDrawnAtAll(string? password)
	{
		PasswordAssessment assessment = PasswordStrength.Assess(password);

		assessment.Level.ShouldBe(PasswordStrengthLevel.Empty);
		assessment.Label.ShouldBe(string.Empty);
	}

	/// <summary>
	/// The rules Identity is configured with, phrased the way the field reports them. Each
	/// case breaks exactly one, so a missing arm shows up as a rule nobody is told about.
	/// </summary>
	[Theory]
	[InlineData("Aa1", "at least 6 characters")]
	[InlineData("abcdef1", "an uppercase letter")]
	[InlineData("ABCDEF1", "a lowercase letter")]
	[InlineData("Abcdefg", "a digit")]
	public void APasswordBreakingOneRule_NamesThatRule_AndReadsWeak(string password, string expected)
	{
		PasswordAssessment assessment = PasswordStrength.Assess(password);

		assessment.MeetsPolicy.ShouldBeFalse();
		assessment.Unmet.ShouldContain(expected);
		assessment.Level.ShouldBe(PasswordStrengthLevel.Weak,
			"a password the server would refuse must not be described as anything better.");
	}

	/// <summary>
	/// Length alone never outranks a broken rule. Without this the meter and the rule copy
	/// beside it disagree: "Strong", and then a rejection at submit for a missing digit.
	/// </summary>
	[Fact]
	public void ALongPasswordMissingARule_IsStillWeak()
	{
		PasswordAssessment assessment = PasswordStrength.Assess("thisisaveryverylongpassphrase");

		assessment.Level.ShouldBe(PasswordStrengthLevel.Weak);
		assessment.Unmet.ShouldContain("an uppercase letter");
		assessment.Unmet.ShouldContain("a digit");
	}

	/// <summary>
	/// Scraping past the rules is the floor, not an endorsement — §7.2's minimum is six
	/// characters, and six characters is a weak password whatever shape it has.
	/// </summary>
	[Fact]
	public void TheShortestPasswordTheServerAccepts_ReadsWeak_ButBreaksNoRule()
	{
		PasswordAssessment assessment = PasswordStrength.Assess("Abc1de");

		assessment.MeetsPolicy.ShouldBeTrue();
		assessment.Unmet.ShouldBeEmpty();
		assessment.Level.ShouldBe(PasswordStrengthLevel.Weak);
	}

	/// <summary>
	/// Repetition is not length. <c>Aa1Aa1Aa1Aa1Aa1Aa1</c> satisfies every rule and is
	/// eighteen characters, but there are three distinct ones in it — scoring it on length
	/// would be the meter telling a plain lie.
	/// </summary>
	[Fact]
	public void RepeatedCharacters_DoNotBuyLength()
	{
		PasswordStrength.Assess("Aa1Aa1Aa1Aa1Aa1Aa1").Level.ShouldBe(PasswordStrengthLevel.Weak);
	}

	/// <summary>
	/// The last two cases are the point: a passphrase with no symbol in it reaches the top of
	/// the bar on length alone. A meter that only says "Strong" for <c>Ride4mountains!</c>
	/// would be demanding the special character §7.2 deliberately does not require.
	/// </summary>
	[Theory]
	[InlineData("Ride4mount", PasswordStrengthLevel.Good)]
	[InlineData("Ride4mountains", PasswordStrengthLevel.Good)]
	[InlineData("Ride4mountainsEveryWeekend", PasswordStrengthLevel.Strong)]
	[InlineData("Ride4mountains!", PasswordStrengthLevel.Strong)]
	public void LengthAndVariety_MoveTheBarUp(string password, PasswordStrengthLevel expected)
	{
		PasswordAssessment assessment = PasswordStrength.Assess(password);

		assessment.MeetsPolicy.ShouldBeTrue();
		assessment.Level.ShouldBe(expected);
	}

	/// <summary>
	/// The one the removal of the breach check makes worth stating out loud: <c>Passw0rd1</c>
	/// is in every corpus there is, and nothing in the client can know that. The meter says
	/// "Fair" — which is the honest answer to what it can actually measure, and the reason
	/// §7.2 no longer claims a leaked password will be caught.
	/// </summary>
	[Fact]
	public void AWellKnownLeakedShape_ScoresOnItsArithmetic_BecauseNothingChecksACorpusAnyMore()
	{
		PasswordAssessment assessment = PasswordStrength.Assess("Passw0rd1");

		assessment.MeetsPolicy.ShouldBeTrue();
		assessment.Level.ShouldBe(PasswordStrengthLevel.Fair);
	}

	[Fact]
	public void EveryNonEmptyLevel_HasAWordBesideTheBar()
	{
		PasswordStrength.Assess("Abc1de").Label.ShouldBe("Weak");
		PasswordStrength.Assess("Passw0rd1").Label.ShouldBe("Fair");
		PasswordStrength.Assess("Ride4mountains").Label.ShouldBe("Good");
		PasswordStrength.Assess("Ride4mountains!").Label.ShouldBe("Strong");
	}
}
