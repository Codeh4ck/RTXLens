namespace RTXLens.Plugins.WireView.Device;

internal interface IWireViewPort : IDisposable
{
    string PortName { get; }
    int BytesToRead { get; }
    bool RtsEnable { get; set; }
    bool Open();
    void Close();
    void DiscardInBuffer();
    int Read(byte[] buffer, int offset, int count);
    void Write(byte[] buffer, int offset, int count);
}
