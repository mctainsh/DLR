using BlazorDLR.Shared.Services;

namespace DLR.UI.Tests.Fakes;

/// <summary>
/// An <see cref="IFileSaver"/> that keeps what it was handed instead of writing anything. The
/// seam exists because the hosts disagree about what saving means — a browser download on the
/// web, the system share sheet on a phone — so the thing worth asserting here is that the page
/// got the bytes as far as the seam at all, with a sensible name and content type.
/// </summary>
public sealed class FakeFileSaver : IFileSaver
{
	/// <summary>Every save the page asked for, oldest first.</summary>
	public List<SavedFile> Saves { get; } = [];

	/// <summary>What the next save answers. Set it to make the page render its failure path.</summary>
	public FileSaveResult Result { get; set; } = FileSaveResult.Saved();

	/// <inheritdoc />
	public Task<FileSaveResult> SaveAsync(
		string fileName,
		string contentType,
		byte[] content,
		CancellationToken cancellationToken = default)
	{
		Saves.Add(new SavedFile(fileName, contentType, content));
		return Task.FromResult(Result);
	}
}

/// <param name="FileName">The name the page suggested.</param>
/// <param name="ContentType">The MIME type the page declared.</param>
/// <param name="Content">The bytes themselves.</param>
public sealed record SavedFile(string FileName, string ContentType, byte[] Content);
