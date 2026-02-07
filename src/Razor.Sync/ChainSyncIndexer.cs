using System.Formats.Cbor;
using Chrysalis.Cbor.Extensions.Cardano.Core;
using Chrysalis.Cbor.Extensions.Cardano.Core.Header;
using Chrysalis.Cbor.Serialization;
using Chrysalis.Cbor.Types.Cardano.Core.Header;
using Chrysalis.Network.Cbor.ChainSync;
using Chrysalis.Network.Cbor.Common;
using Chrysalis.Network.Multiplexer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Razor.Core.Storage;
using Razor.Core.Sync;

namespace Razor.Sync;

public sealed class ChainSyncIndexer : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly IBlockStore _blockStore;
    private readonly ChainEventHub _events;
    private readonly ChainSyncOptions _options;
    private readonly ILogger<ChainSyncIndexer> _logger;
    private bool _atTip;

    public ChainSyncIndexer(
        IBlockStore blockStore,
        ChainEventHub events,
        IOptions<ChainSyncOptions> options,
        ILogger<ChainSyncIndexer> logger)
    {
        _blockStore = blockStore;
        _events = events;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!TryGetStartPoint(out Point startPoint, out string? error))
        {
            _logger.LogError("ChainSync disabled: {Error}", error);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(startPoint, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ChainSync failed. Reconnecting in {DelaySeconds}s.", ReconnectDelay.TotalSeconds);
                await Task.Delay(ReconnectDelay, stoppingToken);
            }
        }
    }

    private async Task RunOnceAsync(Point startPoint, CancellationToken cancellationToken)
    {
        using PeerClient peer = await ConnectAsync(cancellationToken);
        await peer.StartAsync(_options.NetworkMagic, TimeSpan.FromSeconds(_options.KeepAliveSeconds));

        _logger.LogInformation("ChainSync connected. Finding intersection...");
        ChainSyncMessage intersect = await peer.ChainSync.FindIntersectionAsync([startPoint], cancellationToken);

        switch (intersect)
        {
            case MessageIntersectFound found:
                _logger.LogInformation(
                    "Intersection found at slot {Slot} hash {Hash}",
                    found.Point.Slot,
                    ToHex(found.Point.Hash));
                break;

            case MessageIntersectNotFound notFound:
                _logger.LogError(
                    "Intersection not found. Tip slot {Slot} hash {Hash}",
                    notFound.Tip.Slot.Slot,
                    ToHex(notFound.Tip.Slot.Hash));
                return;

            default:
                _logger.LogError("Unexpected intersection response.");
                return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            MessageNextResponse? response = await peer.ChainSync.NextRequestAsync(cancellationToken);

            switch (response)
            {
                case MessageRollForward rollForward:
                    _atTip = false;
                    HandleRollForward(rollForward);
                    break;

                case MessageRollBackward rollBackward:
                    _atTip = false;
                    HandleRollBackward(rollBackward);
                    break;

                case MessageAwaitReply:
                    if (!_atTip)
                    {
                        _logger.LogInformation("ChainSync: Awaiting next block.");
                        _atTip = true;
                    }
                    break;
            }
        }
    }

    private async Task<PeerClient> ConnectAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.SocketPath))
        {
            _logger.LogInformation("Connecting to node via Unix socket {SocketPath}", _options.SocketPath);
            return await PeerClient.ConnectAsync(_options.SocketPath, cancellationToken);
        }

        _logger.LogInformation("Connecting to node via TCP {Host}:{Port}", _options.TcpHost, _options.TcpPort);
        return await PeerClient.ConnectAsync(_options.TcpHost, _options.TcpPort, cancellationToken);
    }

    private void HandleRollForward(MessageRollForward rollForward)
    {
        if (!TryDecodeHeader(rollForward.Payload.Value, out HeaderInfo header))
        {
            _logger.LogWarning("Failed to decode block header. Skipping rollforward.");
            return;
        }

        var record = new BlockRecord(
            new BlockRef(header.Slot, header.Hash, header.BlockNumber, 0),
            rollForward.Payload.Value);

        _blockStore.Apply(record);

        BlockRef? tip = ToBlockRef(rollForward.Tip) ?? record.Ref;
        _logger.LogInformation(
            "ChainSync: RollForward Slot={Slot} Hash={Hash} Height={Height}",
            record.Ref.Slot,
            ToHex(record.Ref.Hash),
            record.Ref.Height);
        _events.Publish(new ChainEvent(ChainEventKind.Apply, record, null, tip));
    }

    private void HandleRollBackward(MessageRollBackward rollBackward)
    {
        var point = new BlockRef(rollBackward.Point.Slot, rollBackward.Point.Hash, 0, 0);
        _blockStore.RollbackTo(point);

        BlockRef? tip = ToBlockRef(rollBackward.Tip);
        _logger.LogInformation(
            "ChainSync: RollBackward Slot={Slot} Hash={Hash}",
            point.Slot,
            ToHex(point.Hash));
        _events.Publish(new ChainEvent(ChainEventKind.Reset, null, point, tip));
    }

    private static BlockRef? ToBlockRef(Tip tip)
    {
        if (tip is null)
        {
            return null;
        }

        ulong height = tip.BlockNumber is >= 0 ? (ulong)tip.BlockNumber.Value : 0;
        return new BlockRef(tip.Slot.Slot, tip.Slot.Hash, height, 0);
    }

    private bool TryGetStartPoint(out Point startPoint, out string? error)
    {
        startPoint = default!;
        error = null;

        if (string.IsNullOrWhiteSpace(_options.StartHash))
        {
            var tip = _blockStore.GetTip();
            if (tip is null)
            {
                error = "Sync:StartHash is required (hex string) unless storage already has a tip.";
                return false;
            }

            startPoint = new Point(tip.Value.Slot, tip.Value.Hash);
            return true;
        }

        if (!TryParseHash(_options.StartHash, out byte[] hash, out error))
        {
            return false;
        }

        startPoint = new Point(_options.StartSlot, hash);
        return true;
    }

    private static bool TryParseHash(string value, out byte[] hash, out string? error)
    {
        hash = Array.Empty<byte>();
        error = null;

        string normalized = value.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }

        if (normalized.Length == 0 || normalized.Length % 2 != 0)
        {
            error = "Hash must be a non-empty hex string with an even length.";
            return false;
        }

        try
        {
            hash = Convert.FromHexString(normalized);
            return true;
        }
        catch (FormatException)
        {
            error = "Hash must be a valid hex string.";
            return false;
        }
    }

    private static bool TryDecodeHeader(byte[] payload, out HeaderInfo headerInfo)
    {
        headerInfo = default;

        if (payload.Length == 0)
        {
            return false;
        }

        if (!TryExtractHeaderBytes(payload, out byte variant, out byte[] headerBytes))
        {
            return false;
        }

        if (variant == 0)
        {
            return false;
        }

        try
        {
            BlockHeader header = CborSerializer.Deserialize<BlockHeader>(headerBytes);
            ulong slot = header.HeaderBody.Slot();
            ulong blockNumber = header.HeaderBody.BlockNumber();
            byte[] hash = Convert.FromHexString(header.Hash());
            headerInfo = new HeaderInfo(slot, blockNumber, hash);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractHeaderBytes(byte[] payload, out byte variant, out byte[] headerBytes)
    {
        variant = 0;
        headerBytes = Array.Empty<byte>();

        try
        {
            CborReader reader = new(payload, CborConformanceMode.Lax);
            int? outerLength = reader.ReadStartArray();

            variant = checked((byte)reader.ReadUInt64());

            if (variant == 0)
            {
                int? innerLength = reader.ReadStartArray();
                int? prefixLength = reader.ReadStartArray();
                _ = reader.ReadUInt64();
                _ = reader.ReadUInt64();

                if (prefixLength is null)
                {
                    reader.ReadEndArray();
                }

                _ = reader.ReadTag();
                headerBytes = reader.ReadByteString();

                if (innerLength is null)
                {
                    reader.ReadEndArray();
                }
            }
            else
            {
                _ = reader.ReadTag();
                headerBytes = reader.ReadByteString();
            }

            if (outerLength is null)
            {
                reader.ReadEndArray();
            }

            return headerBytes.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private readonly record struct HeaderInfo(ulong Slot, ulong BlockNumber, byte[] Hash);
}
