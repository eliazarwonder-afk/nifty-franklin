using FlashKV.Core.Storage;

namespace FlashKV.Server;

public sealed class ServerConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 6379;
    public long MaxMemoryBytes { get; set; } = 0;
    public EvictionPolicy EvictionPolicy { get; set; } = EvictionPolicy.NoEviction;
    public bool AofEnabled { get; set; } = false;
    public string AofPath { get; set; } = "flashkv.aof";
    public int StripeCount { get; set; } = 64;
    public int TtlIntervalMs { get; set; } = 100;
}
