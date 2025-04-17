using Microsoft.Extensions.Logging;
using Chrysalis.Network.Multiplexer;
using Chrysalis.Network.Cbor.Common;
using Chrysalis.Network.Cbor.ChainSync;
using Microsoft.Extensions.Options;
using Razor.Configuration;
using Chrysalis.Network.MiniProtocols.Extensions;
using Chrysalis.Network.Cbor.LocalStateQuery;
using Chrysalis.Wallet.Models.Addresses;
using Chrysalis.Cbor.Serialization;
using Chrysalis.Cbor.Types.Cardano.Core.Transaction;
using Chrysalis.Network.Cbor.LocalTxSubmit;
using Chrysalis.Cbor.Types;
using Chrysalis.Wallet.Utils;
using Chrysalis.Cbor.Types.Cardano.Core;
using Chrysalis.Cbor.Types.Cardano.Core.Header;
using Razor.Utils;

namespace Razor.Services;

public class NodeService(ILoggerFactory loggerFactory, IOptions<NodeConfiguration> options)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("Razor.Services.NodeService.ChainSync");
    private readonly NodeConfiguration _configuration = options.Value;
    private bool _isRunning;
    private CancellationTokenSource? _cancellationTokenSource;

    public async Task StartNodeAsync()
    {
        if (_isRunning)
        {
            _logger.LogWarning("Razor is already running");
            return;
        }

        _logger.LogInformation("Initializing Razor");
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {

            NodeClient client = await NodeClient.ConnectAsync(_configuration.SocketPath);

            await client.StartAsync();

            Point point = new(_configuration.IntersectionPoint.Slot,
                Convert.FromHexString(_configuration.IntersectionPoint.Hash));

            _logger.LogInformation("Finding intersection at slot {Slot}...", _configuration.IntersectionPoint.Slot);
            await client.ChainSync!.FindIntersectionAsync([point], _cancellationTokenSource.Token);
            _logger.LogInformation("Intersection found");

            _isRunning = true;
            _logger.LogInformation("Razor started successfully");

            _ = Task.Run(async () =>
            {
                try
                {
                    while (_isRunning && !_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        try
                        {
                            MessageNextResponse? nextResponse = await client.ChainSync!.NextRequestAsync(_cancellationTokenSource.Token);

                            switch (nextResponse)
                            {
                                case MessageRollBackward msg:
                                    _logger.LogInformation("Chain extended, rolling back to slot {Slot}", msg.Point.Slot);
                                    break;
                                case MessageRollForward msg:

                                    Block? block = BlockUtils.DeserializeBlockWithEra(msg.Payload.Value);
                                    ulong blockSlot = 0;
                                    string blockHash = string.Empty;
                                    switch (block)
                                    {
                                        case AlonzoCompatibleBlock alonzoBlock:
                                            switch (alonzoBlock.Header.HeaderBody)
                                            {
                                                case AlonzoHeaderBody alonzoBlockHeaderBody:
                                                    blockSlot = alonzoBlockHeaderBody.Slot;
                                                    blockHash = Convert.ToHexString(alonzoBlockHeaderBody.BlockBodyHash);
                                                    break;
                                                case BabbageHeaderBody babbageHeaderBody:
                                                    blockSlot = babbageHeaderBody.Slot;
                                                    blockHash = Convert.ToHexString(babbageHeaderBody.BlockBodyHash);
                                                    break;
                                                default:
                                                    throw new NotSupportedException($"Unsupported Alonzo block header body: {alonzoBlock.Header.HeaderBody.GetType()}");
                                            }
                                            break;
                                        case BabbageBlock babbageBlock:
                                            switch (babbageBlock.Header.HeaderBody)
                                            {
                                                case AlonzoHeaderBody alonzoHeaderBody:
                                                    blockSlot = alonzoHeaderBody.Slot;
                                                    blockHash = Convert.ToHexString(alonzoHeaderBody.BlockBodyHash);
                                                    break;
                                                case BabbageHeaderBody babbageHeaderBody:
                                                    blockSlot = babbageHeaderBody.Slot;
                                                    blockHash = Convert.ToHexString(babbageHeaderBody.BlockBodyHash);
                                                    break;
                                                default:
                                                    throw new NotSupportedException($"Unsupported Babbage block header body: {babbageBlock.Header.HeaderBody.GetType()}");
                                            }
                                            break;
                                        case ConwayBlock conwayBlock:
                                            switch (conwayBlock.Header.HeaderBody)
                                            {
                                                case AlonzoHeaderBody alonzoHeaderBody:
                                                    blockSlot = alonzoHeaderBody.Slot;
                                                    blockHash = Convert.ToHexString(alonzoHeaderBody.BlockBodyHash);
                                                    break;
                                                case BabbageHeaderBody babbageHeaderBody:
                                                    blockSlot = babbageHeaderBody.Slot;
                                                    blockHash = Convert.ToHexString(babbageHeaderBody.BlockBodyHash);
                                                    break;
                                                default:
                                                    throw new NotSupportedException($"Unsupported Conway block header body: {conwayBlock.Header.HeaderBody.GetType()}");
                                            }
                                            break;
                                        default:
                                            throw new NotSupportedException($"Unsupported block type: {block?.GetType()}");
                                    }
                                    _logger.LogInformation("Chain extended, new tip: {Hash} at slot {Slot}",
                                        blockHash,
                                        blockSlot);
                                    break;
                                case MessageAwaitReply msg:
                                _logger.LogInformation("Reached tip, waiting for new blocks");
                                    break;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error processing chain sync message");
                            await Task.Delay(1000, _cancellationTokenSource.Token);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Fatal error in ChainSync processing loop");
                }
            }, _cancellationTokenSource.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start razor");
            _isRunning = false;
            _cancellationTokenSource?.Cancel();
            throw;
        }
    }

    public void StopNode()
    {
        if (!_isRunning)
        {
            _logger.LogWarning("Razor is not running");
            return;
        }

        _logger.LogInformation("Stopping Razor");


        _cancellationTokenSource?.Cancel();

        _isRunning = false;
        _logger.LogInformation("Razor stopped");

    }

    public async Task<Tip> GetCurrentTipAsync()
    {
        NodeClient client = await NodeClient.ConnectAsync(_configuration.SocketPath);

        await client.StartAsync();

        Tip tip = await client.LocalStateQuery.GetTipAsync();

        return tip;

    }

    public async Task<UtxoByAddressResponse> GetUtxosByAddress(string address)
    {
        NodeClient client = await NodeClient.ConnectAsync(_configuration.SocketPath);
        await client.StartAsync();
        Address addr = new(address);
        UtxoByAddressResponse utxos = await client.LocalStateQuery.GetUtxosByAddressAsync([addr.ToBytes()]);
        return utxos;
    }

    public async Task<string> SubmitTransaction(string txCborHex)
    {
        NodeClient client = await NodeClient.ConnectAsync(_configuration.SocketPath);
        await client.StartAsync();

        Transaction transaction = CborSerializer.Deserialize<Transaction>(Convert.FromHexString(txCborHex));

        TransactionBody transactionBody = transaction switch
        {
            ShelleyTransaction tx => tx.TransactionBody,
            AllegraTransaction tx => tx.TransactionBody,
            PostMaryTransaction tx => tx.TransactionBody,
            _ => throw new InvalidOperationException("Transaction Type not Supported")
        };

        byte[] txBody = CborSerializer.Serialize(transactionBody);

        EraTx eraTx = new(6, new CborEncodedValue(Convert.FromHexString(txCborHex)));
        LocalTxSubmissionMessage result = await client.LocalTxSubmit.SubmitTxAsync(new SubmitTx(new Value0(0), eraTx), CancellationToken.None);

        string txHash = result switch
        {
            AcceptTx _ => Convert.ToHexString(HashUtil.Blake2b256(txBody)).ToLowerInvariant(),
            _ => throw new InvalidOperationException("Transaction submission failed")
        };

        return txHash;
    }
}
