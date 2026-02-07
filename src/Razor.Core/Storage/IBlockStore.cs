namespace Razor.Core.Storage;

public interface IBlockStore : IDisposable
{
    BlockRef? GetTip();
    void Apply(BlockRecord record);
    void RollbackTo(BlockRef point);

    bool TryGetByHash(byte[] hash, out BlockRecord record);
    bool TryGetBySlot(ulong slot, out BlockRecord record);
    bool TryGetByHeight(ulong height, out BlockRecord record);

    IReadOnlyList<BlockRecord> GetHistory(BlockRef? startToken, int maxItems, out BlockRef? nextToken);
}
