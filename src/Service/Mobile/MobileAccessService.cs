using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using RTXLens.Core.Diagnostics;
using RTXLens.Core.Streaming;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Mobile;
using RTXLens.Contracts.Sensors;
using Microsoft.AspNetCore.Connections;

namespace RTXLens.Service.Mobile;

internal sealed class MobileAccessService(
    MobilePairing pairing,
    SensorStreamHub hub,
    TimeProvider clock,
    NoIpUpdater noIp,
    IMobileNetworkAccess network,
    ILogger<MobileAccessService> logger,
    IPAddress listenAddress,
    bool debugInstance = false,
    MobileWarningHistory? warningHistory = null,
    MobileHistoryEndpoints? historyEndpoints = null,
    MobilePushDelivery? push = null) : IHostedService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly MobileSimulationWarnings? _simulationWarnings = debugInstance ? new(clock) : null;
    private MobileAccessCertificate? _certificate;
    private WebApplication? _listener;
    private string? _error;
    private volatile bool _stateLoaded;

    public void ObserveWarnings(SensorSnapshot snapshot, DebugWarningHistoryFrame? debugHistory = null)
    {
        if (_stateLoaded)
        {
            warningHistory?.Observe(snapshot, pairing.Data.Settings.WarningHistoryDays, debugHistory);
            if (snapshot.Warnings is { } warnings)
            {
                push?.Observe(warnings);
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            pairing.Load();
            _stateLoaded = true;

            await ReconcileAsync(cancellationToken);
        }
        catch (Exception error) when (IsAccessFailure(error))
        {
            RecordFailure("Mobile access could not load its settings. Local monitoring is available.", error);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopListenerAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MobileAccessStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return GetStatus();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MobileAccessStatus> ConfigureAsync(MobileAccessSettings settings, CancellationToken cancellationToken,
        bool keepNoIpCredentials = false)
    {
        if (!settings.IsValid)
        {
            throw new ArgumentException("The mobile access settings are invalid.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_stateLoaded)
            {
                throw new InvalidOperationException("The service could not read its mobile access state.");
            }

            var data = pairing.Data;
            if (keepNoIpCredentials)
            {
                settings = settings with
                {
                    NoIp = data.Settings.NoIp ?? throw new ArgumentException("Enter a No-IP DDNS key.")
                };
            }

            if (!settings.Enabled)
            {
                await StopListenerAsync();
            }

            if (settings != data.Settings)
            {
                pairing.SaveSettings(settings, data.PrivateKey, data.NetworkRules);
            }

            _error = null;
            try
            {
                await ReconcileAsync(cancellationToken);
            }
            catch (Exception error) when (IsAccessFailure(error))
            {
                RecordFailure("Mobile Access could not save its network setup. Check the RTX Lens Event Log and try again.", error);
            }

            return GetStatus();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MobilePairingInvite?> CreateInviteAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _certificate is { } certificate ? pairing.CreateInvite(certificate.Fingerprint) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> PublishSimulationWarningAsync(MobileSimulationWarning warning, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_simulationWarnings is null || _listener is null || !warning.IsValid)
            {
                return false;
            }

            _simulationWarnings.Add(warning);
            push?.SendSimulation(warning.Title);

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool SendTestNotification(Guid id) => push?.SendTest(id) == true;

    internal Uri? ListenerAddress => _listener is { } listener ? MobileAccessListener.GetAddress(listener) : null;

    public void Dispose() => _gate.Dispose();

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var data = pairing.Data;
        var settings = data.Settings;
        var networkRequested = settings.Enabled && (settings.OpenFirewall || settings.PortForwarding is not null);
        var rules = data.NetworkRules;
        var cleanupFailed = false;

        if (!settings.Enabled || (_listener is not null && ListenerAddress?.Port != settings.Port))
        {
            await StopListenerAsync();
        }

        if (rules is not null && (!networkRequested || rules.LocalPort != settings.Port
            || rules.OpenFirewall != settings.OpenFirewall || rules.PortForwarding != settings.PortForwarding))
        {
            try
            {
                network.Remove(rules);
                rules = null;
                pairing.SaveSettings(settings, data.PrivateKey, null);
            }
            catch (MobileNetworkException error)
            {
                RecordFailure(error.Message, error);
                cleanupFailed = true;
            }
        }

        if (!settings.Enabled)
        {
            return;
        }

        if (_listener is null)
        {
            var key = data.PrivateKey;
            if (key is null)
            {
                using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);

                key = generated.ExportPkcs8PrivateKey();
                pairing.SaveSettings(settings, key, rules);
            }

            try
            {
                await StartListenerAsync(settings.Port, key, cancellationToken);
            }
            catch (Exception error) when (IsAccessFailure(error))
            {
                var portInUse = error is AddressInUseException || error.GetBaseException() is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse };

                var message = portInUse
                    ? $"TCP port {settings.Port} is already in use. Choose another Mobile Access port."
                    : $"Mobile Access could not start its HTTPS listener on TCP port {settings.Port}. Check the RTX Lens Event Log.";

                RecordFailure(message, error);
                return;
            }
        }

        if (networkRequested && !cleanupFailed)
        {
            if (rules is null)
            {
                rules = new MobileNetworkRules(Guid.NewGuid(), settings.Port, settings.OpenFirewall, settings.PortForwarding);
                // Persist ownership before applying either rule so cleanup can recover from a partial setup.
                pairing.SaveSettings(settings, pairing.Data.PrivateKey, rules);
            }

            try
            {
                network.Apply(rules);
            }
            catch (MobileNetworkException error)
            {
                RecordFailure(error.Message, error);
            }
        }
    }

    private async Task StartListenerAsync(int port, byte[] privateKey, CancellationToken cancellationToken)
    {
        var certificate = new MobileAccessCertificate(privateKey, clock);

        try
        {
            Func<byte[], byte[]>? formatFrame = _simulationWarnings != null ? _simulationWarnings.AddToFrame : null;

            _listener = await MobileAccessListener.StartAsync(listenAddress, port, certificate, pairing, hub, cancellationToken, formatFrame, historyEndpoints);
            _certificate = certificate;

            pairing.SetListening(true);
            logger.LogActivity(LogLevel.Information, "mobile.listening", "Mobile access", "The remote sensor listener is ready.");
        }
        catch
        {
            certificate.Dispose();
            throw;
        }
    }

    private async Task StopListenerAsync()
    {
        if (_listener is not { } listener)
        {
            return;
        }

        pairing.SetListening(false);
        _simulationWarnings?.Clear();

        _listener = null;

        try
        {
            await listener.StopAsync();
        }
        finally
        {
            await listener.DisposeAsync();

            _certificate?.Dispose();
            _certificate = null;
        }

        logger.LogActivity(LogLevel.Information, "mobile.stopped", "Mobile access", "The remote sensor listener stopped.");
    }

    private MobileAccessStatus GetStatus()
    {
        var settings = pairing.Data.Settings;

        return new MobileAccessStatus(settings.Enabled, _listener is not null, settings.Port, settings.NoIp?.Hostname,
            noIp.GetStatus(settings.NoIp), settings.OpenFirewall, settings.PortForwarding,
            pairing.GetPendingApproval(), pairing.GetDevices(), _error, settings.LocalAddress, settings.PublicAddress, settings.WarningHistoryDays);
    }

    private void RecordFailure(string message, Exception error)
    {
        _error = message;

        var cause = error.GetBaseException();

        logger.LogActivity(LogLevel.Warning, "mobile.setup-failed", "Mobile access", message,
            detail: $"{cause.GetType().Name} (0x{cause.HResult:X8})", exception: error);
    }

    private static bool IsAccessFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or CryptographicException or COMException
            or InvalidOperationException or ArgumentException or System.Text.Json.JsonException
            or SocketException or System.Security.SecurityException
            or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException;
}
