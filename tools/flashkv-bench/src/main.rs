use std::io::{Read, Write};
use std::net::TcpStream;
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::{Duration, Instant};

#[derive(Clone, Debug)]
struct BenchConfig {
    host: String,
    port: u16,
    total_requests: usize,
    concurrency: usize,
    test_type: String,
    data_size: usize,
    pipeline: usize,
}

impl Default for BenchConfig {
    fn default() -> Self {
        Self {
            host: "127.0.0.1".to_string(),
            port: 6379,
            total_requests: 100_000,
            concurrency: 20,
            test_type: "mixed".to_string(),
            data_size: 64,
            pipeline: 1,
        }
    }
}

fn parse_args() -> BenchConfig {
    let mut config = BenchConfig::default();
    let args: Vec<String> = std::env::args().collect();
    let mut i = 1;

    while i < args.len() {
        match args[i].as_str() {
            "-h" | "--host" if i + 1 < args.len() => {
                config.host = args[i + 1].clone();
                i += 1;
            }
            "-p" | "--port" if i + 1 < args.len() => {
                if let Ok(p) = args[i + 1].parse() {
                    config.port = p;
                }
                i += 1;
            }
            "-n" | "--requests" if i + 1 < args.len() => {
                if let Ok(n) = args[i + 1].parse() {
                    config.total_requests = n;
                }
                i += 1;
            }
            "-c" | "--clients" if i + 1 < args.len() => {
                if let Ok(c) = args[i + 1].parse() {
                    config.concurrency = c;
                }
                i += 1;
            }
            "-t" | "--type" if i + 1 < args.len() => {
                config.test_type = args[i + 1].to_lowercase();
                i += 1;
            }
            "-d" | "--size" if i + 1 < args.len() => {
                if let Ok(d) = args[i + 1].parse() {
                    config.data_size = d;
                }
                i += 1;
            }
            "-P" | "--pipeline" if i + 1 < args.len() => {
                if let Ok(p) = args[i + 1].parse() {
                    config.pipeline = p;
                }
                i += 1;
            }
            "--help" => {
                print_help();
                std::process::exit(0);
            }
            _ => {}
        }
        i += 1;
    }

    config
}

fn print_help() {
    println!(
        r#"
flashkv-bench: Ultra-low latency benchmarking tool for FlashKV & Redis

USAGE:
    flashkv-bench [OPTIONS]

OPTIONS:
    -h, --host <IP>          Target server host (default: 127.0.0.1)
    -p, --port <PORT>        Target server port (default: 6379)
    -n, --requests <NUM>     Total number of requests (default: 100000)
    -c, --clients <NUM>      Number of parallel connections (default: 20)
    -t, --type <TYPE>        Workload: set, get, ping, mixed (default: mixed)
    -d, --size <BYTES>       Payload size in bytes (default: 64)
    -P, --pipeline <NUM>     Pipeline depth (default: 1)
        --help               Display this help message
"#
    );
}

fn encode_resp_command(args: &[&str]) -> Vec<u8> {
    let mut buf = Vec::with_capacity(128);
    buf.extend_from_slice(format!("*{}\r\n", args.len()).as_bytes());
    for arg in args {
        buf.extend_from_slice(format!("${}\r\n{}\r\n", arg.len(), arg).as_bytes());
    }
    buf
}

fn main() {
    let config = parse_args();

    println!("\x1b[1;36m");
    println!(
        r#"
  ______ _           _     _  ____      __     ____                  _     
 |  ____| |         | |   | |/ /\ \    / /    |  _ \                | |    
 | |__  | | __ _ ___| |__ | ' /  \ \  / /_____| |_) | ___ _ __   ___| |__  
 |  __| | |/ _` / __| '_ \|  <    \ \/ /______|  _ < / _ \ '_ \ / __| '_ \ 
 | |    | | (_| \__ \ | | | . \    \  /       | |_) |  __/ | | | (__| | | |
 |_|    |_|\__,_|___/_| |_|_|\_\    \/        |____/ \___|_| |_|\___|_| |_|
"#
    );
    println!("\x1b[0m");
    println!(" FlashKV Ultra-High-Throughput Benchmark (Rust Engine)");
    println!(" -------------------------------------------------------------");
    println!(" Server Address  : {}:{}", config.host, config.port);
    println!(" Total Requests  : {:}", config.total_requests);
    println!(" Concurrency     : {} parallel workers", config.concurrency);
    println!(" Workload Type   : {}", config.test_type);
    println!(" Payload Size    : {} bytes", config.data_size);
    println!(" Pipeline Depth  : {}", config.pipeline);
    println!(" -------------------------------------------------------------\n");

    let payload = "x".repeat(config.data_size);
    let requests_per_client = config.total_requests / config.concurrency;
    let actual_total = requests_per_client * config.concurrency;

    let completed_counter = Arc::new(AtomicUsize::new(0));
    let mut handles = Vec::with_capacity(config.concurrency);

    println!("[*] Warming up connections and starting test...");
    let wall_clock_start = Instant::now();

    for client_id in 0..config.concurrency {
        let cfg = config.clone();
        let payload_str = payload.clone();
        let counter = Arc::clone(&completed_counter);

        let handle = std::thread::spawn(move || {
            let addr = format!("{}:{}", cfg.host, cfg.port);
            let mut stream = match TcpStream::connect(&addr) {
                Ok(s) => s,
                Err(e) => {
                    eprintln!("Thread {} failed to connect to {}: {}", client_id, addr, e);
                    return Vec::new();
                }
            };

            let _ = stream.set_nodelay(true);
            let _ = stream.set_read_timeout(Some(Duration::from_secs(10)));
            let _ = stream.set_write_timeout(Some(Duration::from_secs(10)));

            let mut latencies_us = Vec::with_capacity(requests_per_client);
            let mut read_buf = [0u8; 4096];

            let mut i = 0;
            while i < requests_per_client {
                let batch = std::cmp::min(cfg.pipeline, requests_per_client - i);
                let mut batch_bytes = Vec::new();

                for k in 0..batch {
                    let req_idx = i + k;
                    let key = format!("bench:{}:{}", client_id, req_idx % 2000);
                    let cmd = match cfg.test_type.as_str() {
                        "ping" => encode_resp_command(&["PING"]),
                        "get" => encode_resp_command(&["GET", &key]),
                        "set" => encode_resp_command(&["SET", &key, &payload_str]),
                        _ => {
                            if req_idx % 2 == 0 {
                                encode_resp_command(&["SET", &key, &payload_str])
                            } else {
                                encode_resp_command(&["GET", &key])
                            }
                        }
                    };
                    batch_bytes.extend_from_slice(&cmd);
                }

                let t0 = Instant::now();
                if stream.write_all(&batch_bytes).is_err() {
                    break;
                }

                // Read responses for the batch
                let mut received_responses = 0;
                while received_responses < batch {
                    match stream.read(&mut read_buf) {
                        Ok(0) => break, // EOF
                        Ok(n) => {
                            // Count CR LF delimiters in response stream
                            for window in read_buf[..n].windows(2) {
                                if window == b"\r\n" {
                                    received_responses += 1;
                                    if received_responses >= batch {
                                        break;
                                    }
                                }
                            }
                        }
                        Err(_) => break,
                    }
                }

                let elapsed_us = t0.elapsed().as_micros() as u64;
                for _ in 0..batch {
                    latencies_us.push(elapsed_us / batch as u64);
                }

                counter.fetch_add(batch, Ordering::Relaxed);
                i += batch;
            }

            latencies_us
        });

        handles.push(handle);
    }

    // Wait for all workers to finish and collect metrics
    let mut all_latencies = Vec::with_capacity(actual_total);
    for h in handles {
        if let Ok(lats) = h.join() {
            all_latencies.extend(lats);
        }
    }

    let total_duration = wall_clock_start.elapsed();
    let total_secs = total_duration.as_secs_f64();
    let actual_completed = all_latencies.len();
    let rps = actual_completed as f64 / total_secs;

    if all_latencies.is_empty() {
        eprintln!("\x1b[1;31mError: No successful requests recorded. Is FlashKV running?\x1b[0m");
        return;
    }

    all_latencies.sort_unstable();

    let p50 = all_latencies[actual_completed * 50 / 100];
    let p90 = all_latencies[actual_completed * 90 / 100];
    let p95 = all_latencies[actual_completed * 95 / 100];
    let p99 = all_latencies[actual_completed * 99 / 100];
    let p999 = all_latencies[actual_completed * 999 / 1000];
    let min = all_latencies[0];
    let max = all_latencies[actual_completed - 1];
    let avg = all_latencies.iter().sum::<u64>() as f64 / actual_completed as f64;

    println!("\x1b[1;32m");
    println!("==================== BENCHMARK RESULTS ====================");
    println!("\x1b[0m");
    println!(" Completed Requests : {:>12}", actual_completed);
    println!(" Total Time Elapsed : {:>12.3} s", total_secs);
    println!(
        " \x1b[1;33mThroughput         : {:>12.2} req/sec\x1b[0m",
        rps
    );
    println!(" ----------------------------------------------------------");
    println!(" Latency Distribution (microseconds / ms):");
    println!(
        "   Min Latency      : {:>10} us ({:.2} ms)",
        min,
        min as f64 / 1000.0
    );
    println!(
        "   Average Latency  : {:>10.1} us ({:.2} ms)",
        avg,
        avg / 1000.0
    );
    println!(
        "   p50 (Median)     : {:>10} us ({:.2} ms)",
        p50,
        p50 as f64 / 1000.0
    );
    println!(
        "   p90 Percentile   : {:>10} us ({:.2} ms)",
        p90,
        p90 as f64 / 1000.0
    );
    println!(
        "   p95 Percentile   : {:>10} us ({:.2} ms)",
        p95,
        p95 as f64 / 1000.0
    );
    println!(
        "   p99 Percentile   : {:>10} us ({:.2} ms)",
        p99,
        p99 as f64 / 1000.0
    );
    println!(
        "   p99.9 Percentile : {:>10} us ({:.2} ms)",
        p999,
        p999 as f64 / 1000.0
    );
    println!(
        "   Max Latency      : {:>10} us ({:.2} ms)",
        max,
        max as f64 / 1000.0
    );
    println!(" ----------------------------------------------------------");

    // Cumulative distribution table
    println!(" Cumulative Distribution:");
    let thresholds_us = [200, 500, 1000, 2000, 5000, 10000];
    for &thresh in &thresholds_us {
        let count = all_latencies.partition_point(|&x| x <= thresh);
        let pct = (count as f64 / actual_completed as f64) * 100.0;
        println!(
            "   <= {:>5} us ({:>4.1} ms) : {:>6.2}%",
            thresh,
            thresh as f64 / 1000.0,
            pct
        );
    }
    println!("===========================================================\n");
}
