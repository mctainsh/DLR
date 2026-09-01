using System.Text.Json;
using DLR.Server.Identity;
using Microsoft.Extensions.Configuration;

namespace DLR.Server.Tests.Identity;

/// <summary>
/// The signing key comes from an environment variable or a Docker secret, and never from a
/// file that ships with the code (§7.4).
/// <para>
/// A rule enforced at startup rather than at review, because the failure mode is a signing key
/// in git history - and git history is permanent in practice, so the fix is rotating the key
/// rather than deleting the line. The moment this rule gets broken is not a code review; it is
/// an afternoon when the app will not start and the other settings are right there.
/// </para>
/// </summary>
public sealed class SigningKeySourceTests : IDisposable
{
	private const string Adequate = "a-signing-key-that-clears-the-thirty-two-byte-floor";

	private readonly string _contentRoot = Directory.CreateTempSubdirectory("dlr-key-source").FullName;

	[Fact]
	public void Validate_KeyFromEnvironment_IsAccepted()
	{
		IConfigurationRoot configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?> { [SigningKeySource.KeyPath] = Adequate })
			.Build();

		Should.NotThrow(() => SigningKeySource.Validate(configuration, _contentRoot));
	}

	[Fact]
	public void SigningKey_InAFileThatShipsWithTheCode_RefusesToStart()
	{
		WriteSettings("appsettings.json", Adequate);

		IConfigurationRoot configuration = new ConfigurationBuilder()
			.SetBasePath(_contentRoot)
			.AddJsonFile("appsettings.json")
			.Build();

		Exception refusal = Should.Throw<InvalidOperationException>(
			() => SigningKeySource.Validate(configuration, _contentRoot));

		refusal.Message.ShouldContain("appsettings.json");
		refusal.Message.ShouldContain("rotate");
	}

	/// <summary>
	/// The distinction being drawn is "ships with the code", not "is a file". A Docker secret
	/// is a file too, and refusing it would leave no supported way to run the server.
	/// </summary>
	[Fact]
	public void Validate_KeyInAFileOutsideTheContentRoot_IsAccepted()
	{
		string elsewhere = Directory.CreateTempSubdirectory("dlr-secrets").FullName;

		try
		{
			File.WriteAllText(
				Path.Combine(elsewhere, "secrets.json"),
				JsonSerializer.Serialize(new Dictionary<string, object>
				{
					["Auth"] = new Dictionary<string, string> { ["SigningKey"] = Adequate },
				}));

			IConfigurationRoot configuration = new ConfigurationBuilder()
				.SetBasePath(elsewhere)
				.AddJsonFile("secrets.json")
				.Build();

			Should.NotThrow(() => SigningKeySource.Validate(configuration, _contentRoot));
		}
		finally
		{
			Directory.Delete(elsewhere, recursive: true);
		}
	}

	/// <summary>
	/// An environment variable set over a committed default is the normal deployment shape,
	/// and it is not a violation: the value in use is the one from the environment.
	/// </summary>
	[Fact]
	public void Validate_AppSettingsOverriddenByEnvironment_IsAccepted()
	{
		WriteSettings("appsettings.json", "the-placeholder-that-nobody-should-ever-deploy");

		IConfigurationRoot configuration = new ConfigurationBuilder()
			.SetBasePath(_contentRoot)
			.AddJsonFile("appsettings.json")
			.AddInMemoryCollection(new Dictionary<string, string?> { [SigningKeySource.KeyPath] = Adequate })
			.Build();

		Should.NotThrow(() => SigningKeySource.Validate(configuration, _contentRoot));
	}

	/// <summary>
	/// A guard that only says what is forbidden sends the next person to the design document.
	/// This one has to name the fix, because the fix is one command and is not guessable -
	/// user secrets satisfy §7.4 precisely because that file is not in the repository.
	/// </summary>
	[Fact]
	public void Validate_NoKey_RefusesToStartAndSaysHowToFixIt()
	{
		IConfigurationRoot configuration = new ConfigurationBuilder().Build();

		string message = Should.Throw<InvalidOperationException>(
			() => SigningKeySource.Validate(configuration, _contentRoot)).Message;

		message.ShouldContain("dotnet user-secrets set");
		message.ShouldContain(SigningKeySource.KeyPath);

		// A single underscore binds to nothing and does it silently, so the message spells the
		// variable out rather than describing it.
		message.Contains(SigningKeySource.EnvironmentVariableName, StringComparison.Ordinal)
			.ShouldBeTrue();

		message.ShouldContain("appsettings.json");
	}

	/// <summary>HS256 is only as strong as its key, and 256 bits is the floor.</summary>
	[Theory]
	[InlineData("short")]
	[InlineData("thirty-one-bytes-exactly-here!!")]
	public void Validate_KeyShorterThanThirtyTwoBytes_RefusesToStart(string key)
	{
		IConfigurationRoot configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?> { [SigningKeySource.KeyPath] = key })
			.Build();

		Should.Throw<InvalidOperationException>(
			() => SigningKeySource.Validate(configuration, _contentRoot))
			.Message.ShouldContain("32 bytes");
	}

	/// <inheritdoc />
	public void Dispose() => Directory.Delete(_contentRoot, recursive: true);

	private void WriteSettings(string fileName, string key) =>
		File.WriteAllText(
			Path.Combine(_contentRoot, fileName),
			JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["Auth"] = new Dictionary<string, string> { ["SigningKey"] = key },
			}));
}
