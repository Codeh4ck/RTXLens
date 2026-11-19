namespace RTXLens.Plugins.Msi.Contracts.Safeguard;

internal interface ISafeguardJournalStorage
{
    int RetentionDays { get; }
    string Location(string area, Guid id);
    IEnumerable<Guid> Ids(string area);
    T? Read<T>(string area, Guid id) where T : class;
    void Write<T>(string area, Guid id, T value);
    void Delete(string area, Guid id);
    void DeleteExecutionHistory(Guid id);
}
