using Chrysalis.Cbor.Extensions.Cardano.Core.Common;
using Chrysalis.Cbor.Extensions.Cardano.Core.Transaction;
using Chrysalis.Cbor.Types.Cardano.Core.Common;
using Chrysalis.Network.Cbor.Common;
using Microsoft.Extensions.Logging;
using Razor.Services;


namespace Razor.Commands;

public class QueryCommand(ILogger<QueryCommand> logger, NodeService nodeService) : BaseCommand(logger)
{
    private readonly NodeService _nodeService = nodeService;

    public override async Task ExecuteAsync()
    {
        Logger.LogWarning("Please specify a query subcommand (tip, utxos)");
        await Task.CompletedTask;
    }


    public async Task QueryTipAsync()
    {
        try
        {
            Tip tip = await _nodeService.GetCurrentTipAsync();

            Logger.LogInformation("Current tip: {Hash} at slot {Slot}", Convert.ToHexString(tip.Slot.Hash), tip.Slot.Slot);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to query tip");
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

    public async Task QueryUtxosByAddressAsync(string address)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                Logger.LogError("Address is required for UTXO query");
                Console.WriteLine("Error: Address is required. Usage: razor query utxos --address <address>");
                return;
            }

            Logger.LogInformation("Querying UTXOs for address: {Address}", address);
            var utxos = await _nodeService.GetUtxosByAddress(address);

            if (utxos.Utxos.Count == 0)
            {
                Console.WriteLine($"No UTXOs found for address: {address}");
                return;
            }

            int txHashWidth = 70;
            int txIxWidth = 10;
            int amountWidth = 20;

            Console.WriteLine($"\n{"TxHash".PadRight(txHashWidth)}{"TxIx".PadRight(txIxWidth)}{"Amount".PadRight(amountWidth)}");

            Console.WriteLine(new string('-', txHashWidth + txIxWidth + amountWidth));

            foreach (var utxo in utxos.Utxos)
            {
                Console.WriteLine("");
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write($"{Convert.ToHexString(utxo.Key.TransactionId).PadRight(txHashWidth)}");

                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.Write($"{utxo.Key.Index.ToString().PadRight(txIxWidth)}");

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"{utxo.Value.Amount().Lovelace()} lovelace");

                if (utxo.Value.Amount() is LovelaceWithMultiAsset multiAsset)
                {
                    foreach (var asset in multiAsset.MultiAsset())
                    {
                        string indentation = new(' ', txHashWidth + txIxWidth);

                        string policyId = Convert.ToHexString(asset.Key);
                        foreach (var name in asset.Value.Value)
                        {
                            string assetName = Convert.ToHexString(name.Key);
                            ulong quantity = name.Value;

                            Console.ForegroundColor = ConsoleColor.DarkGreen;
                            Console.Write($"{indentation}+ ");
                            Console.Write($"{quantity} ");
                            Console.ForegroundColor = ConsoleColor.DarkMagenta;
                            Console.WriteLine($"{policyId}.{assetName}");
                        }
                    }
                }
                else
                {

                }

                Console.ResetColor();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to query UTXOs for address: {Address}", address);
            Console.WriteLine($"Error: {ex.Message}");
        }
    }
}