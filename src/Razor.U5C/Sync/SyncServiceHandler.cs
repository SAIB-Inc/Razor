using Chrysalis.Cbor.Extensions.Cardano.Core;
using Chrysalis.Cbor.Extensions.Cardano.Core.Transaction;
using Chrysalis.Cbor.Serialization;
using Chrysalis.Cbor.Types.Cardano.Core;
using Chrysalis.Cbor.Types.Cardano.Core.Common;
using Chrysalis.Cbor.Types.Cardano.Core.Transaction;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Razor.Core.Storage;
using Razor.Core.Sync;
using Utxorpc.V1alpha.Sync;
using Cardano = Utxorpc.V1alpha.Cardano;
using CBlock = Chrysalis.Cbor.Types.Cardano.Core.Block;
using CoreBlockRef = Razor.Core.Storage.BlockRef;
using ProtoBlockRef = Utxorpc.V1alpha.Sync.BlockRef;

namespace Razor.U5C.Sync;

public sealed partial class SyncServiceHandler(
    IBlockStore blockStore,
    IChainEventSource events,
    ILogger<SyncServiceHandler> logger) : SyncService.SyncServiceBase
{
    private const int MaxDumpHistoryItems = 100;
    private const int FollowTipHistoryPageSize = 100;

    public override Task<FetchBlockResponse> FetchBlock(FetchBlockRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        FetchBlockResponse response = new();

        foreach (ProtoBlockRef blockRef in request.Ref)
        {
            if (!TryResolveBlock(blockRef, out BlockRecord record))
            {
                throw new RpcException(new Status(StatusCode.NotFound, $"Failed to find block: {blockRef}"));
            }

            response.Block.Add(ToAnyBlock(record));
        }

        return Task.FromResult(response);
    }

    public override Task<DumpHistoryResponse> DumpHistory(DumpHistoryRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.MaxItems > MaxDumpHistoryItems)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"max_items must be less than or equal to {MaxDumpHistoryItems}"));
        }

        DumpHistoryResponse response = new();
        int maxItems = (int)Math.Min(request.MaxItems, int.MaxValue);
        CoreBlockRef? startToken = TryMapBlockRef(request.StartToken);

        IReadOnlyList<BlockRecord> history = blockStore.GetHistory(startToken, maxItems, out CoreBlockRef? nextToken);
        foreach (BlockRecord record in history)
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
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            CoreBlockRef? tip = blockStore.GetTip();
            CoreBlockRef? intersection = TryResolveIntersection(request.Intersect) ?? tip;
            CoreBlockRef? suppressApply = null;

            if (intersection is not null)
            {
                await responseStream.WriteAsync(
                    new FollowTipResponse
                    {
                        Reset = ToProto(intersection.Value),
                        Tip = tip is not null ? ToProto(tip.Value) : ToProto(intersection.Value)
                    },
                    context.CancellationToken).ConfigureAwait(false);

                suppressApply = intersection.Value;
            }

            if (intersection is not null && tip is not null)
            {
                await StreamHistory(intersection.Value, tip.Value, responseStream, context.CancellationToken).ConfigureAwait(false);
            }

            await foreach (ChainEvent chainEvent in events.Subscribe(context.CancellationToken).ConfigureAwait(false))
            {
                FollowTipResponse response = new();
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
                            response.Tip = ToProto(chainEvent.Block.Value.Ref);
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
                            response.Tip = ToProto(chainEvent.Point.Value);
                        }
                        break;
                    default:
                        throw new InvalidOperationException($"Unexpected chain event kind: {chainEvent.Kind}");
                }

                if (response.Tip is null && chainEvent.Tip is not null)
                {
                    response.Tip = ToProto(chainEvent.Tip.Value);
                }
                else if (response.Tip is null && tip is not null)
                {
                    response.Tip = ToProto(tip.Value);
                }

                if (response.ActionCase != FollowTipResponse.ActionOneofCase.None)
                {
                    await responseStream.WriteAsync(response, context.CancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            Log.FollowTipCanceled(logger);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            Log.FollowTipCanceled(logger);
        }
    }

    public override Task<ReadTipResponse> ReadTip(ReadTipRequest request, ServerCallContext context)
    {
        ReadTipResponse response = new();
        CoreBlockRef? tip = blockStore.GetTip() ?? throw new RpcException(new Status(StatusCode.Internal, "chain has no data."));

        response.Tip = ToProto(tip.Value);
        return Task.FromResult(response);
    }

    private static AnyChainBlock ToAnyBlock(BlockRecord record)
    {
        ReadOnlyMemory<byte> rawBlock = StripTag24(record.Bytes);
        return new AnyChainBlock
        {
            NativeBytes = ByteString.CopyFrom(rawBlock.Span),
            Cardano = ToCardanoBlock(record, rawBlock)
        };
    }

    private static Cardano.Block ToCardanoBlock(BlockRecord record, ReadOnlyMemory<byte> rawBlock)
    {
        return new Cardano.Block
        {
            Header = new Cardano.BlockHeader
            {
                Slot = record.Ref.Slot,
                Hash = ByteString.CopyFrom(record.Ref.Hash.Span),
                Height = record.Ref.Height
            },
            Body = TryMapBlockBody(rawBlock),
            Timestamp = record.Ref.Timestamp
        };
    }

    private static Cardano.BlockBody TryMapBlockBody(ReadOnlyMemory<byte> rawBlock)
    {
        if (rawBlock.Length == 0)
        {
            return new Cardano.BlockBody();
        }

        try
        {
            BlockWithEra blockWithEra = CborSerializer.Deserialize<BlockWithEra>(rawBlock);
            return MapBlockBody(blockWithEra.Block);
        }
        catch (Exception)
        {
            return new Cardano.BlockBody();
        }
    }

    private static ReadOnlyMemory<byte> StripTag24(ReadOnlyMemory<byte> bytes)
    {
        ReadOnlySpan<byte> span = bytes.Span;
        if (span.Length < 3 || (span[0] & 0xE0) != 0xC0)
        {
            return bytes;
        }

        int tagHeaderSize = CborHeaderSize(span);
        int bstrHeaderSize = CborHeaderSize(span[tagHeaderSize..]);
        return bytes[(tagHeaderSize + bstrHeaderSize)..];
    }

    private static int CborHeaderSize(ReadOnlySpan<byte> data)
    {
        int additionalInfo = data[0] & 0x1F;
        return additionalInfo switch
        {
            < 24 => 1,
            24 => 2,
            25 => 3,
            26 => 5,
            27 => 9,
            _ => 1
        };
    }

    private static Cardano.BlockBody MapBlockBody(CBlock block)
    {
        Cardano.BlockBody body = new();

        IEnumerable<TransactionBody> txBodies = block.TransactionBodies();
        HashSet<int>? invalidIndices = block.InvalidTransactions()?.ToHashSet();

        int index = 0;
        foreach (TransactionBody txBody in txBodies)
        {
            bool successful = invalidIndices is null || !invalidIndices.Contains(index);
            body.Tx.Add(MapTransaction(txBody, successful));
            index++;
        }

        return body;
    }

    private static Cardano.Tx MapTransaction(TransactionBody txBody, bool successful)
    {
        Cardano.Tx tx = new()
        {
            Fee = new Cardano.BigInt { Int = (long)txBody.Fee() },
            Hash = ByteString.CopyFrom(Convert.FromHexString(txBody.Hash())),
            Successful = successful
        };

        foreach (TransactionInput input in txBody.Inputs())
        {
            tx.Inputs.Add(new Cardano.TxInput
            {
                TxHash = ByteString.CopyFrom(input.TransactionId.Span),
                OutputIndex = (uint)input.Index
            });
        }

        foreach (TransactionOutput output in txBody.Outputs())
        {
            tx.Outputs.Add(MapTxOutput(output));
        }

        return tx;
    }

    private static Cardano.TxOutput MapTxOutput(TransactionOutput output)
    {
        Value amount = output.Amount();
        Cardano.TxOutput txOutput = new()
        {
            Address = ByteString.CopyFrom(output.Address().Span)
        };

        switch (amount)
        {
            case LovelaceWithMultiAsset multiAsset:
                txOutput.Coin = new Cardano.BigInt { Int = (long)multiAsset.LovelaceValue.Value };
                foreach (KeyValuePair<ReadOnlyMemory<byte>, TokenBundleOutput> policy in multiAsset.MultiAsset.Value)
                {
                    Cardano.Multiasset protoAsset = new()
                    {
                        PolicyId = ByteString.CopyFrom(policy.Key.Span)
                    };
                    foreach (KeyValuePair<ReadOnlyMemory<byte>, ulong> asset in policy.Value.Value)
                    {
                        protoAsset.Assets.Add(new Cardano.Asset
                        {
                            Name = ByteString.CopyFrom(asset.Key.Span),
                            OutputCoin = new Cardano.BigInt { Int = (long)asset.Value }
                        });
                    }
                    txOutput.Assets.Add(protoAsset);
                }
                break;
            case Lovelace lovelace:
                txOutput.Coin = new Cardano.BigInt { Int = (long)lovelace.Value };
                break;
            default:
                break;
        }

        return txOutput;
    }

    private static ProtoBlockRef ToProto(CoreBlockRef blockRef)
    {
        return new ProtoBlockRef
        {
            Slot = blockRef.Slot,
            Hash = ByteString.CopyFrom(blockRef.Hash.Span),
            Height = blockRef.Height,
            Timestamp = blockRef.Timestamp
        };
    }

    private bool TryResolveBlock(ProtoBlockRef blockRef, out BlockRecord record)
    {
        if (blockRef.Hash is { Length: > 0 })
        {
            return blockStore.TryGetByHash(blockRef.Hash.ToByteArray(), out record);
        }

        if (blockRef.Height != 0)
        {
            return blockStore.TryGetByHeight(blockRef.Height, out record);
        }

        if (blockRef.Slot != 0)
        {
            return blockStore.TryGetBySlot(blockRef.Slot, out record);
        }

        record = default;
        return false;
    }

    private CoreBlockRef? TryResolveIntersection(IEnumerable<ProtoBlockRef> intersects)
    {
        foreach (ProtoBlockRef intersect in intersects)
        {
            if (intersect.Hash is { Length: > 0 })
            {
                if (blockStore.TryGetByHash(intersect.Hash.ToByteArray(), out BlockRecord record))
                {
                    return MergeRef(intersect, record.Ref);
                }
            }

            if (intersect.Height != 0)
            {
                if (blockStore.TryGetByHeight(intersect.Height, out BlockRecord record))
                {
                    return record.Ref;
                }
            }

            if (intersect.Slot != 0)
            {
                if (blockStore.TryGetBySlot(intersect.Slot, out BlockRecord record))
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
            IReadOnlyList<BlockRecord> history = blockStore.GetHistory(nextToken, FollowTipHistoryPageSize, out CoreBlockRef? newToken);
            foreach (BlockRecord record in history)
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
                    cancellationToken).ConfigureAwait(false);

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
        return (left.Slot == right.Slot && left.Hash.Span.SequenceEqual(right.Hash.Span))
            || (left.Height != 0 && left.Height == right.Height && left.Hash.Span.SequenceEqual(right.Hash.Span));
    }

    private static CoreBlockRef? TryMapBlockRef(ProtoBlockRef? protoRef)
    {
        return protoRef is null
            ? null
            : protoRef.Slot == 0 && protoRef.Height == 0 && protoRef.Timestamp == 0 && protoRef.Hash.Length == 0
                ? null
                : new CoreBlockRef(
                    protoRef.Slot,
                    protoRef.Hash.ToByteArray(),
                    protoRef.Height,
                    protoRef.Timestamp);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "FollowTip canceled by client.")]
        public static partial void FollowTipCanceled(ILogger logger);
    }
}
