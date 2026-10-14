# FlashKV Architecture & Engineering Design

This document details the internal architecture, design principles, and engineering trade-offs of FlashKV.

---

## 1. System Overview & Core Principles

FlashKV is designed around four core architectural requirements:

1. **Sub-millisecond Tail Latency ($p99 < 1\text{ ms}$)**: Low garbage collection pressure via buffer pooling and zero-allocation parsing.
2. **True Multi-Core Concurrency**: Linear scale across multi-core processors without a single-threaded bottleneck.
3. **Strict Wire Compatibility**: Direct compatibility with existing Redis client libraries (RESP2 / RESP3).
4. **Resilient Persistence**: Non-blocking Append-Only File (AOF) logging with deterministic state replay.

---

## 2. Network & Protocol Layer (`System.IO.Pipelines`)

Traditional socket servers using `NetworkStream.Read` or byte arrays create significant heap allocation churn under high throughput, causing frequent GC pauses.

FlashKV utilizes **`System.IO.Pipelines`**:

```
 [Socket Kernel Buffer]
           │
           ▼
    [PipeReader] ──> ReadOnlySequence<byte> (Pooled buffer memory)
           │
           ▼
    [RespParser] ──> Extracts RespValue slices without allocating strings
           │
           ▼
   [CommandDispatcher]
           │
           ▼
    [PipeWriter] ──> Formats response directly to socket buffer
```

### Protocol Parsing Mechanics
The `RespParser` operates over `ReadOnlySequence<byte>` using `SequenceReader<byte>`:
- **Prefix Inspection**: Evaluates first byte (`+`, `-`, `:`, `$`, `*`).
- **Delimiter Matching**: Uses `reader.TryReadTo(out line, "\r\n")` to consume frames.
- **Payload Extraction**: For bulk strings (`$`), advances the sequence reader directly over the payload without allocating intermediate memory strings until necessary.
- **Pipelining**: If multiple commands are present in the buffer, the parser decodes all of them sequentially in a single pass before a single network flush.

---

## 3. Storage Engine & Striped Locking

Unlike traditional Redis (which operates on a single-threaded event loop), FlashKV takes advantage of modern multi-core architectures by employing **Striped Segment Locking**:

```
Key: "user:session:9812"
          │
          ▼
   Hash(key) & 63
          │
          ▼
    Segment #44
 ┌──────────────────────────────────────┐
 │ Segment Lock (Dedicated Mutex)       │
 │ Dictionary<string, StoreEntry>       │
 │ HashSet<string> VolatileKeys (TTL)   │
 │ EstimatedBytes Counter               │
 └──────────────────────────────────────┘
```

### Why Striped Locking?
1. **Parallel Execution**: 64 independent segments allow up to 64 CPU cores to read/write concurrently without lock contention.
2. **Low Memory Overhead**: Each stripe maintains its own small lock object and hash table, eliminating the overhead of concurrent skip lists or fine-grained per-node locks.
3. **Key Isolation**: Operations on unrelated keys execute in parallel; operations on the same key are safely serialized.

---

## 4. TTL Expiration & Memory Eviction

FlashKV uses a hybrid **active + passive** expiration strategy:

### Passive Expiration
Every time a key is accessed (`GET`, `EXISTS`, `INCR`, `HGET`, etc.), FlashKV checks `entry.ExpiresAtUnixMs <= now`. If expired:
1. The entry is immediately deleted from the segment.
2. Memory accounting is updated.
3. The operation returns `null` or `0`.

### Active Probabilistic Expiration (`TtlManager`)
To prevent expired, unread keys from consuming memory indefinitely:
1. A background timer fires every 100ms.
2. For each segment, FlashKV samples up to 20 keys from its dedicated `VolatileKeys` set.
3. Any expired keys in the sample are removed.
4. If more than 25% of the sampled keys were expired, the cycle repeats immediately to quickly reclaim memory.

### Cache Eviction Policies
When `MaxMemoryBytes` is reached:
- **`AllKeysLRU`**: Samples entries across segments and evicts the key with the smallest `LastAccessedUnixMs`.
- **`AllKeysLFU`**: Evicts the key with the lowest `AccessCount`.
- **`VolatileLRU` / `VolatileLFU`**: Evaluates candidates exclusively from the set of keys with an expiration timestamp.

---

## 5. Persistence Subsystem (Append-Only File)

To ensure durability without introducing disk I/O latency into the client request path, FlashKV uses an asynchronous queue architecture:

```
 Client Request (SET key val)
        │
        ▼
   [DataStore] ─── (Mutates memory state)
        │
        ▼
  [AofEngine] ─── (Serializes command to byte buffer)
        │
        ▼
 [Bounded Channel] (100,000 capacity)
        │
        ▼ (Background Task)
  [FileStream] ───> disk: flashkv.aof
```

- Mutating commands (`SET`, `DEL`, `INCR`, `HSET`, `LPUSH`, etc.) are written to a bounded `System.Threading.Channels.Channel<byte[]>`.
- A dedicated background consumer thread pulls batches from the channel, writes them sequentially to `flashkv.aof`, and issues periodic `FlushAsync()` calls.
- On server startup, `AofEngine.ReplayAsync` reads the append log and executes each command in sequence to reconstruct state.

---

## 6. Rust Benchmarking Engine (`flashkv-bench`)

The accompanying benchmarking tool is written in **Rust** to provide independent, unmanaged load generation:

- **Zero-Allocation Socket Loop**: Raw `std::net::TcpStream` with `TCP_NODELAY` enabled.
- **Native Thread Concurrency**: Spawns $N$ OS threads, each with its own persistent TCP socket.
- **High-Resolution Microsecond Timers**: Employs `std::time::Instant` to record individual request/response round-trip times.
- **Percentile Calculation**: Latencies are collected, sorted in memory, and evaluated for exact percentiles ($p50, p90, p95, p99, p99.9$).
