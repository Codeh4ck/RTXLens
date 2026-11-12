using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using RTXLens.App.Controls;
using RTXLens.App.Views.Shell;
using RTXLens.SafeguardReporting;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Safeguard;

internal sealed class SafeguardExecutionReportWindow : LensWindow
{
    public SafeguardExecutionReportWindow(SafeguardReport report)
    {
        Title = "RTX Lens - Safeguard+ execution";
        Width = 780;
        Height = 700;
        MinWidth = 510;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var status = Alert();
        var export = ActionButton("Export HTML report", "chart-export", async void () =>
        {
            try
            {
                var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Export Safeguard+ report",
                    SuggestedFileName = $"rtxlens-safeguard-{report.Id:N}.html",
                    FileTypeChoices = [new FilePickerFileType("HTML report") { Patterns = ["*.html"] }]
                });
                if (target is null)
                {
                    return;
                }

                var (html, _) = SafeguardReportRenderer.Render(report);
                await using var stream = await target.OpenWriteAsync();
                stream.SetLength(0);
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(html);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
            {
                SetAlert(status, error.Message, "error");
            }
        });
        export.HorizontalAlignment = HorizontalAlignment.Right;

        var sections = new StackPanel { Spacing = 12 };
        foreach (var block in SafeguardReportRenderer.Describe(report))
        {
            sections.Children.Add(CreateBlock(block));
        }

        var scroll = Scroll(new Border { Child = sections, Padding = new Thickness(0, 0, 14, 0) });
        var body = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("Auto,Auto,*"), RowSpacing = 8 };
        body.Children.Add(export);
        Grid.SetRow(status, 1);
        body.Children.Add(status);
        Grid.SetRow(scroll, 2);
        body.Children.Add(scroll);
        Body = body;
    }

    private static Control CreateBlock(SafeguardReportBlock block) => block switch
    {
        SafeguardReportHeading heading => CreateHeading(heading),
        SafeguardReportNotice { IsWarning: true } notice => Message(notice.Text, "warning"),
        SafeguardReportNotice notice => Caption(notice.Text),
        SafeguardReportRows rows => Section(rows.Title, WithSubtitle(rows.Subtitle, rows.Rows.Select(CreateRow))),
        SafeguardReportPinChart chart => CreateChart(chart),
        _ => throw new ArgumentOutOfRangeException(nameof(block))
    };

    private static Control CreateHeading(SafeguardReportHeading heading)
    {
        var title = new TextBlock { Text = heading.Title, FontSize = heading.Subtitle is null ? 19 : 25, FontWeight = FontWeight.SemiBold };
        var content = new StackPanel { Spacing = 4, Children = { title } };
        if (heading.Subtitle is { } subtitle)
        {
            content.Children.Add(Caption(subtitle));
        }

        return content;
    }

    private static Control CreateRow((string Label, string Value) row)
    {
        var label = Caption(row.Label);
        label.TextWrapping = TextWrapping.Wrap;
        var value = new SelectableTextBlock
        {
            Text = row.Value,
            TextAlignment = TextAlignment.Right,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,3*"), ColumnSpacing = 12, Children = { label, value } };
        Grid.SetColumn(value, 1);
        return grid;
    }

    private static SectionPanel CreateChart(SafeguardReportPinChart chart)
    {
        var plot = new UniformGrid { Columns = chart.Pins.Count };
        foreach (var pin in chart.Pins)
        {
            plot.Children.Add(new PinBar { Label = pin.Label, Reading = pin.Value, FillFraction = pin.Fraction });
        }

        var scale = Caption($"Scale {chart.Minimum:0.##} to {chart.Maximum:0.##} {chart.Unit}");
        return Section(chart.Title, WithSubtitle(chart.Subtitle, [scale, plot]));
    }

    private static StackPanel WithSubtitle(string? subtitle, IEnumerable<Control> controls)
    {
        var content = new StackPanel { Spacing = 8 };
        if (subtitle is not null)
        {
            var caption = Caption(subtitle);
            caption.TextWrapping = TextWrapping.Wrap;
            content.Children.Add(caption);
        }

        foreach (var control in controls)
        {
            content.Children.Add(control);
        }

        return content;
    }
}
