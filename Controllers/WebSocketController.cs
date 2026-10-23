using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SseApi.Models;
using SseApi.Services;

namespace SseApi.Controllers;

[ApiController]
public class WebSocketController(
    MessageStore store, MessageFeed feed, Broadcaster broadcaster, TransportStats stats) : ControllerBase
{
    private const int MaxIncomingBytes = 16 * 1024;

    /// <summary>
    /// WEBSOCKET: the HTTP request is upgraded to a persistent two-way connection.
    /// The server pushes messages down it, and the client can send messages up the same socket.
    /// </summary>
    [HttpGet("/ws")]
    public async Task Get([FromQuery] long after = 0, CancellationToken ct = default)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        using var _ = stats.Open(TransportStats.WebSocket);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var sender = SendLoop(socket, after, cts.Token);
        var receiver = ReceiveLoop(socket, cts.Token);

        await Task.WhenAny(sender, receiver); // either side ending ends the session
        cts.Cancel();

        try { await Task.WhenAll(sender, receiver); }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException) { }

        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
            catch (WebSocketException) { }
        }
    }

    private async Task SendLoop(WebSocket socket, long after, CancellationToken ct)
    {
        await foreach (var m in feed.Stream(after, TimeSpan.FromSeconds(15), ct))
        {
            if (m is null) continue; // WebSockets have their own ping/pong, no heartbeat needed

            var bytes = JsonSerializer.SerializeToUtf8Bytes(m, JsonSerializerOptions.Web);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
            stats.MessagesSent(TransportStats.WebSocket, 1);
        }
    }

    private async Task ReceiveLoop(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();

        while (!ct.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return;

            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxIncomingBytes)
            {
                await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, null, CancellationToken.None);
                return;
            }
            if (!result.EndOfMessage) continue;

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            HandleIncoming(text);
        }
    }

    // The client sent {"text":"..."} up the socket: same effect as POST /messages.
    private void HandleIncoming(string json)
    {
        try
        {
            var request = JsonSerializer.Deserialize<CreateMessageRequest>(json, JsonSerializerOptions.Web);
            var text = request?.Text?.Trim();
            if (string.IsNullOrEmpty(text) || text.Length > 1000) return;

            broadcaster.Publish(store.Add(text));
        }
        catch (JsonException)
        {
            // ignore malformed input
        }
    }
}
