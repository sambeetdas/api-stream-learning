using Microsoft.AspNetCore.Mvc;
using SseApi.Models;
using SseApi.Services;

namespace SseApi.Controllers;

[ApiController]
[Route("messages")]
public class MessagesController(MessageStore store, Broadcaster broadcaster, TransportStats stats) : ControllerBase
{
    /// <summary>
    /// SHORT POLLING: answers immediately with whatever is newer than <paramref name="after"/>
    /// (often an empty list). The client has to keep asking.
    /// </summary>
    [HttpGet]
    public ActionResult<List<Message>> Get([FromQuery] long after = 0)
    {
        using var _ = stats.Open(TransportStats.Polling);
        var messages = store.GetAfter(after);
        stats.MessagesSent(TransportStats.Polling, messages.Count);
        return messages;
    }

    /// <summary>Saves a message to SQLite and pushes it to every connected client.</summary>
    [HttpPost]
    public ActionResult<Message> Create(CreateMessageRequest request)
    {
        var message = store.Add(request.Text.Trim());
        broadcaster.Publish(message);
        return StatusCode(StatusCodes.Status201Created, message);
    }
}
