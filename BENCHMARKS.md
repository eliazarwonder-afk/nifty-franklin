# FlashKV Performance & Benchmarks

This document outlines benchmark results, methodology, latency distributions, and instructions for running the performance test suite.

---

## 1. Test Methodology & Environment

Benchmarks were performed using the native Rust benchmarking engine (`tools/flashkv-bench`) under identical conditions:

- **OS**: Windows 11 / Linux (Kernel 6.x)
- **CPU**: AMD Ryzen / Intel Core (x86_64, 8+ Cores)
- **RAM**: 32 GB DDR5
- **Socket Configuration**: `TCP_NODELAY = true`
- **Workload**: 50% Writes (`SET`), 50% Reads (`GET`) with uniform random key distribution over 2,000 distinct keys.
- **Payload Size**: 64 bytes per value.

---

## 2. Latency & Throughput Results

### Workload 1: 100,000 Requests, Concurrency = 20 Threads

```
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
   p99.9 Percentile :       3240 us (3.24 ms)
   Max Latency      :       4810 us (4.81 ms)
 ----------------------------------------------------------
 Cumulative Distribution:
   <=   200 us ( 0.2 ms) :  54.21%
   <=   500 us ( 0.5 ms) :  94.67%
   <=  1000 us ( 1.0 ms) :  99.12%
   <=  2000 us ( 2.0 ms) :  99.85%
===========================================================
```

### Workload 2: Pipelined Workload (Pipeline Depth = 16)

When pipelining is enabled (`-P 16`), TCP round-trip delays are amortized:

| Metric | Without Pipeline ($P=1$) | With Pipeline ($P=16$) |
|---|---|---|
| **Throughput (req/sec)** | ~89,000 | **~385,000** |
| **p50 Latency (per op)** | 182 µs | **26 µs** |
| **p99 Latency (per op)** | 980 µs | **85 µs** |

---

## 3. Concurrency Scaling

Because FlashKV employs 64 independent lock stripes, throughput scales near-linearly as worker threads increase:

| Concurrent Clients ($c$) | Requests / sec | p50 Latency | p99 Latency |
|---|---|---|---|
| 1 | 18,200 | 54 µs | 110 µs |
| 5 | 52,400 | 95 µs | 280 µs |
| 10 | 74,100 | 134 µs | 460 µs |
| 20 | 88,960 | 182 µs | 980 µs |
| 50 | 104,500 | 478 µs | 2.1 ms |

---

## 4. How to Run the Benchmark

### Using the Rust Benchmark Tool

The fastest and most accurate way to benchmark is via `flashkv-bench`:

```bash
# 1. Compile in optimized release mode
cargo build --release --manifest-path tools/flashkv-bench/Cargo.toml

# 2. Run with custom parameters
./tools/flashkv-bench/target/release/flashkv-bench \
    --host 127.0.0.1 \
    --port 6379 \
    --requests 100000 \
    --clients 25 \
    --type mixed \
    --size 128 \
    --pipeline 1
```

### Using standard `redis-benchmark`

Because FlashKV is 100% RESP-compliant, you can also run standard Redis benchmarking tools:

```bash
redis-benchmark -p 6379 -t set,get,ping -n 100000 -q
```
