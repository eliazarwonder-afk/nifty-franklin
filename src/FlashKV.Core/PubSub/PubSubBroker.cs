using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using FlashKV.Core.Resp;

namespace FlashKV.Core.PubSub;

/// <summary>
/// Thread-safe in-memory publish/subscribe broker for channel subscriptions.
/// </summary>
public sealed class PubSubBroker
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<long, Action<RespValue>>> _channels = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<string, byte>> _clientSubscriptions = new();

    /// <summary>
    /// Subscribes a client session to the given channel.
    /// </summary>
    public int Subscribe(string channel, long sessionId, Action<RespValue> messageSink)
    {
        var subscribers = _channels.GetOrAdd(channel, _ => new ConcurrentDictionary<long, Action<RespValue>>());
        subscribers[sessionId] = messageSink;

        var clientChannels = _clientSubscriptions.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        clientChannels.TryAdd(channel, 0);

        return clientChannels.Count;
    }

    /// <summary>
    /// Unsubscribes a client session from a channel.
    /// </summary>
    public int Unsubscribe(string channel, long sessionId)
    {
        if (_channels.TryGetValue(channel, out var subscribers))
        {
            subscribers.TryRemove(sessionId, out _);
            if (subscribers.IsEmpty)
            {
                _channels.TryRemove(channel, out _);
            }
        }

        if (_clientSubscriptions.TryGetValue(sessionId, out var clientChannels))
        {
            clientChannels.TryRemove(channel, out _);
            return clientChannels.Count;
        }

        return 0;
    }

    /// <summary>
    /// Removes all subscriptions for a disconnected client session.
    /// </summary>
    public void UnsubscribeAll(long sessionId)
    {
        if (_clientSubscriptions.TryRemove(sessionId, out var clientChannels))
        {
            foreach (var channel in clientChannels.Keys)
            {
                if (_channels.TryGetValue(channel, out var subscribers))
                {
                    subscribers.TryRemove(sessionId, out _);
                    if (subscribers.IsEmpty)
                    {
                        _channels.TryRemove(channel, out _);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Publishes a message to all subscribers of a channel. Returns number of recipients.
    /// </summary>
    public int Publish(string channel, ReadOnlyMemory<byte> message)
    {
        if (!_channels.TryGetValue(channel, out var subscribers) || subscribers.IsEmpty)
        {
            return 0;
        }

        // Format RESP push message: ["message", channel, message]
        var respMessage = RespValue.Array(new[]
        {
            RespValue.BulkString("message"),
            RespValue.BulkString(channel),
            RespValue.BulkString(message)
        });

        int delivered = 0;
        foreach (var sink in subscribers.Values)
        {
            try
            {
                sink(respMessage);
                delivered++;
            }
            catch
            {
                // Ignore transient client sink failures
            }
        }

        return delivered;
    }
}
