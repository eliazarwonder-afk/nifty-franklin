using System;
using System.Text;
using System.Threading.Tasks;
using FlashKV.Core.Storage;
using Xunit;

namespace FlashKV.Core.Tests;

public class ConcurrencyStressTests
{
    [Fact]
    public async Task StripedStore_HandlesHighConcurrencyWithoutCorruption()
    {
        var store = new DataStore(stripeCount: 64);
        const int numTasks = 20;
        const int opsPerTask = 500;

        var tasks = new Task[numTasks];

        for (int t = 0; t < numTasks; t++)
        {
            int taskId = t;
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < opsPerTask; i++)
                {
                    string key = $"stress:{taskId}:{i}";
                    byte[] val = Encoding.UTF8.GetBytes($"val_{taskId}_{i}");

                    // Concurrent write
                    bool setOk = store.Set(key, val);
                    Assert.True(setOk);

                    // Concurrent read
                    var readVal = store.Get(key);
                    Assert.NotNull(readVal);
                    Assert.Equal($"val_{taskId}_{i}", Encoding.UTF8.GetString(readVal.Value.Span));

                    // Concurrent atomic counter increment
                    store.Incr("stress:global:counter", 1);
                }
            });
        }

        await Task.WhenAll(tasks);

        // Verify total keys
        Assert.Equal(numTasks * opsPerTask + 1, store.DbSize());

        // Verify atomic global counter
        var counterVal = store.Get("stress:global:counter");
        Assert.NotNull(counterVal);
        Assert.Equal((numTasks * opsPerTask).ToString(), Encoding.UTF8.GetString(counterVal.Value.Span));
    }
}
