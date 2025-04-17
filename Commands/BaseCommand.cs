
using Microsoft.Extensions.Logging;

namespace Razor.Commands;


public abstract class BaseCommand(ILogger logger)
{
    protected readonly ILogger Logger = logger;

    public abstract Task ExecuteAsync();
}