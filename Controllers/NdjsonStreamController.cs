using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SseApi.Services;

namespace SseApi.Controllers;

[ApiController]
[Route("stream/ndjson")]
public class NdjsonStreamController(MessageFeed feed, TransportStats stats) : ControllerBase
{
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);

    /// <summary>
    /// PLAIN HTTP STREAMING: one long-lived response, one JSON object per line (NDJSON).
    /// No special protocol: the client reads the body as it arrives (fetch + ReadableStream).
    /// There is no built-in reconnect, so the client passes ?after= to resume.
    /// </summary>
    [HttpGet]
    public async Task Get([FromQuery] long after = 0, CancellationToken ct = default)
    {
        Response.Headers.ContentType = "application/x-ndjson";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        using var _ = stats.Open(TransportStats.Ndjson);
        try
        {
            await Response.Body.FlushAsync(ct); // send the headers right away

            await foreach (var m in feed.Stream(after, Heartbeat, ct))
            {
                if (m is null)
                {
                    await Response.WriteAsync("\n", ct); // empty line = heartbeat, clients skip it
                }
                else
                {
                    var json = JsonSerializer.Serialize(m, JsonSerializerOptions.Web);
                    await Response.WriteAsync(json + "\n", ct);
                    stats.MessagesSent(TransportStats.Ndjson, 1);
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
