using Microsoft.AspNetCore.Mvc;
using SseApi.Services;

namespace SseApi.Controllers;

[ApiController]
[Route("reset")]
public class ResetController(MessageStore store, TransportStats stats) : ControllerBase
{
    /// <summary>RESET: deletes every message and zeroes the server statistics, to start fresh.</summary>
    [HttpPost]
    public IActionResult Reset()
    {
        store.DeleteAll();
        stats.Reset();
        return Ok(new { resetVersion = stats.ResetVersion });
    }
}
