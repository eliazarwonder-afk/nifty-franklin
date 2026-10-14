using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FlashKV.Core.Commands;
using FlashKV.Core.PubSub;
using FlashKV.Core.Storage;

namespace FlashKV.Server;

/// <summary>
/// High-concurrency TCP socket server hosting the FlashKV engine.
/// </summary>
public sealed class TcpServer : IAsyncDisposable
{
    private readonly ServerConfig _config;
    private readonly DataStore _store;
    private readonly PubSubBroker _pubSub;
    private readonly AofEngine _aof;
    private readonly TtlManager _ttlManager;
    private readonly Socket _listener;
    private readonly ConcurrentDictionary<long, ClientSession> _sessions = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptTask;
    private int _activeClients;
    private long _totalConnections;

    public int ActiveClients => Volatile.Read(ref _activeClients);
    public long TotalConnections => Volatile.Read(ref _totalConnections);
    public DataStore Store => _store;
    public ServerConfig Config => _config;

    public TcpServer(ServerConfig config)
    {
        _config = config;
        _store = new DataStore(config.MaxMemoryBytes, config.EvictionPolicy, config.StripeCount);
        _pubSub = new PubSubBroker();
        _aof = new AofEngine(config.AofPath, config.AofEnabled);
        _ttlManager = new TtlManager(_store, config.TtlIntervalMs);

        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    }

    public async Task StartAsync()
    {
        // Replay AOF if enabled and file exists
        if (_config.AofEnabled)
        {
            Console.WriteLine($"[FlashKV] Replaying persistence log: {_config.AofPath}...");
            var replayCtx = new CommandContext(_store, _pubSub, _aof, -1, _ => { });
            await AofEngine.ReplayAsync(_config.AofPath, cmd =>
            {
                CommandDispatcher.Execute(cmd, replayCtx);
                return Task.CompletedTask;
            });
            Console.WriteLine($"[FlashKV] Replay complete. Restored {_store.DbSize()} keys.");
        }

        var ip = _config.Host == "0.0.0.0" || _config.Host == "*" ? IPAddress.Any : IPAddress.Parse(_config.Host);
        _listener.Bind(new IPEndPoint(ip, _config.Port));
        _listener.Listen(1024);

        _acceptTask = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        var token = _cts.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                Socket clientSocket = await _listener.AcceptAsync(token).ConfigureAwait(false);
                clientSocket.NoDelay = true; // Disable Nagle's algorithm for low-latency command/response

                Interlocked.Increment(ref _activeClients);
                Interlocked.Increment(ref _totalConnections);

                var session = new ClientSession(clientSocket, _store, _pubSub, _aof);
                _sessions[session.SessionId] = session;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await session.ProcessAsync(token).ConfigureAwait(false);
                    }
                    finally
                    {
                        _sessions.TryRemove(session.SessionId, out _);
                        Interlocked.Decrement(ref _activeClients);
                    }
                }, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                if (token.IsCancellationRequested) break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        try
        {
            _listener.Close();
        }
        catch { }

        if (_acceptTask != null)
        {
            try { await _acceptTask.ConfigureAwait(false); } catch { }
        }

        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        _sessions.Clear();

        _ttlManager.Dispose();
        await _aof.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
        _listener.Dispose();
    }
}
