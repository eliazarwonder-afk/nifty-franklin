# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [0.1.0] - 2026-10-14

### Initial Release

#### 🚀 Features
- **Zero-Allocation RESP Parser**: Complete RESP2 and RESP3 parser supporting inline commands, simple strings, errors, integers, bulk strings, and arrays via `System.IO.Pipelines`.
- **Striped Concurrency Engine**: 64-segment striped lock architecture eliminating global mutex bottlenecks on multi-core CPUs.
- **Rich Data Structures**:
  - Full String suite (`GET`, `SET`, `MGET`, `MSET`, `INCR`, `DECR`, `APPEND`, `STRLEN`).
  - Hash operations (`HSET`, `HGET`, `HGETALL`, `HDEL`, `HEXISTS`, `HLEN`, `HINCRBY`).
  - List operations (`LPUSH`, `RPUSH`, `LPOP`, `RPOP`, `LLEN`, `LRANGE`).
  - Set operations (`SADD`, `SREM`, `SMEMBERS`, `SISMEMBER`, `SCARD`).
- **TTL Management**: Millisecond-resolution expiration with both passive on-access eviction and active probabilistic background sampling.
- **Cache Eviction**: Configurable memory caps supporting `AllKeysLRU`, `VolatileLRU`, `AllKeysLFU`, and `VolatileLFU`.
- **Append-Only File (AOF)**: Non-blocking asynchronous persistence logging with automatic crash recovery replay on boot.
- **Pub/Sub Subsystem**: Thread-safe channel message distribution supporting `SUBSCRIBE`, `UNSUBSCRIBE`, and `PUBLISH`.
- **FlashKV CLI**: Full-featured interactive C# terminal REPL with color formatting and single-shot execution.
- **Rust Benchmarking Suite**: Multi-threaded load generator in Rust (`flashkv-bench`) measuring throughput and full latency percentiles (min, avg, p50, p90, p95, p99, p99.9, max).

#### 🧪 Tests
- 15 automated xUnit unit and integration tests covering parser behavior, concurrency, eviction, and data structures.
- End-to-end integration test validating TCP socket communication and benchmark execution.
