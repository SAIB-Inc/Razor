using Microsoft.Extensions.Logging;
using Razor.Services;


namespace Razor.Commands;

public class TransactionCommand(ILogger<TransactionCommand> logger, NodeService nodeService) : BaseCommand(logger)
{
    private readonly NodeService _nodeService = nodeService;

    public override async Task ExecuteAsync()
    {
        Logger.LogWarning("Please specify a transaction subcommand (submit)");
        await Task.CompletedTask;
    }

    public async Task SubmitTransactionAsync(string txCborHex)
    {
        try
        {
            Console.WriteLine("");
            string txHash = await _nodeService.SubmitTransaction(txCborHex);

            Logger.LogInformation("Transaction Submitted Successfully : {Hash}", txHash);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to submit Transaction");
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

}