using System.Text.Json;
using System.Text.Json.Serialization;

namespace RTXLens.Contracts.Mobile;

public static class MobileAccessProtocol
{
    public const int DefaultPort = 59275;
    public const int DefaultWarningHistoryDays = 30;
    public const int MaxRequestSize = 4096;
    public const int MaxResponseSize = 65536;

    public static string GetPipeName(bool debug) => debug ? "RTXLens.Debug.MobileAccess" : "RTXLens.MobileAccess";

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter<MobileAccessOperation>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
}

public enum MobileAccessOperation
{
    Status,
    Configure,
    CreateInvite,
    Approve,
    Reject,
    Revoke,
    PublishSimulationWarning,
    SendTestNotification,
    GetWarningActions,
    ConfigureWarningActions
}

public sealed record MobileAccessRequest(
    MobileAccessOperation Operation,
    MobileAccessSettings? Settings = null,
    Guid? Id = null,
    bool KeepNoIpCredentials = false,
    MobileSimulationWarning? SimulationWarning = null,
    MobileWarningActionSettings? WarningActions = null);

public sealed record MobileSimulationWarning(string Title, string Detail)
{
    [JsonIgnore]
    public bool IsValid => !string.IsNullOrWhiteSpace(Title) && Title.Length <= 160 && Detail is { Length: <= 480 };
}

public sealed record MobileAccessResponse(
    bool Success,
    MobileAccessStatus? Status = null,
    MobilePairingInvite? Invite = null,
    string? Error = null,
    MobileWarningActionOptions? WarningActions = null);

public sealed record MobileAccessStatus(
    bool Enabled,
    bool Listening,
    int Port,
    string? NoIpHostname,
    NoIpUpdateStatus? NoIp,
    bool OpenFirewall,
    MobilePortForwarding? PortForwarding,
    MobilePairingApproval? PendingApproval,
    MobileDevice[] Devices,
    string? Error,
    string? LocalAddress = null,
    string? PublicAddress = null,
    int WarningHistoryDays = MobileAccessProtocol.DefaultWarningHistoryDays);

public sealed record MobilePairingInvite(string Secret, string PublicKeyFingerprint, DateTimeOffset ExpiresAt);
public sealed record MobilePairingApproval(Guid Id, string DeviceName, DateTimeOffset ExpiresAt);
public sealed record MobileDevice(Guid Id, string Name, DateTimeOffset? LastConnectedAt, DateTimeOffset? LastExternalConnectionAt = null,
    bool LockScreenNotifications = false);

public sealed record MobilePairingCode(int SchemaVersion, Uri LocalAddress, Uri RemoteAddress, string PublicKeyFingerprint, string Secret, DateTimeOffset ExpiresAt);

public enum NoIpUpdateState
{
    Current,
    RetryScheduled,
    ActionRequired
}

public sealed record NoIpUpdateStatus(NoIpUpdateState State, string Message, DateTimeOffset CheckedAt);
