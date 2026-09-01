using DLR.Server;
using Microsoft.Extensions.Configuration;

namespace DLR.Server.Tests.Hosting;

/// <summary>
/// The settings the server refuses to start without (§9.1, §14.3).
/// <para>
/// The blob root earns a startup check for a reason the connection string does not: a missing
/// database announces itself on the first query, whereas a missing blob root <em>works</em>.
/// <c>BlobStoreOptions.RootPath</c> defaults to the empty string, <c>Path.Combine</c> makes
/// that relative, and the server cheerfully writes every uploaded photograph into whatever the
/// working directory happens to be - the source tree, when it is run from the project folder.
/// Nothing fails, so nothing gets noticed until the uploads turn up in <c>git status</c>.
/// </para>
/// </summary>
public sealed class RequiredSettingsTests
{
	private static IConfiguration With(string? blobRoot) =>
		new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				[RequiredSettings.BlobRootPath] = blobRoot,
			})
			.Build();

	[Fact]
	public void BlobRoot_Absolute_IsAccepted()
	{
		string absolute = Directory.CreateTempSubdirectory("dlr-blob-root").FullName;

		Should.NotThrow(() => RequiredSettings.ValidateBlobRoot(With(absolute)));
	}

	/// <summary>
	/// A path that does not exist yet is fine - <c>FileSystemBlobStore</c> creates each blob's
	/// directory on the way past, and a volume mounted a moment after the container starts is
	/// ordinary. What cannot be recovered from later is not knowing where to write.
	/// </summary>
	[Fact]
	public void BlobRoot_AbsoluteButNotYetCreated_IsAccepted()
	{
		string notYetThere = Path.Combine(Path.GetTempPath(), $"dlr-{Guid.NewGuid():N}", "blobs");

		Directory.Exists(notYetThere).ShouldBeFalse();

		Should.NotThrow(() => RequiredSettings.ValidateBlobRoot(With(notYetThere)));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void BlobRoot_Missing_RefusesToStart(string? unset)
	{
		Exception refusal = Should.Throw<InvalidOperationException>(
			() => RequiredSettings.ValidateBlobRoot(With(unset)));

		refusal.Message.ShouldContain(RequiredSettings.BlobRootPath);
		refusal.Message.ShouldContain("not set");
	}

	/// <summary>
	/// The one that matters. A relative path is not a smaller version of the mistake - it is the
	/// same silent write-to-the-working-directory behaviour, merely with a value in the setting
	/// to make it look configured.
	/// </summary>
	[Theory]
	[InlineData("blobs")]
	[InlineData("./blobs")]
	[InlineData("../blobs")]
	public void BlobRoot_Relative_RefusesToStart(string relative)
	{
		Exception refusal = Should.Throw<InvalidOperationException>(
			() => RequiredSettings.ValidateBlobRoot(With(relative)));

		refusal.Message.ShouldContain(relative);
		refusal.Message.ShouldContain("must be absolute");
	}

	/// <summary>
	/// The refusal has to say what to do about it. A startup exception naming only the key it
	/// wanted is the kind that gets worked around with a guess.
	/// </summary>
	[Fact]
	public void TheRefusal_SaysHowToFixIt()
	{
		Exception refusal = Should.Throw<InvalidOperationException>(
			() => RequiredSettings.ValidateBlobRoot(With(null)));

		refusal.Message.ShouldContain("user-secrets", Case.Insensitive);
		refusal.Message.ShouldContain(RequiredSettings.BlobRootVariable);
	}
}
