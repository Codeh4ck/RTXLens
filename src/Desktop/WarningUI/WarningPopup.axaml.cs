using Avalonia;
using Avalonia.Controls;

namespace RTXLens.WarningUI;

public sealed partial class WarningPopup : Window
{
    public WarningPopup() : this(null) { }

    public WarningPopup(Action? viewWarnings)
    {
        InitializeComponent();
        ViewWarnings.IsVisible = viewWarnings is not null;
        ViewWarnings.Click += (_, _) => viewWarnings?.Invoke();
        Dismiss.Click += (_, _) => Close();
        Opened += (_, _) => Place();
        SizeChanged += (_, _) => Place();
    }

    public void Update(string heading, string detail)
    {
        Heading.Text = heading;
        Details.Text = detail;
    }

    private void Place()
    {
        if (Screens.Primary is not { } screen)
        {
            return;
        }

        var bounds = screen.WorkingArea;
        var scale = screen.Scaling;
        Position = new PixelPoint(bounds.Right - (int)((Width + 12) * scale),
            bounds.Bottom - (int)((Bounds.Height + 12) * scale));
    }
}
