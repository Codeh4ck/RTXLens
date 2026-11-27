using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using RTXLens.App.Views.Shell;
using RTXLens.Contracts.Warnings;
using RTXLens.WarningUI;
using static RTXLens.App.Views.Shell.DesktopControls;
using static RTXLens.App.Views.Diagnostics.WarningVisuals;

namespace RTXLens.App.Views.Diagnostics;

internal sealed class WarningIncidentWindow : LensWindow
{
    public WarningIncidentWindow(string source, string heading, WarningIncident incident, bool fahrenheit)
    {
        Title = "Warning info";
        Width = 780;
        Height = 680;
        MinWidth = 420;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var warning = PopupWarning.FromIncident(incident, source, heading);
        var status = Badge(incident.Status);
        var title = Field(source, heading, 23);
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12,
            Children = { Glyph(incident.Kind == "safeguard" ? Tone(incident.Status) : "error", 44, incident.Kind == "safeguard"), title, status }
        };
        Grid.SetColumn(title, 1);
        Grid.SetColumn(status, 2);

        var summary = new StackPanel { Spacing = 12, Children = { header } };
        if (warning.Detail.Length > 0)
        {
            summary.Children.Add(DialogText(warning.Detail));
        }

        if (warning.Context.Length > 0)
        {
            summary.Children.Add(Caption(warning.Context));
        }

        if (incident.Status == "Interrupted")
        {
            summary.Children.Add(Alert(InterruptedExplanation, "warning"));
        }

        var timing = new WrapPanel { ItemSpacing = 22, LineSpacing = 10,
            Children = { Field("Started", incident.StartedAt.LocalDateTime.ToString("G")) } };
        if (incident.EndedAt is { } end)
        {
            timing.Children.Add(Field("Ended", end.LocalDateTime.ToString("G")));
            if (end >= incident.StartedAt)
            {
                var duration = end - incident.StartedAt;
                timing.Children.Add(Field("Duration", duration.Days == 0 ? duration.ToString(@"hh\:mm\:ss") : duration.ToString("c")));
            }
        }
        if (incident.Connector is { } connector)
        {
            timing.Children.Add(Field("Connector", connector.ToString()));
        }

        if (incident.DelayMs is { } ms)
        {
            timing.Children.Add(Field("Delay", $"{ms} ms"));
        }

        if (!string.IsNullOrWhiteSpace(incident.DeliveryStatus))
        {
            timing.Children.Add(Field("Delivery", incident.DeliveryStatus));
        }

        if (!string.IsNullOrWhiteSpace(incident.DeviceId))
        {
            timing.Children.Add(Field("Device", incident.DeviceId));
        }

        summary.Children.Add(new Border
        {
            Padding = new Thickness(12), CornerRadius = new CornerRadius(6),
            [!Border.BackgroundProperty] = new DynamicResourceExtension("InsetBrush"),
            Child = timing
        });

        var body = new StackPanel { Spacing = 12, Children =
        {
            new Border { Classes = { "panel-frame" }, Padding = new Thickness(16), Child = summary }
        } };
        if (incident.Steps.Length > 0)
        {
            var steps = new StackPanel { Spacing = 8 };
            foreach (var step in incident.Steps)
            {
                steps.Children.Add(Field($"{step.Timestamp.LocalDateTime:T} · {step.Name}",
                    step.Result + (step.ExitCode is { } code ? $" (exit {code})" : "")));
            }

            body.Children.Add(Section("Execution steps", steps));
        }
        body.Children.Add(new WarningSnapshotView(incident, warning, fahrenheit));

        var close = ActionButton("Close", "cancel-action", Close);
        close.IsCancel = true;
        close.HorizontalAlignment = HorizontalAlignment.Right;
        var layout = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 10 };
        layout.Children.Add(Scroll(body));
        Grid.SetRow(close, 1);
        layout.Children.Add(close);
        Body = layout;
    }
}
