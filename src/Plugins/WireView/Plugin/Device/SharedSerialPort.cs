using System.IO.Ports;

namespace RTXLens.Plugins.WireView.Device;

internal sealed class SharedSerialPort(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits)
    : SerialPort(portName, baudRate, parity, dataBits, stopBits), IWireViewPort
{
    private readonly Mutex _mutex = new(false, "Global\\Access_USB_Sensors");
    private bool _hasMutex;

    public new bool Open()
    {
        if (_hasMutex)
        {
            return true;
        }

        var acquired = false;
        try
        {
            try
            {
                acquired = _mutex.WaitOne(2000);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                return false;
            }

            base.Open();
            _hasMutex = true;

            return true;
        }
        catch
        {
            if (acquired)
            {
                _mutex.ReleaseMutex();
            }

            throw;
        }
    }

    public new void Close()
    {
        if (!_hasMutex)
        {
            return;
        }

        // SerialPort.Close calls Dispose; use the base implementation directly to avoid re-entering this override.
        try
        {
            base.Dispose(true);
        }
        finally
        {
            _hasMutex = false;
            _mutex.ReleaseMutex();
        }
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            if (!disposing)
            {
                return;
            }

            try
            {
                Close();
            }
            finally
            {
                _mutex.Dispose();
            }
        }
        finally
        {
            base.Dispose(disposing);
        }
    }
}