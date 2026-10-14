using System;
using System.Text;
using System.Threading;
using FlashKV.Core.Storage;
using Xunit;

namespace FlashKV.Core.Tests;

public class DataStoreTests
{
    [Fact]
    public void Set_And_Get_ReturnsStoredValue()
    {
        var store = new DataStore();
        string key = "user:100";
        byte[] val = Encoding.UTF8.GetBytes("Alice");

        bool setRes = store.Set(key, val);
        var getRes = store.Get(key);

        Assert.True(setRes);
        Assert.NotNull(getRes);
        Assert.Equal("Alice", Encoding.UTF8.GetString(getRes.Value.Span));
    }

    [Fact]
    public void Key_WithTtl_ExpiresProperly()
    {
        var store = new DataStore();
        string key = "temp:session";
        byte[] val = "token123"u8.ToArray();

        store.Set(key, val, TimeSpan.FromMilliseconds(50));

        Assert.NotNull(store.Get(key));

        Thread.Sleep(70);

        Assert.Null(store.Get(key));
    }

    [Fact]
    public void Incr_IncrementsNumber()
    {
        var store = new DataStore();
        string key = "counter";

        var (s1, r1, _) = store.Incr(key, 1);
        var (s2, r2, _) = store.Incr(key, 5);

        Assert.True(s1);
        Assert.Equal(1, r1);
        Assert.True(s2);
        Assert.Equal(6, r2);
    }

    [Fact]
    public void Hash_Operations_WorkCorrectly()
    {
        var store = new DataStore();
        string key = "user:profile";

        int added = store.HSet(key, new[]
        {
            new KeyValuePair<string, ReadOnlyMemory<byte>>("name", Encoding.UTF8.GetBytes("Bob")),
            new KeyValuePair<string, ReadOnlyMemory<byte>>("age", Encoding.UTF8.GetBytes("30"))
        });

        Assert.Equal(2, added);

        var nameVal = store.HGet(key, "name");
        Assert.NotNull(nameVal);
        Assert.Equal("Bob", Encoding.UTF8.GetString(nameVal.Value.Span));

        var all = store.HGetAll(key);
        Assert.NotNull(all);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void List_Operations_WorkCorrectly()
    {
        var store = new DataStore();
        string key = "tasks";

        store.RPush(key, new ReadOnlyMemory<byte>[]
        {
            Encoding.UTF8.GetBytes("task1"),
            Encoding.UTF8.GetBytes("task2")
        });

        Assert.Equal(2, store.LLen(key));

        var popped = store.LPop(key);
        Assert.Single(popped);
        Assert.Equal("task1", Encoding.UTF8.GetString(popped[0].Span));

        Assert.Equal(1, store.LLen(key));
    }

    [Fact]
    public void Set_Operations_WorkCorrectly()
    {
        var store = new DataStore();
        string key = "tags";

        store.SAdd(key, new[] { "csharp", "rust", "redis" });

        Assert.Equal(3, store.SCard(key));
        Assert.True(store.SIsMember(key, "csharp"));
        Assert.False(store.SIsMember(key, "python"));

        store.SRem(key, new[] { "redis" });
        Assert.Equal(2, store.SCard(key));
    }
}
