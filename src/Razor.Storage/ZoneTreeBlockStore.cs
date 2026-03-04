using System.Buffers.Binary;
using Razor.Core.Storage;
using Razor.Storage.Internal;
using Tenray.ZoneTree;
using Tenray.ZoneTree.Comparers;
using Tenray.ZoneTree.Serializers;

namespace Razor.Storage;

public sealed class ZoneTreeBlockStore : IBlockStore
{
    private static readonly Memory<byte> TipKey = new byte[] { 0x01 };

    private readonly IZoneTree<Memory<byte>, Memory<byte>> _blocksByHash;
    private readonly IZoneTree<Memory<byte>, Memory<byte>> _refByHash;
    private readonly IZoneTree<Memory<byte>, Memory<byte>> _hashBySlot;
    private readonly IZoneTree<Memory<byte>, Memory<byte>> _hashByHeight;
    private readonly IZoneTree<Memory<byte>, Memory<byte>> _tip;

    public ZoneTreeBlockStore(string rootPath)
    {
        _ = Directory.CreateDirectory(rootPath);

        _blocksByHash = new ZoneTreeFactory<Memory<byte>, Memory<byte>>()
            .SetDataDirectory(Path.Combine(rootPath, "blocks_by_hash"))
            .SetComparer(new ByteArrayComparerAscending())
            .SetKeySerializer(new ByteArraySerializer())
            .SetValueSerializer(new ByteArraySerializer())
            .OpenOrCreate();

        _refByHash = new ZoneTreeFactory<Memory<byte>, Memory<byte>>()
            .SetDataDirectory(Path.Combine(rootPath, "ref_by_hash"))
            .SetComparer(new ByteArrayComparerAscending())
            .SetKeySerializer(new ByteArraySerializer())
            .SetValueSerializer(new ByteArraySerializer())
            .OpenOrCreate();

        _hashBySlot = new ZoneTreeFactory<Memory<byte>, Memory<byte>>()
            .SetDataDirectory(Path.Combine(rootPath, "hash_by_slot"))
            .SetComparer(new ByteArrayComparerAscending())
            .SetKeySerializer(new ByteArraySerializer())
            .SetValueSerializer(new ByteArraySerializer())
            .OpenOrCreate();

        _hashByHeight = new ZoneTreeFactory<Memory<byte>, Memory<byte>>()
            .SetDataDirectory(Path.Combine(rootPath, "hash_by_height"))
            .SetComparer(new ByteArrayComparerAscending())
            .SetKeySerializer(new ByteArraySerializer())
            .SetValueSerializer(new ByteArraySerializer())
            .OpenOrCreate();

        _tip = new ZoneTreeFactory<Memory<byte>, Memory<byte>>()
            .SetDataDirectory(Path.Combine(rootPath, "tip"))
            .SetComparer(new ByteArrayComparerAscending())
            .SetKeySerializer(new ByteArraySerializer())
            .SetValueSerializer(new ByteArraySerializer())
            .OpenOrCreate();
    }

    public BlockRef? GetTip()
    {
        Memory<byte> key = TipKey;
        return !_tip.TryGet(in key, out Memory<byte> payload)
            ? null
            : DeserializeBlockRef(payload.ToArray());
    }

    public void Apply(BlockRecord record)
    {
        if (record.Ref.Hash.Length == 0)
        {
            throw new ArgumentException("Block hash is required.");
        }

        Memory<byte> hashKey = record.Ref.Hash.ToArray();
        Memory<byte> blockBytes = record.Bytes.ToArray();
        _ = _blocksByHash.Upsert(in hashKey, in blockBytes);

        Memory<byte> refValue = SerializeBlockRef(record.Ref);
        _ = _refByHash.Upsert(in hashKey, in refValue);

        Memory<byte> slotKey = StorageKeys.SlotKey(record.Ref.Slot);
        Memory<byte> slotHash = record.Ref.Hash.ToArray();
        _ = _hashBySlot.Upsert(in slotKey, in slotHash);

        if (record.Ref.Height != 0)
        {
            Memory<byte> heightKey = StorageKeys.HeightKey(record.Ref.Height);
            Memory<byte> heightHash = record.Ref.Hash.ToArray();
            _ = _hashByHeight.Upsert(in heightKey, in heightHash);
        }

        Memory<byte> tipKey = TipKey;
        Memory<byte> tipValue = SerializeBlockRef(record.Ref);
        _ = _tip.Upsert(in tipKey, in tipValue);
    }

    public void RollbackTo(BlockRef point)
    {
        Memory<byte> tipKey = TipKey;
        Memory<byte> tipValue = SerializeBlockRef(point);
        _ = _tip.Upsert(in tipKey, in tipValue);
    }

    public bool TryGetByHash(byte[] hash, out BlockRecord record)
    {
        Memory<byte> hashKey = hash;
        if (_blocksByHash.TryGet(in hashKey, out Memory<byte> bytes))
        {
            if (_refByHash.TryGet(in hashKey, out Memory<byte> refPayload))
            {
                record = new BlockRecord(DeserializeBlockRef(refPayload.ToArray()), bytes.ToArray());
                return true;
            }

            record = new BlockRecord(new BlockRef(0, hash, 0, 0), bytes.ToArray());
            return true;
        }

        record = default;
        return false;
    }

    public bool TryGetBySlot(ulong slot, out BlockRecord record)
    {
        Memory<byte> slotKey = StorageKeys.SlotKey(slot);
        if (_hashBySlot.TryGet(in slotKey, out Memory<byte> hash))
        {
            if (_blocksByHash.TryGet(in hash, out Memory<byte> bytes))
            {
                if (_refByHash.TryGet(in hash, out Memory<byte> refPayload))
                {
                    record = new BlockRecord(DeserializeBlockRef(refPayload.ToArray()), bytes.ToArray());
                    return true;
                }

                record = new BlockRecord(new BlockRef(slot, hash.ToArray(), 0, 0), bytes.ToArray());
                return true;
            }
        }

        record = default;
        return false;
    }

    public bool TryGetByHeight(ulong height, out BlockRecord record)
    {
        Memory<byte> heightKey = StorageKeys.HeightKey(height);
        if (_hashByHeight.TryGet(in heightKey, out Memory<byte> hash))
        {
            if (_blocksByHash.TryGet(in hash, out Memory<byte> bytes))
            {
                if (_refByHash.TryGet(in hash, out Memory<byte> refPayload))
                {
                    record = new BlockRecord(DeserializeBlockRef(refPayload.ToArray()), bytes.ToArray());
                    return true;
                }

                record = new BlockRecord(new BlockRef(0, hash.ToArray(), height, 0), bytes.ToArray());
                return true;
            }
        }

        record = default;
        return false;
    }

    public IReadOnlyList<BlockRecord> GetHistory(BlockRef? startToken, int maxItems, out BlockRef? nextToken)
    {
        IZoneTreeIterator<Memory<byte>, Memory<byte>> iterator = _hashBySlot.CreateIterator(
            IteratorType.AutoRefresh,
            includeDeletedRecords: false,
            contributeToTheBlockCache: false);

        if (startToken is not null)
        {
            Memory<byte> startKey = StorageKeys.SlotKey(startToken.Value.Slot);
            iterator.Seek(in startKey);
        }
        else
        {
            iterator.SeekFirst();
        }

        if (!iterator.Next())
        {
            nextToken = null;
            return [];
        }

        if (maxItems <= 0)
        {
            nextToken = BuildRef(iterator.CurrentKey, iterator.CurrentValue);
            return [];
        }

        List<BlockRecord> results = new(maxItems);
        int count = 0;

        while (true)
        {
            Memory<byte> hash = iterator.CurrentValue;
            if (_blocksByHash.TryGet(in hash, out Memory<byte> bytes))
            {
                if (_refByHash.TryGet(in hash, out Memory<byte> refPayload))
                {
                    results.Add(new BlockRecord(DeserializeBlockRef(refPayload.ToArray()), bytes.ToArray()));
                }
                else
                {
                    ulong slot = BinaryPrimitives.ReadUInt64BigEndian(iterator.CurrentKey.Span);
                    results.Add(new BlockRecord(new BlockRef(slot, hash.ToArray(), 0, 0), bytes.ToArray()));
                }
            }

            count++;
            if (count >= maxItems)
            {
                nextToken = iterator.Next() ? BuildRef(iterator.CurrentKey, iterator.CurrentValue) : null;
                return results;
            }

            if (!iterator.Next())
            {
                nextToken = null;
                return results;
            }
        }
    }

    public void Dispose()
    {
        _blocksByHash.Dispose();
        _refByHash.Dispose();
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
        blockRef.Hash.Span.CopyTo(buffer.AsSpan(28));
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

    private BlockRef BuildRef(Memory<byte> slotKey, Memory<byte> hash)
    {
        if (_refByHash.TryGet(in hash, out Memory<byte> refPayload))
        {
            return DeserializeBlockRef(refPayload.ToArray());
        }

        ulong slot = BinaryPrimitives.ReadUInt64BigEndian(slotKey.Span);
        return new BlockRef(slot, hash.ToArray(), 0, 0);
    }
}
