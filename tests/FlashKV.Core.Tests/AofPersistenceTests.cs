using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FlashKV.Core.Commands;
using FlashKV.Core.PubSub;
using FlashKV.Core.Resp;
using FlashKV.Core.Storage;
using Xunit;

namespace FlashKV.Core.Tests;

public class AofPersistenceTests : IDisposable
{
    private readonly string _testAofPath;

    public AofPersistenceTests()
    {
        _testAofPath = Path.Combine(Path.GetTempPath(), $"flashkv_test_{Guid.NewGuid():N}.aof");
    }

    public void Dispose()
    {
        if (File.Exists(_testAofPath))
        {
            try { File.Delete(_testAofPath); } catch { }
        }
    }

    [Fact]
    public async Task Aof_RecordsAndReplaysStateSuccessfully()
    {
        var store = new DataStore();
        var pubsub = new PubSubBroker();
        await using (var aof = new AofEngine(_testAofPath, enabled: true))
        {
            var ctx = new CommandContext(store, pubsub, aof, 1, _ => { });

            // Execute mutating commands
            CommandDispatcher.Execute(RespValue.Array(new[]
            {
                RespValue.BulkString("SET"),
                RespValue.BulkString("site:url"),
                RespValue.BulkString("https://flashkv.dev")
            }), ctx);

            CommandDispatcher.Execute(RespValue.Array(new[]
            {
                RespValue.BulkString("HSET"),
                RespValue.BulkString("user:10"),
                RespValue.BulkString("name"),
                RespValue.BulkString("Charlie")
            }), ctx);

            CommandDispatcher.Execute(RespValue.Array(new[]
            {
                RespValue.BulkString("RPUSH"),
                RespValue.BulkString("queue"),
                RespValue.BulkString("itemA"),
                RespValue.BulkString("itemB")
            }), ctx);

            // Allow background channel flush to write to disk
            await Task.Delay(200);
        }

        // Verify the file was created and contains data
        Assert.True(File.Exists(_testAofPath));
        var fileInfo = new FileInfo(_testAofPath);
        Assert.True(fileInfo.Length > 0);

        // Replay into a completely new, clean DataStore
        var restoredStore = new DataStore();
        var replayCtx = new CommandContext(restoredStore, pubsub, new AofEngine(_testAofPath, enabled: false), -1, _ => { });

        await AofEngine.ReplayAsync(_testAofPath, cmd =>
        {
            CommandDispatcher.Execute(cmd, replayCtx);
            return Task.CompletedTask;
        });

        // Assert all data was restored faithfully
        var restoredUrl = restoredStore.Get("site:url");
        Assert.NotNull(restoredUrl);
        Assert.Equal("https://flashkv.dev", Encoding.UTF8.GetString(restoredUrl.Value.Span));

        var restoredName = restoredStore.HGet("user:10", "name");
        Assert.NotNull(restoredName);
        Assert.Equal("Charlie", Encoding.UTF8.GetString(restoredName.Value.Span));

        var restoredList = restoredStore.LRange("queue", 0, -1);
        Assert.Equal(2, restoredList.Count);
        Assert.Equal("itemA", Encoding.UTF8.GetString(restoredList[0].Span));
        Assert.Equal("itemB", Encoding.UTF8.GetString(restoredList[1].Span));
    }
}
