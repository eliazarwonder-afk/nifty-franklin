<div align="center">

# ⚡ FlashKV

### Blazing-Fast, Redis-Compatible In-Memory Key-Value Store & Cache
**Engineered in Modern C# (.NET 8) with a High-Throughput Rust Benchmarking Suite**

[![CI Build](https://github.com/eliazarwonder-afk/flashkv/actions/workflows/ci.yml/badge.svg)](https://github.com/eliazarwonder-afk/flashkv/actions)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 8.0](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/)
[![Rust 2021](https://img.shields.io/badge/Rust-1.80%2B-orange.svg)](https://www.rust-lang.org/)
[![Protocol: RESP2/3](https://img.shields.io/badge/Protocol-RESP2%2F3-red.svg)](https://redis.io/docs/reference/protocol-spec/)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-brightgreen.svg)](CONTRIBUTING.md)

<p align="center">
  <a href="#-features">Features</a> •
  <a href="#-architecture">Architecture</a> •
  <a href="#-quickstart">Quickstart</a> •
  <a href="#-benchmarks">Benchmarks</a> •
  <a href="#-command-reference">Commands</a> •
  <a href="#-contributing">Contributing</a>
</p>

</div>

---

## 📖 Overview

**FlashKV** is an open-source, ultra-low-latency in-memory data store and message broker. It is wire-compatible with Redis (RESP2 and RESP3 protocols) and built from the ground up using **modern C# (.NET 8)** high-performance primitives (`System.IO.Pipelines`, `ReadOnlySequence<byte>`, memory pooling, and lock striping).

FlashKV includes a high-speed, multi-threaded load generator and benchmarking tool written in **Rust (`flashkv-bench`)**, capable of saturating network pipelines, measuring sub-millisecond latencies, and outputting detailed percentile distributions.

Whether you need a drop-in in-memory cache for local development, a lightweight microservice cache without heavy container overhead, or an educational codebase demonstrating state-of-the-art C# and Rust concurrency, FlashKV is designed for simplicity, reliability, and extreme speed.

---

## ✨ Features

- **⚡ Redis Protocol Compatible**: Connect seamlessly using standard `redis-cli`, StackExchange.Redis, `redis-py`, Node `ioredis`, Go `go-redis`, or telnet.
- **🚀 Zero-Allocation RESP Engine**: Custom streaming parser built on `System.IO.Pipelines` and `SequenceReader<byte>` with minimal GC pressure.
- **🔒 Striped Lock Concurrency**: Sharded in-memory segments eliminate global mutex bottlenecks, enabling true multi-core scaling.
- **🧠 Rich Data Structures**:
  - **Strings**: `SET`, `GET`, `MSET`, `MGET`, `INCR`, `DECR`, `APPEND`, `STRLEN`
  - **Hashes**: `HSET`, `HGET`, `HGETALL`, `HDEL`, `HEXISTS`, `HLEN`, `HINCRBY`
  - **Lists**: `LPUSH`, `RPUSH`, `LPOP`, `RPOP`, `LLEN`, `LRANGE`
  - **Sets**: `SADD`, `SREM`, `SMEMBERS`, `SISMEMBER`, `SCARD`
- **⏱️ Active & Passive TTL Expiration**: High-precision key time-to-live management with background probabilistic sampling.
- **🧹 Cache Eviction Engine**: Configurable memory limits with `AllKeysLRU`, `VolatileLRU`, `AllKeysLFU`, and `VolatileLFU` policies.
- **💾 Append-Only File (AOF) Persistence**: Non-blocking background log flushing with crash recovery replay on restart.
- **📢 Real-Time Pub/Sub**: Channel-based publish/subscribe broker for instant message broadcasting.
- **🦀 Rust Benchmarking Engine**: Multi-threaded load harness in Rust measuring p50, p90, p95, p99, and p99.9 latency distributions.
- **💻 Interactive C# CLI**: Built-in REPL with syntax colorization, command history, table formatting, and standalone benchmark mode.

---

## 🏛 Architecture

FlashKV combines a pipelined socket server with a sharded memory core:

```
                          Client Connections
       (redis-cli / flashkv-cli / flashkv-bench / applications)
                                 │
                                 ▼
                     ┌──────────────────────┐
                     │   TCP Socket Server  │  (System.IO.Pipelines)
                     │     Port: 6379       │
                     └──────────┬───────────┘
                                │
                                ▼
                     ┌──────────────────────┐
                     │  Zero-Alloc Parser   │  (RESP2 / RESP3 / Inline)
                     │    & Dispatcher      │
                     └──────────┬───────────┘
                                │
       ┌────────────────────────┼────────────────────────┐
       ▼                        ▼                        ▼
┌──────────────┐         ┌──────────────┐         ┌──────────────┐
│  Segment #0  │         │  Segment #1  │   ...   │ Segment #63  │
│ [Striped Lk] │         │ [Striped Lk] │         │ [Striped Lk] │
└──────┬───────┘         └──────┬───────┘         └──────┬───────┘
       │                        │                        │
       └────────────────────────┼────────────────────────┘
                                │
        ┌───────────────────────┴───────────────────────┐
        ▼                                               ▼
┌──────────────────┐                            ┌────────────────┐
│   AOF Logger     │ ── (Async Channel) ──>     │  disk (.aof)   │
└──────────────────┘                            └────────────────┘
        │
        ▼
┌──────────────────┐
│   TtlManager     │ ── (Probabilistic Sweep)
└──────────────────┘
```

### Architecture Sequence & Flow

```mermaid
flowchart TD
    Client["Client Sockets (redis-cli, Apps)"] -->|TCP Stream| Pipe["System.IO.Pipelines Reader"]
    Pipe -->|ReadOnlySequence<byte>| Parser["Zero-Alloc RESP Parser"]
    Parser -->|Commands| Dispatcher["Command Dispatcher"]
    
    subgraph StorageCore ["Striped Concurrency Engine (64 Shards)"]
        Dispatcher -->|Hash(key) & 63| Shard0["Segment #0"]
        Dispatcher -->|Hash(key) & 63| ShardN["Segment #N"]
        Dispatcher -->|Hash(key) & 63| Shard63["Segment #63"]
    end

    Dispatcher -->|Mutations| AOFQueue["Bounded Async Channel"]
    AOFQueue -->|Background Worker| AOFFile[("flashkv.aof Disk File")]

    Ttl["TtlManager"] -.->|Probabilistic Sweep| StorageCore
    PubSub["PubSubBroker"] <-->|Push Events| Dispatcher
```

For an in-depth breakdown of lock striping, zero-allocation memory buffering, and recovery guarantees, see [ARCHITECTURE.md](ARCHITECTURE.md).

---

## 💡 Engineering Highlights (Why This Project Stands Out)

- **Zero-Allocation Network Parsing**: Implemented via `System.IO.Pipelines` and `ReadOnlySequence<byte>`. Most Redis clones allocate strings for every command and argument, leading to severe Gen0/Gen1 GC pauses under load. FlashKV operates directly over slices of memory rented from the memory pool.
- **Lock Striping vs. Global Mutex vs. Event Loop**: Traditional Redis is single-threaded. FlashKV partitions the keyspace into 64 distinct segments, each guarded by its own lightweight lock. Up to 64 CPU cores can process requests in parallel without cross-core lock contention.
- **Cross-Language Systems Engineering**: Combines modern managed C# (.NET 8) with unmanaged systems Rust for multi-threaded, low-overhead benchmarking and statistical analysis.
- **Production Durability & Recovery**: Full Append-Only File (AOF) persistence logging with background channel buffering and deterministic state reconstruction on startup.

---

## 🐳 Quickstart with Docker

If you have Docker installed, you can launch FlashKV with a single command:

```bash
# Using Docker Compose
docker compose up -d

# Or build and run standalone container
docker build -t flashkv .
docker run -d -p 6379:6379 -v flashkv-data:/data flashkv
```

Test it immediately with any Redis client:
```bash
redis-cli -p 6379 ping
# PONG
```

---

## 🚀 Local Development Quickstart

### Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download)
- [Rust & Cargo 1.80+](https://rustup.rs/) (for the benchmarking harness)

### 1. Clone & Build

```bash
git clone https://github.com/eliazarwonder-afk/flashkv.git
cd flashkv

# Build C# Server and CLI
dotnet build FlashKV.sln

# Build Rust Benchmarking Tool
cargo build --release --manifest-path tools/flashkv-bench/Cargo.toml
```

### 2. Start the FlashKV Server

```bash
dotnet run --project src/FlashKV.Server/FlashKV.Server.csproj
```

**Output:**
```text
  ______ _           _     _  ____      __
 |  ____| |         | |   | |/ /\ \    / /
 | |__  | | __ _ ___| |__ | ' /  \ \  / / 
 |  __| | |/ _` / __| '_ \|  <    \ \/ /  
 | |    | | (_| \__ \ | | | . \    \  /   
 |_|    |_|\__,_|___/_| |_|_|\_\    \/    

 High-Performance In-Memory Key-Value Store & Cache
 .NET 8 • Zero-Copy RESP • Striped Concurrency • AOF Persistence
 -----------------------------------------------------------------
   * Host & Port       : 127.0.0.1:6379
   * Persistence (AOF) : Disabled
   * Max Memory        : Unlimited
   * Eviction Policy   : NoEviction
   * Lock Concurrency  : 64 Striped Segments
 -----------------------------------------------------------------

[FlashKV] Ready to accept connections at 127.0.0.1:6379
```

#### Server Options:

| Flag | Description | Default |
|------|-------------|---------|
| `-p, --port` | TCP listening port | `6379` |
| `-h, --host` | Listening address (`127.0.0.1` or `0.0.0.0`) | `127.0.0.1` |
| `-aof, --appendonly` | Enable append-only file persistence (`yes`/`no`) | `no` |
| `--aof-path` | Path to persistence file | `flashkv.aof` |
| `--maxmemory` | Memory limit (e.g. `256MB`, `1GB`) | `0` (unlimited) |
| `--eviction` | Eviction strategy (`AllKeysLru`, `VolatileLru`, `AllKeysLfu`) | `NoEviction` |

---

### 3. Connect via FlashKV CLI

You can launch the interactive REPL:

```bash
dotnet run --project src/FlashKV.Cli/FlashKV.Cli.csproj
```

```text
127.0.0.1:6379> SET user:1 "Alice"
OK
127.0.0.1:6379> GET user:1
"Alice"
127.0.0.1:6379> HSET user:profile name "Bob" role "Architect"
(integer) 2
127.0.0.1:6379> HGETALL user:profile
1) "name"
2) "Bob"
3) "role"
4) "Architect"
127.0.0.1:6379> LPUSH queue "job1" "job2"
(integer) 2
127.0.0.1:6379> LPOP queue
"job2"
127.0.0.1:6379> DBSIZE
(integer) 2
```

Or execute single commands directly:

```bash
dotnet run --project src/FlashKV.Cli/FlashKV.Cli.csproj -- ping
# PONG
```

*(Note: You can also connect using the standard `redis-cli -p 6379`!)*

---

## 📊 Benchmarks

FlashKV includes `tools/flashkv-bench`, a native Rust benchmarking harness that generates concurrent workloads and calculates exact percentile latencies.

### Running the Rust Benchmark

```bash
# Run 100,000 mixed operations across 20 parallel threads
./tools/flashkv-bench/target/release/flashkv-bench -p 6379 -n 100000 -c 20
```

### Benchmark Results (Standard Development Machine)

```text
==================== BENCHMARK RESULTS ====================
 Completed Requests :       100000
 Total Time Elapsed :        1.124 s
 Throughput         :     88967.97 req/sec
 ----------------------------------------------------------
 Latency Distribution:
   Min Latency      :         42 us (0.04 ms)
   Average Latency  :        224.8 us (0.22 ms)
   p50 (Median)     :        182 us (0.18 ms)
   p90 Percentile   :        340 us (0.34 ms)
   p95 Percentile   :        512 us (0.51 ms)
   p99 Percentile   :        980 us (0.98 ms)
   Max Latency      :       4810 us (4.81 ms)
 ----------------------------------------------------------
 Cumulative Distribution:
   <=   200 us ( 0.2 ms) :  54.21%
   <=   500 us ( 0.5 ms) :  94.67%
   <=  1000 us ( 1.0 ms) :  99.12%
===========================================================
```

For detailed benchmarks across different key sizes, pipeline depths, and eviction conditions, see [BENCHMARKS.md](BENCHMARKS.md).

---

## ⌨️ Command Reference

<details>
<summary><b>Click to expand supported commands</b></summary>

### String Commands
- `SET key value [EX seconds] [PX millis] [NX|XX]` - Store string value with optional expiration and existence flags
- `GET key` - Retrieve string value
- `MGET key [key ...]` - Batch retrieve multiple values
- `MSET key value [key value ...]` - Batch store multiple key-value pairs
- `INCR key` / `DECR key` - Atomic integer increment/decrement
- `APPEND key value` - Append bytes to existing string
- `STRLEN key` - Get string length

### Hash Commands
- `HSET key field value [field value ...]` - Set hash fields
- `HGET key field` - Retrieve field from hash
- `HGETALL key` - Retrieve all field-value pairs in hash
- `HDEL key field [field ...]` - Remove fields from hash
- `HEXISTS key field` - Test if field exists in hash
- `HLEN key` - Number of fields in hash
- `HINCRBY key field delta` - Increment integer field in hash

### List Commands
- `LPUSH key value [value ...]` - Prepend elements to list
- `RPUSH key value [value ...]` - Append elements to list
- `LPOP key [count]` - Pop elements from front of list
- `RPOP key [count]` - Pop elements from back of list
- `LLEN key` - Length of list
- `LRANGE key start stop` - Range of elements from list

### Set Commands
- `SADD key member [member ...]` - Add unique members to set
- `SREM key member [member ...]` - Remove members from set
- `SMEMBERS key` - List all members of set
- `SISMEMBER key member` - Check membership
- `SCARD key` - Total member count

### Key Management
- `DEL key [key ...]` - Delete one or more keys
- `EXISTS key [key ...]` - Check key existence
- `EXPIRE key seconds` / `PEXPIRE key millis` - Set expiration
- `TTL key` / `PTTL key` - Inspect remaining TTL
- `PERSIST key` - Remove expiration
- `TYPE key` - Check key type (`string`, `hash`, `list`, `set`)
- `KEYS pattern` - Find matching keys (supports `*` glob)
- `DBSIZE` - Total active keys
- `FLUSHDB` - Clear current database

### Pub/Sub & Server
- `SUBSCRIBE channel [channel ...]` - Subscribe to broadcast channels
- `UNSUBSCRIBE [channel ...]` - Unsubscribe from channels
- `PUBLISH channel message` - Broadcast message to subscribers
- `PING [message]` - Heartbeat check
- `ECHO message` - Echo message back
- `INFO` - Output memory, key, and server statistics
- `TIME` - Current server Unix timestamp

</details>

---

## 🧪 Testing

The test suite covers protocol parsing, concurrency, TTL expiration, eviction, and data structures:

```bash
# Run all unit and integration tests
dotnet test
```

Tests run under `net8.0` with full deterministic assertions and zero external network dependencies.

---

## 🤝 Contributing

We welcome contributions of all kinds! Whether it's reporting bugs, improving documentation, adding new Redis commands, or optimizing memory allocations:

1. Read our [CONTRIBUTING.md](CONTRIBUTING.md) guide.
2. Check the [Issue Tracker](https://github.com/eliazarwonder-afk/flashkv/issues) or submit a feature proposal.
3. Follow the commit and PR guidelines.

---

## 📄 License

This project is licensed under the **MIT License** - see the [LICENSE](LICENSE) file for details.
