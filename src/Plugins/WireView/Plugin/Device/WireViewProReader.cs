using RTXLens.Plugins.WireView.Contracts.Common;
using System.IO.Ports;
using System.Text;
using System.Security.Cryptography;
using RTXLens.Core.Configuration;

namespace RTXLens.Plugins.WireView.Device;

// ReSharper disable once ContextualLoggerProblem
public sealed class WireViewProReader(ILogger<WireViewPlugin> logger) : IWireViewDeviceReader
{
    private const int Baud = 115200;
    private const int StructSize = 100;
    private const string WelcomeString = "Thermal Grizzly WireView Pro II";

    private const int ReadTimeoutMs = 500;
    private const int LockTimeoutMs = 1000;

    private const int OffTs = 0;
    private const int OffReadings = 12;
    private const int OffTotalPower = 84;
    private const int OffTotalCurrent = 88;
    private const int OffAverageVoltage = 92;
    private const int OffHpwrCap = 94;
    private const int OffFaultStatus = 96;
    private const int OffFaultLog = 98;

    private const int PsVoltage = 0;
    private const int PsCurrent = 4;
    private const int PsSize = 12;


    private const byte CmdReadVendorData = 0x01;
    private const byte CmdReadUid = 0x02;
    private const byte CmdReadSensors = 0x04;
    private const byte CmdReadConfig = 0x05;
    private const byte CmdWriteConfig = 0x06;
    private const byte CmdNvmConfig = 0xF2;
    private const byte NvmLoad = 1;
    private const byte NvmStore = 2;

    // Protocol IDs, not Windows USB VID/PID pairs
    private static readonly Dictionary<(byte VendorId, byte ProductId), string> SupportedDevices = new()
    {
        [(0xEF, 0x05)] = "WireView Pro II",
        [(0xEF, 0x06)] = "WireView Pro II Noctua Edition"
    };

    private readonly object _portGate = new();
    private SharedSerialPort? _port;

    private bool _handshakeComplete;
    private byte _firmwareVersion;
    public string PortName { get; private set; } = string.Empty;

    public WireViewSensorData? TryReadSensors(CancellationToken token) => ReadAvailableSensors(token);
    public void Dispose() => DisposePort();
    public void Reset() => DisposePort();

    private void DisposePort()
    {
        lock (_portGate)
        {
            try
            {
                _port?.Dispose();
            }
            catch
            {
                // ignored
            }

            _port = null;
            PortName = string.Empty;
            _handshakeComplete = false;
        }
    }

    private WireViewSensorData? ReadAvailableSensors(CancellationToken token)
    {
        lock (_portGate)
        {
            var ports = FindComPorts();

            // Keep the active device first instead of switching with enumeration order.
            if (ports.Remove(PortName))
            {
                ports.Insert(0, PortName);
            }

            foreach (var portName in ports)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    if (_port is null || PortName != portName)
                    {
                        DisposePort();
                        PortName = portName;
                        _port = new SharedSerialPort(portName, Baud, Parity.None, 8, StopBits.One)
                        {
                            ReadTimeout = ReadTimeoutMs,
                            WriteTimeout = ReadTimeoutMs
                        };
                    }

                    var data = ReadSensors();
                    if (data is not null)
                    {
                        return data;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Error probing or reading WireView on {Port}", portName);
                }

                DisposePort();
            }

            DisposePort();
            return null;
        }
    }

    private WireViewSensorData? ReadSensors()
    {
        if (_port is null)
        {
            return null;
        }

        if (!Monitor.TryEnter(_portGate, LockTimeoutMs))
        {
            logger.LogDebug("Could not acquire port lock on {Port}, skipping poll", PortName);
            return null;
        }

        try
        {
            if (_port is null || !_port.Open())
            {
                logger.LogDebug("Could not open WireView candidate {Port}", PortName);
                return null;
            }

            if (!_handshakeComplete)
            {
                if (!Handshake(_port))
                {
                    _port.Close();
                    return null;
                }

                _handshakeComplete = true;
            }

            _port.DiscardInBuffer();
            _port.Write([CmdReadSensors], 0, 1);

            var buffer = ReadExact(_port, StructSize);
            _port.Close();

            if (buffer is null)
            {
                _handshakeComplete = false;
                logger.LogDebug("Incomplete WireView sensor response on {Port}", PortName);
            }

            return buffer is null ? null : Parse(buffer);
        }
        finally
        {
            _port?.Close();
            Monitor.Exit(_portGate);
        }
    }

    public bool ClearFaults(ushort faultStatusMask = 0xFFFF, ushort faultLogMask = 0xFFFF)
    {
        if (_port is null)
        {
            logger.LogWarning("Cannot clear faults - no port available");
            return false;
        }

        if (!Monitor.TryEnter(_portGate, LockTimeoutMs))
        {
            logger.LogWarning("Could not acquire port lock on {Port}, skipping fault clear", PortName);
            return false;
        }

        try
        {
            if (_port is null || !_port.Open())
            {
                return false;
            }

            if (!Handshake(_port))
            {
                return false;
            }

            _port.DiscardInBuffer();

            _port.Write([
                14,
                (byte)(faultStatusMask & byte.MaxValue),
                (byte)(faultStatusMask >> 8 & byte.MaxValue),
                (byte)(faultLogMask & byte.MaxValue),
                (byte)(faultLogMask >> 8 & byte.MaxValue)
            ], 0, 5);

            _port.Close();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clear faults on {Port}", PortName);
            return false;
        }
        finally
        {
            _port?.Close();
            Monitor.Exit(_portGate);
        }
    }

    public DeviceConfig? ReadConfig() => WithConfigPort((port, payload) =>
        ParseConfig(payload, payload[2]) with { EditToken = ReadEditToken(port, payload), FirmwareVersion = _firmwareVersion });

    public byte[]? ReadConfigRaw() => WithConfigPort((_, payload) => payload);

    public DeviceConfig ApplyConfig(DeviceConfig changes) => WithConfigPort((port, original) =>
    {
        var token = ReadEditToken(port, original);
        if (token is null)
        {
            throw new ArgumentException("This WireView firmware did not provide a complete, editable configuration.");
        }

        if (!string.Equals(token, changes.EditToken, StringComparison.Ordinal))
        {
            throw new ConfigurationConflictException();
        }

        var desired = WireViewConfigCodec.Patch(original, changes, _firmwareVersion);
        if (desired.AsSpan().SequenceEqual(original))
        {
            return ParseConfig(original, original[2]) with { EditToken = token, FirmwareVersion = _firmwareVersion };
        }

        var saved = WriteAndVerifyConfig(desired, frame => port.Write(frame, 0, frame.Length),
            () => ReadConfigPayload(port), Thread.Sleep);

        return ParseConfig(saved, saved[2]) with { EditToken = ReadEditToken(port, saved), FirmwareVersion = _firmwareVersion };
    }) ?? throw new IOException("Could not read WireView settings before applying changes.");

    private T? WithConfigPort<T>(Func<SharedSerialPort, byte[], T> operation) where T : class
    {
        if (!Monitor.TryEnter(_portGate, LockTimeoutMs))
        {
            return null;
        }

        try
        {
            if (_port is not { } port || !port.Open() || !Handshake(port))
            {
                return null;
            }

            var payload = ReadConfigPayload(port);
            return payload is null ? null : operation(port, payload);
        }
        finally
        {
            _port?.Close();
            Monitor.Exit(_portGate);
        }
    }

    private static byte[]? ReadConfigPayload(SharedSerialPort port)
    {
        port.DiscardInBuffer();
        port.Write([CmdReadConfig], 0, 1);

        var header = ReadExact(port, 4);
        if (header is null)
        {
            return null;
        }

        var size = WireViewConfigCodec.Size(header[2]);
        if (size == 0)
        {
            return null;
        }

        var payload = new byte[size];
        header.CopyTo(payload, 0);
        var length = 4 + ReadInto(port, payload, 4);

        if (port.BytesToRead > 0)
        {
            return null;
        }

        if (length == size)
        {
            return payload;
        }

        // Keep the original read-only layouts. A truncated write layout is never editable.
        var legacySize = header[2] switch { 0 => 64, 1 => 72, 2 => 88, _ => 0 };
        return length == legacySize ? payload[..length] : null;
    }

    private string? ReadEditToken(SharedSerialPort port, byte[] payload)
    {
        if (_firmwareVersion < WireViewSettingsValidation.MinimumWritableFirmware || payload.Length != WireViewConfigCodec.Size(payload[2]))
        {
            return null;
        }

        port.DiscardInBuffer();
        port.Write([CmdReadUid], 0, 1);
        var uid = ReadExact(port, 12);

        return uid is null || uid.All(value => value == 0) || uid.All(value => value == byte.MaxValue)
            ? null : Convert.ToHexString(SHA256.HashData([_firmwareVersion, .. uid, .. payload.AsSpan(2)]));
    }

    internal static byte[] WriteAndVerifyConfig(byte[] desired, Action<byte[]> send, Func<byte[]?> read, Action<int> delay)
    {
        foreach (var frame in ConfigWriteFrames(desired))
        {
            send(frame);
        }

        ReadBack(desired, read, delay);
        send(NvmFrame(NvmStore));
        delay(250);

        send(NvmFrame(NvmLoad));
        return ReadBack(desired, read, delay);
    }

    private static IEnumerable<byte[]> ConfigWriteFrames(byte[] payload)
    {
        const int chunkSize = 62;
        for (var offset = 0; offset < payload.Length; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, payload.Length - offset);
            var frame = new byte[count + 2];
            frame[0] = CmdWriteConfig;
            frame[1] = (byte)offset;
            payload.AsSpan(offset, count).CopyTo(frame.AsSpan(2));
            yield return frame;
        }
    }

    private static byte[] NvmFrame(byte command) => [CmdNvmConfig, 0x55, 0xAA, 0x55, 0xAA, command];

    private static byte[] ReadBack(byte[] expected, Func<byte[]?> read, Action<int> delay)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            delay(75);
            var actual = read();
            if (actual?.Length == expected.Length && actual.AsSpan(2).SequenceEqual(expected.AsSpan(2)))
            {
                return actual;
            }
        }

        throw new IOException("WireView did not return the expected settings. Refresh before trying again.");
    }

    private bool Handshake(SharedSerialPort port)
    {
        _handshakeComplete = false;
        var responseSize = WelcomeString.Length + 1; // 32 bytes

        port.DiscardInBuffer();
        byte[]? buffer;

        try
        {
            port.RtsEnable = true;
            port.Write(Array.Empty<byte>(), 0, 0);
            buffer = ReadExact(port, responseSize);
        }
        finally
        {
            port.RtsEnable = false;
        }

        if (buffer is null)
        {
            logger.LogDebug("No complete WireView welcome response on {Port}", port.PortName);
            return false;
        }

        var response = Encoding.ASCII.GetString(buffer, 0, responseSize).TrimEnd('\0');
        if (!string.Equals(response, WelcomeString, StringComparison.Ordinal))
        {
            logger.LogDebug("Unexpected WireView welcome response on {Port}: {Response}", port.PortName, response);
            return false;

        }

        port.DiscardInBuffer();
        port.Write([CmdReadVendorData], 0, 1);

        var vendorData = ReadExact(port, 3);
        if (vendorData is null)
        {
            logger.LogDebug("Incomplete WireView vendor response on {Port}", port.PortName);
            return false;
        }

        if (!SupportedDevices.TryGetValue((vendorData[0], vendorData[1]), out var model))
        {
            logger.LogDebug("Unsupported WireView device on {Port}: vendor {VendorId:X2}, product {ProductId:X2}",

                port.PortName, vendorData[0], vendorData[1]);
            return false;
        }

        _firmwareVersion = vendorData[2];
        logger.LogInformation("Identified {Model} on {Port}, firmware {FirmwareVersion}", model, port.PortName, vendorData[2]);
        _handshakeComplete = true;

        return true;
    }

    private static byte[]? ReadExact(SerialPort port, int size)
    {
        var buffer = new byte[size];
        return ReadInto(port, buffer, 0) == size ? buffer : null;
    }

    private static int ReadInto(SerialPort port, byte[] buffer, int startOffset)
    {
        var offset = startOffset;
        var start = Environment.TickCount;

        while (offset < buffer.Length && Environment.TickCount - start < ReadTimeoutMs)
        {
            if (port.BytesToRead > 0)
            {
                offset += port.Read(buffer, offset, buffer.Length - offset);
            }
            else
            {
                Thread.Sleep(1); // yield CPU instead of spinning
            }
        }

        return offset - startOffset;
    }

    internal static WireViewSensorData Parse(byte[] b)
    {
        var tempIn = ReadI16(b, OffTs + 0) / 10.0;
        var tempOut = ReadI16(b, OffTs + 2) / 10.0;
        var extTemp1 = ReadI16(b, OffTs + 4) / 10.0;
        var extTemp2 = ReadI16(b, OffTs + 6) / 10.0;

        var voltage = new double[6];
        var current = new double[6];
        var power = new double[6];

        for (var i = 0; i < 6; i++)
        {
            var @base = OffReadings + i * PsSize;
            voltage[i] = ReadI16(b, @base + PsVoltage) / 1000.0; // mV → V
            current[i] = ReadU32(b, @base + PsCurrent) / 1000.0; // mA → A
            power[i] = voltage[i] * current[i];
        }

        var psuCapW = b[OffHpwrCap] switch { 0 => 600, 1 => 450, 2 => 300, 3 => 150, _ => 0 };

        return new WireViewSensorData(
            voltage, current, power,
            tempIn, tempOut, extTemp1, extTemp2,
            psuCapW,
            ReadU16(b, OffFaultStatus),
            ReadU16(b, OffFaultLog))
        {
            ReportedAverageVoltageV = ReadU16(b, OffAverageVoltage) / 1000.0,
            ReportedTotalPowerW = ReadU32(b, OffTotalPower) / 1000.0,
            ReportedTotalCurrentA = ReadU32(b, OffTotalCurrent) / 1000.0
        };
    }

    internal static DeviceConfig ParseConfig(byte[] b, int version)
    {
        var friendlyName = Encoding.ASCII.GetString(b, 3, 32).TrimEnd('\0');

        // 1 byte padding at [35] for FanConfig alignment
        var fanMode = b[36];
        var tempSource = b[37];
        var dutyMin = b[38];
        var dutyMax = b[39];
        var fanTempMin = ReadI16(b, 40) / 10.0;
        var fanTempMax = ReadI16(b, 42) / 10.0;

        var backlightDuty = b[44];
        // 1 byte padding at [45] for ushort alignment

        var faultDisplayEnable = ReadU16(b, 46);
        var faultBuzzerEnable = ReadU16(b, 48);
        var faultSoftPowerEnable = ReadU16(b, 50);
        var faultHardPowerEnable = ReadU16(b, 52);
        var tsFaultThreshold = ReadI16(b, 54) / 10.0;
        var ocpFaultThreshold = b[56];
        var wireOcpFaultThreshold = b[57];
        var oppFaultThreshold = ReadU16(b, 58);
        var currentImbalanceThreshold = b[60];
        var currentImbalanceMinLoad = b[61];
        var shutdownWaitTime = b[62];
        var loggingInterval = b[63];

        var averaging = version >= 1 && b[64] < DeviceConfig.AveragingOptions.Count
            ? DeviceConfig.AveragingOptions[b[64]] : null;

        return new DeviceConfig
        {
            Version = version,
            Display = WireViewConfigCodec.ReadDisplay(b, version),
            FriendlyName = friendlyName,
            BacklightDuty = backlightDuty,
            LoggingInterval = loggingInterval,
            Averaging = averaging,
            FanMode = fanMode == 0 ? "Curve" : "Fixed",
            FanTempSource = tempSource switch
            {
                0 => "TsIn", 1 => "TsOut", 2 => "Ts1", 3 => "Ts2", 4 => "Tmax", _ => "Unknown"
            },
            FanDutyMin = dutyMin,
            FanDutyMax = dutyMax,
            FanTempMinC = fanTempMin,
            FanTempMaxC = fanTempMax,
            TsFaultThresholdC = tsFaultThreshold,
            OcpFaultThresholdA = ocpFaultThreshold,
            WireOcpFaultThresholdA = wireOcpFaultThreshold / 10.0,
            OppFaultThresholdW = oppFaultThreshold,
            CurrentImbalanceThreshold = currentImbalanceThreshold,
            CurrentImbalanceMinLoad = currentImbalanceMinLoad,
            ShutdownWaitTimeS = shutdownWaitTime,
            FaultDisplayEnable = faultDisplayEnable,
            FaultBuzzerEnable = faultBuzzerEnable,
            FaultSoftPowerEnable = faultSoftPowerEnable,
            FaultHardPowerEnable = faultHardPowerEnable
        };
    }

    private static short ReadI16(byte[] b, int o) => (short)(b[o] | b[o + 1] << 8);
    private static ushort ReadU16(byte[] b, int o) => (ushort)(b[o] | b[o + 1] << 8);
    private static uint ReadU32(byte[] b, int o) => (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);

    private List<string> FindComPorts()
    {
        try
        {
            var ports = Stm32PortFinder.FindMatchingComPorts();
            if (ports.Count == 0)
            {
                logger.LogDebug("No matching WireView COM ports found");
            }

            return ports;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to enumerate COM ports");
            return [];
        }
    }
}
