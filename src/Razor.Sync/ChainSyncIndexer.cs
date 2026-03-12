using System.Net.Sockets;
using Chrysalis.Network.Cbor.BlockFetch;
using Chrysalis.Network.Cbor.ChainSync;
using Chrysalis.Network.Cbor.Common;
using Chrysalis.Network.Multiplexer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Razor.Core.Storage;
using Razor.Core.Sync;

namespace Razor.Sync;

public sealed partial class ChainSyncIndexer(
    IBlockStore blockStore,
    ChainEventHub events,
    GenesisConfig genesis,
    IOptions<ChainSyncOptions> options,
    ILogger<ChainSyncIndexer> logger) : BackgroundService
{
    private readonly IBlockStore _blockStore = blockStore;
    private readonly ChainEventHub _events = events;
    private readonly GenesisConfig _genesis = genesis;
    private readonly ChainSyncOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<ChainSyncIndexer> _logger = logger;
    private bool _atTip;
    private ulong _lastHeaderSlot;

    public override void Dispose()
    {
        _blockStore.Dispose();
        _events.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan reconnectDelay = TimeSpan.FromSeconds(_options.ReconnectDelaySeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Point startPoint = GetStartPoint();
                await RunOnceAsync(startPoint, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException ex)
            {
                Log.ChainSyncFailed(_logger, ex, reconnectDelay.TotalSeconds);
                await Task.Delay(reconnectDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                Log.ChainSyncFailed(_logger, ex, reconnectDelay.TotalSeconds);
                await Task.Delay(reconnectDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunOnceAsync(Point startPoint, CancellationToken cancellationToken)
    {
        using PeerClient peer = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await peer.StartAsync(_options.NetworkMagic, TimeSpan.FromSeconds(_options.KeepAliveSeconds)).ConfigureAwait(false);

        Log.FindingIntersection(_logger);
        ChainSyncMessage intersect = await peer.ChainSync.FindIntersectionAsync([startPoint], cancellationToken).ConfigureAwait(false);

        if (intersect is not MessageIntersectFound found)
        {
            if (intersect is MessageIntersectNotFound notFound && notFound.Tip.Slot is SpecificPoint tipPoint)
            {
                string tipHash = ToHex(tipPoint.Hash.Span);
                Log.IntersectionNotFoundWithTip(_logger, tipPoint.Slot, tipHash);
            }
            else
            {
                Log.IntersectionNotFound(_logger);
            }
            return;
        }

        Point tipPoint2 = found.Tip?.Slot ?? Point.Origin;
        if (found.Point is SpecificPoint sp)
        {
            string hash = ToHex(sp.Hash.Span);
            Log.IntersectionFound(_logger, sp.Slot, hash);
        }
        else
        {
            Log.IntersectionFoundAtOrigin(_logger);
        }

        await RunPipelinedSyncAsync(peer, tipPoint2, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunPipelinedSyncAsync(PeerClient peer, Point tipPoint, CancellationToken cancellationToken)
    {
        int maxDepth = _options.MaxPipelineDepth;
        List<PendingBlock> pendingHeaders = new(maxDepth);

        while (!cancellationToken.IsCancellationRequested)
        {
            int pipelineDepth = ComputeAdaptivePipelineDepth(maxDepth, tipPoint);
            await peer.ChainSync.SendNextRequestBatchAsync(pipelineDepth, cancellationToken).ConfigureAwait(false);

            bool hitAwait = false;
            int received = 0;

            while (received < pipelineDepth && !cancellationToken.IsCancellationRequested)
            {
                MessageNextResponse response = await peer.ChainSync.ReceiveNextResponseAsync(cancellationToken).ConfigureAwait(false);
                received++;

                switch (response)
                {
                    case MessageRollForward rollForward:
                        _atTip = false;
                        if (TryDecodeHeader(rollForward.Payload.Value, out HeaderInfo header))
                        {
                            pendingHeaders.Add(new PendingBlock(header, rollForward.Tip));
                            _lastHeaderSlot = header.Slot;
                            tipPoint = rollForward.Tip?.Slot ?? tipPoint;
                        }
                        else
                        {
                            Log.FailedToDecodeHeader(_logger);
                        }
                        break;

                    case MessageRollBackward rollBackward:
                        _atTip = false;
                        if (pendingHeaders.Count > 0)
                        {
                            await FetchAndApplyBatchAsync(peer, pendingHeaders, cancellationToken).ConfigureAwait(false);
                            pendingHeaders.Clear();
                        }
                        HandleRollBackward(rollBackward);
                        tipPoint = rollBackward.Tip?.Slot ?? tipPoint;
                        break;

                    case MessageAwaitReply:
                        hitAwait = true;
                        break;

                    default:
                        break;
                }

                if (hitAwait)
                {
                    break;
                }
            }

            if (pendingHeaders.Count > 0)
            {
                await FetchAndApplyBatchAsync(peer, pendingHeaders, cancellationToken).ConfigureAwait(false);
                pendingHeaders.Clear();
            }

            if (hitAwait)
            {
                if (!_atTip)
                {
                    Log.AwaitingNextBlock(_logger);
                    _atTip = true;
                }

                MessageNextResponse tipResponse = await peer.ChainSync.NextRequestAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Unexpected null response after AwaitReply.");

                switch (tipResponse)
                {
                    case MessageRollForward rollForward:
                        _atTip = false;
                        if (TryDecodeHeader(rollForward.Payload.Value, out HeaderInfo header))
                        {
                            pendingHeaders.Add(new PendingBlock(header, rollForward.Tip));
                            _lastHeaderSlot = header.Slot;
                            tipPoint = rollForward.Tip?.Slot ?? tipPoint;
                            await FetchAndApplyBatchAsync(peer, pendingHeaders, cancellationToken).ConfigureAwait(false);
                            pendingHeaders.Clear();
                        }
                        else
                        {
                            Log.FailedToDecodeHeader(_logger);
                        }
                        break;

                    case MessageRollBackward rollBackward:
                        _atTip = false;
                        HandleRollBackward(rollBackward);
                        tipPoint = rollBackward.Tip?.Slot ?? tipPoint;
                        break;

                    default:
                        break;
                }
            }
        }
    }

    private async Task FetchAndApplyBatchAsync(
        PeerClient peer,
        List<PendingBlock> headers,
        CancellationToken cancellationToken)
    {
        if (headers.Count == 0)
        {
            return;
        }

        Point from = Point.Specific(headers[0].Header.Slot, headers[0].Header.Hash);
        Point to = Point.Specific(headers[^1].Header.Slot, headers[^1].Header.Hash);

        await peer.BlockFetch.RequestRangeAsync(from, to, cancellationToken).ConfigureAwait(false);

        int index = 0;
        await foreach (BlockFetchMessage msg in peer.BlockFetch.ReceiveBlockMessagesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (msg is not BlockBody blockBody || index >= headers.Count)
            {
                continue;
            }

            PendingBlock pending = headers[index];
            ulong timestamp = _genesis.SlotToTimestamp(pending.Header.Slot);
            BlockRecord record = new(
                new BlockRef(pending.Header.Slot, pending.Header.Hash, pending.Header.BlockNumber, timestamp),
                blockBody.Body.Value);

            _blockStore.Apply(record);

            BlockRef? tip = ToBlockRef(pending.Tip) ?? record.Ref;
            string blockHash = ToHex(record.Ref.Hash.Span);
            Log.RollForward(_logger, record.Ref.Slot, blockHash, record.Ref.Height);
            _events.Publish(new ChainEvent(ChainEventKind.Apply, record, null, tip));

            index++;
        }
    }

    private async Task<PeerClient> ConnectAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.SocketPath))
        {
            Log.ConnectingUnixSocket(_logger, _options.SocketPath);
            return await PeerClient.ConnectAsync(_options.SocketPath, cancellationToken).ConfigureAwait(false);
        }

        Log.ConnectingTcp(_logger, _options.TcpHost, _options.TcpPort);
        return await PeerClient.ConnectAsync(_options.TcpHost, _options.TcpPort, cancellationToken).ConfigureAwait(false);
    }

    private void HandleRollBackward(MessageRollBackward rollBackward)
    {
        if (rollBackward.Point is not SpecificPoint sp)
        {
            Log.RollBackwardToOrigin(_logger);
            return;
        }

        BlockRef point = new(sp.Slot, sp.Hash, 0, 0);
        _blockStore.RollbackTo(point);

        BlockRef? tip = ToBlockRef(rollBackward.Tip);
        string rollbackHash = ToHex(point.Hash.Span);
        Log.RollBackward(_logger, point.Slot, rollbackHash);
        _events.Publish(new ChainEvent(ChainEventKind.Reset, null, point, tip));
    }

    private static BlockRef? ToBlockRef(Tip tip)
    {
        return tip?.Slot is not SpecificPoint sp
            ? null
            : new BlockRef(sp.Slot, sp.Hash, tip.BlockNumber is >= 0 ? (ulong)tip.BlockNumber.Value : 0, 0);
    }

    private Point GetStartPoint()
    {
        BlockRef? tip = _blockStore.GetTip();
        if (tip is null)
        {
            Log.SyncingFromOrigin(_logger);
            return Point.Origin;
        }

        string tipHash = ToHex(tip.Value.Hash.Span);
        Log.SyncingFromTip(_logger, tip.Value.Slot, tipHash);
        return Point.Specific(tip.Value.Slot, tip.Value.Hash);
    }

    private int ComputeAdaptivePipelineDepth(int maxDepth, Point tipPoint)
    {
        if (tipPoint is not SpecificPoint tip || _lastHeaderSlot == 0)
        {
            return maxDepth;
        }

        ulong tipGap = tip.Slot > _lastHeaderSlot ? tip.Slot - _lastHeaderSlot : 0;

        int depth = tipGap switch
        {
            <= 4 => 1,
            <= 20 => 2,
            <= 100 => 5,
            <= 500 => 20,
            <= 2000 => 100,
            <= 10000 => 500,
            <= 50000 => 2000,
            _ => maxDepth
        };

        return Math.Min(depth, maxDepth);
    }

    private static bool TryDecodeHeader(ReadOnlyMemory<byte> payload, out HeaderInfo headerInfo)
    {
        headerInfo = default;

        if (payload.Length == 0)
        {
            return false;
        }

        try
        {
            ChainSyncHeader content = ChainSyncHeader.Decode(payload);
            ChainPoint point = content.ExtractPoint();
            headerInfo = new HeaderInfo(point.Slot, point.BlockNumber, point.Hash);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException
            || ex.GetType().Name == "CborException")
        {
            return false;
        }
    }

    private static string ToHex(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(bytes).ToUpperInvariant();
    }

    private readonly record struct HeaderInfo(ulong Slot, ulong BlockNumber, byte[] Hash);
    private readonly record struct PendingBlock(HeaderInfo Header, Tip Tip);

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "No stored tip. Syncing from origin (genesis).")]
        public static partial void SyncingFromOrigin(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Resuming sync from stored tip Slot={Slot} Hash={Hash}")]
        public static partial void SyncingFromTip(ILogger logger, ulong slot, string hash);

        [LoggerMessage(Level = LogLevel.Error, Message = "ChainSync failed. Reconnecting in {DelaySeconds}s.")]
        public static partial void ChainSyncFailed(ILogger logger, Exception ex, double delaySeconds);

        [LoggerMessage(Level = LogLevel.Information, Message = "ChainSync connected. Finding intersection...")]
        public static partial void FindingIntersection(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Intersection found at slot {Slot} hash {Hash}")]
        public static partial void IntersectionFound(ILogger logger, ulong slot, string hash);

        [LoggerMessage(Level = LogLevel.Information, Message = "Intersection found at origin.")]
        public static partial void IntersectionFoundAtOrigin(ILogger logger);

        [LoggerMessage(Level = LogLevel.Error, Message = "Intersection not found. Tip slot {Slot} hash {Hash}")]
        public static partial void IntersectionNotFoundWithTip(ILogger logger, ulong slot, string hash);

        [LoggerMessage(Level = LogLevel.Error, Message = "Intersection not found.")]
        public static partial void IntersectionNotFound(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "ChainSync: Awaiting next block.")]
        public static partial void AwaitingNextBlock(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Connecting to node via Unix socket {SocketPath}")]
        public static partial void ConnectingUnixSocket(ILogger logger, string socketPath);

        [LoggerMessage(Level = LogLevel.Information, Message = "Connecting to node via TCP {Host}:{Port}")]
        public static partial void ConnectingTcp(ILogger logger, string host, int port);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to decode block header. Skipping rollforward.")]
        public static partial void FailedToDecodeHeader(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "ChainSync: RollForward Slot={Slot} Hash={Hash} Height={Height}")]
        public static partial void RollForward(ILogger logger, ulong slot, string hash, ulong height);

        [LoggerMessage(Level = LogLevel.Warning, Message = "ChainSync: RollBackward to origin. Ignoring.")]
        public static partial void RollBackwardToOrigin(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "ChainSync: RollBackward Slot={Slot} Hash={Hash}")]
        public static partial void RollBackward(ILogger logger, ulong slot, string hash);
    }
}
