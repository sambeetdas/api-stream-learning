using System.Collections.Concurrent;
using System.Threading.Channels;
using SseApi.Models;

namespace SseApi.Services;

/// <summary>Fans out each published message to every connected SSE client.</summary>
public class Broadcaster
{
    private readonly ConcurrentDictionary<Guid, Channel<Message>> _subscribers = new();

    public Subscription Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<Message>();
        _subscribers[id] = channel;
        return new Subscription(channel.Reader, () => _subscribers.TryRemove(id, out _));
    }

    public void Publish(Message message)
    {
        foreach (var channel in _subscribers.Values)
            channel.Writer.TryWrite(message);
    }

    public sealed class Subscription(ChannelReader<Message> reader, Action onDispose) : IDisposable
    {
        public ChannelReader<Message> Reader { get; } = reader;
        public void Dispose() => onDispose();
    }
}
