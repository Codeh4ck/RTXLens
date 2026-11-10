using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using RTXLens.App.Services;
using RTXLens.App.Views;
using RTXLens.Presentation.Performance;

namespace RTXLens.App.Diagnostics;

internal static class PerformanceChartSmokeTests
{
    public static async Task RunAsync(MainWindow window, AppRuntime runtime, string output)
    {
        window.Navigate("performance");
        var view = (PerformanceChartView)window.CurrentView!;
        await WaitAsync(() => VisualTreeQuery.Descendants<CheckBox>(view).Count() > 3);
        var scroll = VisualTreeQuery.Descendants<ScrollViewer>(view).Single(item => item.Name == "SourcesScroll");
        if (scroll.Padding.Right < 20 || scroll.ActualWidth < 330)
        {
            throw new InvalidOperationException("The source list must reserve scrollbar space and fit metric names at the default wide size.");
        }

        foreach (var group in VisualTreeQuery.Descendants<Expander>(view))
        {
            var position = group.TransformToVisual(scroll).TransformPoint(new Windows.Foundation.Point(0, 0));
            if (position.X + group.ActualWidth > scroll.ActualWidth - scroll.Padding.Right + 1)
            {
                throw new InvalidOperationException("A source group overlaps the reserved scrollbar area.");
            }
        }

        var boxes = VisualTreeQuery.Descendants<CheckBox>(view).ToArray();
        foreach (var box in boxes.Take(10))
        {
            box.IsChecked = true;
        }

        var start = VisualTreeQuery.Descendants<Button>(view).Single(button => button.Name == "StartButton");
        await WaitAsync(() => start.IsEnabled);
        Invoke(start);
        await WaitAsync(() => runtime.Performance.ReadFrame(null, new Dictionary<string, int>(), false).SampleCount > 10);

        var configuration = await runtime.SettingsProfileApi.GetConfigurationAsync(runtime.StopToken);
        var originalAsus = configuration.Configuration.Plugins["asus-astral"];
        configuration.Configuration.Plugins["asus-astral"] = originalAsus with { Enabled = false };
        runtime.Performance.SetConfiguration(configuration.Configuration);
        await WaitAsync(() => VisualTreeQuery.Descendants<CheckBox>(view).Count() < boxes.Length);
        if (VisualTreeQuery.Descendants<CheckBox>(view).Any(box =>
            VisualTreeQuery.Descendants<TextBlock>(box).Any(text => text.Text.StartsWith("ROG Astral", StringComparison.Ordinal))))
        {
            throw new InvalidOperationException("Disabled Astral sources must be removed from the performance source list.");
        }

        configuration.Configuration.Plugins["asus-astral"] = originalAsus;
        runtime.Performance.SetConfiguration(configuration.Configuration);
        await WaitAsync(() => VisualTreeQuery.Descendants<CheckBox>(view).Count() == boxes.Length);

        var browser = VisualTreeQuery.Descendants<WebView2>(view).Single();
        await WaitAsync(() => browser.CoreWebView2 is not null);
        await Task.Delay(1500);
        var plotCount = await browser.ExecuteScriptAsync("document.querySelectorAll('section').length");
        if (!int.TryParse(plotCount, out var count) || count == 0)
        {
            var diagnostic = await browser.ExecuteScriptAsync("JSON.stringify({uri:location.href, ready:document.readyState, text:document.body.innerText, embedded:Boolean(window.chrome?.webview), scripts:document.scripts.length})");
            var notices = string.Join(" / ", VisualTreeQuery.Descendants<InfoBar>(view).Select(notice => notice.Message));
            await File.WriteAllTextAsync(Path.Combine(output, "performance-browser.json"), diagnostic + "\n" + notices);
            throw new InvalidOperationException("Live readings did not reach the embedded performance chart.");
        }

        var screenshot = Path.GetFullPath(Path.Combine(output, "performance-webview.png"));
        await File.WriteAllBytesAsync(screenshot, []);
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(screenshot);
        using (var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite))
        {
            await browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, stream);
        }

        await SmokeTestRunner.CaptureElementAsync(window.ThemeRoot, Path.Combine(output, "performance-native.png"));
        if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.Maximize();
            await Task.Delay(200);
            if (!ReferenceEquals(view, window.CurrentView))
            {
                throw new InvalidOperationException("Maximizing must preserve the chart tab and view.");
            }

            presenter.Restore();
        }

        var before = runtime.Performance.ReadFrame(null, new Dictionary<string, int>(), false).SampleCount;
        window.Navigate("settings");
        runtime.SetRendering(false);
        await Task.Delay(2500);
        var after = runtime.Performance.ReadFrame(null, new Dictionary<string, int>(), false);
        if (!after.Recording || after.SampleCount <= before)
        {
            throw new InvalidOperationException("Recording must continue while the view is unloaded and UI rendering is paused.");
        }

        runtime.SetRendering(true);
        window.Navigate("performance");
        view = (PerformanceChartView)window.CurrentView!;
        await WaitAsync(() => VisualTreeQuery.Descendants<Button>(view).Any(button => button is { Name: "StopButton", IsEnabled: true }));
        Invoke(VisualTreeQuery.Descendants<Button>(view).Single(button => button.Name == "StopButton"));
        var session = runtime.Performance.CompletedSession();
        var html = PerformanceHtmlExporter.Export(session);
        await File.WriteAllTextAsync(Path.Combine(output, "performance.html"), html);
        var data = PerformanceRecordingFile.Serialize(session);
        await File.WriteAllTextAsync(Path.Combine(output, "performance.rtxchart"), data);
        runtime.Performance.Import(PerformanceRecordingFile.Deserialize(html));
        var fromHtml = runtime.Performance.CompletedSession();
        runtime.Performance.Import(PerformanceRecordingFile.Deserialize(data));
        var fromData = runtime.Performance.CompletedSession();
        if (fromHtml.SampleCount != session.SampleCount || fromData.SampleCount != session.SampleCount)
        {
            throw new InvalidOperationException("HTML and recording imports must retain all captured readings.");
        }

        if (session.Series.Length < 2)
        {
            throw new InvalidOperationException("The performance export must retain multiple selected traces.");
        }

        await File.WriteAllTextAsync(Path.Combine(output, "performance-result.txt"),
            $"Passed native checkbox selection, live WebView2 plots ({count}), background recording, stop, HTML and data-file import/export ({session.SampleCount} samples).");
    }

    private static void Invoke(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        if (!condition())
        {
            throw new TimeoutException("The performance chart did not become ready.");
        }
    }
}
