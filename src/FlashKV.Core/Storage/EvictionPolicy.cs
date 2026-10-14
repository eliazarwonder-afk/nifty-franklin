namespace FlashKV.Core.Storage;

public enum DataType : byte
{
    String,
    Hash,
    List,
    Set
}

public enum EvictionPolicy
{
    NoEviction,
    AllKeysLru,
    AllKeysLfu,
    VolatileLru,
    VolatileLfu
}
