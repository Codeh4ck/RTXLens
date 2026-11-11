using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RTXLens.Presentation.Telemetry;

namespace RTXLens.App.Controls;

public sealed partial class MetricTile : UserControl
{
    public MetricViewModel Model { get; }
    public bool ShowIcon { get; }
    public string DisplayLabel => Model.Label switch { "Core temperature" => "Core temp", "VRAM temperature" => "VRAM temp", _ => Model.Label };

    public MetricTile(MetricViewModel model, bool showIcon = true, double valueMaxWidth = 210, bool usePanelBorder = false,
        bool singleLineValue = false, UIElement? valueAction = null)
    {
        Model = model;
        ShowIcon = showIcon;
        InitializeComponent();
        ValueText.MaxWidth = valueMaxWidth;
        if (singleLineValue)
        {
            ValueText.TextWrapping = TextWrapping.NoWrap;
            ValueText.TextTrimming = TextTrimming.CharacterEllipsis;
        }

        if (valueAction is not null)
        {
            ValueActionHost.Content = valueAction;
            ValueActionHost.Visibility = Visibility.Visible;
        }

        if (usePanelBorder)
        {
            Frame.ClearValue(Border.BorderBrushProperty);
        }

        if (!ShowIcon)
        {
            Grid.SetColumn(MetricLabel, 0);
            Grid.SetColumnSpan(MetricLabel, 2);
        }
        Loaded += (_, _) =>
        {
            Model.PropertyChanged += OnModelChanged;
            UpdateTone();
        };
        Unloaded += (_, _) => Model.PropertyChanged -= OnModelChanged;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Model.Label))
        {
            MetricLabel.Text = DisplayLabel;
            ToolTipService.SetToolTip(MetricLabel, Model.Label);
        }

        if (args.PropertyName == nameof(Model.Tone))
        {
            UpdateTone();
        }
    }

    private void UpdateTone() => VisualStateManager.GoToState(this, Model.Tone.ToString(), false);
}
