using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FlashKV.Core.Resp;

namespace FlashKV.Core.Storage;

/// <summary>
/// Append-Only File (AOF) persistence engine with asynchronous background flushing and crash recovery.
/// </summary>
public sealed class AofEngine : IAsyncDisposable
{
    private readonly string _filePath;
    private readonly Channel<byte[]> _writeChannel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task? _flushTask;
    private FileStream? _fileStream;
    private bool _isDisposed;

    public bool IsEnabled { get; }

    public AofEngine(string filePath, bool enabled = true, int bufferCapacity = 100_000)
    {
        _filePath = filePath;
        IsEnabled = enabled;

        if (IsEnabled)
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _fileStream = new FileStream(
                _filePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 64 * 1024,
                useAsync: true);

            _writeChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(bufferCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true
            });

            _flushTask = Task.Run(FlushLoopAsync);
        }
        else
        {
            _writeChannel = Channel.CreateBounded<byte[]>(1);
        }
    }

    /// <summary>
    /// Appends a mutating command to the AOF log queue.
    /// </summary>
    public void AppendCommand(RespValue command)
    {
        if (!IsEnabled || _isDisposed) return;

        byte[] bytes = RespWriter.ToBytes(command);
        _writeChannel.Writer.TryWrite(bytes);
    }

    private async Task FlushLoopAsync()
    {
        if (_fileStream == null) return;

        var reader = _writeChannel.Reader;
        var token = _cts.Token;

        try
        {
            while (!token.IsCancellationRequested && await reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (reader.TryRead(out byte[]? entry))
                {
                    await _fileStream.WriteAsync(entry, token).ConfigureAwait(false);
                }

                await _fileStream.FlushAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception)
        {
            // Handle or log AOF write failure
        }
    }

    /// <summary>
    /// Replays logged commands from disk to restore state on startup.
    /// </summary>
    public static async Task ReplayAsync(string filePath, Func<RespValue, Task> commandExecutor)
    {
        if (!File.Exists(filePath)) return;

        byte[] allBytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
        var sequence = new System.Buffers.ReadOnlySequence<byte>(allBytes);

        while (RespParser.TryParse(ref sequence, out RespValue cmd))
        {
            await commandExecutor(cmd).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (IsEnabled)
        {
            _writeChannel.Writer.Complete();
            _cts.Cancel();

            if (_flushTask != null)
            {
                try
                {
                    await _flushTask.ConfigureAwait(false);
                }
                catch { }
            }

            if (_fileStream != null)
            {
                await _fileStream.FlushAsync().ConfigureAwait(false);
                await _fileStream.DisposeAsync().ConfigureAwait(false);
            }

            _cts.Dispose();
        }
    }
}
