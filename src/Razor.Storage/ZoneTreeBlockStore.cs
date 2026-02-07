using System.Buffers.Binary;
using Razor.Core.Storage;
using Razor.Storage.Internal;
using Tenray.ZoneTree;
using Tenray.ZoneTree.Comparers;
using Tenray.ZoneTree.Serializers;

namespace Razor.Storage;

public sealed class ZoneTreeBlockStore : IBlockStore
{
    private readonly IZoneTree<byte[], byte[]> _blocksByHash;
    private readonly IZoneTree<byte[], byte[]> _hashBySlot;
    private readonly IZoneTree<byte[], byte[]> _hashByHeight;
    private readonly IZoneTree<byte[], byte[]> _tip;

    public ZoneTreeBlockStore(string rootPath)
    {
        Directory.CreateDirectory(rootPath);

        _blocksByHash = new ZoneTreeFactory<byte[], byte[]>()
            .SetDataDirectory(Path.Combine(rootPath, "blocks_by_hash"))
            .SetComparer(new ByteArrayComparerAscending())
            .SetKeySerializer(new ByteArraySerializer())
            .SetValueSerializer(new ByteArraySerializer())
            .OpenOrCreate();

        _hashBySlot = new ZoneTreeFactory<byte[], byte[]>()
            .SetDataDirectory(Path.Combine(rootPath, "hash_by_slot"))
            .SetComparer(new ByteArrayComparerAscending())
            .SetKeySerializer(new ByteArraySerializer())
            .SetValueSerializer(new ByteArraySerializer())
            .OpenOrCreate();

        _hashByHeight = new ZoneTreeFactory<byte[], byte[]>()
            .SetDataDirectory(Path.Combine(rootPath, "hash_by_height"))
            .SetComparer(new ByteArrayComparerAscending())
            .SetKeySerializer(new ByteArraySerializer())
            .SetValueSerializer(new ByteArraySerializer())
            .OpenOrCreate();

        _tip = new ZoneTreeFactory<byte[], byte[]>()
            .SetDataDirectory(Path.Combine(rootPath, "tip"))
            .SetComparer(new ByteArrayComparerAscending())
            .SetKeySerializer(new ByteArraySerializer())
            .SetValueSerializer(new ByteArraySerializer())
            .OpenOrCreate();
    }

    public BlockRef? GetTip()
    {
        if (!_tip.TryGet([0x01], out var payload))
        {
            return null;
        }

        return DeserializeBlockRef(payload);
    }

    public void Apply(BlockRecord record)
    {
        if (record.Ref.Hash.Length == 0)
        {
            throw new ArgumentException("Block hash is required.");
        }

        _blocksByHash.Upsert(record.Ref.Hash, record.Bytes);
        _hashBySlot.Upsert(StorageKeys.SlotKey(record.Ref.Slot), record.Ref.Hash);

        if (record.Ref.Height != 0)
        {
            _hashByHeight.Upsert(StorageKeys.HeightKey(record.Ref.Height), record.Ref.Hash);
        }

        _tip.Upsert([0x01], SerializeBlockRef(record.Ref));
    }

    public void RollbackTo(BlockRef point)
    {
        _tip.Upsert([0x01], SerializeBlockRef(point));
    }

    public bool TryGetByHash(byte[] hash, out BlockRecord record)
    {
        if (_blocksByHash.TryGet(hash, out var bytes))
        {
            record = new BlockRecord(new BlockRef(0, hash, 0, 0), bytes);
            return true;
        }

        record = default;
        return false;
    }

    public bool TryGetBySlot(ulong slot, out BlockRecord record)
    {
        if (_hashBySlot.TryGet(StorageKeys.SlotKey(slot), out var hash) &&
            _blocksByHash.TryGet(hash, out var bytes))
        {
            record = new BlockRecord(new BlockRef(slot, hash, 0, 0), bytes);
            return true;
        }

        record = default;
        return false;
    }

    public bool TryGetByHeight(ulong height, out BlockRecord record)
    {
        if (_hashByHeight.TryGet(StorageKeys.HeightKey(height), out var hash) &&
            _blocksByHash.TryGet(hash, out var bytes))
        {
            record = new BlockRecord(new BlockRef(0, hash, height, 0), bytes);
            return true;
        }

        record = default;
        return false;
    }

    public IReadOnlyList<BlockRecord> GetHistory(BlockRef? startToken, int maxItems, out BlockRef? nextToken)
    {
        if (maxItems <= 0)
        {
            nextToken = null;
            return Array.Empty<BlockRecord>();
        }

        var iterator = _hashBySlot.CreateIterator();
        if (startToken is not null)
        {
            iterator.Seek(StorageKeys.SlotKey(startToken.Value.Slot));
        }
        else
        {
            iterator.SeekToFirst();
        }

        List<BlockRecord> results = new(maxItems);
        while (iterator.MoveNext() && results.Count < maxItems)
        {
            var slot = BinaryPrimitives.ReadUInt64BigEndian(iterator.CurrentKey);
            var hash = iterator.CurrentValue;
            if (!_blocksByHash.TryGet(hash, out var bytes))
            {
                continue;
            }

            results.Add(new BlockRecord(new BlockRef(slot, hash, 0, 0), bytes));
        }

        nextToken = null;
        if (iterator.MoveNext())
        {
            var slot = BinaryPrimitives.ReadUInt64BigEndian(iterator.CurrentKey);
            nextToken = new BlockRef(slot, iterator.CurrentValue, 0, 0);
        }

        return results;
    }

    public void Dispose()
    {
        _blocksByHash.Dispose();
        _hashBySlot.Dispose();
        _hashByHeight.Dispose();
        _tip.Dispose();
    }

    private static byte[] SerializeBlockRef(BlockRef blockRef)
    {
        byte[] buffer = new byte[8 + 8 + 8 + 4 + blockRef.Hash.Length];
        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(0, 8), blockRef.Slot);
        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(8, 8), blockRef.Height);
        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(16, 8), blockRef.Timestamp);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(24, 4), blockRef.Hash.Length);
        blockRef.Hash.CopyTo(buffer, 28);
        return buffer;
    }

    private static BlockRef DeserializeBlockRef(byte[] buffer)
    {
        ulong slot = BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(0, 8));
        ulong height = BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(8, 8));
        ulong timestamp = BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(16, 8));
        int hashLen = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(24, 4));
        byte[] hash = new byte[hashLen];
        Array.Copy(buffer, 28, hash, 0, hashLen);
        return new BlockRef(slot, hash, height, timestamp);
    }
}
