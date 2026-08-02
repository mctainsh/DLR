using System.Security.Cryptography;

namespace DLR.Core.Rides;

/// <summary>
/// The six-character code an organiser hands out (§5.2).
/// <para>
/// Crockford base32: no <c>I</c>, <c>L</c>, <c>O</c> or <c>U</c>. The first three are dropped
/// because a code is read aloud across a car park and typed by someone wearing gloves — and
/// <c>U</c> because excluding it is how the alphabet avoids spelling things nobody wants
/// printed on a screen.
/// </para>
/// <para>
/// Reading is deliberately forgiving in the directions the omissions create: <c>I</c> and
/// <c>L</c> normalise to <c>1</c>, <c>O</c> to <c>0</c>, and case is ignored. Somebody who
/// mistypes the way the alphabet anticipated still gets into the ride.
/// </para>
/// </summary>
public static class JoinCode
{
	/// <summary>Crockford's encoding alphabet, in order.</summary>
	public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

	/// <summary>How many characters a code has.</summary>
	public const int Length = 6;

	/// <summary>
	/// How many codes exist: 32⁶, a little over a billion.
	/// <para>
	/// Impractical to guess at human speed and entirely practical for a script, which is why
	/// §14.5 requires the join endpoint to be rate-limited before the repository goes public.
	/// Publishing the format makes that obvious to anyone reading — so the limit is the
	/// defence, not the alphabet.
	/// </para>
	/// </summary>
	public const long Combinations = 1_073_741_824;

	/// <summary>Generates a code.</summary>
	public static string Generate()
	{
		Span<char> code = stackalloc char[Length];

		for (int index = 0; index < Length; index++)
		{
			code[index] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
		}

		return new string(code);
	}

	/// <summary>
	/// Puts a typed code into the form stored in the database, or null if it cannot be one.
	/// </summary>
	/// <param name="typed">Whatever the rider entered.</param>
	public static string? Normalise(string? typed)
	{
		if (string.IsNullOrWhiteSpace(typed))
		{
			return null;
		}

		Span<char> normalised = stackalloc char[Length];
		int written = 0;

		foreach (char character in typed)
		{
			// Spaces and hyphens are how people write a code down, not part of it.
			if (character is ' ' or '-')
			{
				continue;
			}

			if (written == Length)
			{
				return null;
			}

			char upper = char.ToUpperInvariant(character);

			char mapped = upper switch
			{
				'I' or 'L' => '1',
				'O' => '0',
				_ => upper,
			};

			if (!Alphabet.Contains(mapped, StringComparison.Ordinal))
			{
				return null;
			}

			normalised[written++] = mapped;
		}

		return written == Length ? new string(normalised) : null;
	}
}
