using RTXLens.Contracts.Configuration;

namespace RTXLens.Core.Plugins;

public interface ISensorPlugin : IDisposable
{
    PluginDescriptor Descriptor { get; }
    ValueTask<PluginSample> PollAsync(CancellationToken cancellationToken);
    void ApplySettings(PluginSettings settings);
}

public interface IPollTimingSource
{
    string DescribeLastPoll();
}
