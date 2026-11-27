using RTXLens.Contracts.Configuration;

namespace RTXLens.Core.Configuration;

public interface IRuntimeConfiguration
{
    ConfigurationSnapshot Current { get; }
    event Action? Changed;

    Task<ConfigurationSnapshot> UpdateAsync(Func<ServiceConfiguration, ServiceConfiguration> update,
        CancellationToken cancellationToken, string? expectedVersion = null);
}
