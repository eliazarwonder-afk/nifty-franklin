using System;
using System.Collections.Generic;
using System.Text;
using FlashKV.Core.Resp;
using FlashKV.Core.Storage;

namespace FlashKV.Core.Commands;

/// <summary>
/// Routes parsed RESP array commands to storage operations and returns RESP responses.
/// </summary>
public static class CommandDispatcher
{
    public static RespValue Execute(RespValue command, CommandContext ctx)
    {
        if (command.ArrayValue == null || command.ArrayValue.Count == 0)
        {
            return RespValue.Error("ERR empty command");
        }

        string cmdName = command.ArrayValue[0].AsString().ToUpperInvariant();
        var args = command.ArrayValue;

        // If client is subscribed to pub/sub channels, only allow SUBSCRIBE, UNSUBSCRIBE, PING, QUIT
        if (ctx.IsSubscribed &&
            cmdName != "SUBSCRIBE" &&
            cmdName != "UNSUBSCRIBE" &&
            cmdName != "PING" &&
            cmdName != "QUIT")
        {
            return RespValue.Error($"ERR only (P)SUBSCRIBE / (P)UNSUBSCRIBE / PING / QUIT allowed in this context");
        }

        return cmdName switch
        {
            "PING" => HandlePing(args),
            "ECHO" => HandleEcho(args),
            "SET" => HandleSet(args, ctx),
            "GET" => HandleGet(args, ctx),
            "MGET" => HandleMGet(args, ctx),
            "MSET" => HandleMSet(args, ctx),
            "INCR" => HandleIncr(args, ctx, 1),
            "DECR" => HandleIncr(args, ctx, -1),
            "APPEND" => HandleAppend(args, ctx),
            "STRLEN" => HandleStrLen(args, ctx),
            "DEL" => HandleDel(args, ctx),
            "EXISTS" => HandleExists(args, ctx),
            "EXPIRE" => HandleExpire(args, ctx, isMillis: false),
            "PEXPIRE" => HandleExpire(args, ctx, isMillis: true),
            "TTL" => HandleTtl(args, ctx, isMillis: false),
            "PTTL" => HandleTtl(args, ctx, isMillis: true),
            "PERSIST" => HandlePersist(args, ctx),
            "TYPE" => HandleType(args, ctx),
            "KEYS" => HandleKeys(args, ctx),
            "DBSIZE" => RespValue.Integer(ctx.Store.DbSize()),
            "FLUSHDB" => HandleFlushDb(ctx),
            "HSET" => HandleHSet(args, ctx),
            "HGET" => HandleHGet(args, ctx),
            "HDEL" => HandleHDel(args, ctx),
            "HEXISTS" => HandleHExists(args, ctx),
            "HGETALL" => HandleHGetAll(args, ctx),
            "HLEN" => HandleHLen(args, ctx),
            "HINCRBY" => HandleHIncrBy(args, ctx),
            "LPUSH" => HandleLPush(args, ctx),
            "RPUSH" => HandleRPush(args, ctx),
            "LPOP" => HandleLPop(args, ctx),
            "RPOP" => HandleRPop(args, ctx),
            "LLEN" => HandleLLen(args, ctx),
            "LRANGE" => HandleLRange(args, ctx),
            "SADD" => HandleSAdd(args, ctx),
            "SREM" => HandleSRem(args, ctx),
            "SMEMBERS" => HandleSMembers(args, ctx),
            "SISMEMBER" => HandleSIsMember(args, ctx),
            "SCARD" => HandleSCard(args, ctx),
            "SUBSCRIBE" => HandleSubscribe(args, ctx),
            "UNSUBSCRIBE" => HandleUnsubscribe(args, ctx),
            "PUBLISH" => HandlePublish(args, ctx),
            "INFO" => HandleInfo(ctx),
            "TIME" => HandleTime(),
            "COMMAND" => RespValue.Array(Array.Empty<RespValue>()),
            "CLIENT" => RespValue.Ok,
            "SELECT" => RespValue.Ok,
            "QUIT" => RespValue.Ok,
            _ => RespValue.Error($"ERR unknown command '{cmdName}'")
        };
    }

    #region Handlers

    private static RespValue HandlePing(IReadOnlyList<RespValue> args)
    {
        if (args.Count == 1) return RespValue.Pong;
        return RespValue.BulkString(args[1].BinaryValue);
    }

    private static RespValue HandleEcho(IReadOnlyList<RespValue> args)
    {
        if (args.Count < 2) return RespValue.Error("ERR wrong number of arguments for 'echo' command");
        return RespValue.BulkString(args[1].BinaryValue);
    }

    private static RespValue HandleSet(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 3) return RespValue.Error("ERR wrong number of arguments for 'set' command");

        string key = args[1].AsString();
        var val = args[2].BinaryValue;
        TimeSpan? ttl = null;
        bool nx = false;
        bool xx = false;

        for (int i = 3; i < args.Count; i++)
        {
            string opt = args[i].AsString().ToUpperInvariant();
            if (opt == "EX" && i + 1 < args.Count && long.TryParse(args[i + 1].AsString(), out long exSec))
            {
                ttl = TimeSpan.FromSeconds(exSec);
                i++;
            }
            else if (opt == "PX" && i + 1 < args.Count && long.TryParse(args[i + 1].AsString(), out long pxMs))
            {
                ttl = TimeSpan.FromMilliseconds(pxMs);
                i++;
            }
            else if (opt == "NX")
            {
                nx = true;
            }
            else if (opt == "XX")
            {
                xx = true;
            }
        }

        bool success = ctx.Store.Set(key, val, ttl, nx, xx);
        if (success)
        {
            ctx.Aof.AppendCommand(RespValue.Array(args));
            return RespValue.Ok;
        }
        return RespValue.NullBulkString();
    }

    private static RespValue HandleGet(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 2) return RespValue.Error("ERR wrong number of arguments for 'get' command");
        string key = args[1].AsString();
        var val = ctx.Store.Get(key);
        return val.HasValue ? RespValue.BulkString(val.Value) : RespValue.NullBulkString();
    }

    private static RespValue HandleMGet(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 2) return RespValue.Error("ERR wrong number of arguments for 'mget' command");
        var items = new List<RespValue>(args.Count - 1);
        for (int i = 1; i < args.Count; i++)
        {
            var val = ctx.Store.Get(args[i].AsString());
            items.Add(val.HasValue ? RespValue.BulkString(val.Value) : RespValue.NullBulkString());
        }
        return RespValue.Array(items);
    }

    private static RespValue HandleMSet(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 3 || (args.Count - 1) % 2 != 0)
            return RespValue.Error("ERR wrong number of arguments for 'mset' command");

        for (int i = 1; i < args.Count; i += 2)
        {
            string key = args[i].AsString();
            ctx.Store.Set(key, args[i + 1].BinaryValue);
        }
        ctx.Aof.AppendCommand(RespValue.Array(args));
        return RespValue.Ok;
    }

    private static RespValue HandleIncr(IReadOnlyList<RespValue> args, CommandContext ctx, long delta)
    {
        if (args.Count < 2) return RespValue.Error("ERR wrong number of arguments for incr/decr command");
        string key = args[1].AsString();
        var (success, result, err) = ctx.Store.Incr(key, delta);
        if (!success) return RespValue.Error(err ?? "ERR");
        ctx.Aof.AppendCommand(RespValue.Array(args));
        return RespValue.Integer(result);
    }

    private static RespValue HandleAppend(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 3) return RespValue.Error("ERR wrong number of arguments for 'append' command");
        string key = args[1].AsString();
        int len = ctx.Store.Append(key, args[2].BinaryValue);
        if (len < 0) return RespValue.Error("WRONGTYPE Operation against a key holding the wrong kind of value");
        ctx.Aof.AppendCommand(RespValue.Array(args));
        return RespValue.Integer(len);
    }

    private static RespValue HandleStrLen(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 2) return RespValue.Error("ERR wrong number of arguments for 'strlen' command");
        string key = args[1].AsString();
        return RespValue.Integer(ctx.Store.StrLen(key));
    }

    private static RespValue HandleDel(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 2) return RespValue.Error("ERR wrong number of arguments for 'del' command");
        var keys = new List<string>(args.Count - 1);
        for (int i = 1; i < args.Count; i++) keys.Add(args[i].AsString());
        int deleted = ctx.Store.Del(keys);
        ctx.Aof.AppendCommand(RespValue.Array(args));
        return RespValue.Integer(deleted);
    }

    private static RespValue HandleExists(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 2) return RespValue.Error("ERR wrong number of arguments for 'exists' command");
        var keys = new List<string>(args.Count - 1);
        for (int i = 1; i < args.Count; i++) keys.Add(args[i].AsString());
        return RespValue.Integer(ctx.Store.Exists(keys));
    }

    private static RespValue HandleExpire(IReadOnlyList<RespValue> args, CommandContext ctx, bool isMillis)
    {
        if (args.Count < 3) return RespValue.Error("ERR wrong number of arguments for expire command");
        string key = args[1].AsString();
        if (!long.TryParse(args[2].AsString(), out long num)) return RespValue.Error("ERR value is not an integer");
        var ttl = isMillis ? TimeSpan.FromMilliseconds(num) : TimeSpan.FromSeconds(num);
        bool res = ctx.Store.Expire(key, ttl);
        if (res) ctx.Aof.AppendCommand(RespValue.Array(args));
        return res ? RespValue.One : RespValue.Zero;
    }

    private static RespValue HandleTtl(IReadOnlyList<RespValue> args, CommandContext ctx, bool isMillis)
    {
        if (args.Count < 2) return RespValue.Error("ERR wrong number of arguments for ttl command");
        string key = args[1].AsString();
        return RespValue.Integer(isMillis ? ctx.Store.PTtl(key) : ctx.Store.Ttl(key));
    }

    private static RespValue HandlePersist(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 2) return RespValue.Error("ERR wrong number of arguments for 'persist' command");
        string key = args[1].AsString();
        return ctx.Store.Persist(key) ? RespValue.One : RespValue.Zero;
    }

    private static RespValue HandleType(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 2) return RespValue.Error("ERR wrong number of arguments for 'type' command");
        string key = args[1].AsString();
        var type = ctx.Store.GetType(key);
        if (!type.HasValue) return RespValue.SimpleString("none");
        return RespValue.SimpleString(type.Value.ToString().ToLowerInvariant());
    }

    private static RespValue HandleKeys(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        string pattern = args.Count > 1 ? args[1].AsString() : "*";
        var keys = ctx.Store.Keys(pattern);
        var respKeys = new List<RespValue>(keys.Count);
        foreach (var k in keys) respKeys.Add(RespValue.BulkString(k));
        return RespValue.Array(respKeys);
    }

    private static RespValue HandleFlushDb(CommandContext ctx)
    {
        ctx.Store.FlushDb();
        ctx.Aof.AppendCommand(RespValue.Array(new[] { RespValue.BulkString("FLUSHDB") }));
        return RespValue.Ok;
    }

    private static RespValue HandleHSet(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 4 || (args.Count - 2) % 2 != 0)
            return RespValue.Error("ERR wrong number of arguments for 'hset' command");

        string key = args[1].AsString();
        var pairs = new List<KeyValuePair<string, ReadOnlyMemory<byte>>>((args.Count - 2) / 2);
        for (int i = 2; i < args.Count; i += 2)
        {
            pairs.Add(new KeyValuePair<string, ReadOnlyMemory<byte>>(args[i].AsString(), args[i + 1].BinaryValue));
        }

        int added = ctx.Store.HSet(key, pairs);
        if (added < 0) return RespValue.Error("WRONGTYPE Operation against a key holding the wrong kind of value");
        ctx.Aof.AppendCommand(RespValue.Array(args));
        return RespValue.Integer(added);
    }

    private static RespValue HandleHGet(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 3) return RespValue.Error("ERR wrong number of arguments for 'hget' command");
        string key = args[1].AsString();
        string field = args[2].AsString();
        var val = ctx.Store.HGet(key, field);
        return val.HasValue ? RespValue.BulkString(val.Value) : RespValue.NullBulkString();
    }

    private static RespValue HandleHGetAll(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 2) return RespValue.Error("ERR wrong number of arguments for 'hgetall' command");
        string key = args[1].AsString();
        var pairs = ctx.Store.HGetAll(key);
        if (pairs == null) return RespValue.Array(Array.Empty<RespValue>());

        var result = new List<RespValue>(pairs.Count * 2);
        foreach (var p in pairs)
        {
            result.Add(RespValue.BulkString(p.Key));
            result.Add(RespValue.BulkString(p.Value));
        }
        return RespValue.Array(result);
    }

    private static RespValue HandleHDel(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 3) return RespValue.Error("ERR wrong number of arguments for 'hdel' command");
        string key = args[1].AsString();
        var fields = new List<string>(args.Count - 2);
        for (int i = 2; i < args.Count; i++) fields.Add(args[i].AsString());
        int removed = ctx.Store.HDel(key, fields);
        if (removed < 0) return RespValue.Error("WRONGTYPE Operation against a key holding the wrong kind of value");
        ctx.Aof.AppendCommand(RespValue.Array(args));
        return RespValue.Integer(removed);
    }

    private static RespValue HandleHExists(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 3) return RespValue.Error("ERR wrong number of arguments for 'hexists' command");
        string key = args[1].AsString();
        string field = args[2].AsString();
        var val = ctx.Store.HGet(key, field);
        return val.HasValue ? RespValue.One : RespValue.Zero;
    }

    private static RespValue HandleHLen(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 2) return RespValue.Error("ERR wrong number of arguments for 'hlen' command");
        string key = args[1].AsString();
        return RespValue.Integer(ctx.Store.HLen(key));
    }

    private static RespValue HandleHIncrBy(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 4) return RespValue.Error("ERR wrong number of arguments for 'hincrby' command");
        string key = args[1].AsString();
        string field = args[2].AsString();
        if (!long.TryParse(args[3].AsString(), out long delta)) return RespValue.Error("ERR value is not an integer");

        var cur = ctx.Store.HGet(key, field);
        long curVal = 0;
        if (cur.HasValue)
        {
            if (!long.TryParse(Encoding.UTF8.GetString(cur.Value.Span), out curVal))
                return RespValue.Error("ERR hash value is not an integer");
        }

        long newVal = curVal + delta;
        byte[] bytes = Encoding.UTF8.GetBytes(newVal.ToString());
        ctx.Store.HSet(key, new[] { new KeyValuePair<string, ReadOnlyMemory<byte>>(field, bytes) });
        ctx.Aof.AppendCommand(RespValue.Array(args));
        return RespValue.Integer(newVal);
    }

    private static RespValue HandleLPush(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 3) return RespValue.Error("ERR wrong number of arguments for 'lpush' command");
        string key = args[1].AsString();
        var values = new List<ReadOnlyMemory<byte>>(args.Count - 2);
        for (int i = 2; i < args.Count; i++) values.Add(args[i].BinaryValue);
        int len = ctx.Store.LPush(key, values);
        if (len < 0) return RespValue.Error("WRONGTYPE Operation against a key holding the wrong kind of value");
        ctx.Aof.AppendCommand(RespValue.Array(args));
        return RespValue.Integer(len);
    }

    private static RespValue HandleRPush(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 3) return RespValue.Error("ERR wrong number of arguments for 'rpush' command");
        string key = args[1].AsString();
        var values = new List<ReadOnlyMemory<byte>>(args.Count - 2);
        for (int i = 2; i < args.Count; i++) values.Add(args[i].BinaryValue);
        int len = ctx.Store.RPush(key, values);
        if (len < 0) return RespValue.Error("WRONGTYPE Operation against a key holding the wrong kind of value");
        ctx.Aof.AppendCommand(RespValue.Array(args));
        return RespValue.Integer(len);
    }

    private static RespValue HandleLPop(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 2) return RespValue.Error("ERR wrong number of arguments for 'lpop' command");
        string key = args[1].AsString();
        int count = 1;
        if (args.Count > 2 && int.TryParse(args[2].AsString(), out int c)) count = c;

        var popped = ctx.Store.LPop(key, count);
        if (popped.Count == 0) return RespValue.NullBulkString();
        if (args.Count == 2) return RespValue.BulkString(popped[0]);

        var arr = new List<RespValue>(popped.Count);
        foreach (var p in popped) arr.Add(RespValue.BulkString(p));
        return RespValue.Array(arr);
    }

    private static RespValue HandleRPop(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 2) return RespValue.Error("ERR wrong number of arguments for 'rpop' command");
        string key = args[1].AsString();
        int count = 1;
        if (args.Count > 2 && int.TryParse(args[2].AsString(), out int c)) count = c;

        var popped = ctx.Store.RPop(key, count);
        if (popped.Count == 0) return RespValue.NullBulkString();
        if (args.Count == 2) return RespValue.BulkString(popped[0]);

        var arr = new List<RespValue>(popped.Count);
        foreach (var p in popped) arr.Add(RespValue.BulkString(p));
        return RespValue.Array(arr);
    }

    private static RespValue HandleLLen(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 2) return RespValue.Error("ERR wrong number of arguments for 'llen' command");
        string key = args[1].AsString();
        return RespValue.Integer(ctx.Store.LLen(key));
    }

    private static RespValue HandleLRange(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 4) return RespValue.Error("ERR wrong number of arguments for 'lrange' command");
        string key = args[1].AsString();
        if (!int.TryParse(args[2].AsString(), out int start) || !int.TryParse(args[3].AsString(), out int stop))
            return RespValue.Error("ERR value is not an integer or out of range");

        var range = ctx.Store.LRange(key, start, stop);
        var arr = new List<RespValue>(range.Count);
        foreach (var item in range) arr.Add(RespValue.BulkString(item));
        return RespValue.Array(arr);
    }

    private static RespValue HandleSAdd(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 3) return RespValue.Error("ERR wrong number of arguments for 'sadd' command");
        string key = args[1].AsString();
        var members = new List<string>(args.Count - 2);
        for (int i = 2; i < args.Count; i++) members.Add(args[i].AsString());
        int added = ctx.Store.SAdd(key, members);
        if (added < 0) return RespValue.Error("WRONGTYPE Operation against a key holding the wrong kind of value");
        ctx.Aof.AppendCommand(RespValue.Array(args));
        return RespValue.Integer(added);
    }

    private static RespValue HandleSRem(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 3) return RespValue.Error("ERR wrong number of arguments for 'srem' command");
        string key = args[1].AsString();
        var members = new List<string>(args.Count - 2);
        for (int i = 2; i < args.Count; i++) members.Add(args[i].AsString());
        int removed = ctx.Store.SRem(key, members);
        if (removed < 0) return RespValue.Error("WRONGTYPE Operation against a key holding the wrong kind of value");
        ctx.Aof.AppendCommand(RespValue.Array(args));
        return RespValue.Integer(removed);
    }

    private static RespValue HandleSMembers(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 2) return RespValue.Error("ERR wrong number of arguments for 'smembers' command");
        string key = args[1].AsString();
        var members = ctx.Store.SMembers(key);
        var arr = new List<RespValue>(members.Count);
        foreach (var m in members) arr.Add(RespValue.BulkString(m));
        return RespValue.Array(arr);
    }

    private static RespValue HandleSIsMember(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 3) return RespValue.Error("ERR wrong number of arguments for 'sismember' command");
        string key = args[1].AsString();
        string member = args[2].AsString();
        return ctx.Store.SIsMember(key, member) ? RespValue.One : RespValue.Zero;
    }

    private static RespValue HandleSCard(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 2) return RespValue.Error("ERR wrong number of arguments for 'scard' command");
        string key = args[1].AsString();
        return RespValue.Integer(ctx.Store.SCard(key));
    }

    private static RespValue HandleSubscribe(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 2) return RespValue.Error("ERR wrong number of arguments for 'subscribe' command");
        ctx.IsSubscribed = true;

        for (int i = 1; i < args.Count; i++)
        {
            string channel = args[i].AsString();
            int subCount = ctx.PubSub.Subscribe(channel, ctx.SessionId, ctx.WritePushMessage);
            ctx.WritePushMessage(RespValue.Array(new[]
            {
                RespValue.BulkString("subscribe"),
                RespValue.BulkString(channel),
                RespValue.Integer(subCount)
            }));
        }

        return RespValue.Null; // Messages are pushed via callback
    }

    private static RespValue HandleUnsubscribe(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count == 1)
        {
            ctx.PubSub.UnsubscribeAll(ctx.SessionId);
            ctx.IsSubscribed = false;
            return RespValue.Array(new[]
            {
                RespValue.BulkString("unsubscribe"),
                RespValue.NullBulkString(),
                RespValue.Zero
            });
        }

        for (int i = 1; i < args.Count; i++)
        {
            string channel = args[i].AsString();
            int subCount = ctx.PubSub.Unsubscribe(channel, ctx.SessionId);
            if (subCount == 0) ctx.IsSubscribed = false;

            ctx.WritePushMessage(RespValue.Array(new[]
            {
                RespValue.BulkString("unsubscribe"),
                RespValue.BulkString(channel),
                RespValue.Integer(subCount)
            }));
        }

        return RespValue.Null;
    }

    private static RespValue HandlePublish(IReadOnlyList<RespValue> args, CommandContext ctx)
    {
        if (args.Count < 3) return RespValue.Error("ERR wrong number of arguments for 'publish' command");
        string channel = args[1].AsString();
        var message = args[2].BinaryValue;
        int recipients = ctx.PubSub.Publish(channel, message);
        return RespValue.Integer(recipients);
    }

    private static RespValue HandleInfo(CommandContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Server");
        sb.AppendLine("redis_version:7.0.0-FlashKV");
        sb.AppendLine("flashkv_version:1.0.0");
        sb.AppendLine("os:" + Environment.OSVersion);
        sb.AppendLine("process_id:" + Environment.ProcessId);
        sb.AppendLine("tcp_port:6379");
        sb.AppendLine();
        sb.AppendLine("# Memory");
        sb.AppendLine($"used_memory:{ctx.Store.GetTotalBytes()}");
        sb.AppendLine($"used_memory_human:{FormatBytes(ctx.Store.GetTotalBytes())}");
        sb.AppendLine($"maxmemory:{ctx.Store.MaxMemoryBytes}");
        sb.AppendLine($"maxmemory_policy:{ctx.Store.EvictionPolicy}");
        sb.AppendLine();
        sb.AppendLine("# Stats");
        sb.AppendLine($"total_commands_processed:{ctx.Store.TotalOperations}");
        sb.AppendLine();
        sb.AppendLine("# Keyspace");
        sb.AppendLine($"db0:keys={ctx.Store.DbSize()},expires=0,avg_ttl=0");

        return RespValue.BulkString(sb.ToString());
    }

    private static RespValue HandleTime()
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long sec = nowMs / 1000;
        long micro = (nowMs % 1000) * 1000;
        return RespValue.Array(new[]
        {
            RespValue.BulkString(sec.ToString()),
            RespValue.BulkString(micro.ToString())
        });
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int idx = 0;
        double val = bytes;
        while (val >= 1024 && idx < suffixes.Length - 1)
        {
            val /= 1024;
            idx++;
        }
        return $"{val:0.##}{suffixes[idx]}";
    }

    #endregion
}
