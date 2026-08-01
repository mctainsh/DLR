using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Rides;
using DLR.Server.Hubs;
using DLR.TestSupport.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;

namespace DLR.Server.Tests.Hubs;

/// <summary>
/// A hub connection wired through <c>TestServer</c> (§5.3).
/// <para>
/// WebSockets rather than long polling, because the transport is part of what is being tested:
/// §7.6's query-string token lift exists <em>because</em> a browser cannot set headers on a
/// WebSocket handshake, and long polling would send the token in an <c>Authorization</c> header
/// and never exercise the lift at all.
/// </para>
/// </summary>
public static class HubClient
{
	/// <summary>Opens a connection as the given session.</summary>
	/// <param name="app">The server.</param>
	/// <param name="session">Whose token to present, or null for an anonymous attempt.</param>
	/// <returns>A started connection.</returns>
	public static async Task<HubConnection> ConnectAsync(
		DlrWebApplicationFactory app,
		TokenResponse? session)
	{
		HubConnection connection = Build(app, session is null ? null : () => session.AccessToken);

		await connection.StartAsync();

		return connection;
	}

	/// <summary>
	/// Builds a connection without starting it, with the token supplied per attempt.
	/// </summary>
	/// <param name="app">The server.</param>
	/// <param name="token">Called for each connect; null presents no token.</param>
	/// <returns>The unstarted connection.</returns>
	public static HubConnection Build(DlrWebApplicationFactory app, Func<string>? token)
	{
		TestServer server = app.Server;

		return new HubConnectionBuilder()
			.WithUrl(new Uri(server.BaseAddress, "hubs/ride"), options =>
			{
				options.Transports = HttpTransportType.WebSockets;
				options.HttpMessageHandlerFactory = _ => server.CreateHandler();

				// The token goes in the query string, put there by hand.
				//
				// The .NET client would set an Authorization header on ClientWebSocketOptions,
				// which TestServer's WebSocketClient has no way to carry — but more importantly a
				// header is not what the code under test is for. §7.6's lift exists because a
				// *browser* cannot set headers on a WebSocket handshake and SignalR's JavaScript
				// client therefore sends `?access_token=`. Appending it here reproduces the
				// browser's handshake, which is the one the lift has to handle.
				options.WebSocketFactory = async (context, cancellationToken) =>
					await server
						.CreateWebSocketClient()
						.ConnectAsync(WithToken(context.Uri, token?.Invoke()), cancellationToken);

				if (token is not null)
				{
					options.AccessTokenProvider = () => Task.FromResult<string?>(token());
				}
			})
			.Build();
	}

	private static Uri WithToken(Uri uri, string? token)
	{
		if (token is null)
		{
			return uri;
		}

		UriBuilder builder = new(uri);

		string separator = string.IsNullOrEmpty(builder.Query) ? string.Empty : "&";

		builder.Query = $"{builder.Query.TrimStart('?')}{separator}access_token={Uri.EscapeDataString(token)}";

		return builder.Uri;
	}

	/// <summary>
	/// Waits for the next position batch for a ride.
	/// </summary>
	/// <param name="connection">The connection to listen on.</param>
	/// <param name="rideId">Which ride's batch is wanted.</param>
	/// <returns>A task completing on the first matching batch.</returns>
	public static Task<PositionBatch> NextBatchAsync(HubConnection connection, Guid rideId)
	{
		TaskCompletionSource<PositionBatch> received =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		connection.On<PositionBatch>(nameof(IRideClient.PositionsUpdated), batch =>
		{
			if (batch.RideId == rideId)
			{
				received.TrySetResult(batch);
			}
		});

		return received.Task;
	}
}
