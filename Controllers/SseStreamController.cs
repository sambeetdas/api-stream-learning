using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SseApi.Services;

namespace SseApi.Controllers;

[ApiController]
[Route("stream")]
public class SseStreamController(MessageFeed feed, TransportStats stats) : ControllerBase
{
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);

    /// <summary>
    /// SERVER-SENT EVENTS: one long-lived response in the text/event-stream format.
    /// Honors Last-Event-ID, which the browser sends automatically when it reconnects.
    /// </summary>
    [HttpGet]
    public async Task Get(CancellationToken ct)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        long.TryParse(Request.Headers["Last-Event-ID"].FirstOrDefault(), out var lastId);

        using var _ = stats.Open(TransportStats.Sse);
        try
        {
            await Response.WriteAsync("retry: 3000\n\n", ct);
            await Response.Body.FlushAsync(ct);

            await foreach (var m in feed.Stream(lastId, Heartbeat, ct))
            {
                if (m is null)
                {
                    // comment line: keeps proxies from closing an idle connection
                    await Response.WriteAsync(": ping\n\n", ct);
                }
                else
                {
                    var json = JsonSerializer.Serialize(m, JsonSerializerOptions.Web);
                    await Response.WriteAsync($"id: {m.Id}\nevent: message\ndata: {json}\n\n", ct);
                    stats.MessagesSent(TransportStats.Sse, 1);
                }
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // client disconnected
        }
    }
}
