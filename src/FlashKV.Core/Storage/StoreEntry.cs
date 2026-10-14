using System;

namespace FlashKV.Core.Storage;

/// <summary>
/// Container for in-memory items stored in FlashKV, including metadata for TTL and eviction.
/// </summary>
public sealed class StoreEntry
{
    public DataType Type { get; set; }
    public object Value { get; set; }
    public long? ExpiresAtUnixMs { get; set; }
    public long LastAccessedUnixMs { get; set; }
    public long AccessCount { get; set; }
    public long EstimatedBytes { get; set; }

    public StoreEntry(DataType type, object value, long? expiresAtUnixMs = null, long estimatedBytes = 32)
    {
        Type = type;
        Value = value;
        ExpiresAtUnixMs = expiresAtUnixMs;
        LastAccessedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        AccessCount = 1;
        EstimatedBytes = estimatedBytes;
    }

    public bool IsExpired(long nowUnixMs)
    {
        return ExpiresAtUnixMs.HasValue && ExpiresAtUnixMs.Value <= nowUnixMs;
    }

    public void Touch(long nowUnixMs)
    {
        LastAccessedUnixMs = nowUnixMs;
        AccessCount++;
    }
}
