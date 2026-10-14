using System;
using FlashKV.Core.PubSub;
using FlashKV.Core.Resp;
using FlashKV.Core.Storage;

namespace FlashKV.Core.Commands;

/// <summary>
/// Execution context for a command invoked by a client connection.
/// </summary>
public sealed class CommandContext
{
    public DataStore Store { get; }
    public PubSubBroker PubSub { get; }
    public AofEngine Aof { get; }
    public long SessionId { get; }
    public Action<RespValue> WritePushMessage { get; }
    public bool IsSubscribed { get; set; }

    public CommandContext(
        DataStore store,
        PubSubBroker pubSub,
        AofEngine aof,
        long sessionId,
        Action<RespValue> writePushMessage)
    {
        Store = store;
        PubSub = pubSub;
        Aof = aof;
        SessionId = sessionId;
        WritePushMessage = writePushMessage;
    }
}
