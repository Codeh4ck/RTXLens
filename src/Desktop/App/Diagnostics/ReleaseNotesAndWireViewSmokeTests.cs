using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using RTXLens.App.Controls;
using RTXLens.Presentation.Telemetry;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using RTXLens.App.Services;
using RTXLens.App.Updates;
using RTXLens.App.Views;
using RTXLens.Contracts.Updates;
using Windows.Graphics;

namespace RTXLens.App.Diagnostics;

internal static class ReleaseNotesAndWireViewSmokeTests
{
    public static async Task RunAsync(MainWindow window, AppRuntime runtime, string output)
    {
        await VerifyWindowPlacementAsync(window, runtime);

        var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var scale = window.ThemeRoot.XamlRoot.RasterizationScale;
        var width = Math.Min(area.Width, (int)(1400 * scale));
        var height = Math.Min(area.Height, (int)(650 * scale));

        window.Navigate("settings");
        window.AppWindow.Resize(new SizeInt32(width, height));
        await Task.Delay(200);
        var normalSize = window.AppWindow.Size;

        window.ShowPanel("wireview-pro2");
        var view = (HardwareView)window.CurrentView!;
        await Task.Delay(350);
        if (!view.IsLoaded || window.AppWindow.Size != normalSize || view.HiddenContentHeight <= 1)
        {
            throw new InvalidOperationException("A small window must scroll through WireView content without resizing.");
        }

        var headline = (Grid)view.FindName("HeadlineGrid");
        var summaryCards = headline.Children.OfType<ContentControl>().Select(card => ((MetricViewModel)card.Content).Label);
        var gpuStatus = VisualTreeQuery.Descendants<GpuStatusView>(view).Single();
        var temperatures = VisualTreeQuery.Descendants<MetricTile>(gpuStatus).TakeLast(2).Select(tile => tile.Model.Label);
        if (!summaryCards.SequenceEqual(new[] { "Cable rating", "Fault Status", "Fault Log Status" })
            || !temperatures.SequenceEqual(new[] { "Board Temp In/Out", "External Temp 1/2" })
            || ((Border)view.FindName("NativeDetailsCard")).Visibility != Visibility.Collapsed)
        {
            throw new InvalidOperationException("WireView summary and temperature readings must use the compact layout.");
        }

        VerifyFaultHeadlineColors(headline, view.Model);

        window.Navigate("settings");
        await Task.Delay(200);

        var notes = new ReleaseNotesWindow(runtime.Preferences);
        runtime.TrackWindow(notes, "test-release-notes", 700, 490);
        try
        {
            var release = new ReleaseNotes
            {
                ReleaseTag = "v1.0.6",
                PublishedAt = new DateTimeOffset(2026, 11, 9, 5, 36, 39, TimeSpan.Zero),
                ReleaseUrl = ReleaseManifest.RepositoryUrl + "/releases/tag/v1.0.6",
                Changelog = "- Add **Safeguard+ History**.\n- Improve updates."
            };
            notes.Display(release, null);
            notes.Activate();
            await Task.Delay(200);

            var root = (Grid)notes.Content;
            var scroll = (ScrollViewer)root.FindName("NotesScroll");
            var body = (StackPanel)root.FindName("MarkdownBody");
            if (scroll.VerticalScrollBarVisibility != ScrollBarVisibility.Hidden || scroll.ScrollableHeight > 1
                || root.Background is not SolidColorBrush background
                || ((SolidColorBrush)((Grid)window.Content).Background).Color != background.Color)
            {
                throw new InvalidOperationException("Short release notes must fit and use the main window palette.");
            }

            var shortHeight = notes.AppWindow.Size.Height;
            var firstRow = (Grid)body.Children[0];
            var listItem = (RichTextBlock)firstRow.Children.OfType<StackPanel>().Single().Children[0];
            var paragraph = (Paragraph)listItem.Blocks[0];
            if (body.Children.Count != 2 || !paragraph.Inlines.OfType<Bold>().Any())
            {
                throw new InvalidOperationException("Release notes must render list items and bold text as native elements.");
            }
            notes.Display(release with { Changelog = string.Concat(Enumerable.Repeat(release.Changelog + "\n", 40)) }, null);
            await Task.Delay(400);
            if (notes.AppWindow.Size.Height != shortHeight || scroll.ScrollableHeight <= 1)
            {
                throw new InvalidOperationException("Long release notes must scroll without resizing the window.");
            }
        }
        finally
        {
            notes.Close();
        }

        await File.WriteAllTextAsync(Path.Combine(output, "release-notes-and-wireview-results.txt"),
            $"PASS: Fixed window size and WireView scrolling at {normalSize.Width}x{normalSize.Height}, release notes Markdown list and bold text.");
    }

    private static async Task VerifyWindowPlacementAsync(Window owner, AppRuntime runtime)
    {
        var area = DisplayArea.GetFromWindowId(owner.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var scale = ((FrameworkElement)owner.Content).XamlRoot.RasterizationScale;
        var size = new SizeInt32((int)(620 * scale), (int)(450 * scale));
        var position = new PointInt32(area.X + 10, area.Y + 20);
        var key = "test-placement-" + Guid.NewGuid().ToString("N");
        var content = new Border { Width = 400, Height = 200 };
        var view = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = content
        };
        var sample = new Window { Content = view };
        runtime.TrackWindow(sample, key, 540, 330);
        sample.Activate();

        try
        {
            await Task.Delay(250);
            var initialSize = sample.AppWindow.Size;
            content.Width = 690;
            content.Height = 600;
            await Task.Delay(250);
            if (sample.AppWindow.Size != initialSize || view.ScrollableWidth <= 1 || view.ScrollableHeight <= 1)
            {
                throw new InvalidOperationException("Overflowing content must be scrollable without changing the window size.");
            }

            runtime.Settings.RememberWindowSize = true;
            runtime.Settings.RememberWindowPosition = true;
            sample.AppWindow.MoveAndResize(new RectInt32(position.X, position.Y, size.Width, size.Height));
            await Task.Delay(250);
            if (sample.AppWindow.Size != size || sample.AppWindow.Position != position)
            {
                throw new InvalidOperationException("Window size and position must follow manual changes.");
            }
        }
        finally
        {
            sample.Close();
        }

        var reopened = new Window { Content = new Grid() };
        try
        {
            runtime.TrackWindow(reopened, key, 540, 330);
            reopened.Activate();
            await Task.Delay(250);
            if (reopened.AppWindow.Size != size || reopened.AppWindow.Position != position)
            {
                throw new InvalidOperationException("Window size and position were not restored on reopening.");
            }
        }
        finally
        {
            reopened.Close();
        }
    }

    private static void VerifyFaultHeadlineColors(Grid headline, HardwarePanelViewModel model)
    {
        var theme = headline.ActualTheme.ToString();
        var palette = Application.Current.Resources.MergedDictionaries
            .Where(dictionary => dictionary.ThemeDictionaries.ContainsKey(theme))
            .Select(dictionary => (ResourceDictionary)dictionary.ThemeDictionaries[theme]).Last();
        var danger = ((SolidColorBrush)palette["MonitorDanger"]).Color;
        var surface = ((SolidColorBrush)palette["MonitorWireViewFaultSurface"]).Color;

        var faultValue = model.FaultStatus.Value;
        var logValue = model.FaultLogStatus.Value;

        try
        {
            model.FaultStatus.Value = "Fault detected";
            model.FaultLogStatus.Value = "OK";
            VerifyCard(model.FaultStatus, highlighted: true);
            VerifyCard(model.FaultLogStatus, highlighted: false);

            model.FaultStatus.Value = "OK";
            model.FaultLogStatus.Value = "Faults logged";
            VerifyCard(model.FaultStatus, highlighted: false);
            VerifyCard(model.FaultLogStatus, highlighted: true);

            model.FaultLogStatus.Value = "OK";
            VerifyCard(model.FaultLogStatus, highlighted: false);
        }
        finally
        {
            model.FaultStatus.Value = faultValue;
            model.FaultLogStatus.Value = logValue;
        }

        void VerifyCard(MetricViewModel metric, bool highlighted)
        {
            var host = headline.Children.OfType<ContentControl>().Single(card => ReferenceEquals(card.Content, metric));
            var frame = VisualTreeQuery.Descendants<Border>(host).Single(border => border.Name == "HeadlineCard");
            var labels = VisualTreeQuery.Descendants<TextBlock>(frame).ToArray();
            var actual = ((SolidColorBrush)frame.BorderBrush).Color == danger
                && ((SolidColorBrush)frame.Background).Color == surface;
            var whiteText = labels.All(label => ((SolidColorBrush)label.Foreground).Color == Microsoft.UI.Colors.White);

            if (actual != highlighted || whiteText != highlighted)
            {
                throw new InvalidOperationException($"{metric.Label} must use a red card with white text only while faults are reported.");
            }
        }
    }
}

