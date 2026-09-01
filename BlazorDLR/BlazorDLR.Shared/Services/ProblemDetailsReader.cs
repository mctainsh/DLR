using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// Reads RFC 7807 <c>ProblemDetails</c> bodies out of an <see cref="HttpRequestException"/>'s
/// response and turns them into something a screen can render (§18.2).
/// <para>
/// Two shapes come back from this project's server: a plain <c>ProblemDetails</c> (title +
/// detail) on 401/409/429, and a <c>ValidationProblemDetails</c> (errors keyed by field) on
/// 400s from register / reset-password / edit / etc. Either way the useful message is inside
/// the response body, and the client's job is to unwrap it rather than to invent a friendlier
/// one that discards it.
/// </para>
/// </summary>
public static class ProblemDetailsReader
{
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	/// <summary>
	/// Given an <see cref="HttpResponseMessage"/> that was not successful, read the
	/// <c>ProblemDetails</c> body and return the messages it holds. Never throws - a body
	/// that fails to parse becomes a one-line <see cref="ApiError"/> quoting the status.
	/// </summary>
	public static async Task<ApiError> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
	{
		string? contentType = response.Content.Headers.ContentType?.MediaType;
		if (contentType is null
			|| !(contentType.Equals("application/problem+json", StringComparison.OrdinalIgnoreCase)
				|| contentType.Equals("application/json", StringComparison.OrdinalIgnoreCase)))
		{
			return new ApiError(response.StatusCode, response.ReasonPhrase ?? "Request failed.", []);
		}

		try
		{
			WireProblem? problem = await response.Content.ReadFromJsonAsync<WireProblem>(Json, cancellationToken);
			if (problem is null)
			{
				return new ApiError(response.StatusCode, response.ReasonPhrase ?? "Request failed.", []);
			}

			List<string> messages = new();
			if (problem.Errors is { Count: > 0 })
			{
				foreach ((string field, string[] fieldMessages) in problem.Errors)
				{
					foreach (string message in fieldMessages)
					{
						messages.Add(string.IsNullOrWhiteSpace(field) ? message : $"{message}");
					}
				}
			}
			else if (!string.IsNullOrWhiteSpace(problem.Detail))
			{
				messages.Add(problem.Detail);
			}

			return new ApiError(
				StatusCode: response.StatusCode,
				Title: problem.Title ?? response.ReasonPhrase ?? "Request failed.",
				Messages: messages);
		}
		catch (JsonException)
		{
			// A malformed body should not become a second failure the user has to interpret;
			// the status code is still meaningful.
			return new ApiError(response.StatusCode, response.ReasonPhrase ?? "Request failed.", []);
		}
	}

	/// <summary>
	/// Convenience for the common shape: a caller caught an <see cref="HttpRequestException"/>
	/// and wants to know why. The exception itself does not carry the response body - the
	/// caller has to have kept the <see cref="HttpResponseMessage"/>, so this overload takes
	/// the exception and a fallback message and gives back whatever it can.
	/// </summary>
	public static ApiError FromException(HttpRequestException exception, string fallback)
	{
		if (exception.StatusCode is { } status)
		{
			return new ApiError(status, fallback, []);
		}
		return new ApiError(default, fallback, []);
	}

	// The wire shape covers both ProblemDetails and ValidationProblemDetails - the latter is
	// just the former with an `errors` dictionary added.
	private sealed record WireProblem(
		string? Title,
		string? Detail,
		int? Status,
		Dictionary<string, string[]>? Errors);
}

/// <summary>A parsed API error a screen can render.</summary>
/// <param name="StatusCode">The HTTP status the server answered.</param>
/// <param name="Title">Short, human-readable - "Sign-in failed", "Antiforgery check failed".</param>
/// <param name="Messages">
/// Zero or more specific reasons. For a 401 sign-in this is typically one line ("This account
/// no longer exists"); for a 400 register it is one line per rule the password broke
/// ("Passwords must be at least 6 characters.", "Passwords must have at least one digit").
/// </param>
public sealed record ApiError(
	System.Net.HttpStatusCode StatusCode,
	string Title,
	IReadOnlyList<string> Messages);

/// <summary>
/// Thrown by <see cref="HttpApiClient"/> when the server answered non-success and returned a
/// <c>ProblemDetails</c> body (§18.2). Carries the parsed <see cref="ApiError"/> so a screen
/// can render <see cref="ApiError.Title"/> and every message the server said, rather than a
/// generic "request failed".
/// </summary>
public sealed class ApiException : HttpRequestException
{
	public ApiException(ApiError error)
		: base(HttpRequestError.Unknown, error.Title, null, error.StatusCode)
	{
		Error = error;
	}

	/// <summary>The parsed <c>ProblemDetails</c> the server returned.</summary>
	public ApiError Error { get; }
}
