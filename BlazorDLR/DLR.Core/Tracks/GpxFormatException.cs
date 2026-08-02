namespace DLR.Core.Tracks;

/// <summary>Why a GPX file could not be read, in a form a caller can act on.</summary>
public enum GpxProblem
{
	/// <summary>The bytes are not XML at all.</summary>
	NotXml = 0,

	/// <summary>Well-formed XML, but nothing that looks like GPX.</summary>
	NotGpx = 1,

	/// <summary>The document ends mid-element.</summary>
	Truncated = 2,

	/// <summary>
	/// A document type declaration. Refused before it is read, because a DTD is both the XXE
	/// vector and the billion-laughs vector (§15.3).
	/// </summary>
	DtdNotAllowed = 3,

	/// <summary>A latitude, longitude or elevation that is not a usable number.</summary>
	InvalidCoordinate = 4,

	/// <summary>The file holds more points than the cap allows.</summary>
	TooManyPoints = 5,
}

/// <summary>
/// A GPX file that could not be read, naming the problem (§15.3).
/// <para>
/// "Invalid file" is useless to somebody whose exporter emits something slightly unusual, and
/// this is a feature people will meet with files from a dozen different tools. So the message
/// carries the element and the position where the reader knows them, and the caller turns it
/// into Problem Details that say the same thing.
/// </para>
/// </summary>
public sealed class GpxFormatException : Exception
{
	/// <summary>Creates one.</summary>
	/// <param name="problem">Which kind of failure.</param>
	/// <param name="message">What went wrong, in words a person can use.</param>
	/// <param name="lineNumber">Where, when the reader knows.</param>
	/// <param name="linePosition">Where on the line, when the reader knows.</param>
	/// <param name="innerException">The underlying failure, if any.</param>
	public GpxFormatException(
		GpxProblem problem,
		string message,
		int? lineNumber = null,
		int? linePosition = null,
		Exception? innerException = null)
		: base(message, innerException)
	{
		Problem = problem;
		LineNumber = lineNumber;
		LinePosition = linePosition;
	}

	/// <summary>Which kind of failure this was.</summary>
	public GpxProblem Problem { get; }

	/// <summary>Line the reader was on, when it knows.</summary>
	public int? LineNumber { get; }

	/// <summary>Position on that line, when the reader knows.</summary>
	public int? LinePosition { get; }

	/// <summary>The message with the position appended, for a Problem Details detail string.</summary>
	public string Describe() =>
		LineNumber is { } line ? $"{Message} (line {line}, position {LinePosition ?? 0})" : Message;
}
