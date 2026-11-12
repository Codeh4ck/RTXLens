using HidSharp;
using Microsoft.Extensions.Logging;
using RTXLens.Plugins.Msi.Contracts.Configuration;
using RTXLens.Plugins.Msi.Contracts.Devices;
using RTXLens.Plugins.Msi.Contracts.Telemetry;
using RTXLens.Plugins.Msi.Control;
using RTXLens.Plugins.Msi.Protocol;

namespace RTXLens.Plugins.Msi.Device;

public sealed class MsiPsuReader(ILogger<MsiPsuReader> logger) : IMsiPsuReader
{
    private readonly MsiFanControlSession _fan = new(logger);
    private MsiPsuTransport? _transport;
    private string? _deviceId;
    private string _model = "";
    private string _revision = "";

    public MsiDeviceInfo[] Devices => DiscoverDevices();

    internal static MsiDeviceInfo[] DiscoverDevices() => OperatingSystem.IsWindows()
        ? [.. SupportedDevices().Select(device => new MsiDeviceInfo(device.DevicePath, "MSI " + MsiPsuModels.Name(device.VendorID, device.ProductID)))]
        : [];

    private static HidDevice[] SupportedDevices() =>
    [
        .. DeviceList.Local.GetHidDevices(MsiPsuModels.VendorId)
            .Where(device => MsiPsuModels.Name(device.VendorID, device.ProductID) is not null)
    ];

    public MsiSnapshot? Read(MsiOptions options, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        options.Validate();

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var devices = SupportedDevices();
        var device = options.DevicePath is { } path
            ? devices.SingleOrDefault(item => item.DevicePath == path)
            : devices.Length == 1 ? devices[0] : null;

        if (device is null)
        {
            Disconnect();
            return null;
        }

        try
        {
            if (_deviceId != device.DevicePath || _transport is null)
            {
                Disconnect();
                _transport = new MsiPsuTransport(device);

                ReadPacket(MsiProtocol.Handshake(), 1, cancellationToken);
                _model = MsiProtocol.Text(ReadRegister(0x11, 1, cancellationToken));
                _revision = MsiProtocol.Text(ReadRegister(0x12, 1, cancellationToken));

                if (!MsiPsuModels.Matches(device.ProductID, _model))
                {
                    throw new IOException("The connected PSU model does not match a supported MSI MPG TS device.");
                }

                _deviceId = device.DevicePath;
            }

            // Control only uses fresh, physical PSU readings from this transaction cycle.
            // Desktop simulation and the consumer sensor stream never enter this path.
            var freshness = System.Diagnostics.Stopwatch.StartNew();
            var temperature = MsiProtocol.Linear11(ReadRegister(0x30, 2, cancellationToken), 3);
            var currents = MsiProtocol.Currents(ReadRegister(0xE0, 30, cancellationToken));
            var rpm = MsiProtocol.Linear11(ReadRegister(0x40, 2, cancellationToken), 3);

            if (!double.IsFinite(temperature) || temperature is < 0 or > 125
                || !double.IsFinite(rpm) || rpm < 0 || currents.SelectMany(pins => pins).Any(value => !double.IsFinite(value) || value < 0))
            {
                throw new IOException("Invalid MSI temperature, current or fan telemetry.");
            }

            if (freshness.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new IOException("The MSI fan control sample expired before it could be applied.");
            }

            _fan.Update(options, temperature, observedAt, (packet, length) => ReadPacket(packet, length, cancellationToken));

            var mode = ReadRegister(0x41, 1, cancellationToken)[3];
            var duty = ReadRegister(0x42, 4, cancellationToken)[3];

            if (mode is not (1 or 3) || duty > 100)
            {
                throw new IOException("Unknown MSI fan mode or duty response.");
            }

            return new MsiSnapshot(_deviceId!, _model, _revision, observedAt, currents, temperature, rpm, mode == 1, duty)
            {
                TargetFanDutyPercent = _fan.TargetDuty,
                FanControllerState = _fan.State(options),
                FanControllerMessage = _fan.Failure
            };
        }
        catch (Exception exception)
        {
            if (options.FanControl != MsiFanControl.Unmanaged && _transport is not null && _deviceId is not null)
            {
                _fan.Fail(exception, ExchangeWithoutCancellation);
            }

            Disconnect();
            throw;
        }
    }

    public void ReleaseFanControl()
    {
        if (_transport is not null)
        {
            _fan.Release(ExchangeWithoutCancellation);
        }

        _fan.ResetFailure();
    }

    private byte[] ExchangeWithoutCancellation(byte[] request, int length) => ReadPacket(request, length, CancellationToken.None);
    private byte[] ReadRegister(byte register, int length, CancellationToken token) => ReadPacket(MsiProtocol.ReadRequest(register), length, token);
    private byte[] ReadPacket(byte[] request, int length, CancellationToken token) => (_transport ?? throw new IOException("MSI PSU disconnected.")).Exchange(request, length, token);

    private void Disconnect()
    {
        try
        {
            if (_transport is not null)
            {
                _fan.Release(ExchangeWithoutCancellation);
            }
        }
        catch (Exception exception)
        {
            _fan.LogRestoreFailure(exception);
        }
        finally
        {
            _transport?.Dispose();
            _transport = null;
            _deviceId = null;
            _fan.Disconnected();
        }
    }

    public void Dispose() => Disconnect();
}
