using System.Net;
using System.Text;
using BlazorDLR.Shared.Services;

namespace DLR.UI.Tests.Services;

/// <summary>
/// This is the class that took "The details you entered were rejected" and turned it
/// into "Passwords must have at least one uppercase letter" (§18.2 as amended in
/// v0.22). Every branch that survives a live server's ProblemDetails body wants a test.
/// </summary>
public sealed class ProblemDetailsReaderTests
{
	private static HttpResponseMessage MakeResponse(HttpStatusCode status, string body, string contentType = "application/problem+json") =>
		new(status)
		{
			Content = new StringContent(body, Encoding.UTF8, contentType),
		};

	[Fact]
	public async Task ValidationProblemDetails_ReturnsOneMessagePerRuleBroken()
	{
		string body = """
		{
			"title": "Bad request",
			"status": 400,
			"errors": {
				"Password": [
					"Passwords must be at least 6 characters.",
					"Passwords must have at least one uppercase letter."
				]
			}
		}
		""";

		using HttpResponseMessage response = MakeResponse(HttpStatusCode.BadRequest, body);
		ApiError result = await ProblemDetailsReader.ReadAsync(response);

		result.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		result.Title.ShouldBe("Bad request");
		result.Messages.Count.ShouldBe(2, "one message per Identity rule broken.");
		result.Messages[0].ShouldContain("6 characters");
		result.Messages[1].ShouldContain("uppercase");
	}

	[Fact]
	public async Task PlainProblemDetails_ReturnsDetailAsSingleMessage()
	{
		string body = """
		{
			"title": "Sign-in failed",
			"status": 401,
			"detail": "This account no longer exists."
		}
		""";

		using HttpResponseMessage response = MakeResponse(HttpStatusCode.Unauthorized, body);
		ApiError result = await ProblemDetailsReader.ReadAsync(response);

		result.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		result.Title.ShouldBe("Sign-in failed");
		result.Messages.Count.ShouldBe(1);
		result.Messages[0].ShouldBe("This account no longer exists.");
	}

	[Fact]
	public async Task MalformedBody_FallsBackToStatusAndReason()
	{
		using HttpResponseMessage response = new(HttpStatusCode.InternalServerError)
		{
			Content = new StringContent("<html>not JSON</html>", Encoding.UTF8, "application/problem+json"),
			ReasonPhrase = "Internal Server Error",
		};

		ApiError result = await ProblemDetailsReader.ReadAsync(response);

		// A malformed body should not become a second failure the user has to interpret.
		// The status code is still useful; a screen renders it with the status text.
		result.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
		result.Title.ShouldBe("Internal Server Error");
		result.Messages.ShouldBeEmpty();
	}

	[Fact]
	public async Task NonJsonContent_ReturnsStatusWithNoMessages()
	{
		using HttpResponseMessage response = new(HttpStatusCode.BadGateway)
		{
			Content = new StringContent("not json at all", Encoding.UTF8, "text/plain"),
			ReasonPhrase = "Bad Gateway",
		};

		ApiError result = await ProblemDetailsReader.ReadAsync(response);

		result.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
		result.Title.ShouldBe("Bad Gateway");
		result.Messages.ShouldBeEmpty();
	}
}
