using System.Net;
using System.Security.Cryptography;
using System.Text;
using RTXLens.Core.Diagnostics;
using RTXLens.Contracts.Mobile;

namespace RTXLens.Service.Mobile;

internal sealed record MobilePairingRequest(string? Invite, string? DeviceName);
internal sealed record MobilePairingClaim(Guid Id, string Secret);
internal sealed record MobilePairingCompletion(Guid Id, string? Secret);
internal sealed record MobilePairingCredential(Guid DeviceId, string Token);

internal sealed class MobilePairing(IMobileAccessStore store, TimeProvider clock, ILogger logger)
{
    internal const int DeviceLimit = 16;
    private static readonly TimeSpan InviteLifetime = TimeSpan.FromMinutes(5);
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, PairedDevice> _devices = [];
    private MobileAccessData _data = MobileAccessData.Empty;
    private PendingInvite? _invite;
    private bool _listening;

    public MobileAccessData Data
    {
        get
        {
            lock (_gate)
            {
                return _data;
            }
        }
    }

    public void Load()
    {
        lock (_gate)
        {
            _data = store.Load();

            foreach (var credential in _data.Devices)
            {
                _devices.Add(credential.Id, new PairedDevice(credential));
            }
        }
    }

    public void SaveSettings(MobileAccessSettings settings, byte[]? privateKey, MobileNetworkRules? rules)
    {
        lock (_gate)
        {
            Save(_data with { Settings = settings, PrivateKey = privateKey, NetworkRules = rules });
        }
    }

    public void SetListening(bool listening)
    {
        lock (_gate)
        {
            _listening = listening;
            if (!listening)
            {
                _invite = null;
            }
        }
    }

    public MobilePairingInvite? CreateInvite(string fingerprint)
    {
        lock (_gate)
        {
            if (!_listening || _devices.Count >= DeviceLimit)
            {
                return null;
            }

            var secret = CreateSecret();
            var expiresAt = clock.GetUtcNow() + InviteLifetime;

            _invite = new PendingInvite(Hash(secret), expiresAt);

            return new MobilePairingInvite(secret, fingerprint, expiresAt);
        }
    }

    public MobilePairingClaim? Begin(MobilePairingRequest request)
    {
        var name = request.DeviceName?.Trim();
        if (name is not { Length: > 0 and <= 80 } || name.Any(char.IsControl))
        {
            return null;
        }

        lock (_gate)
        {
            var invite = CurrentInvite();
            if (invite is null || invite.Claim is not null || !Matches(request.Invite, invite.SecretHash))
            {
                return null;
            }

            var secret = CreateSecret();
            var claim = new PendingClaim(Guid.NewGuid(), name, Hash(secret));

            invite.Claim = claim;

            logger.LogActivity(LogLevel.Information, "mobile.pairing-requested", "Mobile access", "A phone requested pairing.");
            return new MobilePairingClaim(claim.Id, secret);
        }
    }

    public MobilePairingApproval? GetPendingApproval()
    {
        lock (_gate)
        {
            var invite = CurrentInvite();
            return invite?.Claim is { Approved: false } claim ? new MobilePairingApproval(claim.Id, claim.Name, invite.ExpiresAt) : null;
        }
    }

    public bool Approve(Guid id)
    {
        lock (_gate)
        {
            if (CurrentInvite()?.Claim is not { Approved: false } claim || claim.Id != id)
            {
                return false;
            }

            claim.Approved = true;
            return true;
        }
    }

    public bool Reject(Guid id)
    {
        lock (_gate)
        {
            if (CurrentInvite()?.Claim?.Id != id)
            {
                return false;
            }

            _invite = null;
            return true;
        }
    }

    public (bool Valid, MobilePairingCredential? Credential) Complete(MobilePairingCompletion request)
    {
        lock (_gate)
        {
            var claim = CurrentInvite()?.Claim;
            if (claim is null || request.Id != claim.Id || !Matches(request.Secret, claim.SecretHash))
            {
                return (false, null);
            }

            if (!claim.Approved)
            {
                return (true, null);
            }

            if (claim.Credential is not null)
            {
                return (true, claim.Credential);
            }

            var secret = CreateSecret();
            var device = new MobileDeviceCredential(Guid.NewGuid(), claim.Name, Hash(secret));

            Save(_data with { Devices = [.. _data.Devices, device] });

            _devices.Add(device.Id, new PairedDevice(device));
            claim.Credential = new MobilePairingCredential(device.Id, $"{device.Id:N}.{secret}");

            logger.LogActivity(LogLevel.Information, "mobile.paired", "Mobile access", "A phone was paired.");

            return (true, claim.Credential);
        }
    }

    public bool TryAuthenticate(string? token, out Guid id, out CancellationToken revoked)
    {
        id = Guid.Empty;
        revoked = CancellationToken.None;

        if (token is not { Length: 76 } || token[32] != '.' || !Guid.TryParseExact(token.AsSpan(0, 32), "N", out id))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_listening || !_devices.TryGetValue(id, out var device) || !Matches(token[33..], device.Credential.TokenHash))
            {
                return false;
            }

            revoked = device.Revoked.Token;
            return true;
        }
    }

    public void RecordConnection(Guid id, IPAddress? remoteAddress)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(id, out var device))
            {
                return;
            }

            var now = clock.GetUtcNow();
            device.LastConnectedAt = now;

            if (MobileAccessSettings.IsPublicIpv4(remoteAddress))
            {
                device.LastExternalConnectionAt = now;
            }
        }
    }

    public bool Revoke(Guid id)
    {
        PairedDevice device;

        lock (_gate)
        {
            if (!_devices.TryGetValue(id, out var existing))
            {
                return false;
            }

            device = existing;
            Save(_data with { Devices = [.. _data.Devices.Where(value => value.Id != id)] });

            _devices.Remove(id);

            if (_invite?.Claim?.Credential?.DeviceId == id)
            {
                _invite = null;
            }
        }

        device.Revoked.Cancel();
        logger.LogActivity(LogLevel.Information, "mobile.revoked", "Mobile access", "A phone’s access was revoked.");

        return true;
    }

    public MobileDevice[] GetDevices()
    {
        lock (_gate)
        {
            return
            [
                .. _devices.Values.Select(device =>
                    new MobileDevice(device.Credential.Id, device.Credential.Name, device.LastConnectedAt, device.LastExternalConnectionAt, device.Credential.PushToken is not null))
            ];
        }
    }

    public MobileWarningActionSettings? GetWarningActions(Guid id, bool requireListening = true)
    {
        lock (_gate)
        {
            return (!requireListening || _listening) && _devices.TryGetValue(id, out var device)
                ? device.Credential.DesktopWarningActions ?? new()
                : null;
        }
    }

    public bool SetWarningActions(Guid id, MobileWarningActionSettings settings)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(id, out var device))
            {
                return false;
            }

            if (device.Credential.DesktopWarningActions != settings)
            {
                var updated = device.Credential with { DesktopWarningActions = settings };
                Save(_data with { Devices = [.. _data.Devices.Select(value => value.Id == id ? updated : value)] });
                device.Credential = updated;
            }

            return true;
        }
    }

    public bool SetPushToken(Guid id, string? token, string? expected = null, MobileNotificationPreferences? preferences = null)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(id, out var device) || (expected is not null && device.Credential.PushToken != expected))
            {
                return false;
            }

            var updated = device.Credential with
            {
                PushToken = token,
                NotificationPreferences = preferences ?? device.Credential.NotificationPreferences
            };

            if (updated == device.Credential)
            {
                return true;
            }

            Save(_data with { Devices = [.. _data.Devices.Select(value => value.Id == id ? updated : value)] });
            device.Credential = updated;

            return true;
        }
    }

    public MobilePushTarget[] GetPushTargets(Guid? recipient = null)
    {
        lock (_gate)
        {
            return !_listening || !_data.Settings.Enabled ? [] : [.. _devices.Values
                .Where(device => device.Credential.PushToken is not null && (recipient is null || device.Credential.Id == recipient))
                .Select(device => new MobilePushTarget(device.Credential.Id, device.Credential.PushToken!, device.Revoked.Token))];
        }
    }

    public MobileNotificationPreferences? GetPushPreferences(MobilePushTarget target)
    {
        lock (_gate)
        {
            return _listening && _data.Settings.Enabled && _devices.TryGetValue(target.Id, out var device)
                && device.Credential.PushToken == target.Token
                ? device.Credential.NotificationPreferences ?? MobileNotificationPreferences.Default : null;
        }
    }

    private PendingInvite? CurrentInvite() => _listening && _invite is { } invite && invite.ExpiresAt > clock.GetUtcNow() ? invite : null;

    private void Save(MobileAccessData data)
    {
        store.Save(data);
        _data = data;
    }

    private static string CreateSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Hash(string secret) => SHA256.HashData(Encoding.ASCII.GetBytes(secret));

    private static bool Matches(string? secret, byte[] hash) => secret is { Length: 43 } && CryptographicOperations.FixedTimeEquals(Hash(secret), hash);

    private sealed record PendingInvite(byte[] SecretHash, DateTimeOffset ExpiresAt)
    {
        public PendingClaim? Claim { get; set; }
    }

    private sealed record PendingClaim(Guid Id, string Name, byte[] SecretHash)
    {
        public bool Approved { get; set; }
        public MobilePairingCredential? Credential { get; set; }
    }

    private sealed class PairedDevice(MobileDeviceCredential credential)
    {
        public MobileDeviceCredential Credential { get; set; } = credential;
        public CancellationTokenSource Revoked { get; } = new();
        public DateTimeOffset? LastConnectedAt { get; set; }
        public DateTimeOffset? LastExternalConnectionAt { get; set; }
    }
}
