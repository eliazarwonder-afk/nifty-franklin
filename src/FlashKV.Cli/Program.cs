using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FlashKV.Core.Resp;

namespace FlashKV.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string host = "127.0.0.1";
        int port = 6379;
        bool benchmarkMode = false;
        int benchRequests = 50_000;
        int benchClients = 10;
        var directCommand = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if ((arg == "-h" || arg == "--host") && i + 1 < args.Length)
            {
                host = args[++i];
            }
            else if ((arg == "-p" || arg == "--port") && i + 1 < args.Length && int.TryParse(args[i + 1], out int p))
            {
                port = p;
                i++;
            }
            else if (arg == "--bench" || arg == "--benchmark")
            {
                benchmarkMode = true;
            }
            else if (arg == "-n" && i + 1 < args.Length && int.TryParse(args[i + 1], out int n))
            {
                benchRequests = n;
                i++;
            }
            else if (arg == "-c" && i + 1 < args.Length && int.TryParse(args[i + 1], out int c))
            {
                benchClients = c;
                i++;
            }
            else
            {
                directCommand.Add(arg);
            }
        }

        if (benchmarkMode)
        {
            await RunBenchmarkAsync(host, port, benchRequests, benchClients);
            return 0;
        }

        if (directCommand.Count > 0)
        {
            return await ExecuteSingleCommandAsync(host, port, directCommand);
        }

        await RunReplAsync(host, port);
        return 0;
    }

    private static async Task<int> ExecuteSingleCommandAsync(string host, int port, List<string> cmdArgs)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(host, port);
            using var stream = new NetworkStream(socket);

            var respCmd = FormatCommand(cmdArgs);
            byte[] cmdBytes = RespWriter.ToBytes(respCmd);
            await stream.WriteAsync(cmdBytes);

            var reader = PipeReader.Create(stream);
            var result = await reader.ReadAsync();
            var buffer = result.Buffer;

            if (RespParser.TryParse(ref buffer, out RespValue reply))
            {
                PrintRespValue(reply);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    private static async Task RunReplAsync(string host, int port)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
 FlashKV Interactive CLI v1.0.0
 Type 'help' for command reference or 'quit' to exit.
");
        Console.ResetColor();

        Socket? socket = null;
        NetworkStream? stream = null;
        PipeReader? reader = null;

        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(host, port);
            stream = new NetworkStream(socket);
            reader = PipeReader.Create(stream);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Connected to FlashKV at {host}:{port}\n");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Could not connect to FlashKV at {host}:{port} - {ex.Message}");
            Console.ResetColor();
            return;
        }

        while (true)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write($"{host}:{port}> ");
            Console.ResetColor();

            string? line = Console.ReadLine();
            if (line == null) break;

            line = line.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            if (line.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (line.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                Console.Clear();
                continue;
            }

            if (line.Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                PrintHelp();
                continue;
            }

            string[] tokens = SplitArguments(line);
            if (tokens.Length == 0) continue;

            var cmd = FormatCommand(tokens);
            byte[] bytes = RespWriter.ToBytes(cmd);

            try
            {
                await stream.WriteAsync(bytes);

                while (true)
                {
                    var readResult = await reader.ReadAsync();
                    var buffer = readResult.Buffer;

                    if (RespParser.TryParse(ref buffer, out RespValue reply))
                    {
                        PrintRespValue(reply);
                        reader.AdvanceTo(buffer.Start, buffer.End);
                        break;
                    }

                    reader.AdvanceTo(buffer.Start, buffer.End);

                    if (readResult.IsCompleted)
                    {
                        Console.WriteLine("Connection closed by server.");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"(error) Connection error: {ex.Message}");
                Console.ResetColor();
                break;
            }
        }

        socket?.Dispose();
    }

    private static void PrintHelp()
    {
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine("\nSupported Command Groups:");
        Console.ResetColor();
        Console.WriteLine("  Strings:  SET key value [EX s] [PX ms] [NX|XX], GET key, MGET, MSET, INCR, DECR, APPEND, STRLEN");
        Console.WriteLine("  Hashes:   HSET key field val, HGET key field, HGETALL key, HDEL, HEXISTS, HLEN, HINCRBY");
        Console.WriteLine("  Lists:    LPUSH key val, RPUSH key val, LPOP key [count], RPOP, LLEN, LRANGE key start stop");
        Console.WriteLine("  Sets:     SADD key member, SREM, SMEMBERS key, SISMEMBER key member, SCARD");
        Console.WriteLine("  Keys:     DEL key, EXISTS key, EXPIRE key s, TTL key, PTTL, PERSIST, KEYS pattern, DBSIZE, FLUSHDB");
        Console.WriteLine("  Pub/Sub:  SUBSCRIBE channel, PUBLISH channel message, UNSUBSCRIBE");
        Console.WriteLine("  Server:   PING [msg], ECHO msg, INFO, TIME, QUIT\n");
    }

    private static void PrintRespValue(RespValue val, string indent = "")
    {
        switch (val.Type)
        {
            case RespType.SimpleString:
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"{indent}{val.StringValue}");
                Console.ResetColor();
                break;

            case RespType.Error:
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"{indent}(error) {val.StringValue}");
                Console.ResetColor();
                break;

            case RespType.Integer:
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"{indent}(integer) {val.IntegerValue}");
                Console.ResetColor();
                break;

            case RespType.BulkString:
                if (val.IsNull)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"{indent}(nil)");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"{indent}\"{val.AsString()}\"");
                    Console.ResetColor();
                }
                break;

            case RespType.Array:
                if (val.ArrayValue == null)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"{indent}(empty array)");
                    Console.ResetColor();
                }
                else if (val.ArrayValue.Count == 0)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"{indent}(empty list or set)");
                    Console.ResetColor();
                }
                else
                {
                    for (int i = 0; i < val.ArrayValue.Count; i++)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write($"{indent}{i + 1}) ");
                        Console.ResetColor();
                        PrintRespValue(val.ArrayValue[i]);
                    }
                }
                break;
        }
    }

    private static async Task RunBenchmarkAsync(string host, int port, int totalRequests, int concurrency)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n--- FlashKV Built-In Benchmark ---");
        Console.WriteLine($"Host: {host}:{port} | Requests: {totalRequests:N0} | Concurrency: {concurrency}");
        Console.ResetColor();

        int requestsPerClient = totalRequests / concurrency;
        var sw = Stopwatch.StartNew();
        int completedCount = 0;

        var tasks = new Task[concurrency];
        for (int c = 0; c < concurrency; c++)
        {
            int clientId = c;
            tasks[c] = Task.Run(async () =>
            {
                using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                sock.NoDelay = true;
                await sock.ConnectAsync(host, port);
                using var strm = new NetworkStream(sock);
                var rdr = PipeReader.Create(strm);

                for (int req = 0; req < requestsPerClient; req++)
                {
                    // Alternate SET and GET
                    string key = $"bench:key:{clientId}:{req % 1000}";
                    string val = $"val_{req}";

                    var cmd = (req % 2 == 0)
                        ? FormatCommand(new[] { "SET", key, val })
                        : FormatCommand(new[] { "GET", key });

                    byte[] b = RespWriter.ToBytes(cmd);
                    await strm.WriteAsync(b);

                    var readRes = await rdr.ReadAsync();
                    var buf = readRes.Buffer;
                    while (RespParser.TryParse(ref buf, out _)) { }
                    rdr.AdvanceTo(buf.Start, buf.End);

                    Interlocked.Increment(ref completedCount);
                }
            });
        }

        await Task.WhenAll(tasks);
        sw.Stop();

        double sec = sw.Elapsed.TotalSeconds;
        double rps = completedCount / sec;

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\nCompleted {completedCount:N0} requests in {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"Throughput: {rps:N2} requests per second");
        Console.WriteLine($"Avg Latency: {(sw.ElapsedMilliseconds / (double)completedCount) * 1000:N2} us\n");
        Console.ResetColor();
    }

    private static RespValue FormatCommand(IEnumerable<string> tokens)
    {
        var list = new List<RespValue>();
        foreach (var t in tokens)
        {
            list.Add(RespValue.BulkString(t));
        }
        return RespValue.Array(list);
    }

    private static string[] SplitArguments(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result.ToArray();
    }
}
