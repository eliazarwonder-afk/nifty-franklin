using System;
using System.Buffers;
using System.IO;
using System.Text;

namespace FlashKV.Core.Resp;

/// <summary>
/// High-throughput RESP serializer formatting directly to streams or IBufferWriter.
/// </summary>
public static class RespWriter
{
    private static readonly byte[] CrLf = "\r\n"u8.ToArray();
    private static readonly byte[] NullBulk = "$-1\r\n"u8.ToArray();
    private static readonly byte[] NullArray = "*-1\r\n"u8.ToArray();
    private static readonly byte[] OkBytes = "+OK\r\n"u8.ToArray();
    private static readonly byte[] PongBytes = "+PONG\r\n"u8.ToArray();

    public static void Write(IBufferWriter<byte> writer, RespValue value)
    {
        switch (value.Type)
        {
            case RespType.SimpleString:
                if (value.StringValue == "OK")
                {
                    writer.Write(OkBytes);
                    return;
                }
                if (value.StringValue == "PONG")
                {
                    writer.Write(PongBytes);
                    return;
                }
                WritePrefixAndString(writer, (byte)'+', value.StringValue ?? string.Empty);
                break;

            case RespType.Error:
                WritePrefixAndString(writer, (byte)'-', value.StringValue ?? "ERR unknown error");
                break;

            case RespType.Integer:
                WritePrefixAndInteger(writer, (byte)':', value.IntegerValue);
                break;

            case RespType.BulkString:
                if (value.IsNull)
                {
                    writer.Write(NullBulk);
                    return;
                }

                ReadOnlySpan<byte> span = value.BinaryValue.Span;
                WritePrefixAndInteger(writer, (byte)'$', span.Length);
                writer.Write(span);
                writer.Write(CrLf);
                break;

            case RespType.Array:
                if (value.ArrayValue == null)
                {
                    writer.Write(NullArray);
                    return;
                }

                WritePrefixAndInteger(writer, (byte)'*', value.ArrayValue.Count);
                foreach (var element in value.ArrayValue)
                {
                    Write(writer, element);
                }
                break;

            default:
                writer.Write(NullBulk);
                break;
        }
    }

    public static byte[] ToBytes(RespValue value)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        Write(buffer, value);
        return buffer.WrittenSpan.ToArray();
    }

    private static void WritePrefixAndString(IBufferWriter<byte> writer, byte prefix, string text)
    {
        int byteCount = Encoding.UTF8.GetByteCount(text);
        Span<byte> span = writer.GetSpan(byteCount + 3);
        span[0] = prefix;
        int written = Encoding.UTF8.GetBytes(text, span[1..]);
        span[written + 1] = (byte)'\r';
        span[written + 2] = (byte)'\n';
        writer.Advance(written + 3);
    }

    private static void WritePrefixAndInteger(IBufferWriter<byte> writer, byte prefix, long number)
    {
        Span<byte> span = writer.GetSpan(32);
        span[0] = prefix;
        if (!System.Buffers.Text.Utf8Formatter.TryFormat(number, span[1..], out int bytesWritten))
        {
            string str = number.ToString();
            bytesWritten = Encoding.UTF8.GetBytes(str, span[1..]);
        }
        span[bytesWritten + 1] = (byte)'\r';
        span[bytesWritten + 2] = (byte)'\n';
        writer.Advance(bytesWritten + 3);
    }
}
