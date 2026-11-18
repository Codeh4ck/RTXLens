namespace RTXLens.Contracts.Sensors;

public sealed record SafeguardExecutionNotice(Guid Id, string Script, int Connector, DateTimeOffset StartedAt, string Status,
    bool IsSimulation = false);
