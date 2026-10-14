using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FlashKV.Core.Storage;

namespace FlashKV.Server;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var config = ParseArguments(args);

        PrintBanner(config);

        var server = new TcpServer(config);
        await server.StartAsync();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[FlashKV] Ready to accept connections at {config.Host}:{config.Port}");
        Console.ResetColor();
        Console.WriteLine("[FlashKV] Press Ctrl+C to terminate cleanly.\n");

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine("\n[FlashKV] Shutdown requested. Flushing buffers and terminating...");
        };

        // Live metrics reporter task
        var metricsTask = Task.Run(async () =>
        {
            long lastOps = server.Store.TotalOperations;
            var sw = Stopwatch.StartNew();

            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2000, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                double elapsedSec = sw.Elapsed.TotalSeconds;
                sw.Restart();

                long currentOps = server.Store.TotalOperations;
                long deltaOps = currentOps - lastOps;
                lastOps = currentOps;
                double opsPerSec = deltaOps / Math.Max(0.001, elapsedSec);

                long memoryBytes = server.Store.GetTotalBytes();
                string memFormatted = FormatBytes(memoryBytes);

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($"Clients: {server.ActiveClients,-4} ");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write($"Keys: {server.Store.DbSize(),-6} ");
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.Write($"Mem: {memFormatted,-8} ");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Throughput: {opsPerSec,8:N0} ops/sec");
                Console.ResetColor();
            }
        });

        // Wait for cancellation
        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException) { }

        await server.DisposeAsync();
        try { await metricsTask; } catch { }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("[FlashKV] Server stopped safely. Goodbye!");
        Console.ResetColor();
    }

    private static void PrintBanner(ServerConfig config)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
  ______ _           _     _  ____      __
 |  ____| |         | |   | |/ /\ \    / /
 | |__  | | __ _ ___| |__ | ' /  \ \  / / 
 |  __| | |/ _` / __| '_ \|  <    \ \/ /  
 | |    | | (_| \__ \ | | | . \    \  /   
 |_|    |_|\__,_|___/_| |_|_|\_\    \/    
");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine(" High-Performance In-Memory Key-Value Store & Cache");
        Console.WriteLine(" .NET 8 • Zero-Copy RESP • Striped Concurrency • AOF Persistence");
        Console.WriteLine(" -----------------------------------------------------------------");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"   * Host & Port       : {config.Host}:{config.Port}");
        Console.WriteLine($"   * Persistence (AOF) : {(config.AofEnabled ? $"Enabled ({config.AofPath})" : "Disabled")}");
        Console.WriteLine($"   * Max Memory        : {(config.MaxMemoryBytes > 0 ? FormatBytes(config.MaxMemoryBytes) : "Unlimited")}");
        Console.WriteLine($"   * Eviction Policy   : {config.EvictionPolicy}");
        Console.WriteLine($"   * Lock Concurrency  : {config.StripeCount} Striped Segments");
        Console.WriteLine(" -----------------------------------------------------------------\n");
        Console.ResetColor();
    }

    private static ServerConfig ParseArguments(string[] args)
    {
        var config = new ServerConfig();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if ((arg == "-p" || arg == "--port") && i + 1 < args.Length && int.TryParse(args[i + 1], out int port))
            {
                config.Port = port;
                i++;
            }
            else if ((arg == "-h" || arg == "--host") && i + 1 < args.Length)
            {
                config.Host = args[i + 1];
                i++;
            }
            else if ((arg == "-aof" || arg == "--appendonly") && i + 1 < args.Length)
            {
                config.AofEnabled = args[i + 1].Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                                    args[i + 1].Equals("true", StringComparison.OrdinalIgnoreCase);
                i++;
            }
            else if (arg == "--aof-path" && i + 1 < args.Length)
            {
                config.AofPath = args[i + 1];
                i++;
            }
            else if (arg == "--maxmemory" && i + 1 < args.Length)
            {
                config.MaxMemoryBytes = ParseMemoryString(args[i + 1]);
                i++;
            }
            else if (arg == "--eviction" && i + 1 < args.Length && Enum.TryParse<EvictionPolicy>(args[i + 1], true, out var pol))
            {
                config.EvictionPolicy = pol;
                i++;
            }
        }

        return config;
    }

    private static long ParseMemoryString(string val)
    {
        val = val.Trim().ToUpperInvariant();
        long multiplier = 1;
        if (val.EndsWith("GB") || val.EndsWith("G"))
        {
            multiplier = 1024L * 1024 * 1024;
            val = val.TrimEnd('G', 'B');
        }
        else if (val.EndsWith("MB") || val.EndsWith("M"))
        {
            multiplier = 1024L * 1024;
            val = val.TrimEnd('M', 'B');
        }
        else if (val.EndsWith("KB") || val.EndsWith("K"))
        {
            multiplier = 1024L;
            val = val.TrimEnd('K', 'B');
        }

        if (long.TryParse(val, out long num))
        {
            return num * multiplier;
        }
        return 0;
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int idx = 0;
        double val = bytes;
        while (val >= 1024 && idx < suffixes.Length - 1)
        {
            val /= 1024;
            idx++;
        }
        return $"{val:0.##} {suffixes[idx]}";
    }
}
