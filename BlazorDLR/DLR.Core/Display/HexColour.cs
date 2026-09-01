using System.Globalization;

namespace DLR.Core.Display;

/// <summary>
/// The one reader of a <c>#rrggbb</c> colour string, and the one answer to "what colour of ink
/// is legible on top of it".
/// <para>
/// <c>#rrggbb</c> exactly - six digits, one leading hash. It is the only form the Skia overlay's
/// own parser accepts and the only form an <c>&lt;input type="color"&gt;</c> ever produces, so
/// accepting more here would mean a value that validates, saves, persists, and then silently
/// draws as somebody's fallback blue.
/// </para>
/// <para>
/// In <c>DLR.Core</c> rather than beside the overlay because both ends need it: the server
/// validates the colour a rider chose before it is stored (§7.14), and every host draws with it.
/// Two copies of a colour parser is two answers to "is <c>#GGG</c> a colour".
/// </para>
/// </summary>
public static class HexColour
{
	/// <summary>Black, in the one form this type accepts.</summary>
	public const string Black = "#000000";

	/// <summary>White, in the one form this type accepts.</summary>
	public const string White = "#ffffff";

	/// <summary>Whether <paramref name="colour"/> is a <c>#rrggbb</c> string. Case-insensitive.</summary>
	/// <param name="colour">The candidate colour.</param>
	/// <returns><c>true</c> when it is six hex digits behind a hash.</returns>
	public static bool IsHex(string? colour)
	{
		if (colour is not { Length: 7 } || colour[0] != '#')
		{
			return false;
		}

		for (int index = 1; index < colour.Length; index++)
		{
			if (!Uri.IsHexDigit(colour[index]))
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// Lower-cases a <c>#rrggbb</c> colour, or answers <paramref name="fallback"/> when the string
	/// is not one.
	/// </summary>
	/// <param name="colour">The candidate colour.</param>
	/// <param name="fallback">What to answer when it is not a six-digit hex colour.</param>
	/// <returns>The normalised colour, or the fallback unchanged.</returns>
	public static string Normalise(string? colour, string fallback) =>
		IsHex(colour) ? colour!.ToLowerInvariant() : fallback;

	/// <summary>
	/// Splits a <c>#rrggbb</c> colour into its three channels.
	/// </summary>
	/// <param name="colour">The candidate colour.</param>
	/// <param name="red">The red channel, 0–255.</param>
	/// <param name="green">The green channel, 0–255.</param>
	/// <param name="blue">The blue channel, 0–255.</param>
	/// <returns><c>false</c> when the string is not a colour, in which case the channels are zero.</returns>
	public static bool TryChannels(string? colour, out byte red, out byte green, out byte blue)
	{
		red = green = blue = 0;

		return IsHex(colour)
			&& byte.TryParse(colour.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out red)
			&& byte.TryParse(colour.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out green)
			&& byte.TryParse(colour.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out blue);
	}

	/// <summary>
	/// WCAG relative luminance, 0 for black and 1 for white.
	/// <para>
	/// The gamma-corrected form, not the plain channel average: sRGB is not linear in light, and
	/// the average is what makes a saturated blue and a saturated yellow score the same when one
	/// of them plainly needs white text and the other plainly needs black.
	/// </para>
	/// </summary>
	/// <param name="colour">A <c>#rrggbb</c> colour.</param>
	/// <returns>The luminance, or 1 (white) for a string that is not a colour - the value that
	/// makes <see cref="ContrastingForeground"/> answer black, which is the safe ink for a
	/// surface nobody could measure.</returns>
	public static double RelativeLuminance(string? colour)
	{
		if (!TryChannels(colour, out byte red, out byte green, out byte blue))
		{
			return 1;
		}

		return (0.2126 * Linear(red)) + (0.7152 * Linear(green)) + (0.0722 * Linear(blue));
	}

	/// <summary>
	/// Black or white, whichever has the higher contrast ratio against
	/// <paramref name="background"/> - the "opposite colour" a marker's text and border are drawn
	/// in (§16.3).
	/// <para>
	/// Ratios rather than a hard luminance threshold, because the threshold is only ever the
	/// answer this comparison already gives and a number copied out of it drifts when somebody
	/// rounds it.
	/// </para>
	/// </summary>
	/// <param name="background">The colour being drawn on top of.</param>
	/// <returns><see cref="Black"/> or <see cref="White"/>.</returns>
	public static string ContrastingForeground(string? background)
	{
		double luminance = RelativeLuminance(background);

		double blackRatio = (luminance + 0.05) / 0.05;
		double whiteRatio = 1.05 / (luminance + 0.05);

		return blackRatio >= whiteRatio ? Black : White;
	}

	/// <summary>One channel, converted from sRGB to linear light.</summary>
	private static double Linear(byte channel)
	{
		double value = channel / 255.0;

		return value <= 0.03928
			? value / 12.92
			: Math.Pow((value + 0.055) / 1.055, 2.4);
	}
}
