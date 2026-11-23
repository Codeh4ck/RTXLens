using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RTXLens.Plugins.WireView;
using RTXLens.Plugins.WireView.Device;
using RTXLens.Tests.Infrastructure;

namespace RTXLens.Tests.Core;

internal static class WireViewPollingTests
{
    public static void Run(TestRunner runner)
    {
        runner.Run(HealthyPollsReadFreshFramesWithoutRediscovery);
        runner.Run(RediscoversAfterUnplugAndRejectsAnUnsupportedReplacement);
        runner.Run(RechecksSamePortIdentityAfterFailedOpen);
        runner.Run(CancelledPollDoesNotDiscoverOrOpenPorts);
        runner.Run(ShortFrameDoesNotPublishPartialReadingsAndNextPollRecovers);
    }

    private static void HealthyPollsReadFreshFramesWithoutRediscovery()
    {
        var bus = new FakeBus();
        var device = bus.Plug("COM3");
        using var reader = bus.CreateReader();
        for (ushort volts = 11000; volts <= 12000; volts += 100)
        {
            device.Millivolts = volts;
            TestAssert.Equal(volts / 1000.0, reader.TryReadSensors(CancellationToken.None)?.ReportedAverageVoltageV);
        }

        TestAssert.Equal(11, device.SensorRequests);
        TestAssert.Equal(1, device.IdentityRequests);
        TestAssert.Equal(1, bus.Discoveries);
    }

    private static void RediscoversAfterUnplugAndRejectsAnUnsupportedReplacement()
    {
        var bus = new FakeBus();
        bus.Plug("COM3");
        using var reader = bus.CreateReader();
        TestAssert.Equal(12d, reader.TryReadSensors(CancellationToken.None)?.ReportedAverageVoltageV);

        bus.Devices.Clear();
        TestAssert.True(reader.TryReadSensors(CancellationToken.None) is null, "Unplugging must stop publication.");
        TestAssert.Equal(string.Empty, reader.PortName);

        var other = bus.Plug("COM3", product: 0xff);
        bus.Plug("COM8", product: 0x06).Millivolts = 11950;
        TestAssert.Equal(11.95, reader.TryReadSensors(CancellationToken.None)?.ReportedAverageVoltageV);
        TestAssert.Equal("COM8", reader.PortName);
        TestAssert.Equal(0, other.SensorRequests);
    }

    private static void RechecksSamePortIdentityAfterFailedOpen()
    {
        var bus = new FakeBus();
        bus.Plug("COM3");
        using var reader = bus.CreateReader();
        TestAssert.Equal(12d, reader.TryReadSensors(CancellationToken.None)?.ReportedAverageVoltageV);

        var other = bus.Plug("COM3", product: 0xff);
        bus.FailNextOpen = true;
        TestAssert.True(reader.TryReadSensors(CancellationToken.None) is null,
            "Rediscovery after an open failure must recheck the device identity, even on the same COM number.");
        TestAssert.Equal(2, bus.Discoveries);
        TestAssert.Equal(0, other.SensorRequests);
    }

    private static void ShortFrameDoesNotPublishPartialReadingsAndNextPollRecovers()
    {
        var bus = new FakeBus();
        var device = bus.Plug("COM3");
        device.FrameLength = 96;
        using var reader = bus.CreateReader();
        TestAssert.True(reader.TryReadSensors(CancellationToken.None) is null, "A truncated frame must not publish readings.");
        device.FrameLength = 100;
        TestAssert.Equal(12d, reader.TryReadSensors(CancellationToken.None)?.ReportedAverageVoltageV);
    }

    private static void CancelledPollDoesNotDiscoverOrOpenPorts()
    {
        var bus = new FakeBus();
        bus.Plug("COM4");
        using var reader = bus.CreateReader();
        reader.TryReadSensors(new CancellationToken(canceled: true));
        TestAssert.Equal(0, bus.Discoveries);
        TestAssert.Equal(0, bus.Opens);
    }

    private sealed class Device(byte product)
    {
        public byte Product { get; } = product;
        public int FrameLength { get; set; } = 100;
        public ushort Millivolts { get; set; } = 12000;
        public int SensorRequests { get; set; }
        public int IdentityRequests { get; set; }
    }

    private sealed class FakeBus
    {
        public Dictionary<string, Device> Devices { get; } = [];
        public int Discoveries { get; private set; }
        public int Opens { get; set; }
        public bool FailNextOpen { get; set; }

        public Device Plug(string name, byte product = 0x05) => Devices[name] = new Device(product);

        public WireViewProReader CreateReader() => new(NullLogger<WireViewPlugin>.Instance, () =>
        {
            Discoveries++;
            return [.. Devices.Keys];
        }, name => new Port(this, name));
    }

    private sealed class Port(FakeBus bus, string name) : IWireViewPort
    {
        private readonly Queue<byte> _response = new();
        private Device? _device;
        public string PortName => name;
        public int BytesToRead => _response.Count;
        public bool RtsEnable { get; set; }

        public bool Open()
        {
            bus.Opens++;
            if (bus.FailNextOpen)
            {
                bus.FailNextOpen = false;
                throw new IOException("Simulated disconnect while opening the port.");
            }
            return bus.Devices.TryGetValue(name, out _device);
        }

        public void Close() => _device = null;
        public void Dispose() => Close();
        public void DiscardInBuffer() => _response.Clear();

        public int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, BytesToRead);
            for (var i = 0; i < read; i++)
            {
                buffer[offset + i] = _response.Dequeue();
            }
            return read;
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            var device = _device ?? throw new IOException("The port is closed.");
            if (!bus.Devices.TryGetValue(name, out var connected) || !ReferenceEquals(device, connected))
            {
                throw new IOException("The open device was unplugged.");
            }
            byte[] reply;
            if (RtsEnable && count == 0)
            {
                reply = Encoding.ASCII.GetBytes("Thermal Grizzly WireView Pro II\0");
            }
            else if (count == 1 && buffer[offset] == 0x01)
            {
                device.IdentityRequests++;
                reply = [0xef, device.Product, 7];
            }
            else if (count == 1 && buffer[offset] == 0x04)
            {
                device.SensorRequests++;
                reply = new byte[device.FrameLength];
                BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(92), device.Millivolts);
            }
            else
            {
                throw new InvalidOperationException("Unexpected command during a read-only sensor poll.");
            }

            foreach (var value in reply)
            {
                _response.Enqueue(value);
            }
        }
    }
}
