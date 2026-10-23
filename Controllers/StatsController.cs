using Microsoft.AspNetCore.Mvc;
using SseApi.Services;

namespace SseApi.Controllers;

public record StatsResponse(long ResetVersion, List<TransportSnapshot> Transports);

[ApiController]
[Route("stats")]
public class StatsController(TransportStats stats) : ControllerBase
{
    /// <summary>
    /// Server-side counters per transport. <c>ResetVersion</c> changes after every reset,
    /// which tells open pages (other tabs) to start fresh as well.
    /// </summary>
    [HttpGet]
    public StatsResponse Get() => new(stats.ResetVersion, stats.Snapshot());
}
