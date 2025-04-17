using Microsoft.Extensions.Logging;
using Razor.Services;

namespace Razor.Commands;

public class RunCommand(ILogger<RunCommand> logger, NodeService nodeService) : BaseCommand(logger)
{
    private readonly NodeService _nodeService = nodeService;

    public override async Task ExecuteAsync()
    {

        try
        {
            await _nodeService.StartNodeAsync();

            var cancellationTokenSource = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, e) =>
            {
                Logger.LogInformation("Shutting down Razor..");
                e.Cancel = true;
                cancellationTokenSource.Cancel();
            };

            try
            {
                cancellationTokenSource.Token.WaitHandle.WaitOne();
            }
            catch (OperationCanceledException)
            {
            }

            _nodeService.StopNode();
            Logger.LogInformation("Node stopped successfully");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An error occurred while running the node");
        }
    }
}