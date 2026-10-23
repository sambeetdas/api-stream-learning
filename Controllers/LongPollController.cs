using Microsoft.AspNetCore.Mvc;
using SseApi.Models;
using SseApi.Services;

namespace SseApi.Controllers;

[ApiController]
[Route("longpoll")]
public class LongPollController(MessageFeed feed, TransportStats stats) : ControllerBase
{
    private static readonly TimeSpan HoldFor = TimeSpan.FromSeconds(25);

    /// <summary>
    /// LONG POLLING: like polling, but if nothing is new the server keeps the request open
    /// until a message arrives (or 25s pass), then answers. The client immediately asks again.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<Message>>> Get([FromQuery] long after = 0, CancellationToken ct = default)
    {
        using var _ = stats.Open(TransportStats.LongPolling);
        try
        {
            var messages = await feed.WaitForAfter(after, HoldFor, ct);
            stats.MessagesSent(TransportStats.LongPolling, messages.Count);
            return messages;
        }
        catch (OperationCanceledException)
        {
            return new EmptyResult(); // client went away
        }
    }
}
