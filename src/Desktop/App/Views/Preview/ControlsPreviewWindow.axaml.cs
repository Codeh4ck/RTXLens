using Avalonia.Controls;
using Avalonia.Interactivity;
using RTXLens.App.ViewModels.Preview;
using RTXLens.App.Views.Shell;

namespace RTXLens.App.Views.Preview;

internal sealed partial class ControlsPreviewWindow : LensWindow
{
    private ChartsPreviewWindow? _chartsPreview;

    public ControlsPreviewWindow() => InitializeComponent();

    private void OpenCharts(object? sender, RoutedEventArgs e)
    {
        if (_chartsPreview is { } existing)
        {
            existing.Activate();
            return;
        }

        _chartsPreview = new ChartsPreviewWindow();
        _chartsPreview.Closed += (_, _) => _chartsPreview = null;
        _chartsPreview.Show(this);
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is ControlsPreviewViewModel preview)
        {
            await preview.Appearance.LoadAsync();
        }
    }
}