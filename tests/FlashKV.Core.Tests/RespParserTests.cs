using System.Buffers;
using System.Text;
using FlashKV.Core.Resp;
using Xunit;

namespace FlashKV.Core.Tests;

public class RespParserTests
{
    [Fact]
    public void Parse_SimpleString_ReturnsCorrectValue()
    {
        var data = "+OK\r\n"u8.ToArray();
        var seq = new ReadOnlySequence<byte>(data);

        bool success = RespParser.TryParse(ref seq, out var result);

        Assert.True(success);
        Assert.Equal(RespType.SimpleString, result.Type);
        Assert.Equal("OK", result.StringValue);
        Assert.True(seq.IsEmpty);
    }

    [Fact]
    public void Parse_Integer_ReturnsCorrectValue()
    {
        var data = ":1000\r\n"u8.ToArray();
        var seq = new ReadOnlySequence<byte>(data);

        bool success = RespParser.TryParse(ref seq, out var result);

        Assert.True(success);
        Assert.Equal(RespType.Integer, result.Type);
        Assert.Equal(1000, result.IntegerValue);
    }

    [Fact]
    public void Parse_BulkString_ReturnsData()
    {
        var data = "$5\r\nhello\r\n"u8.ToArray();
        var seq = new ReadOnlySequence<byte>(data);

        bool success = RespParser.TryParse(ref seq, out var result);

        Assert.True(success);
        Assert.Equal(RespType.BulkString, result.Type);
        Assert.Equal("hello", result.AsString());
    }

    [Fact]
    public void Parse_NullBulkString_ReturnsNull()
    {
        var data = "$-1\r\n"u8.ToArray();
        var seq = new ReadOnlySequence<byte>(data);

        bool success = RespParser.TryParse(ref seq, out var result);

        Assert.True(success);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Parse_Array_ReturnsElements()
    {
        var data = "*2\r\n$4\r\nECHO\r\n$5\r\nworld\r\n"u8.ToArray();
        var seq = new ReadOnlySequence<byte>(data);

        bool success = RespParser.TryParse(ref seq, out var result);

        Assert.True(success);
        Assert.Equal(RespType.Array, result.Type);
        Assert.NotNull(result.ArrayValue);
        Assert.Equal(2, result.ArrayValue!.Count);
        Assert.Equal("ECHO", result.ArrayValue[0].AsString());
        Assert.Equal("world", result.ArrayValue[1].AsString());
    }

    [Fact]
    public void Parse_InlineCommand_ReturnsArray()
    {
        var data = "PING\r\n"u8.ToArray();
        var seq = new ReadOnlySequence<byte>(data);

        bool success = RespParser.TryParse(ref seq, out var result);

        Assert.True(success);
        Assert.Equal(RespType.Array, result.Type);
        Assert.Single(result.ArrayValue!);
        Assert.Equal("PING", result.ArrayValue![0].AsString());
    }

    [Fact]
    public void Parse_PartialData_ReturnsFalse()
    {
        var data = "$10\r\npart"u8.ToArray();
        var seq = new ReadOnlySequence<byte>(data);

        bool success = RespParser.TryParse(ref seq, out _);

        Assert.False(success);
    }
}
