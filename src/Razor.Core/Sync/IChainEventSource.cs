namespace Razor.Core.Sync;

public interface IChainEventSource
{
    IAsyncEnumerable<ChainEvent> Subscribe(CancellationToken cancellationToken);
}
