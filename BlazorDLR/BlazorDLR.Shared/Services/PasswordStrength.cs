namespace BlazorDLR.Shared.Services;

/// <summary>How a password reads on the sign-up meter. Advisory, never a gate.</summary>
public enum PasswordStrengthLevel
{
	/// <summary>Nothing typed yet. The meter is not drawn at all.</summary>
	Empty = 0,

	/// <summary>Short, repetitive, or missing one of §7.2's rules.</summary>
	Weak = 1,

	/// <summary>Passes the rules and nothing more. The floor, not a recommendation.</summary>
	Fair = 2,

	/// <summary>Long enough that length is doing real work.</summary>
	Good = 3,

	/// <summary>Long and varied.</summary>
	Strong = 4,
}

/// <summary>
/// What the sign-up form says about a password as it is typed (§7.2).
/// <para>
/// <strong>This is a hint, not a policy.</strong> The server decides what it accepts, and
/// <see cref="Unmet"/> only mirrors the rules Identity is configured with so the rider learns
/// what is missing while they are still in the field, rather than at submit. If the two ever
/// disagree the server wins, which is why nothing here disables the button — a meter that
/// blocks submission on a rule the server does not have is a rider who cannot register.
/// </para>
/// <para>
/// The breach-corpus lookup that used to sit behind registration was removed at v0.23, so
/// there is no longer anything to tell a rider that <c>Passw0rd1</c> is a password the whole
/// world already has. The meter cannot replace that — it can only be honest that a short
/// password scraping past the rules is <see cref="PasswordStrengthLevel.Weak"/>.
/// </para>
/// </summary>
/// <param name="Level">Where the bar sits, and how many segments are filled.</param>
/// <param name="Unmet">The §7.2 rules this password still breaks, phrased for display.</param>
public readonly record struct PasswordAssessment(
	PasswordStrengthLevel Level,
	IReadOnlyList<string> Unmet)
{
	/// <summary>Whether every rule the server is known to enforce is satisfied.</summary>
	public bool MeetsPolicy => Unmet.Count == 0;

	/// <summary>The word beside the bar.</summary>
	public string Label => Level switch
	{
		PasswordStrengthLevel.Weak => "Weak",
		PasswordStrengthLevel.Fair => "Fair",
		PasswordStrengthLevel.Good => "Good",
		PasswordStrengthLevel.Strong => "Strong",
		_ => string.Empty,
	};
}

/// <summary>
/// Scores a candidate password for the sign-up meter (§7.2).
/// <para>
/// Deliberately arithmetic rather than a dictionary: a corpus big enough to be worth
/// consulting is not something to ship into a WASM bundle, and the small one that would fit
/// is the kind that calls <c>hunter2</c> strong. Length and variety are the two things a
/// client can measure honestly.
/// </para>
/// </summary>
public static class PasswordStrength
{
	/// <summary>Identity's <c>RequiredLength</c> (§7.2, v0.22). Kept in step by hand — see the class remarks.</summary>
	public const int MinimumLength = 6;

	/// <summary>Segments in the bar, so the view and the score agree on how full "Strong" is.</summary>
	public const int Segments = 4;

	/// <summary>
	/// Below this many distinct characters, length is an illusion — <c>aaaaaaaaaaaa</c> is
	/// twelve characters and one guess — so the score is capped at
	/// <see cref="PasswordStrengthLevel.Weak"/> however long the string is.
	/// </summary>
	private const int VarietyFloor = 5;

	/// <summary>
	/// Reads a password the way the meter reports it.
	/// </summary>
	/// <param name="password">What is in the field right now; <c>null</c> or empty is <see cref="PasswordStrengthLevel.Empty"/>.</param>
	public static PasswordAssessment Assess(string? password)
	{
		if (string.IsNullOrEmpty(password))
		{
			return new PasswordAssessment(PasswordStrengthLevel.Empty, []);
		}

		bool upper = false;
		bool lower = false;
		bool digit = false;
		bool symbol = false;

		foreach (char character in password)
		{
			if (char.IsUpper(character))
			{
				upper = true;
			}
			else if (char.IsLower(character))
			{
				lower = true;
			}
			else if (char.IsDigit(character))
			{
				digit = true;
			}
			else
			{
				symbol = true;
			}
		}

		List<string> unmet = [];

		if (password.Length < MinimumLength)
		{
			unmet.Add($"at least {MinimumLength} characters");
		}

		if (!upper)
		{
			unmet.Add("an uppercase letter");
		}

		if (!lower)
		{
			unmet.Add("a lowercase letter");
		}

		if (!digit)
		{
			unmet.Add("a digit");
		}

		// A password that does not yet pass is never described as anything but weak, whatever
		// its length: the honest reading of "14 characters, no digit" is that the server will
		// refuse it, and a bar showing "Good" beside a rule the user has not met reads as a
		// disagreement between two parts of the same form.
		if (unmet.Count > 0)
		{
			return new PasswordAssessment(PasswordStrengthLevel.Weak, unmet);
		}

		int distinct = password.Distinct().Count();

		if (distinct < VarietyFloor)
		{
			return new PasswordAssessment(PasswordStrengthLevel.Weak, unmet);
		}

		int variety = (upper ? 1 : 0) + (lower ? 1 : 0) + (digit ? 1 : 0) + (symbol ? 1 : 0);

		// Length is scored on its own ladder rather than as a bonus on top of variety, so a
		// long passphrase can reach the top of the bar without a symbol in it. Anything else
		// would make the meter demand exactly what §7.2 refused to require.
		int length = password.Length switch
		{
			>= 20 => 4,
			>= 14 => 3,
			>= 10 => 2,
			>= 8 => 1,
			_ => 0,
		};

		int points = variety + length;

		PasswordStrengthLevel level = points switch
		{
			<= 3 => PasswordStrengthLevel.Weak,
			4 => PasswordStrengthLevel.Fair,
			5 or 6 => PasswordStrengthLevel.Good,
			_ => PasswordStrengthLevel.Strong,
		};

		return new PasswordAssessment(level, unmet);
	}
}
