namespace Razor.Core.Storage;

public readonly record struct BlockRecord(
    BlockRef Ref,
    ReadOnlyMemory<byte> Bytes
);
