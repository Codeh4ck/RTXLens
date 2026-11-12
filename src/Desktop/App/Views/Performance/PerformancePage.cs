using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RTXLens.App.Controls.Charts;
using RTXLens.App.Views.Shell;
using RTXLens.Presentation.Performance;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Performance;

internal sealed class PerformancePage : UserControl
{
    private readonly PerformanceRecorder _recorder;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly StackPanel _sources = new() { Spacing = 8 };
    private readonly StackPanel _plots = new() { Spacing = 10 };
    private readonly Dictionary<string, XyLineChart> _charts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckBox> _selectors = new(StringComparer.Ordinal);
    private readonly List<(CheckBox CheckBox, string[] Keys)> _bulkSelectors = [];
    private readonly Dictionary<string, (PerformanceSample[] Samples, XySeries Series)> _traceSamples = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IBrush> _traceColors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _cursors = new(StringComparer.Ordinal);
    private readonly TextBlock _status = Caption();
    private readonly TextBlock _emptySources = Caption("Waiting for supported sensor readings.");
    private readonly TextBlock _timelineEnd = Caption("00:00:00");
    private readonly ToggleSwitch _follow = ToolbarToggle("Follow latest", isChecked: true);
    private readonly ScrollBar _timeline = new() { Orientation = Orientation.Horizontal, SmallChange = 1 };
    private readonly Button _start;
    private readonly Button _stop;
    private readonly Button _clear;
    private readonly Button _import;
    private readonly Button _export;
    private XyRange? _visibleRange;
    private string? _sessionId;
    private bool _followLatest = true;
    private bool _updating;
    private int _dirty = 1;

    public PerformancePage(PerformanceRecorder recorder)
    {
        _recorder = recorder;
        _start = ActionButton("New recording", "chart-record", recorder.Start);
        _stop = ActionButton("Stop recording", "chart-stop", recorder.Stop);
        _clear = ActionButton("Clear recording", "chart-clear", recorder.Clear);
        _import = ActionButton("Import recording", "chart-import", async void () => await ImportAsync());
        _export = ActionButton("Export recording", "chart-export", async void () => await ExportAsync());

        var actions = new WrapPanel
        {
            ItemSpacing = 8,
            LineSpacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _start, _stop, _clear, _import, _export }
        };
        foreach (var button in actions.Children)
        {
            button.Classes.Add("desktop-action");
        }

        var title = new StackPanel
        {
            Spacing = 4,
            Children = { new TextBlock { Text = "Performance Chart", Classes = { "heading" } }, _status }
        };
        var heading = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = 12,
            Children = { title, actions }
        };
        Grid.SetColumn(actions, 1);
        heading.SizeChanged += (_, args) =>
        {
            var compact = args.NewSize.Width < 1150;
            heading.RowSpacing = compact ? 8 : 0;
            heading.ColumnSpacing = compact ? 0 : 12;
            Grid.SetRow(actions, compact ? 1 : 0);
            Grid.SetColumn(actions, compact ? 0 : 1);
            Grid.SetColumnSpan(actions, compact ? 2 : 1);
        };

        var navigation = new WrapPanel { ItemSpacing = 7 };
        AddNavigation("Start", "chart-start", chart => chart.GoToStart());
        AddNavigation("Back", "chart-back", chart => chart.Pan(-0.5));
        AddNavigation("Forward", "chart-forward", chart => chart.Pan(0.5));
        AddNavigation("End", "chart-end", chart => chart.GoToEnd());
        AddNavigation("Zoom in", "chart-zoom-in", chart => chart.Zoom(2));
        AddNavigation("Zoom out", "chart-zoom-out", chart => chart.Zoom(0.5));
        AddNavigation("Fit session", "chart-fit", chart => chart.Fit());

        _follow.IsCheckedChanged += (_, _) =>
        {
            if (_updating)
            {
                return;
            }

            _followLatest = _follow.IsChecked == true;
            _visibleRange = _charts.Values.FirstOrDefault()?.VisibleXRange;
            foreach (var chart in _charts.Values)
            {
                chart.FollowLatest = _followLatest;
            }

            Invalidate();
        };
        navigation.Children.Add(_follow);

        var info = new Button
        {
            Classes = { "desktop-action" },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 6),
            Content = new Image { Source = Icon("info-action"), Width = 16, Height = 16 }
        };
        AutomationProperties.SetName(info, "Chart controls");
        ToolTip.SetTip(info, "Chart controls");
        info.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is Window owner)
            {
                await CreateControlsGuide().ShowDialog(owner);
            }
        };
        var tools = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = 8,
            Children = { navigation, info }
        };
        Grid.SetColumn(info, 1);
        tools.SizeChanged += (_, args) =>
        {
            var compact = args.NewSize.Width < 980;
            tools.RowSpacing = compact ? 6 : 0;
            Grid.SetRow(info, compact ? 1 : 0);
            Grid.SetColumn(info, compact ? 0 : 1);
            Grid.SetColumnSpan(info, compact ? 2 : 1);
        };

        _timeline.ValueChanged += (_, _) =>
        {
            if (_updating || _charts.Values.FirstOrDefault() is not { } chart || chart.VisibleXRange is not { } range)
            {
                return;
            }

            chart.Pan((_timeline.Value - range.Minimum) / range.Span);
        };

        var sourcesScroll = Scroll(new Border { Padding = new Thickness(0, 0, 16, 0), Child = _sources });
        var selectorsBody = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 8,
            Children = { _emptySources, sourcesScroll }
        };
        Grid.SetRow(sourcesScroll, 1);

        var selectors = DashboardSection("Metrics and sources", null, selectorsBody);
        selectors.Padding = new Thickness(12);
        selectors.VerticalAlignment = VerticalAlignment.Stretch;

        var timeline = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        timeline.Children.Add(Caption("00:00:00"));
        Grid.SetColumn(_timeline, 1);
        timeline.Children.Add(_timeline);
        Grid.SetColumn(_timelineEnd, 2);
        timeline.Children.Add(_timelineEnd);

        var plotHost = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), RowSpacing = 8 };
        var plotScroll = Scroll(new Border { Padding = new Thickness(10, 0, 16, 10), Child = _plots });
        var timelineBar = new Border { Padding = new Thickness(10, 0), Child = timeline };
        Grid.SetRow(timelineBar, 1);
        Grid.SetRow(plotScroll, 2);
        plotHost.Children.Add(new Border { Padding = new Thickness(10, 8, 10, 0), Child = tools });
        plotHost.Children.Add(timelineBar);
        plotHost.Children.Add(plotScroll);

        var chartFrame = new Border { Classes = { "panel-frame" }, Child = plotHost };
        var content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("380,*"),
            ColumnSpacing = 12,
            Children = { selectors, chartFrame }
        };
        Grid.SetColumn(chartFrame, 1);
        content.SizeChanged += (_, args) =>
        {
            content.ColumnDefinitions[0].Width = new GridLength(Math.Clamp(args.NewSize.Width * 0.35, 280, 380));
        };

        var page = new Grid
        {
            Margin = new Thickness(0, 12, 0, 12),
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 10,
            Children = { heading, content }
        };
        Grid.SetRow(content, 1);
        Content = page;

        _timer.Tick += (_, _) =>
        {
            if (Interlocked.Exchange(ref _dirty, 0) != 0)
            {
                RenderFrame();
            }
        };
        AttachedToVisualTree += (_, _) =>
        {
            _recorder.Changed += Invalidate;
            RenderFrame();
            _timer.Start();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _recorder.Changed -= Invalidate;
            _timer.Stop();
        };

        void AddNavigation(string label, string icon, Action<XyLineChart> action)
        {
            var button = ActionButton(label, icon, () =>
            {
                if (_charts.Values.FirstOrDefault() is { } chart)
                {
                    action(chart);
                    OnRangeChanged(chart);
                }
            });
            button.Classes.Add("desktop-action");
            navigation.Children.Add(button);
        }
    }

    internal static Window CreateControlsGuide()
    {
        var table = new Grid { ColumnDefinitions = new ColumnDefinitions("178,398"), RowSpacing = 8, ColumnSpacing = 16 };
        AddRow("Control", "Description", isHeader: true);
        AddRow("Hover over the plot", "See the timestamp and the recorded value for each trace at that point in time.");
        AddRow("Move through time", "Drag the plot or timeline, or hold Shift while scrolling. Back and Forward move half a view at a time.");
        AddRow("Start / End", "Jump to the first or last part of the recording at the current zoom level.");
        AddRow("Zoom in / out", "Use the buttons, or hold Ctrl while scrolling over the plot to zoom around the pointer.");
        AddRow("Fit session", "Show the full recording, from its first sample to its last.");
        AddRow("Follow latest", "Keep the newest samples in view as the recording grows.");
        AddRow("Legend", "Shows the latest reading in view for each trace, or its reading at the timestamp you hover over.");

        return new LensWindow
        {
            Title = "Performance Chart controls",
            CanResize = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Body = new Border
            {
                Classes = { "panel-frame" },
                Margin = new Thickness(18),
                Padding = new Thickness(18),
                Child = table
            }
        };

        void AddRow(string control, string description, bool isHeader = false)
        {
            var row = table.RowDefinitions.Count;
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            AddCell(control, 0, FontWeight.SemiBold);
            AddCell(description, 1, isHeader ? FontWeight.SemiBold : FontWeight.Normal);

            if (isHeader)
            {
                var separator = new Border { Height = 1 };
                separator[!Border.BackgroundProperty] = new DynamicResourceExtension("PanelBorderBrush");
                table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Grid.SetRow(separator, row + 1);
                Grid.SetColumnSpan(separator, 2);
                table.Children.Add(separator);
            }

            void AddCell(string text, int column, FontWeight weight)
            {
                var cell = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 16, FontWeight = weight };
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                table.Children.Add(cell);
            }
        }
    }

    private static ToggleSwitch ToolbarToggle(string label, bool isChecked = false)
    {
        var toggle = new ToggleSwitch
        {
            IsChecked = isChecked,
            OnContent = label,
            OffContent = label,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(toggle, label);
        return toggle;
    }

    private void Invalidate() => Interlocked.Exchange(ref _dirty, 1);

    private void RenderFrame()
    {
        var metadata = _recorder.ReadFrame(_sessionId, _cursors, includeSamples: false);
        if (_sessionId != metadata.SessionId)
        {
            _sessionId = metadata.SessionId;
            _visibleRange = null;
            _traceSamples.Clear();
        }

        var viewport = new PerformanceViewport(
            _visibleRange?.Minimum ?? 0, _visibleRange?.Maximum ?? Math.Max(1, metadata.DurationSeconds),
            Math.Max(200, (int)_plots.Bounds.Width), Follow: _followLatest, Fit: !_followLatest && _visibleRange is null);
        var frame = _recorder.ReadDisplayFrame(viewport);
        _status.Text = $"{frame.Status} · {frame.SampleCount:N0} samples";
        _start.IsEnabled = !frame.Recording && frame.Series.Any(series => series is { Available: true, Selected: true });
        _stop.IsEnabled = frame.Recording;
        _clear.IsEnabled = frame is { Recording: false, SampleCount: > 0 };
        _import.IsEnabled = !frame.Recording;
        _export.IsEnabled = _clear.IsEnabled;

        _updating = true;
        try
        {
            UpdateSelectors(frame.Series);
            UpdatePlots(frame);
            UpdateTimeline();
        }
        finally
        {
            _updating = false;
        }
    }

    private void UpdateSelectors(PerformanceSeries[] series)
    {
        _emptySources.IsVisible = series.Length == 0;
        var keys = series.Select(item => item.Trace.Key).ToArray();
        if (_selectors.Count != keys.Length || keys.Any(key => !_selectors.ContainsKey(key)))
        {
            var expanded = _sources.Children.OfType<Expander>()
                .Where(expander => expander.IsExpanded).Select(expander => expander.Tag).ToHashSet();
            _selectors.Clear();
            _bulkSelectors.Clear();
            _sources.Children.Clear();

            var quick = new StackPanel { Spacing = 4 };
            foreach (var plugin in series.GroupBy(item => item.Trace.Source))
            {
                var names = plugin.Select(item => item.Trace.Key).ToArray();
                quick.Children.Add(BulkSelection(plugin.Key, names));
            }

            if (series.Length > 0)
            {
                var quickSelection = DashboardSection("Select by plugin", null, quick);
                quickSelection.Padding = new Thickness(10);
                _sources.Children.Add(quickSelection);
            }

            foreach (var category in series.GroupBy(item => item.Trace.Category).OrderBy(group => group.Key == "Temperatures"))
            {
                var group = new StackPanel { Spacing = 4 };
                group.Children.Add(BulkSelection("Select all", [.. category.Select(item => item.Trace.Key)]));
                var separator = new Border { Height = 1, Margin = new Thickness(0, 2) };
                separator[!Border.BackgroundProperty] = new DynamicResourceExtension("PanelBorderBrush");
                group.Children.Add(separator);
                foreach (var item in category)
                {
                    var key = item.Trace.Key;
                    var swatch = new Border
                    {
                        Width = 8,
                        Height = 8,
                        CornerRadius = new CornerRadius(2),
                        Background = TraceColor(item.Trace),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var label = new TextBlock { Text = item.Trace.Label, TextTrimming = TextTrimming.CharacterEllipsis };
                    ToolTip.SetTip(label, item.Trace.Label);
                    var row = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                        ColumnSpacing = 7,
                        Children = { swatch, label }
                    };
                    Grid.SetColumn(label, 1);

                    var input = new CheckBox { Content = row, IsChecked = item.Selected };
                    AutomationProperties.SetName(input, item.Trace.Label);
                    input.IsCheckedChanged += (_, _) =>
                    {
                        if (!_updating)
                        {
                            _recorder.Select(key, input.IsChecked == true);
                        }
                    };
                    _selectors[key] = input;
                    group.Children.Add(input);
                }

                _sources.Children.Add(new Expander
                {
                    Header = category.Key,
                    Tag = category.Key,
                    IsExpanded = expanded.Contains(category.Key) || category.Any(item => item.Selected),
                    Content = group,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                });
            }
        }

        foreach (var item in series)
        {
            var input = _selectors[item.Trace.Key];
            input.IsChecked = item.Selected;
            input.IsEnabled = item.Available || item.Selected;
        }

        UpdateBulkSelections();
    }

    private void UpdateBulkSelections()
    {
        foreach (var (input, keys) in _bulkSelectors)
        {
            var enabled = 0;
            var selected = 0;
            foreach (var key in keys)
            {
                var selector = _selectors[key];
                if (selector.IsEnabled)
                {
                    enabled++;
                    selected += selector.IsChecked == true ? 1 : 0;
                }
            }

            input.IsEnabled = enabled > 0;
            input.IsChecked = selected == 0 ? false : selected == enabled ? true : null;
        }
    }

    private CheckBox BulkSelection(string label, string[] keys)
    {
        var input = new CheckBox { Content = label };
        input.Click += (_, _) =>
        {
            var enabled = keys.Where(key => _selectors[key].IsEnabled).ToArray();
            var select = enabled.Any(key => _selectors[key].IsChecked != true);
            _recorder.SelectMany(enabled, select);
            RenderFrame();
        };
        _bulkSelectors.Add((input, keys));
        return input;
    }

    private void UpdatePlots(PerformanceChartFrame frame)
    {
        var groups = frame.Series.Where(series => series is { Selected: true, Count: > 0 }).GroupBy(series => series.Trace.Group).ToArray();
        var names = groups.Select(group => group.Key).ToArray();

        if (!_charts.Keys.SequenceEqual(names, StringComparer.Ordinal))
        {
            _charts.Clear();
            _plots.Children.Clear();
            foreach (var name in names)
            {
                var chart = new XyLineChart { Height = 245, FollowLatest = _followLatest, ShowLegendValues = true };
                chart.PropertyChanged += (_, args) =>
                {
                    if (args.Property == XyLineChart.VisibleXRangeProperty && !_updating)
                    {
                        OnRangeChanged(chart);
                    }
                };
                _charts.Add(name, chart);
                var plot = DashboardSection(name, null, chart);
                plot.Padding = new Thickness(10);
                _plots.Children.Add(plot);
            }
        }

        foreach (var group in groups)
        {
            var chart = _charts[group.Key];
            var unit = group.First().Trace.Unit;
            var minimum = double.PositiveInfinity;
            var maximum = double.NegativeInfinity;
            foreach (var series in group)
            {
                foreach (var sample in series.Samples)
                {
                    if (sample.Value is { } value)
                    {
                        minimum = Math.Min(minimum, value);
                        maximum = Math.Max(maximum, value);
                    }
                }
            }

            if (!double.IsFinite(minimum) || !double.IsFinite(maximum))
            {
                minimum = 0;
                maximum = 1;
            }

            var padding = Math.Max((maximum - minimum) * 0.08, 0.05);

            var duration = Math.Max(10, frame.DurationSeconds);
            chart.XAxis = new XyAxis(new XyRange(0, duration), Formatter: FormatTime, ReadoutFormatter: FormatHoverTime,
                TickGranularity: 1);
            chart.YAxis = new XyAxis(new XyRange(minimum - padding, maximum + padding), Formatter: value => $"{value:0.##} {unit}");
            chart.HoverLookup = CreateHoverLookup(frame.SessionId, [.. group.Select(series => series.Trace.Key)]);
            chart.Series = [.. group.Select(GetChartSeries)];
            chart.VisibleXRange = _followLatest ? new XyRange(Math.Max(0, duration - 60), duration) : _visibleRange;
        }
    }

    private XyHoverLookup CreateHoverLookup(string sessionId, string[] keys)
    {
        var readings = new PerformanceSample?[keys.Length];
        return (seconds, samples) =>
        {
            _recorder.ReadHover(sessionId, seconds, keys, readings);

            for (var index = 0; index < readings.Length; index++)
            {
                samples[index] = readings[index] is { } reading ? new XySample(reading.Seconds, reading.Value) : null;
            }
        };
    }

    private void OnRangeChanged(XyLineChart source)
    {
        _visibleRange = source.VisibleXRange;
        _followLatest = source.FollowLatest;
        _updating = true;
        try
        {
            _follow.IsChecked = _followLatest;
            foreach (var chart in _charts.Values.Where(chart => chart != source))
            {
                chart.FollowLatest = _followLatest;
                chart.VisibleXRange = _visibleRange;
            }

            UpdateTimeline();
        }
        finally
        {
            _updating = false;
        }

        Invalidate();
    }

    private void UpdateTimeline()
    {
        var chart = _charts.Values.FirstOrDefault();
        var range = chart?.VisibleXRange;
        var duration = chart?.XAxis.Range.Span ?? 0;
        _timeline.Maximum = Math.Max(0, duration - (range?.Span ?? duration));
        _timeline.ViewportSize = range?.Span ?? duration;
        _timeline.Value = range?.Minimum ?? 0;
_timeline.IsEnabled = _timeline.Maximum > 0;
        _timelineEnd.Text = FormatTime(duration);
    }

    private XySeries GetChartSeries(PerformanceSeries series)
    {
        var trace = series.Trace;
        if (_traceSamples.TryGetValue(trace.Key, out var cached) && ReferenceEquals(series.Samples, cached.Samples))
        {
            return cached.Series;
        }

        var output = new XySeries(trace.Label, series.Samples.Select(sample => new XySample(sample.Seconds, sample.Value)).ToArray(),
            TraceColor(trace), value => $"{value:0.##} {trace.Unit}");
        _traceSamples[trace.Key] = (series.Samples, output);
        return output;
    }

    private IBrush TraceColor(PerformanceTrace trace)
    {
        if (!_traceColors.TryGetValue(trace.Key, out var color))
        {
            color = new SolidColorBrush(Color.Parse(trace.Color));
            _traceColors[trace.Key] = color;
        }

        return color;
    }

    private static string FormatTime(double seconds) => TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss");

    private static string FormatHoverTime(double seconds) =>
        TimeSpan.FromMilliseconds(Math.Round(seconds * 1000)).ToString(@"hh\:mm\:ss\.fff");

    private async Task ImportAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import recording",
                FileTypeFilter =
                [
                    new FilePickerFileType("RTX Lens recording or HTML export") { Patterns = ["*.json", "*.rtxlens-recording", "*.html"] }
                ]
            });
            if (files.Count == 0)
            {
                return;
            }

            await using var stream = await files[0].OpenReadAsync();
            if (stream is { CanSeek: true, Length: > PerformanceRecordingFile.MaximumFileBytes })
            {
                throw new InvalidDataException("The recording exceeds the supported file size.");
            }

            using var reader = new StreamReader(stream);
            _recorder.Import(await Task.Run(async () => PerformanceRecordingFile.Deserialize(await reader.ReadToEndAsync())));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
            or System.Text.Json.JsonException)
        {
            _status.Text = error.Message;
        }
    }

    private async Task ExportAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        try
        {
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export recording",
                SuggestedFileName = "RTX Lens recording",
                FileTypeChoices =
                [
                    new FilePickerFileType("HTML report") { Patterns = ["*.html"] },
                    new FilePickerFileType("RTX Lens recording") { Patterns = ["*.json"] }
                ]
            });
            if (file is null)
            {
                return;
            }

            var html = Path.GetExtension(file.Name).Equals(".html", StringComparison.OrdinalIgnoreCase);
            var contents = await Task.Run(() =>
            {
                var session = _recorder.CompletedSession();
                return html ? PerformanceHtmlExporter.Export(session) : PerformanceRecordingFile.Serialize(session);
            });
            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek)
            {
                stream.SetLength(0);
            }

            using var writer = new StreamWriter(stream);
            await writer.WriteAsync(contents);
            _status.Text = "Recording exported.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException)
        {
            _status.Text = error.Message;
        }
    }
}
