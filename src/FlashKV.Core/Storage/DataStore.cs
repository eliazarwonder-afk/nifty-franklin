using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace FlashKV.Core.Storage;

/// <summary>
/// Striped, multi-threaded in-memory key-value store supporting Redis data structures,
/// LRU/LFU eviction, and active TTL expiration.
/// </summary>
public sealed class DataStore
{
    private sealed class Segment
    {
        public readonly object Lock = new();
        public readonly Dictionary<string, StoreEntry> Entries = new(StringComparer.Ordinal);
        public readonly HashSet<string> VolatileKeys = new(StringComparer.Ordinal);
        public long EstimatedBytes;
    }

    private readonly Segment[] _segments;
    private readonly int _segmentMask;
    private long _totalOpsCount;

    public long MaxMemoryBytes { get; }
    public EvictionPolicy EvictionPolicy { get; }

    public long TotalOperations => Volatile.Read(ref _totalOpsCount);

    public DataStore(long maxMemoryBytes = 0, EvictionPolicy evictionPolicy = EvictionPolicy.NoEviction, int stripeCount = 64)
    {
        // Round stripeCount to next power of two
        int count = 1;
        while (count < stripeCount) count <<= 1;

        _segments = new Segment[count];
        for (int i = 0; i < count; i++)
        {
            _segments[i] = new Segment();
        }
        _segmentMask = count - 1;

        MaxMemoryBytes = maxMemoryBytes;
        EvictionPolicy = evictionPolicy;
    }

    private Segment GetSegment(string key)
    {
        uint hash = (uint)key.GetHashCode();
        return _segments[hash & _segmentMask];
    }

    public long GetTotalBytes()
    {
        long total = 0;
        foreach (var s in _segments)
        {
            lock (s.Lock)
            {
                total += s.EstimatedBytes;
            }
        }
        return total;
    }

    public long DbSize()
    {
        long count = 0;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var s in _segments)
        {
            lock (s.Lock)
            {
                foreach (var kvp in s.Entries)
                {
                    if (!kvp.Value.IsExpired(now))
                    {
                        count++;
                    }
                }
            }
        }
        return count;
    }

    public void FlushDb()
    {
        foreach (var s in _segments)
        {
            lock (s.Lock)
            {
                s.Entries.Clear();
                s.VolatileKeys.Clear();
                s.EstimatedBytes = 0;
            }
        }
    }

    #region String Operations

    public bool Set(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl = null, bool nx = false, bool xx = false)
    {
        Interlocked.Increment(ref _totalOpsCount);
        var seg = GetSegment(key);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long? expireAt = ttl.HasValue ? now + (long)ttl.Value.TotalMilliseconds : null;

        lock (seg.Lock)
        {
            bool exists = seg.Entries.TryGetValue(key, out var existing);
            if (exists && existing!.IsExpired(now))
            {
                RemoveExpiredInternal(seg, key, existing);
                exists = false;
            }

            if (nx && exists) return false;
            if (xx && !exists) return false;

            long byteSize = (key.Length * 2) + value.Length + 48;
            EnsureMemoryAvailable(byteSize);

            if (exists)
            {
                seg.EstimatedBytes -= existing!.EstimatedBytes;
            }

            byte[] copy = value.ToArray();
            var entry = new StoreEntry(DataType.String, copy, expireAt, byteSize);
            seg.Entries[key] = entry;
            seg.EstimatedBytes += byteSize;

            if (expireAt.HasValue)
                seg.VolatileKeys.Add(key);
            else
                seg.VolatileKeys.Remove(key);

            return true;
        }
    }

    public ReadOnlyMemory<byte>? Get(string key)
    {
        Interlocked.Increment(ref _totalOpsCount);
        var seg = GetSegment(key);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (seg.Lock)
        {
            if (!seg.Entries.TryGetValue(key, out var entry))
            {
                return null;
            }

            if (entry.IsExpired(now))
            {
                RemoveExpiredInternal(seg, key, entry);
                return null;
            }

            if (entry.Type != DataType.String)
            {
                return null;
            }

            entry.Touch(now);
            return (byte[])entry.Value;
        }
    }

    public (bool success, long result, string? error) Incr(string key, long delta)
    {
        Interlocked.Increment(ref _totalOpsCount);
        var seg = GetSegment(key);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (seg.Lock)
        {
            if (seg.Entries.TryGetValue(key, out var entry))
            {
                if (entry.IsExpired(now))
                {
                    RemoveExpiredInternal(seg, key, entry);
                    entry = null;
                }
            }

            if (entry == null)
            {
                long byteSize = (key.Length * 2) + 32 + 48;
                EnsureMemoryAvailable(byteSize);
                byte[] valBytes = Encoding.UTF8.GetBytes(delta.ToString());
                var newEntry = new StoreEntry(DataType.String, valBytes, null, byteSize);
                seg.Entries[key] = newEntry;
                seg.EstimatedBytes += byteSize;
                return (true, delta, null);
            }

            if (entry.Type != DataType.String)
            {
                return (false, 0, "WRONGTYPE Operation against a key holding the wrong kind of value");
            }

            string currentStr = Encoding.UTF8.GetString((byte[])entry.Value);
            if (!long.TryParse(currentStr, out long currentVal))
            {
                return (false, 0, "ERR value is not an integer or out of range");
            }

            long newVal = checked(currentVal + delta);
            byte[] updated = Encoding.UTF8.GetBytes(newVal.ToString());
            seg.EstimatedBytes = seg.EstimatedBytes - entry.EstimatedBytes + updated.Length;
            entry.EstimatedBytes = (key.Length * 2) + updated.Length + 48;
            entry.Value = updated;
            entry.Touch(now);
            return (true, newVal, null);
        }
    }

    public int Append(string key, ReadOnlyMemory<byte> value)
    {
        Interlocked.Increment(ref _totalOpsCount);
        var seg = GetSegment(key);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (seg.Lock)
        {
            if (seg.Entries.TryGetValue(key, out var entry) && !entry.IsExpired(now))
            {
                if (entry.Type != DataType.String) return -1;

                byte[] cur = (byte[])entry.Value;
                byte[] comb = new byte[cur.Length + value.Length];
                Buffer.BlockCopy(cur, 0, comb, 0, cur.Length);
                value.Span.CopyTo(comb.AsSpan(cur.Length));

                entry.Value = comb;
                seg.EstimatedBytes += value.Length;
                entry.EstimatedBytes += value.Length;
                entry.Touch(now);
                return comb.Length;
            }

            Set(key, value);
            return value.Length;
        }
    }

    public int StrLen(string key)
    {
        var val = Get(key);
        return val.HasValue ? val.Value.Length : 0;
    }

    #endregion

    #region Key Operations

    public int Del(IReadOnlyList<string> keys)
    {
        Interlocked.Increment(ref _totalOpsCount);
        int deleted = 0;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var key in keys)
        {
            var seg = GetSegment(key);
            lock (seg.Lock)
            {
                if (seg.Entries.TryGetValue(key, out var entry))
                {
                    bool expired = entry.IsExpired(now);
                    seg.Entries.Remove(key);
                    seg.VolatileKeys.Remove(key);
                    seg.EstimatedBytes -= entry.EstimatedBytes;
                    if (!expired) deleted++;
                }
            }
        }
        return deleted;
    }

    public int Exists(IReadOnlyList<string> keys)
    {
        Interlocked.Increment(ref _totalOpsCount);
        int count = 0;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var key in keys)
        {
            var seg = GetSegment(key);
            lock (seg.Lock)
            {
                if (seg.Entries.TryGetValue(key, out var entry))
                {
                    if (entry.IsExpired(now))
                    {
                        RemoveExpiredInternal(seg, key, entry);
                    }
                    else
                    {
                        count++;
                    }
                }
            }
        }
        return count;
    }

    public bool Expire(string key, TimeSpan ttl)
    {
        Interlocked.Increment(ref _totalOpsCount);
        var seg = GetSegment(key);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (seg.Lock)
        {
            if (seg.Entries.TryGetValue(key, out var entry))
            {
                if (entry.IsExpired(now))
                {
                    RemoveExpiredInternal(seg, key, entry);
                    return false;
                }

                entry.ExpiresAtUnixMs = now + (long)ttl.TotalMilliseconds;
                seg.VolatileKeys.Add(key);
                return true;
            }
            return false;
        }
    }

    public long Ttl(string key)
    {
        var pttl = PTtl(key);
        if (pttl < 0) return pttl;
        return pttl / 1000;
    }

    public long PTtl(string key)
    {
        Interlocked.Increment(ref _totalOpsCount);
        var seg = GetSegment(key);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (seg.Lock)
        {
            if (!seg.Entries.TryGetValue(key, out var entry))
            {
                return -2; // Key does not exist
            }

            if (entry.IsExpired(now))
            {
                RemoveExpiredInternal(seg, key, entry);
                return -2;
            }

            if (!entry.ExpiresAtUnixMs.HasValue)
            {
                return -1; // No TTL set
            }

            long diff = entry.ExpiresAtUnixMs.Value - now;
            return diff > 0 ? diff : 0;
        }
    }

    public bool Persist(string key)
    {
        var seg = GetSegment(key);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (seg.Lock)
        {
            if (seg.Entries.TryGetValue(key, out var entry) && !entry.IsExpired(now))
            {
                if (entry.ExpiresAtUnixMs.HasValue)
                {
                    entry.ExpiresAtUnixMs = null;
                    seg.VolatileKeys.Remove(key);
                    return true;
                }
            }
            return false;
        }
    }

    public DataType? GetType(string key)
    {
        var seg = GetSegment(key);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (seg.Lock)
        {
            if (seg.Entries.TryGetValue(key, out var entry) && !entry.IsExpired(now))
            {
                return entry.Type;
            }
            return null;
        }
    }

    public List<string> Keys(string pattern)
    {
        var list = new List<string>();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool matchAll = pattern == "*";

        foreach (var seg in _segments)
        {
            lock (seg.Lock)
            {
                foreach (var kvp in seg.Entries)
                {
                    if (!kvp.Value.IsExpired(now))
                    {
                        if (matchAll || IsPatternMatch(kvp.Key, pattern))
                        {
                            list.Add(kvp.Key);
                        }
                    }
                }
            }
        }
        return list;
    }

    #endregion

    #region Hash Operations

    public int HSet(string key, IReadOnlyList<KeyValuePair<string, ReadOnlyMemory<byte>>> fields)
    {
        Interlocked.Increment(ref _totalOpsCount);
        var seg = GetSegment(key);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (seg.Lock)
        {
            Dictionary<string, byte[]> map;
            StoreEntry entry;

            if (seg.Entries.TryGetValue(key, out var ex))
            {
                if (ex.IsExpired(now))
                {
                    RemoveExpiredInternal(seg, key, ex);
                    map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                    entry = new StoreEntry(DataType.Hash, map);
                    seg.Entries[key] = entry;
                }
                else
                {
                    if (ex.Type != DataType.Hash) return -1;
                    map = (Dictionary<string, byte[]>)ex.Value;
                    entry = ex;
                }
            }
            else
            {
                map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                entry = new StoreEntry(DataType.Hash, map);
                seg.Entries[key] = entry;
            }

            int added = 0;
            foreach (var pair in fields)
            {
                if (!map.ContainsKey(pair.Key))
                {
                    added++;
                    long est = (pair.Key.Length * 2) + pair.Value.Length + 32;
                    entry.EstimatedBytes += est;
                    seg.EstimatedBytes += est;
                }
                map[pair.Key] = pair.Value.ToArray();
            }

            entry.Touch(now);
            return added;
        }
    }

    public ReadOnlyMemory<byte>? HGet(string key, string field)
    {
        Interlocked.Increment(ref _totalOpsCount);
        var seg = GetSegment(key);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (seg.Lock)
        {
            if (seg.Entries.TryGetValue(key, out var entry) && !entry.IsExpired(now))
            {
                if (entry.Type != DataType.Hash) return null;
                var map = (Dictionary<string, byte[]>)entry.Value;
                if (map.TryGetValue(field, out var val))
                {
                    entry.Touch(now);
                    return val;
                }
            }
            return null;
        }
    }

    public IReadOnlyList<KeyValuePair<string, ReadOnlyMemory<byte>>>? HGetAll(string key)
    {
        Interlocked.Increment(ref _totalOpsCount);
        var seg = GetSegment(key);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (seg.Lock)
        {
            if (seg.Entries.TryGetValue(key, out var entry) && !entry.IsExpired(now))
            {
                if (entry.Type != DataType.Hash) return null;
                var map = (Dictionary<string, byte[]>)entry.Value;
                var list = new List<KeyValuePair<string, ReadOnlyMemory<byte>>>(map.Count);
                foreach (var kv in map)
                {
                    list.Add(new KeyValuePair<string, ReadOnlyMemory<byte>>(kv.Key, kv.Value));
                }
                entry.Touch(now);
                return list;
            }
            return null;
        }
    }

    public int HDel(string key, IReadOnlyList<string> fields)
    {
        var seg = GetSegment(key);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (seg.Lock)
        {
            if (seg.Entries.TryGetValue(key, out var entry) && !entry.IsExpired(now))
            {
                if (entry.Type != DataType.Hash) return -1;
                var map = (Dictionary<string, byte[]>)entry.Value;
                int removed = 0;
                foreach (var f in fields)
                {
                    if (map.Remove(f)) removed++;
                }
                entry.Touch(now);
                return removed;
            }
            return 0;
        }
    }

    public int HLen(string key)
    {
        var seg = GetSegment(key);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (seg.Lock)
        {
            if (seg.Entries.TryGetValue(key, out var entry) && !entry.IsExpired(now))
            {
                if (entry.Type != DataType.Hash) return 0;
                return ((Dictionary<string, byte[]>)entry.Value).Count;
            }
            return 0;
        }
    }

    #endregion

    #region List Operations

    public int LPush(string key, IReadOnlyList<ReadOnlyMemory<byte>> values)
    {
        Interlocked.Increment(ref _totalOpsCount);
        var seg = GetSegment(key);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (seg.Lock)
        {
            LinkedList<byte[]> list;
            StoreEntry entry;

            if (seg.Entries.TryGetValue(key, out var ex))
            {
                if (ex.IsExpired(now))
                {
                    RemoveExpiredInternal(seg, key, ex);
                    list = new LinkedList<byte[]>();
                    entry = new StoreEntry(DataType.List, list);
                    seg.Entries[key] = entry;
                }
                else
                {
                    if (ex.Type != DataType.List) return -1;
                    list = (LinkedList<byte[]>)ex.Value;
                    entry = ex;
                }
            }
            else
            {
                list = new LinkedList<byte[]>();
                entry = new StoreEntry(DataType.List, list);
                seg.Entries[key] = entry;
            }

            foreach (var val in values)
            {
                list.AddFirst(val.ToArray());
                long est = val.Length + 24;
                entry.EstimatedBytes += est;
                seg.EstimatedBytes += est;
            }

            entry.Touch(now);
            return list.Count;
        }
    }

    public int RPush(string key, IReadOnlyList<ReadOnlyMemory<byte>> values)
    {
        Interlocked.Increment(ref _totalOpsCount);
        var seg = GetSegment(key);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (seg.Lock)
        {
            LinkedList<byte[]> list;
            StoreEntry entry;

            if (seg.Entries.TryGetValue(key, out var ex))
            {
                if (ex.IsExpired(now))
                {
                    RemoveExpiredInternal(seg, key, ex);
                    list = new LinkedList<byte[]>();
                    entry = new StoreEntry(DataType.List, list);
                    seg.Entries[key] = entry;
                }
                else
                {
                    if (ex.Type != DataType.List) return -1;
                    list = (LinkedList<byte[]>)ex.Value;
                    entry = ex;
                }
            }
            else
            {
                list = new LinkedList<byte[]>();
                entry = new StoreEntry(DataType.List, list);
                seg.Entries[key] = entry;
            }

            foreach (var val in values)
            {
                list.AddLast(val.ToArray());
                long est = val.Length + 24;
                entry.EstimatedBytes += est;
                seg.EstimatedBytes += est;
            }

            entry.Touch(now);
            return list.Count;
        }
    }

    public List<ReadOnlyMemory<byte>> LPop(string key, int count = 1)
    {
        var seg = GetSegment(key);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var result = new List<ReadOnlyMemory<byte>>();

        lock (seg.Lock)
        {
            if (seg.Entries.TryGetValue(key, out var entry) && !entry.IsExpired(now))
            {
                if (entry.Type != DataType.List) return result;
                var list = (LinkedList<byte[]>)entry.Value;

                while (list.Count > 0 && result.Count < count)
                {
                    var node = list.First;
                    list.RemoveFirst();
                    result.Add(node!.Value);
                }
                entry.Touch(now);
            }
        }
        return result;
    }

    public List<ReadOnlyMemory<byte>> RPop(string key, int count = 1)
    {
        var seg = GetSegment(key);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var result = new List<ReadOnlyMemory<byte>>();

        lock (seg.Lock)
        {
            if (seg.Entries.TryGetValue(key, out var entry) && !entry.IsExpired(now))
            {
                if (entry.Type != DataType.List) return result;
                var list = (LinkedList<byte[]>)entry.Value;

                while (list.Count > 0 && result.Count < count)
                {
                    var node = list.Last;
                    list.RemoveLast();
                    result.Add(node!.Value);
                }
                entry.Touch(now);
            }
        }
        return result;
    }

    public List<ReadOnlyMemory<byte>> LRange(string key, int start, int stop)
    {
        var seg = GetSegment(key);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var result = new List<ReadOnlyMemory<byte>>();

        lock (seg.Lock)
        {
            if (seg.Entries.TryGetValue(key, out var entry) && !entry.IsExpired(now))
            {
                if (entry.Type != DataType.List) return result;
                var list = (LinkedList<byte[]>)entry.Value;
                int total = list.Count;
                if (total == 0) return result;

                if (start < 0) start = total + start;
                if (stop < 0) stop = total + stop;
                if (start < 0) start = 0;
                if (stop >= total) stop = total - 1;

                if (start > stop || start >= total) return result;

                int idx = 0;
                foreach (var item in list)
                {
                    if (idx >= start && idx <= stop)
                    {
                        result.Add(item);
                    }
                    if (idx > stop) break;
                    idx++;
                }
                entry.Touch(now);
            }
        }
        return result;
    }

    public int LLen(string key)
    {
        var seg = GetSegment(key);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (seg.Lock)
        {
            if (seg.Entries.TryGetValue(key, out var entry) && !entry.IsExpired(now))
            {
                if (entry.Type != DataType.List) return 0;
                return ((LinkedList<byte[]>)entry.Value).Count;
            }
            return 0;
        }
    }

    #endregion

    #region Set Operations

    public int SAdd(string key, IReadOnlyList<string> members)
    {
        Interlocked.Increment(ref _totalOpsCount);
        var seg = GetSegment(key);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (seg.Lock)
        {
            HashSet<string> set;
            StoreEntry entry;

            if (seg.Entries.TryGetValue(key, out var ex))
            {
                if (ex.IsExpired(now))
                {
                    RemoveExpiredInternal(seg, key, ex);
                    set = new HashSet<string>(StringComparer.Ordinal);
                    entry = new StoreEntry(DataType.Set, set);
                    seg.Entries[key] = entry;
                }
                else
                {
                    if (ex.Type != DataType.Set) return -1;
                    set = (HashSet<string>)ex.Value;
                    entry = ex;
                }
            }
            else
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                entry = new StoreEntry(DataType.Set, set);
                seg.Entries[key] = entry;
            }

            int added = 0;
            foreach (var m in members)
            {
                if (set.Add(m))
                {
                    added++;
                    long est = (m.Length * 2) + 24;
                    entry.EstimatedBytes += est;
                    seg.EstimatedBytes += est;
                }
            }

            entry.Touch(now);
            return added;
        }
    }

    public int SRem(string key, IReadOnlyList<string> members)
    {
        var seg = GetSegment(key);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (seg.Lock)
        {
            if (seg.Entries.TryGetValue(key, out var entry) && !entry.IsExpired(now))
            {
                if (entry.Type != DataType.Set) return -1;
                var set = (HashSet<string>)entry.Value;
                int removed = 0;
                foreach (var m in members)
                {
                    if (set.Remove(m)) removed++;
                }
                entry.Touch(now);
                return removed;
            }
            return 0;
        }
    }

    public List<string> SMembers(string key)
    {
        var seg = GetSegment(key);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (seg.Lock)
        {
            if (seg.Entries.TryGetValue(key, out var entry) && !entry.IsExpired(now))
            {
                if (entry.Type != DataType.Set) return new List<string>();
                var set = (HashSet<string>)entry.Value;
                entry.Touch(now);
                return set.ToList();
            }
            return new List<string>();
        }
    }

    public bool SIsMember(string key, string member)
    {
        var seg = GetSegment(key);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (seg.Lock)
        {
            if (seg.Entries.TryGetValue(key, out var entry) && !entry.IsExpired(now))
            {
                if (entry.Type != DataType.Set) return false;
                var set = (HashSet<string>)entry.Value;
                entry.Touch(now);
                return set.Contains(member);
            }
            return false;
        }
    }

    public int SCard(string key)
    {
        var seg = GetSegment(key);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (seg.Lock)
        {
            if (seg.Entries.TryGetValue(key, out var entry) && !entry.IsExpired(now))
            {
                if (entry.Type != DataType.Set) return 0;
                return ((HashSet<string>)entry.Value).Count;
            }
            return 0;
        }
    }

    #endregion

    #region Internal & Eviction

    private void RemoveExpiredInternal(Segment seg, string key, StoreEntry entry)
    {
        seg.Entries.Remove(key);
        seg.VolatileKeys.Remove(key);
        seg.EstimatedBytes -= entry.EstimatedBytes;
    }

    public void ActiveExpireCycle(int sampleCount)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var seg in _segments)
        {
            lock (seg.Lock)
            {
                if (seg.VolatileKeys.Count == 0) continue;

                var sampled = seg.VolatileKeys.Take(sampleCount).ToList();
                int expiredCount = 0;

                foreach (var key in sampled)
                {
                    if (seg.Entries.TryGetValue(key, out var entry) && entry.IsExpired(now))
                    {
                        RemoveExpiredInternal(seg, key, entry);
                        expiredCount++;
                    }
                }
            }
        }
    }

    private void EnsureMemoryAvailable(long neededBytes)
    {
        if (MaxMemoryBytes <= 0 || EvictionPolicy == EvictionPolicy.NoEviction)
        {
            return;
        }

        long currentBytes = GetTotalBytes();
        if (currentBytes + neededBytes <= MaxMemoryBytes)
        {
            return;
        }

        // Evict keys according to policy
        int maxAttempts = 100;
        while (GetTotalBytes() + neededBytes > MaxMemoryBytes && maxAttempts-- > 0)
        {
            if (!TryEvictSingleKey())
            {
                break;
            }
        }
    }

    private bool TryEvictSingleKey()
    {
        string? candidateKey = null;
        Segment? candidateSeg = null;
        long bestScore = long.MaxValue;

        bool volatileOnly = EvictionPolicy == EvictionPolicy.VolatileLru || EvictionPolicy == EvictionPolicy.VolatileLfu;
        bool isLfu = EvictionPolicy == EvictionPolicy.AllKeysLfu || EvictionPolicy == EvictionPolicy.VolatileLfu;

        // Sample a few keys across segments
        foreach (var seg in _segments)
        {
            lock (seg.Lock)
            {
                IEnumerable<string> pool = volatileOnly ? seg.VolatileKeys : seg.Entries.Keys;
                foreach (var k in pool.Take(5))
                {
                    if (seg.Entries.TryGetValue(k, out var entry))
                    {
                        long score = isLfu ? entry.AccessCount : entry.LastAccessedUnixMs;
                        if (score < bestScore)
                        {
                            bestScore = score;
                            candidateKey = k;
                            candidateSeg = seg;
                        }
                    }
                }
            }
        }

        if (candidateKey != null && candidateSeg != null)
        {
            lock (candidateSeg.Lock)
            {
                if (candidateSeg.Entries.TryGetValue(candidateKey, out var entry))
                {
                    candidateSeg.Entries.Remove(candidateKey);
                    candidateSeg.VolatileKeys.Remove(candidateKey);
                    candidateSeg.EstimatedBytes -= entry.EstimatedBytes;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsPatternMatch(string text, string pattern)
    {
        if (pattern == "*") return true;
        if (pattern.EndsWith("*") && text.StartsWith(pattern.Substring(0, pattern.Length - 1))) return true;
        return string.Equals(text, pattern, StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
