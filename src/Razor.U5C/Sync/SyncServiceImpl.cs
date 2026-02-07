using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Razor.Core.Storage;
using Razor.Core.Sync;
using Utxorpc.V1alpha.Sync;
using CoreBlockRef = Razor.Core.Storage.BlockRef;
using ProtoBlockRef = Utxorpc.V1alpha.Sync.BlockRef;

namespace Razor.U5C.Sync;

public sealed class SyncServiceImpl : SyncService.SyncServiceBase
{
    private const int MaxDumpHistoryItems = 100;
    private const int FollowTipHistoryPageSize = 100;

    private readonly IBlockStore _blockStore;
    private readonly IChainEventSource _events;
    private readonly ILogger<SyncServiceImpl> _logger;

    public SyncServiceImpl(IBlockStore blockStore, IChainEventSource events, ILogger<SyncServiceImpl> logger)
    {
        _blockStore = blockStore;
        _events = events;
        _logger = logger;
    }

    public override Task<FetchBlockResponse> FetchBlock(FetchBlockRequest request, ServerCallContext context)
    {
        var response = new FetchBlockResponse();

        foreach (var blockRef in request.Ref)
        {
            if (!TryResolveBlock(blockRef, out var record))
            {
                throw new RpcException(new Status(StatusCode.NotFound, $"Failed to find block: {blockRef}"));
            }

            response.Block.Add(ToAnyBlock(record));
        }

        return Task.FromResult(response);
    }

    public override Task<DumpHistoryResponse> DumpHistory(DumpHistoryRequest request, ServerCallContext context)
    {
        if (request.MaxItems > MaxDumpHistoryItems)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"max_items must be less than or equal to {MaxDumpHistoryItems}"));
        }

        var response = new DumpHistoryResponse();
        int maxItems = (int)Math.Min(request.MaxItems, int.MaxValue);
        var startToken = TryMapBlockRef(request.StartToken);

        var history = _blockStore.GetHistory(startToken, maxItems, out var nextToken);
        foreach (var record in history)
        {
            response.Block.Add(ToAnyBlock(record));
        }

        if (nextToken is not null)
        {
            response.NextToken = ToProto(nextToken.Value);
        }

        return Task.FromResult(response);
    }

    public override async Task FollowTip(FollowTipRequest request, IServerStreamWriter<FollowTipResponse> responseStream, ServerCallContext context)
    {
        var tip = _blockStore.GetTip();
        var intersection = TryResolveIntersection(request.Intersect) ?? tip;
        CoreBlockRef? suppressApply = null;

        if (intersection is not null)
        {
            await responseStream.WriteAsync(
                new FollowTipResponse
                {
                    Reset = ToProto(intersection.Value),
                    Tip = tip is not null ? ToProto(tip.Value) : ToProto(intersection.Value)
                },
                context.CancellationToken);

            suppressApply = intersection.Value;
        }

        if (intersection is not null && tip is not null)
        {
            await StreamHistory(intersection.Value, tip.Value, responseStream, context.CancellationToken);
        }

        await foreach (var chainEvent in _events.Subscribe(context.CancellationToken))
        {
            var response = new FollowTipResponse();
            switch (chainEvent.Kind)
            {
                case ChainEventKind.Apply:
                    if (chainEvent.Block is not null)
                    {
                        if (suppressApply is not null && IsSameRef(chainEvent.Block.Value.Ref, suppressApply.Value))
                        {
                            suppressApply = null;
                            continue;
                        }

                        suppressApply = null;
                        response.Apply = ToAnyBlock(chainEvent.Block.Value);
                    }
                    break;
                case ChainEventKind.Undo:
                    if (chainEvent.Block is not null)
                    {
                        response.Undo = ToAnyBlock(chainEvent.Block.Value);
                    }
                    break;
                case ChainEventKind.Reset:
                    if (chainEvent.Point is not null)
                    {
                        response.Reset = ToProto(chainEvent.Point.Value);
                    }
                    break;
            }

            if (chainEvent.Tip is not null)
            {
                response.Tip = ToProto(chainEvent.Tip.Value);
            }
            else if (tip is not null)
            {
                response.Tip = ToProto(tip.Value);
            }

            if (response.ActionCase != FollowTipResponse.ActionOneofCase.None)
            {
                await responseStream.WriteAsync(response, context.CancellationToken);
            }
        }
    }

    public override Task<ReadTipResponse> ReadTip(ReadTipRequest request, ServerCallContext context)
    {
        var response = new ReadTipResponse();
        var tip = _blockStore.GetTip();
        if (tip is null)
        {
            throw new RpcException(new Status(StatusCode.Internal, "chain has no data."));
        }

        response.Tip = ToProto(tip.Value);
        return Task.FromResult(response);
    }

    private static AnyChainBlock ToAnyBlock(BlockRecord record)
    {
        return new AnyChainBlock
        {
            NativeBytes = ByteString.CopyFrom(record.Bytes)
        };
    }

    private static ProtoBlockRef ToProto(CoreBlockRef blockRef)
    {
        return new ProtoBlockRef
        {
            Slot = blockRef.Slot,
            Hash = ByteString.CopyFrom(blockRef.Hash),
            Height = blockRef.Height,
            Timestamp = blockRef.Timestamp
        };
    }

    private bool TryResolveBlock(ProtoBlockRef blockRef, out BlockRecord record)
    {
        if (blockRef.Hash is { Length: > 0 })
        {
            return _blockStore.TryGetByHash(blockRef.Hash.ToByteArray(), out record);
        }

        if (blockRef.Height != 0)
        {
            return _blockStore.TryGetByHeight(blockRef.Height, out record);
        }

        if (blockRef.Slot != 0)
        {
            return _blockStore.TryGetBySlot(blockRef.Slot, out record);
        }

        record = default;
        return false;
    }

    private CoreBlockRef? TryResolveIntersection(IEnumerable<ProtoBlockRef> intersects)
    {
        foreach (var intersect in intersects)
        {
            if (intersect.Hash is { Length: > 0 })
            {
                if (_blockStore.TryGetByHash(intersect.Hash.ToByteArray(), out var record))
                {
                    return MergeRef(intersect, record.Ref);
                }
            }

            if (intersect.Height != 0)
            {
                if (_blockStore.TryGetByHeight(intersect.Height, out var record))
                {
                    return record.Ref;
                }
            }

            if (intersect.Slot != 0)
            {
                if (_blockStore.TryGetBySlot(intersect.Slot, out var record))
                {
                    return record.Ref;
                }
            }
        }

        return null;
    }

    private static CoreBlockRef MergeRef(ProtoBlockRef proto, CoreBlockRef storeRef)
    {
        return new CoreBlockRef(
            proto.Slot != 0 ? proto.Slot : storeRef.Slot,
            proto.Hash.Length > 0 ? proto.Hash.ToByteArray() : storeRef.Hash,
            proto.Height != 0 ? proto.Height : storeRef.Height,
            proto.Timestamp != 0 ? proto.Timestamp : storeRef.Timestamp);
    }

    private async Task StreamHistory(
        CoreBlockRef intersection,
        CoreBlockRef tip,
        IServerStreamWriter<FollowTipResponse> responseStream,
        CancellationToken cancellationToken)
    {
        if (IsSameRef(intersection, tip))
        {
            return;
        }

        CoreBlockRef? nextToken = intersection;
        bool skipFirst = true;

        while (nextToken is not null && !cancellationToken.IsCancellationRequested)
        {
            var history = _blockStore.GetHistory(nextToken, FollowTipHistoryPageSize, out var newToken);
            foreach (var record in history)
            {
                if (skipFirst && IsSameRef(record.Ref, intersection))
                {
                    skipFirst = false;
                    continue;
                }

                skipFirst = false;

                if (record.Ref.Slot > tip.Slot)
                {
                    return;
                }

                await responseStream.WriteAsync(
                    new FollowTipResponse
                    {
                        Apply = ToAnyBlock(record),
                        Tip = ToProto(record.Ref)
                    },
                    cancellationToken);

                if (IsSameRef(record.Ref, tip))
                {
                    return;
                }
            }

            nextToken = newToken;
            if (newToken is null || history.Count == 0)
            {
                return;
            }
        }
    }

    private static bool IsSameRef(CoreBlockRef left, CoreBlockRef right)
    {
        if (left.Slot == right.Slot && left.Hash.AsSpan().SequenceEqual(right.Hash))
        {
            return true;
        }

        return left.Height != 0 && left.Height == right.Height && left.Hash.AsSpan().SequenceEqual(right.Hash);
    }

    private static CoreBlockRef? TryMapBlockRef(ProtoBlockRef? protoRef)
    {
        if (protoRef is null)
        {
            return null;
        }

        if (protoRef.Slot == 0 && protoRef.Height == 0 && protoRef.Timestamp == 0 && protoRef.Hash.Length == 0)
        {
            return null;
        }

        return new CoreBlockRef(
            protoRef.Slot,
            protoRef.Hash.ToByteArray(),
            protoRef.Height,
            protoRef.Timestamp);
    }
}
