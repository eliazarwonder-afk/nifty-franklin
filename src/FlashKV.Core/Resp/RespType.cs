namespace FlashKV.Core.Resp;

/// <summary>
/// RESP (Redis Serialization Protocol) data types.
/// </summary>
public enum RespType : byte
{
    SimpleString = (byte)'+',
    Error = (byte)'-',
    Integer = (byte)':',
    BulkString = (byte)'$',
    Array = (byte)'*',
    Null = 0
}
