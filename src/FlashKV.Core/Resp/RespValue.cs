using System;
using System.Collections.Generic;
using System.Text;

namespace FlashKV.Core.Resp;

/// <summary>
/// Represents a parsed RESP value or an element to be formatted.
/// </summary>
public readonly struct RespValue : IEquatable<RespValue>
{
    public RespType Type { get; }
    public string? StringValue { get; }
    public long IntegerValue { get; }
    public ReadOnlyMemory<byte> BinaryValue { get; }
    public IReadOnlyList<RespValue>? ArrayValue { get; }

    public bool IsNull => Type == RespType.Null || (Type == RespType.BulkString && BinaryValue.IsEmpty && StringValue == null);

    private RespValue(RespType type, string? strVal, long intVal, ReadOnlyMemory<byte> binVal, IReadOnlyList<RespValue>? arrayVal)
    {
        Type = type;
        StringValue = strVal;
        IntegerValue = intVal;
        BinaryValue = binVal;
        ArrayValue = arrayVal;
    }

    public static RespValue SimpleString(string value) =>
        new(RespType.SimpleString, value, 0, default, null);

    public static RespValue Error(string message) =>
        new(RespType.Error, message, 0, default, null);

    public static RespValue Integer(long value) =>
        new(RespType.Integer, null, value, default, null);

    public static RespValue BulkString(ReadOnlyMemory<byte> data) =>
        new(RespType.BulkString, null, 0, data, null);

    public static RespValue BulkString(string text) =>
        new(RespType.BulkString, text, 0, Encoding.UTF8.GetBytes(text), null);

    public static RespValue NullBulkString() =>
        new(RespType.BulkString, null, 0, default, null);

    public static RespValue NullArray() =>
        new(RespType.Array, null, 0, default, null);

    public static RespValue Array(IReadOnlyList<RespValue> elements) =>
        new(RespType.Array, null, 0, default, elements);

    public static readonly RespValue Ok = SimpleString("OK");
    public static readonly RespValue Pong = SimpleString("PONG");
    public static readonly RespValue One = Integer(1);
    public static readonly RespValue Zero = Integer(0);
    public static readonly RespValue Null = NullBulkString();

    public string AsString()
    {
        if (StringValue != null) return StringValue;
        if (!BinaryValue.IsEmpty) return Encoding.UTF8.GetString(BinaryValue.Span);
        if (Type == RespType.Integer) return IntegerValue.ToString();
        return string.Empty;
    }

    public override string ToString()
    {
        return Type switch
        {
            RespType.SimpleString => $"+{StringValue}",
            RespType.Error => $"-{StringValue}",
            RespType.Integer => $":{IntegerValue}",
            RespType.BulkString => IsNull ? "$-1" : $"${BinaryValue.Length}:{AsString()}",
            RespType.Array => ArrayValue == null ? "*-1" : $"*[{ArrayValue.Count}]",
            _ => "null"
        };
    }

    public bool Equals(RespValue other)
    {
        if (Type != other.Type) return false;
        return Type switch
        {
            RespType.Integer => IntegerValue == other.IntegerValue,
            RespType.SimpleString or RespType.Error => StringValue == other.StringValue,
            RespType.BulkString => BinaryValue.Span.SequenceEqual(other.BinaryValue.Span),
            RespType.Array => ArrayValue == other.ArrayValue || (ArrayValue?.Count == other.ArrayValue?.Count),
            _ => true
        };
    }

    public override bool Equals(object? obj) => obj is RespValue other && Equals(other);
    public override int GetHashCode() => HashCode.Combine((byte)Type, StringValue, IntegerValue);
    public static bool operator ==(RespValue left, RespValue right) => left.Equals(right);
    public static bool operator !=(RespValue left, RespValue right) => !left.Equals(right);
}
