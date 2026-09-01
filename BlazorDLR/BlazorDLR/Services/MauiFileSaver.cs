using System.Text;
using BlazorDLR.Shared.Services;

namespace BlazorDLR.Services;

/// <summary>
/// The phone's <see cref="IFileSaver"/> (§15.2, §17.3).
/// <para>
/// The web trick - a Blob URL on an <c>&lt;a download&gt;</c> - is silently inert inside a
/// <c>BlazorWebView</c>. Android's WebView has never implemented the <c>download</c>
/// attribute and hands nothing to its <c>DownloadListener</c> for a <c>blob:</c> or
/// <c>data:</c> URL, and WKWebView refuses to navigate to one it did not open. The click
/// lands, no exception is raised, and the rider sees the button do nothing. That was the
/// whole of the "Download GPX does nothing on mobile" bug.
/// </para>
/// <para>
/// So write the bytes into the app's own cache - the one directory MAUI's generated
/// <c>FileProvider</c> is already allowed to grant a content URI on - and put the file
/// through the system share sheet, which is where "Save to Files" (iOS) and "Save to
/// Downloads" (Android) live. The desktop targets have no share sheet for files, so they
/// fall back to writing into the documents folder and saying where it went.
/// </para>
/// </summary>
public sealed class MauiFileSaver : IFileSaver
{
	/// <inheritdoc />
	public async Task<FileSaveResult> SaveAsync(
		string fileName,
		string contentType,
		byte[] content,
		CancellationToken cancellationToken = default)
	{
		string safeName = Sanitise(fileName);

		try
		{
			// A directory per save. Two exports of the same route would otherwise fight over
			// one path, and the share sheet can still be holding the previous file open.
			string folder = Path.Combine(FileSystem.CacheDirectory, "shared", Guid.NewGuid().ToString("n"));
			Directory.CreateDirectory(folder);

			string path = Path.Combine(folder, safeName);
			await File.WriteAllBytesAsync(path, content, cancellationToken);

			try
			{
				await Share.Default.RequestAsync(new ShareFileRequest
				{
					Title = safeName,
					File = new ShareFile(path, contentType),
				});

				// The share sheet reports neither the app chosen nor a dismissal, so there is
				// nothing truthful to add - what the rider saw is the receipt.
				return FileSaveResult.Saved();
			}
			catch (FeatureNotSupportedException)
			{
				// Windows and Mac Catalyst: no file share sheet. Put it somewhere the rider
				// can actually find and tell them the path, rather than leaving it in a cache
				// directory they have no way to open.
				return await SaveToDocumentsAsync(safeName, content, cancellationToken);
			}
		}
		catch (OperationCanceledException)
		{
			return FileSaveResult.Cancelled();
		}
		catch (Exception exception)
		{
			return FileSaveResult.Failed(exception.Message);
		}
	}

	private static async Task<FileSaveResult> SaveToDocumentsAsync(
		string fileName,
		byte[] content,
		CancellationToken cancellationToken)
	{
		string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
		if (string.IsNullOrEmpty(documents))
			return FileSaveResult.Unavailable("this device has nowhere to put a file.");

		string folder = Path.Combine(documents, "Dumb Luck Routes");
		Directory.CreateDirectory(folder);

		string path = Path.Combine(folder, fileName);
		await File.WriteAllBytesAsync(path, content, cancellationToken);

		return FileSaveResult.Saved($"Saved to {path}");
	}

	/// <summary>
	/// A route is named by its owner, so the name arriving here is user input that is about to
	/// become a path. Strip anything the file system would object to - or, worse, obey - and
	/// keep the extension the caller asked for.
	/// </summary>
	private static string Sanitise(string fileName)
	{
		StringBuilder builder = new(fileName.Length);
		foreach (char character in Path.GetFileName(fileName))
			builder.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), character) >= 0 ? '-' : character);

		string cleaned = builder.ToString().Trim().Trim('.');
		return cleaned.Length == 0 ? "download" : cleaned;
	}
}
