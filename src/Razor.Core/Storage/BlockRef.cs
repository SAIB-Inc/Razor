namespace Razor.Core.Storage;

public readonly record struct BlockRef(
    ulong Slot,
    byte[] Hash,
    ulong Height,
    ulong Timestamp
);
