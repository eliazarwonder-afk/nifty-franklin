using System;
using System.Buffers;
using System.Diagnostics;
using System.Text;
using FlashKV.Core.Resp;
using FlashKV.Core.Storage;

namespace FlashKV.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine("=== FlashKV Micro-Benchmarks (In-Memory Core) ===");

        BenchmarkRespParsing();
        BenchmarkStorageOperations();
    }

    private static void BenchmarkRespParsing()
    {
        byte[] payload = "*3\r\n$3\r\nSET\r\n$11\r\nuser:session\r\n$16\r\n9f8a3bc214d05e67\r\n"u8.ToArray();
        const int iterations = 1_000_000;

        Console.Write($"Benchmarking RESP parser ({iterations:N0} iterations)... ");
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < iterations; i++)
        {
            var seq = new ReadOnlySequence<byte>(payload);
            RespParser.TryParse(ref seq, out _);
        }

        sw.Stop();
        double opsPerSec = iterations / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"{opsPerSec:N0} parses/sec (Elapsed: {sw.ElapsedMilliseconds} ms)");
    }

    private static void BenchmarkStorageOperations()
    {
        var store = new DataStore(stripeCount: 64);
        byte[] val = Encoding.UTF8.GetBytes("standard_benchmark_payload_value_64_bytes_long_string_data_here");
        const int iterations = 1_000_000;

        Console.Write($"Benchmarking DataStore SET ({iterations:N0} iterations)... ");
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < iterations; i++)
        {
            string key = $"key:{i & 0x3FFF}";
            store.Set(key, val);
        }

        sw.Stop();
        double setOps = iterations / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"{setOps:N0} sets/sec (Elapsed: {sw.ElapsedMilliseconds} ms)");

        Console.Write($"Benchmarking DataStore GET ({iterations:N0} iterations)... ");
        sw.Restart();

        for (int i = 0; i < iterations; i++)
        {
            string key = $"key:{i & 0x3FFF}";
            store.Get(key);
        }

        sw.Stop();
        double getOps = iterations / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"{getOps:N0} gets/sec (Elapsed: {sw.ElapsedMilliseconds} ms)");
    }
}
