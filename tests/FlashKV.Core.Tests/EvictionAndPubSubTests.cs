using System;
using System.Text;
using System.Threading;
using FlashKV.Core.PubSub;
using FlashKV.Core.Resp;
using FlashKV.Core.Storage;
using Xunit;

namespace FlashKV.Core.Tests;

public class EvictionAndPubSubTests
{
    [Fact]
    public void Eviction_AllKeysLru_ReclaimsMemory()
    {
        // Max memory 500 bytes
        var store = new DataStore(maxMemoryBytes: 500, evictionPolicy: EvictionPolicy.AllKeysLru, stripeCount: 4);

        for (int i = 0; i < 50; i++)
        {
            string key = $"k{i}";
            byte[] val = new byte[30];
            store.Set(key, val);
        }

        // Memory should be bounded at or below max memory
        long totalBytes = store.GetTotalBytes();
        Assert.True(totalBytes <= 600, $"Total bytes {totalBytes} should be bounded near 500 bytes");
    }

    [Fact]
    public void PubSub_DeliversMessageToSubscriber()
    {
        var broker = new PubSubBroker();
        string receivedChannel = "";
        string receivedMessage = "";

        broker.Subscribe("news", 1, resp =>
        {
            if (resp.Type == RespType.Array && resp.ArrayValue != null && resp.ArrayValue.Count == 3)
            {
                receivedChannel = resp.ArrayValue[1].AsString();
                receivedMessage = resp.ArrayValue[2].AsString();
            }
        });

        int delivered = broker.Publish("news", Encoding.UTF8.GetBytes("Breaking News!"));

        Assert.Equal(1, delivered);
        Assert.Equal("news", receivedChannel);
        Assert.Equal("Breaking News!", receivedMessage);

        broker.UnsubscribeAll(1);
        int deliveredAfterUnsub = broker.Publish("news", Encoding.UTF8.GetBytes("Another News"));
        Assert.Equal(0, deliveredAfterUnsub);
    }
}
