namespace Razor.Core.Storage;

public readonly record struct BlockRef(
    ulong Slot,
    ReadOnlyMemory<byte> Hash,
    ulong Height,
    ulong Timestamp
);
