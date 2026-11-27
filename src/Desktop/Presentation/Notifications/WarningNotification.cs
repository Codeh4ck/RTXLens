using RTXLens.Contracts.Warnings;

namespace RTXLens.Presentation.Notifications;

public sealed record WarningNotification(string Key, string Title, string Message)
{
    public WarningState[] Episodes { get; init; } = [];
}
