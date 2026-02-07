using Razor.Core.Storage;

namespace Razor.Core.Sync;

public readonly record struct ChainEvent(
    ChainEventKind Kind,
    BlockRecord? Block,
    BlockRef? Point,
    BlockRef? Tip
);
