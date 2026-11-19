using System.Threading.Channels;
using RTXLens.Contracts.Diagnostics;

namespace RTXLens.Core.Streaming;

public sealed class SensorStreamHub : IDisposable
{
    private readonly Lock _lock = new();

    private readonly HashSet<Channel<byte[]>> _clients = [];

    private byte[]? _latest;
    private bool _disposed;

    public void Publish(byte[] frame)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _latest = frame;
            foreach (var client in _clients)
            {
                client.Writer.TryWrite(frame);
            }
        }
    }

    public Subscription Subscribe()
    {
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false
        }, static _ => TelemetryMetrics.FramesReplaced.Add(1));

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _clients.Add(channel);

            if (_latest is { } frame)
            {
                channel.Writer.TryWrite(frame);
            }
        }

        return new Subscription(this, channel);
    }

    public void Remove(Channel<byte[]> channel)
    {
        lock (_lock)
        {
            _clients.Remove(channel);
            channel.Writer.TryComplete();
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            foreach (var client in _clients)
            {
                client.Writer.TryComplete();
            }

            _clients.Clear();
            _latest = null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            foreach (var client in _clients)
            {
                client.Writer.TryComplete();
            }

            _clients.Clear();
        }
    }
}
