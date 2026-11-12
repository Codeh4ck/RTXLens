using Avalonia.Controls;

namespace RTXLens.App.Views.Shell;

internal class LensWindow : Window
{
    private readonly WindowFrame _frame;

    public LensWindow()
    {
        _frame = new WindowFrame(this);
        Content = _frame;

        if (OperatingSystem.IsWindows() && TryGetPlatformHandle()?.HandleDescriptor == "HWND")
        {
            const uint resizeFrame = 0x00040000;
            const uint systemMenu = 0x00080000;

            // Keep Windows resize and system-menu behavior with a client-drawn frame.
            Win32Properties.AddWindowStylesCallback(this, (style, extendedStyle) =>
                (style | systemMenu | (CanResize ? resizeFrame : 0), extendedStyle));
        }

        ExtendClientAreaToDecorationsHint = true;
        WindowDecorations = WindowDecorations.None;
    }

    public Control? Body
    {
        get => _frame.Body;
        set => _frame.Body = value;
    }

    protected override Type StyleKeyOverride => typeof(Window);
}
