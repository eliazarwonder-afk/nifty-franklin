using System;
using System.Text;
using FlashKV.Core.Commands;
using FlashKV.Core.PubSub;
using FlashKV.Core.Resp;
using FlashKV.Core.Storage;
using Xunit;

namespace FlashKV.Core.Tests;

public class CommandDispatcherTests
{
    private readonly DataStore _store = new();
    private readonly PubSubBroker _pubsub = new();
    private readonly AofEngine _aof = new("dummy.aof", enabled: false);

    private CommandContext CreateContext() => new(_store, _pubsub, _aof, 1, _ => { });

    [Fact]
    public void Set_With_Nx_And_Xx_Flags_WorksAsExpected()
    {
        var ctx = CreateContext();

        // 1. SET key val NX (should succeed since key does not exist)
        var res1 = CommandDispatcher.Execute(RespValue.Array(new[]
        {
            RespValue.BulkString("SET"),
            RespValue.BulkString("mykey"),
            RespValue.BulkString("val1"),
            RespValue.BulkString("NX")
        }), ctx);
        Assert.Equal("OK", res1.StringValue);

        // 2. SET key val NX again (should fail/return nil because key already exists)
        var res2 = CommandDispatcher.Execute(RespValue.Array(new[]
        {
            RespValue.BulkString("SET"),
            RespValue.BulkString("mykey"),
            RespValue.BulkString("val2"),
            RespValue.BulkString("NX")
        }), ctx);
        Assert.True(res2.IsNull);

        // 3. SET nonexistent val XX (should fail/return nil because key does not exist)
        var res3 = CommandDispatcher.Execute(RespValue.Array(new[]
        {
            RespValue.BulkString("SET"),
            RespValue.BulkString("nonexistent"),
            RespValue.BulkString("val"),
            RespValue.BulkString("XX")
        }), ctx);
        Assert.True(res3.IsNull);

        // 4. SET mykey val XX (should succeed because key exists)
        var res4 = CommandDispatcher.Execute(RespValue.Array(new[]
        {
            RespValue.BulkString("SET"),
            RespValue.BulkString("mykey"),
            RespValue.BulkString("val_updated"),
            RespValue.BulkString("XX")
        }), ctx);
        Assert.Equal("OK", res4.StringValue);
    }

    [Fact]
    public void MultiKey_Del_And_Exists_WorkCorrectly()
    {
        var ctx = CreateContext();

        // Populate 3 keys
        _store.Set("k1", "v1"u8.ToArray());
        _store.Set("k2", "v2"u8.ToArray());
        _store.Set("k3", "v3"u8.ToArray());

        // EXISTS k1 k2 k3 missing
        var existsRes = CommandDispatcher.Execute(RespValue.Array(new[]
        {
            RespValue.BulkString("EXISTS"),
            RespValue.BulkString("k1"),
            RespValue.BulkString("k2"),
            RespValue.BulkString("k3"),
            RespValue.BulkString("missing")
        }), ctx);
        Assert.Equal(3, existsRes.IntegerValue);

        // DEL k1 k2
        var delRes = CommandDispatcher.Execute(RespValue.Array(new[]
        {
            RespValue.BulkString("DEL"),
            RespValue.BulkString("k1"),
            RespValue.BulkString("k2")
        }), ctx);
        Assert.Equal(2, delRes.IntegerValue);

        // DBSIZE should now be 1
        var sizeRes = CommandDispatcher.Execute(RespValue.Array(new[]
        {
            RespValue.BulkString("DBSIZE")
        }), ctx);
        Assert.Equal(1, sizeRes.IntegerValue);
    }

    [Fact]
    public void Ttl_And_Persist_Commands_WorkProperly()
    {
        var ctx = CreateContext();

        _store.Set("expkey", "val"u8.ToArray());

        // Set EXPIRE 60 seconds
        var expRes = CommandDispatcher.Execute(RespValue.Array(new[]
        {
            RespValue.BulkString("EXPIRE"),
            RespValue.BulkString("expkey"),
            RespValue.BulkString("60")
        }), ctx);
        Assert.Equal(1, expRes.IntegerValue);

        // TTL should be around 50-60 seconds
        var ttlRes = CommandDispatcher.Execute(RespValue.Array(new[]
        {
            RespValue.BulkString("TTL"),
            RespValue.BulkString("expkey")
        }), ctx);
        Assert.True(ttlRes.IntegerValue > 0 && ttlRes.IntegerValue <= 60);

        // PERSIST expkey
        var persistRes = CommandDispatcher.Execute(RespValue.Array(new[]
        {
            RespValue.BulkString("PERSIST"),
            RespValue.BulkString("expkey")
        }), ctx);
        Assert.Equal(1, persistRes.IntegerValue);

        // TTL should now be -1 (key exists with no expiration)
        var ttlAfter = CommandDispatcher.Execute(RespValue.Array(new[]
        {
            RespValue.BulkString("TTL"),
            RespValue.BulkString("expkey")
        }), ctx);
        Assert.Equal(-1, ttlAfter.IntegerValue);
    }

    [Fact]
    public void RedisCli_Bootstrap_Commands_RespondCleanly()
    {
        var ctx = CreateContext();

        // COMMAND
        var cmdRes = CommandDispatcher.Execute(RespValue.Array(new[] { RespValue.BulkString("COMMAND") }), ctx);
        Assert.Equal(RespType.Array, cmdRes.Type);

        // CLIENT
        var clientRes = CommandDispatcher.Execute(RespValue.Array(new[] { RespValue.BulkString("CLIENT"), RespValue.BulkString("SETNAME"), RespValue.BulkString("test") }), ctx);
        Assert.Equal("OK", clientRes.StringValue);

        // SELECT 0
        var selRes = CommandDispatcher.Execute(RespValue.Array(new[] { RespValue.BulkString("SELECT"), RespValue.BulkString("0") }), ctx);
        Assert.Equal("OK", selRes.StringValue);

        // INFO
        var infoRes = CommandDispatcher.Execute(RespValue.Array(new[] { RespValue.BulkString("INFO") }), ctx);
        Assert.Contains("redis_version", infoRes.AsString());
        Assert.Contains("flashkv_version", infoRes.AsString());
    }
}
