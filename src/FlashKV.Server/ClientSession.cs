using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FlashKV.Core.Commands;
using FlashKV.Core.PubSub;
using FlashKV.Core.Resp;
using FlashKV.Core.Storage;

namespace FlashKV.Server;

/// <summary>
/// Handles communication with a connected client socket using System.IO.Pipelines.
/// Supports high-throughput pipelined RESP execution and asynchronous pub/sub push.
/// </summary>
public sealed class ClientSession : IAsyncDisposable
{
    private static long _nextSessionId;
    public long SessionId { get; } = Interlocked.Increment(ref _nextSessionId);

    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly CommandContext _context;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _isDisposed;

    public ClientSession(Socket socket, DataStore store, PubSubBroker pubSub, AofEngine aof)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: false);
        _reader = PipeReader.Create(_stream);
        _writer = PipeWriter.Create(_stream);

        _context = new CommandContext(
            store,
            pubSub,
            aof,
            SessionId,
            WritePushMessageDirect);
    }

    private void WritePushMessageDirect(RespValue msg)
    {
        if (_isDisposed) return;
        Task.Run(async () =>
        {
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                RespWriter.Write(_writer, msg);
                await _writer.FlushAsync().ConfigureAwait(false);
            }
            catch
            {
                // Client probably disconnected
            }
            finally
            {
                _writeLock.Release();
            }
        });
    }

    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ReadResult result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;

                if (buffer.IsEmpty && result.IsCompleted)
                {
                    break; // Connection closed gracefully by client
                }

                bool hasCommands = false;
                await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    while (RespParser.TryParse(ref buffer, out RespValue command))
                    {
                        hasCommands = true;
                        RespValue response = CommandDispatcher.Execute(command, _context);

                        // If response is null (e.g. SUBSCRIBE pushes its own messages), skip writing default response
                        if (response.Type != RespType.Null)
                        {
                            RespWriter.Write(_writer, response);
                        }
                    }

                    if (hasCommands)
                    {
                        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _writeLock.Release();
                }

                _reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Server shutting down
        }
        catch (Exception)
        {
            // Client socket reset or network error
        }
        finally
        {
            _context.PubSub.UnsubscribeAll(SessionId);
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            await _reader.CompleteAsync().ConfigureAwait(false);
            await _writer.CompleteAsync().ConfigureAwait(false);
            await _stream.DisposeAsync().ConfigureAwait(false);
            _socket.Dispose();
        }
        catch { }
        finally
        {
            _writeLock.Dispose();
        }
    }
}
