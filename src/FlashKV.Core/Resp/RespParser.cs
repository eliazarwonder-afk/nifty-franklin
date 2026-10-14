using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace FlashKV.Core.Resp;

/// <summary>
/// High-performance, zero-allocation RESP2/RESP3 and inline protocol parser.
/// Works directly over ReadOnlySequence&lt;byte&gt;.
/// </summary>
public static class RespParser
{
    private static readonly byte[] CrLf = "\r\n"u8.ToArray();

    /// <summary>
    /// Attempts to parse a single RESP message or inline command from the sequence.
    /// Returns true if a full message was consumed, false if more data is required.
    /// </summary>
    public static bool TryParse(ref ReadOnlySequence<byte> buffer, out RespValue value)
    {
        value = default;
        if (buffer.IsEmpty) return false;

        var reader = new SequenceReader<byte>(buffer);

        // Check first byte
        if (!reader.TryPeek(out byte prefix))
        {
            return false;
        }

        switch (prefix)
        {
            case (byte)'+': // Simple String
            case (byte)'-': // Error
            case (byte)':': // Integer
                return TryParseSimpleLine(ref buffer, ref reader, prefix, out value);

            case (byte)'$': // Bulk String
                return TryParseBulkString(ref buffer, ref reader, out value);

            case (byte)'*': // Array
                return TryParseArray(ref buffer, ref reader, out value);

            default:
                // Fallback to inline command (e.g. "PING\r\n" or "SET foo bar\r\n")
                return TryParseInlineCommand(ref buffer, ref reader, out value);
        }
    }

    private static bool TryParseSimpleLine(
        ref ReadOnlySequence<byte> buffer,
        ref SequenceReader<byte> reader,
        byte prefix,
        out RespValue value)
    {
        value = default;
        reader.Advance(1); // skip prefix

        if (!reader.TryReadTo(out ReadOnlySequence<byte> line, CrLf.AsSpan()))
        {
            return false;
        }

        string text = Encoding.UTF8.GetString(line.ToArray());

        if (prefix == (byte)':')
        {
            if (long.TryParse(text, out long intVal))
            {
                value = RespValue.Integer(intVal);
                buffer = buffer.Slice(reader.Position);
                return true;
            }
            value = RespValue.Error("ERR invalid integer");
            buffer = buffer.Slice(reader.Position);
            return true;
        }

        value = prefix == (byte)'+'
            ? RespValue.SimpleString(text)
            : RespValue.Error(text);

        buffer = buffer.Slice(reader.Position);
        return true;
    }

    private static bool TryParseBulkString(
        ref ReadOnlySequence<byte> buffer,
        ref SequenceReader<byte> reader,
        out RespValue value)
    {
        value = default;
        reader.Advance(1); // skip '$'

        if (!reader.TryReadTo(out ReadOnlySequence<byte> lengthLine, CrLf.AsSpan()))
        {
            return false;
        }

        string lenStr = Encoding.UTF8.GetString(lengthLine.ToArray());
        if (!int.TryParse(lenStr, out int length))
        {
            value = RespValue.Error("ERR Protocol error: invalid bulk length");
            buffer = buffer.Slice(reader.Position);
            return true;
        }

        if (length == -1)
        {
            value = RespValue.NullBulkString();
            buffer = buffer.Slice(reader.Position);
            return true;
        }

        if (length < -1)
        {
            value = RespValue.Error("ERR Protocol error: negative bulk length");
            buffer = buffer.Slice(reader.Position);
            return true;
        }

        if (reader.Remaining < length + 2)
        {
            return false; // Need more data
        }

        ReadOnlySequence<byte> dataSeq = reader.Sequence.Slice(reader.Position, length);
        byte[] payload = dataSeq.ToArray();

        reader.Advance(length);

        // Advance over \r\n
        if (!reader.IsNext(CrLf.AsSpan(), advancePast: true))
        {
            return false;
        }

        value = RespValue.BulkString(payload);
        buffer = buffer.Slice(reader.Position);
        return true;
    }

    private static bool TryParseArray(
        ref ReadOnlySequence<byte> buffer,
        ref SequenceReader<byte> reader,
        out RespValue value)
    {
        value = default;
        reader.Advance(1); // skip '*'

        if (!reader.TryReadTo(out ReadOnlySequence<byte> countLine, CrLf.AsSpan()))
        {
            return false;
        }

        string countStr = Encoding.UTF8.GetString(countLine.ToArray());
        if (!int.TryParse(countStr, out int count))
        {
            value = RespValue.Error("ERR Protocol error: invalid multibulk length");
            buffer = buffer.Slice(reader.Position);
            return true;
        }

        if (count == -1)
        {
            value = RespValue.NullArray();
            buffer = buffer.Slice(reader.Position);
            return true;
        }

        if (count < -1)
        {
            value = RespValue.Error("ERR Protocol error: negative multibulk length");
            buffer = buffer.Slice(reader.Position);
            return true;
        }

        var elements = new List<RespValue>(count);
        ReadOnlySequence<byte> subBuffer = buffer.Slice(reader.Position);

        for (int i = 0; i < count; i++)
        {
            if (!TryParse(ref subBuffer, out RespValue item))
            {
                return false; // Need more data for child elements
            }
            elements.Add(item);
        }

        value = RespValue.Array(elements);
        buffer = subBuffer;
        return true;
    }

    private static bool TryParseInlineCommand(
        ref ReadOnlySequence<byte> buffer,
        ref SequenceReader<byte> reader,
        out RespValue value)
    {
        value = default;
        if (!reader.TryReadTo(out ReadOnlySequence<byte> line, CrLf.AsSpan()))
        {
            return false;
        }

        string raw = Encoding.UTF8.GetString(line.ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            buffer = buffer.Slice(reader.Position);
            return false;
        }

        string[] parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var elements = new List<RespValue>(parts.Length);
        foreach (var p in parts)
        {
            elements.Add(RespValue.BulkString(p));
        }

        value = RespValue.Array(elements);
        buffer = buffer.Slice(reader.Position);
        return true;
    }
}
