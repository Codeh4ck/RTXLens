using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using RTXLens.Contracts.Mobile;

namespace RTXLens.Service.Mobile;

internal sealed record MobileAccessData(
    MobileAccessSettings Settings,
    byte[]? PrivateKey,
    MobileDeviceCredential[] Devices,
    MobileNetworkRules? NetworkRules = null)
{
    public static MobileAccessData Empty { get; } = new(new MobileAccessSettings(), null, []);
}

internal sealed record MobileDeviceCredential(Guid Id, string Name, byte[] TokenHash, string? PushToken = null,
    MobileNotificationPreferences? NotificationPreferences = null, MobileWarningActionSettings? DesktopWarningActions = null);

internal sealed record MobileNetworkRules(Guid Id, int LocalPort, bool OpenFirewall, MobilePortForwarding? PortForwarding);

internal interface IMobileAccessStore
{
    MobileAccessData Load();
    void Save(MobileAccessData data);
}

[SupportedOSPlatform("windows")]
internal sealed class MobileAccessStore(bool debugInstance) : IMobileAccessStore
{
    private const string ValueName = "ProtectedState";
    private readonly RegistryKey _root = debugInstance ? Registry.CurrentUser : Registry.LocalMachine;
    private readonly string _subkey = debugInstance ? @"Software\RTXLens.Debug\MobileAccess" : @"Software\RTXLens\MobileAccess";

    public MobileAccessData Load()
    {
        using var key = _root.OpenSubKey(_subkey);
        if (key?.GetValue(ValueName) is not byte[] ciphertext)
        {
            return MobileAccessData.Empty;
        }

        var plaintext = ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
        try
        {
            var data = JsonSerializer.Deserialize<MobileAccessData>(plaintext);
            if (data?.Settings is not { IsValid: true } || data.Devices is null
                                                        || data.Devices.Any(device => device.TokenHash is not { Length: 32 } || device.NotificationPreferences is { IsValid: false })
                                                        || data.Devices.Select(device => device.Id).Distinct().Count() != data.Devices.Length)
            {
                throw new InvalidDataException("Mobile access state is invalid.");
            }

            return data with
            {
                Devices = [.. data.Devices.Select(device => ExpoMobilePushProvider.IsToken(device.PushToken) ? device : device with { PushToken = null })]
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void Save(MobileAccessData data)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(data);

        try
        {
            var ciphertext = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);

            using var key = _root.CreateSubKey(_subkey);
            key.SetValue(ValueName, ciphertext, RegistryValueKind.Binary);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
