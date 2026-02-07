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
        var channel = Channel.CreateUnbounded<ChainEvent>(new UnboundedChannelOptions
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
        foreach (var channel in _channels.Values)
        {
            channel.Writer.TryWrite(chainEvent);
        }
    }

    public void Dispose()
    {
        foreach (var channel in _channels.Values)
        {
            channel.Writer.TryComplete();
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
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return item;
            }
        }
        finally
        {
            _channels.TryRemove(id, out _);
            channel.Writer.TryComplete();
        }
    }
}
