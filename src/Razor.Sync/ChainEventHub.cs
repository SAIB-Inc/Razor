using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Razor.Core.Sync;

namespace Razor.Sync;

public sealed class ChainEventHub : IChainEventSource, IDisposable
{
    private readonly ConcurrentDictionary<int, Channel<ChainEvent>> _channels = new();
    private int _nextId;

    public IAsyncEnumerable<ChainEvent> Subscribe(CancellationToken cancellationToken)
    {
        Channel<ChainEvent> channel = Channel.CreateUnbounded<ChainEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        int id = Interlocked.Increment(ref _nextId);
        _channels[id] = channel;

        return ReadChannel(channel, id, cancellationToken);
    }

    public void Publish(ChainEvent chainEvent)
    {
        foreach (Channel<ChainEvent> channel in _channels.Values)
        {
            _ = channel.Writer.TryWrite(chainEvent);
        }
    }

    public void Dispose()
    {
        foreach (Channel<ChainEvent> channel in _channels.Values)
        {
            _ = channel.Writer.TryComplete();
        }

        _channels.Clear();
    }

    private async IAsyncEnumerable<ChainEvent> ReadChannel(
        Channel<ChainEvent> channel,
        int id,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ChainEvent item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            _ = _channels.TryRemove(id, out _);
            _ = channel.Writer.TryComplete();
        }
    }
}
