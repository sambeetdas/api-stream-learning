namespace SseApi.Services;

public record TransportSnapshot(string Transport, long Active, long TotalConnections, long MessagesSent);

/// <summary>Server-side counters per transport, shown on the demo page.</summary>
public class TransportStats
{
    public const string Polling = "polling";
    public const string LongPolling = "longpolling";
    public const string Sse = "sse";
    public const string Ndjson = "ndjson";
    public const string WebSocket = "websocket";

    private static readonly string[] Names = [Polling, LongPolling, Sse, Ndjson, WebSocket];

    // Fully populated up front and never modified, so it is safe to read from many threads.
    private readonly Dictionary<string, Counter> _counters = Names.ToDictionary(n => n, _ => new Counter());

    /// <summary>Marks a connection/request as open until the returned scope is disposed.</summary>
    public IDisposable Open(string transport)
    {
        var counter = _counters[transport];
        Interlocked.Increment(ref counter.Active);
        Interlocked.Increment(ref counter.Total);
        return new Scope(counter);
    }

    public void MessagesSent(string transport, int count) =>
        Interlocked.Add(ref _counters[transport].Sent, count);

    /// <summary>Goes up by one on every reset, so open pages can notice and start fresh too.</summary>
    public long ResetVersion => Interlocked.Read(ref _resetVersion);
    private long _resetVersion;

    /// <summary>Zeroes the totals. "Active" is left alone: it is a live count that falls by itself as connections close.</summary>
    public void Reset()
    {
        foreach (var c in _counters.Values)
        {
            Interlocked.Exchange(ref c.Total, 0);
            Interlocked.Exchange(ref c.Sent, 0);
        }
        Interlocked.Increment(ref _resetVersion);
    }

    public List<TransportSnapshot> Snapshot() =>
        Names.Select(n =>
        {
            var c = _counters[n];
            return new TransportSnapshot(n, Interlocked.Read(ref c.Active), Interlocked.Read(ref c.Total), Interlocked.Read(ref c.Sent));
        }).ToList();

    private sealed class Counter
    {
        public long Active;
        public long Total;
        public long Sent;
    }

    private sealed class Scope(Counter counter) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref counter.Active);
        }
    }
}
