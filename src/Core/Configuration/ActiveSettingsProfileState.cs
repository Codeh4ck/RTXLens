using RTXLens.Contracts.Configuration;

namespace RTXLens.Core.Configuration;

public sealed class ActiveSettingsProfileState
{
    public ActiveSettingsProfile? Current
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }
}
