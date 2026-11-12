using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RTXLens.App.Views.Shell;

internal sealed partial class WindowFrame : UserControl
{
    private readonly Window _owner;

    public WindowFrame(Window owner)
    {
        _owner = owner;
        InitializeComponent();

        UpdateCaption();
        _owner.PropertyChanged += (_, args) =>
        {
            if (args.Property == Window.TitleProperty || args.Property == Window.WindowStateProperty
                || args.Property == Window.CanResizeProperty || args.Property == Window.CanMaximizeProperty
                || args.Property == Window.CanMinimizeProperty)
            {
                UpdateCaption();
            }
        };
    }

    public Control? Body
    {
        get => (Control?)BodyHost.Content;
        set => BodyHost.Content = value;
    }

    private void MinimizeWindow(object? sender, RoutedEventArgs args) => _owner.WindowState = WindowState.Minimized;

    private void ToggleMaximize(object? sender, RoutedEventArgs args) =>
        _owner.WindowState = _owner.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow(object? sender, RoutedEventArgs args) => _owner.Close();

    private void UpdateCaption()
    {
        var maximized = _owner.WindowState == WindowState.Maximized;
        var label = maximized ? "Restore" : "Maximize";

        CaptionTitle.Text = _owner.Title;
        FrameBorder.BorderThickness = new Thickness(maximized ? 0 : 1);
        MinimizeButton.IsVisible = _owner.CanMinimize;
        MaximizeButton.IsVisible = _owner.CanMaximize;
        MaximizeIcon.IsVisible = !maximized;
        RestoreIcon.IsVisible = maximized;
        AutomationProperties.SetName(MaximizeButton, label);
        ToolTip.SetTip(MaximizeButton, label);
    }
}
