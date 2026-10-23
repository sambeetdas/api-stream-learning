using System.Runtime.CompilerServices;
using SseApi.Models;

namespace SseApi.Services;

/// <summary>
/// The logic every transport shares: "give me what I missed, then what happens next".
/// Each transport (SSE, NDJSON, WebSocket, long polling) only decides how to write it to the wire.
/// </summary>
public class MessageFeed(MessageStore store, Broadcaster broadcaster)
{
    /// <summary>
    /// Replays saved messages after <paramref name="afterId"/>, then yields live ones.
    /// Yields <c>null</c> whenever <paramref name="heartbeat"/> passes with nothing to send,
    /// so callers can write a keep-alive.
    /// </summary>
    public async IAsyncEnumerable<Message?> Stream(
        long afterId, TimeSpan heartbeat, [EnumeratorCancellation] CancellationToken ct)
    {
        // Subscribe before replaying so nothing published during the replay is missed.
        using var subscription = broadcaster.Subscribe();

        foreach (var m in store.GetAfter(afterId))
        {
            afterId = m.Id;
            yield return m;
        }

        while (!ct.IsCancellationRequested)
        {
            Message? next = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(heartbeat);
            try
            {
                next = await subscription.Reader.ReadAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // heartbeat interval elapsed
            }

            if (next is null)
                yield return null;
            else if (next.Id > afterId) // skip anything already covered by the replay
            {
                afterId = next.Id;
                yield return next;
            }
        }
    }

    /// <summary>
    /// Long polling: returns right away if there is anything after <paramref name="afterId"/>,
    /// otherwise holds the request open until a message arrives or <paramref name="timeout"/> passes.
    /// </summary>
    public async Task<List<Message>> WaitForAfter(long afterId, TimeSpan timeout, CancellationToken ct)
    {
        using var subscription = broadcaster.Subscribe();

        var existing = store.GetAfter(afterId);
        if (existing.Count > 0)
            return existing;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await subscription.Reader.ReadAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return [];
        }

        return store.GetAfter(afterId);
    }
}
