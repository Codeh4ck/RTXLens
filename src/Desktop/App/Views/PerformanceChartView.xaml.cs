using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Windows.Storage.Pickers;
using RTXLens.App.Services;
using RTXLens.Presentation.Performance;

namespace RTXLens.App.Views;

public sealed partial class PerformanceChartView : UserControl
{
    private readonly AppRuntime _runtime;
    private readonly PerformanceRecorder _recorder;
    private readonly DispatcherQueueTimer _timer;
    private PerformanceViewport _viewport = new(Keys: []);
    private int _refreshRequested = 1;
    private long _lastStatusTick;
    private bool _refreshing;
    private long _viewportRevision;
    private long _hoverRequestId;
    private readonly Dictionary<string, CheckBox> _choices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StackPanel> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckBox> _categoryChoices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckBox> _pluginChoices = new(StringComparer.Ordinal);
    private PerformanceChartFrame? _sourceFrame;
    private bool _updatingSources;
    private string? _sessionId;
    private bool _chartReady;
    private bool _unloaded;
    private Task? _initialization;
    private Task? _closing;
    private bool _exporting;
    private bool _importing;

    internal PerformanceChartView(AppRuntime runtime)
    {
        _runtime = runtime;
        _recorder = runtime.Performance;
        InitializeComponent();

        _timer = DispatcherQueue.CreateTimer();
        _recorder.Changed += RequestRefresh;
        _timer.Interval = TimeSpan.FromMilliseconds(100);

        _timer.Tick += (_, _) =>
        {
            if (Interlocked.Exchange(ref _refreshRequested, 0) != 0
                || Environment.TickCount64 - _lastStatusTick >= 1000)
            {
                Refresh();
            }
        };

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnThemeChanged;

        SizeChanged += (_, _) =>
        {
            SourcesColumn.Width = new GridLength(Math.Clamp(ActualWidth * 0.35, 280, 380));

            var narrow = ActualWidth < 1150;
            SessionActions.RowSpacing = narrow ? 8 : 0;

            var buttons = new[] { StartButton, StopButton, ClearButton, ImportButton, ExportButton };

            for (var index = 0; index < buttons.Length; index++)
            {
                Grid.SetRow(buttons[index], narrow ? index / 2 : 0);
                Grid.SetColumn(buttons[index], narrow ? index % 2 : index);
            }
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_unloaded)
        {
            return;
        }

        Refresh();

        _timer.Start();

        _initialization ??= InitializeChartAsync();

        await _initialization;
    }

    private async Task InitializeChartAsync()
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RTXLens", "PerformanceChart.WebView2");
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, folder, new CoreWebView2EnvironmentOptions());

            if (_unloaded)
            {
                return;
            }

            await Chart.EnsureCoreWebView2Async(environment);

            if (_unloaded)
            {
                return;
            }

            var core = Chart.CoreWebView2;

            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;

            core.NewWindowRequested += (_, eventArgs) => eventArgs.Handled = true;

            core.NavigationStarting += (_, eventArgs) =>
            {
                var initialHtml = !_chartReady && eventArgs.Uri.StartsWith("data:text/html;", StringComparison.OrdinalIgnoreCase);
                eventArgs.Cancel = eventArgs.Uri != "about:blank" && !initialHtml;

                if (eventArgs.Cancel)
                {
                    ShowNotice("External navigation is unavailable in the chart.");
                }
            };

            core.WebMessageReceived += (_, message) =>
            {
                if (_unloaded)
                {
                    return;
                }

                using var document = JsonDocument.Parse(message.WebMessageAsJson);
                var payload = document.RootElement;

                switch (payload.ValueKind)
                {
                    case JsonValueKind.Object:
                        ReceiveChartRequest(payload);
                        return;
                    case JsonValueKind.String when payload.GetString() == "ready":
                        _chartReady = true;
                        _sessionId = null;

                        SendAppearance();
                        Refresh();

                        break;
                }
            };

            Chart.NavigateToString(PerformanceHtmlExporter.EmptyChart());
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException
            or IOException or UnauthorizedAccessException)
        {
            if (!_unloaded)
            {
                ShowNotice($"The chart could not open. Recording and HTML export remain available. {error.Message}");
            }
        }
    }

    private async void OnUnloaded(object sender, RoutedEventArgs args) => await CloseAsync();

    internal Task CloseAsync()
    {
        _unloaded = true;
        _chartReady = false;
        _timer.Stop();

        _recorder.Changed -= RequestRefresh;
        ActualThemeChanged -= OnThemeChanged;

        return _closing ??= CloseChartAsync();
    }

    private async Task CloseChartAsync()
    {
        if (_initialization is not null)
        {
            await _initialization;
        }

        Chart.Close();
    }

    private async void Refresh()
    {
        if (_unloaded)
        {
            return;
        }

        if (_refreshing)
        {
            RequestRefresh();
            return;
        }

        _refreshing = true;
        try
        {
            _lastStatusTick = Environment.TickCount64;

            var revision = _viewportRevision;
            var ready = _chartReady;
            var viewport = ready ? _viewport : new(Keys: []);

            var (frame, json) = await Task.Run(() =>
            {
                var display = _recorder.ReadDisplayFrame(viewport);
                return (Frame: display, Json: ready ? PerformanceHtmlExporter.SerializeFrame(display) : null);
            });

            if (_unloaded)
            {
                return;
            }

            _sessionId = frame.SessionId;
            UpdateSources(frame);

            var elapsed = TimeSpan.FromSeconds(frame.DurationSeconds);
            var duration = $"{(long)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";

            SessionStatus.Text = $"{frame.Status}  {duration}  ·  {frame.SampleCount:N0} readings";
            StartLabel.Text = frame is { SampleCount: > 0, Recording: false } ? "New recording" : "Start recording";
            ClearButton.IsEnabled = !_exporting && !_importing && frame is { Recording: false, SampleCount: > 0 };
            StartButton.IsEnabled = !_importing && !frame.Recording && frame.Series.Any(series => series is { Selected: true, Available: true });
            StopButton.IsEnabled = frame.Recording;
            ExportButton.IsEnabled = !_exporting && !_importing && frame is { Recording: false, SampleCount: > 0 };
            ImportButton.IsEnabled = !_importing && !frame.Recording;

            switch (_chartReady)
            {
                case true when ready && revision == _viewportRevision:
                    Chart.CoreWebView2.PostWebMessageAsJson(json!);
                    break;
                case true:
                    RequestRefresh();
                    break;
            }
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _chartReady = false;
            _sessionId = null;

            ShowNotice($"The chart is unavailable. Reopen this tab to reload it. {error.Message}");
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void UpdateSources(PerformanceChartFrame frame)
    {
        _updatingSources = true;
        _sourceFrame = frame;

        try
        {
            var visibleKeys = frame.Series.Select(series => series.Trace.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var key in _choices.Keys.Where(key => !visibleKeys.Contains(key)).ToArray())
            {
                var choice = _choices[key];
                if (choice.Parent is Panel parent)
                {
                    parent.Children.Remove(choice);
                }

                _choices.Remove(key);
            }

            foreach (var (name, group) in _groups.Where(pair => frame.Series.All(series => series.Trace.Category != pair.Key)).ToArray())
            {
                var expander = SourcesHost.Children.OfType<Expander>().First(item => ReferenceEquals(item.Content, group));
                SourcesHost.Children.Remove(expander);

                _groups.Remove(name);
                _categoryChoices.Remove(name);
            }

            EmptySources.Visibility = frame.Series.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var (trace, selected, available, _, _, _) in frame.Series)
            {
                if (!_choices.TryGetValue(trace.Key, out var choice))
                {
                    if (!_groups.TryGetValue(trace.Category, out var group))
                    {
                        group = new StackPanel { Spacing = 5 };

                        var category = trace.Category;
                        var selectAll = QuickSelection("Select all", series => series.Trace.Category == category);

                        _categoryChoices.Add(category, selectAll);

                        group.Children.Add(selectAll);
                        group.Children.Add(new Border
                        {
                            Height = 1,
                            Margin = new Thickness(0, 2, 0, 6),
                            Background = Foreground,
                            Opacity = 0.2
                        });

                        var expander = new Expander
                        {
                            Header = trace.Category,
                            Content = group,
                            IsExpanded = true,
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            HorizontalContentAlignment = HorizontalAlignment.Stretch,
                            Padding = new Thickness(8)
                        };

                        _groups.Add(trace.Category, group);

                        var temperatures = SourcesHost.Children.OfType<Expander>().FirstOrDefault(item => Equals(item.Header, "Temperatures"));
                        if (category != "Temperatures" && temperatures is not null)
                        {
                            SourcesHost.Children.Insert(SourcesHost.Children.IndexOf(temperatures), expander);
                        }
                        else
                        {
                            SourcesHost.Children.Add(expander);
                        }
                    }

                    var color = Windows.UI.Color.FromArgb(255, Convert.ToByte(trace.Color[1..3], 16),
                        Convert.ToByte(trace.Color[3..5], 16), Convert.ToByte(trace.Color[5..7], 16));
                    var label = new Grid { ColumnSpacing = 7, HorizontalAlignment = HorizontalAlignment.Stretch };

                    label.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    label.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    label.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(color),
                        Width = 12,
                        Height = 12,
                        CornerRadius = new CornerRadius(2),
                        VerticalAlignment = VerticalAlignment.Center
                    });

                    var text = new TextBlock { Text = trace.Label, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
                    Grid.SetColumn(text, 1);
                    label.Children.Add(text);

                    choice = new CheckBox
                    {
                        Content = label,
                        IsChecked = selected,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch
                    };

                    ToolTipService.SetToolTip(choice, $"{trace.Label}\n{trace.DeviceId}");

                    var key = trace.Key;
                    choice.Checked += (_, _) => Select(key, true);
                    choice.Unchecked += (_, _) => Select(key, false);

                    _choices.Add(key, choice);

                    group.Children.Add(choice);
                }

                choice.IsChecked = selected;
                choice.Opacity = available ? 1 : 0.55;

                var tooltip = $"{trace.Label}\n{trace.DeviceId}" + (available ? "" : "\nCurrently unavailable");
                if (!Equals(ToolTipService.GetToolTip(choice), tooltip))
                {
                    ToolTipService.SetToolTip(choice, tooltip);
                }
            }

            UpdateQuickSelections(frame);
        }
        finally
        {
            _updatingSources = false;
        }
    }

    private CheckBox QuickSelection(string label, Func<PerformanceSeries, bool> matches)
    {
        var choice = new CheckBox { Content = label, IsThreeState = true };

        choice.Click += (_, _) =>
        {
            var members = _sourceFrame?.Series.Where(matches).ToArray() ?? [];
            if (members.Length == 0)
            {
                return;
            }

            _recorder.SelectMany(members.Select(series => series.Trace.Key), !members.All(series => series.Selected));

            UpdateSources(_recorder.ReadFrame(null, new Dictionary<string, int>(), includeSamples: false));
            RequestRefresh();
        };

        return choice;
    }

    private void UpdateQuickSelections(PerformanceChartFrame frame)
    {
        var plugins = frame.Series.GroupBy(series => series.Trace.Identity.PluginId)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        foreach (var id in _pluginChoices.Keys.Where(id => !plugins.ContainsKey(id)).ToArray())
        {
            PluginSelectionsHost.Children.Remove(_pluginChoices[id]);
            _pluginChoices.Remove(id);
        }

        foreach (var (id, members) in plugins)
        {
            if (!_pluginChoices.TryGetValue(id, out var choice))
            {
                choice = QuickSelection(members[0].Trace.Source, series => series.Trace.Identity.PluginId == id);
                _pluginChoices.Add(id, choice);
                PluginSelectionsHost.Children.Add(choice);
            }

            UpdateQuickState(choice, members);
        }

        PluginSelectionPanel.Visibility = plugins.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var (name, choice) in _categoryChoices)
        {
            UpdateQuickState(choice, [.. frame.Series.Where(series => series.Trace.Category == name)]);
        }
    }

    private static void UpdateQuickState(CheckBox choice, PerformanceSeries[] members)
    {
        var selected = members.Count(series => series.Selected);

        choice.IsChecked = selected == 0 ? false : selected == members.Length ? true : null;
        choice.IsEnabled = members.Length > 0;
    }

    private void RequestRefresh() => Interlocked.Exchange(ref _refreshRequested, 1);

    private async void ReceiveChartRequest(JsonElement payload)
    {
        if (!payload.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
        {
            return;
        }

        if (type.GetString() == "viewport"
            && payload.TryGetProperty("start", out var start) && start.TryGetDouble(out var first)
            && payload.TryGetProperty("end", out var end) && end.TryGetDouble(out var last)
            && double.IsFinite(first) && double.IsFinite(last) && first >= 0 && last > first
            && payload.TryGetProperty("width", out var width) && width.TryGetInt32(out var pixels)
            && payload.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Array
            && keys.GetArrayLength() <= 256)
        {
            _viewportRevision++;

            _viewport = new(first, last, Math.Clamp(pixels, 200, 2400),
                payload.TryGetProperty("follow", out var follow) && follow.ValueKind == JsonValueKind.True,
                payload.TryGetProperty("fit", out var fit) && fit.ValueKind == JsonValueKind.True,
                [.. keys.EnumerateArray().Where(key => key.ValueKind == JsonValueKind.String).Select(key => key.GetString()!)]);

            RequestRefresh();
        }
        else if (type.GetString() == "hover"
            && payload.TryGetProperty("sessionId", out var session) && session.ValueKind == JsonValueKind.String
            && payload.TryGetProperty("requestId", out var request) && request.TryGetInt64(out var id)
            && payload.TryGetProperty("seconds", out var seconds) && seconds.TryGetDouble(out var position)
            && double.IsFinite(position) && position >= 0)
        {
            var sessionId = session.GetString()!;
            _hoverRequestId = id;

            var result = await Task.Run(() => _recorder.ReadHover(sessionId, id, position));
            if (!_unloaded && _chartReady && _hoverRequestId == id)
            {
                Chart.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(result, PerformanceChartJsonContext.Default.PerformanceHover));
            }
        }
    }

    private void Select(string key, bool selected)
    {
        if (_updatingSources)
        {
            return;
        }

        _recorder.Select(key, selected);
    }

    private void Start_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            _recorder.Start();
            Notice.IsOpen = false;
        }
        catch (InvalidOperationException error)
        {
            ShowNotice(error.Message);
        }

        Refresh();
    }

    private void Stop_Click(object sender, RoutedEventArgs args)
    {
        _recorder.Stop();
        Refresh();
    }

    private void Clear_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            _recorder.Clear();
            Notice.IsOpen = false;
        }
        catch (InvalidOperationException error)
        {
            ShowNotice(error.Message);
        }

        RequestRefresh();
        Refresh();
    }

    private async void Export_Click(object sender, RoutedEventArgs args)
    {
        if (_exporting)
        {
            return;
        }

        _exporting = true;
        ExportButton.IsEnabled = false;
        Notice.IsOpen = false;

        try
        {
            var session = _recorder.CompletedSession();
            var picker = new FileSavePicker(Win32Interop.GetWindowIdFromWindow(_runtime.MainWindowHandle))
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"RTX-Monitor-{session.StartedAt:yyyyMMdd-HHmmss}"
            };

            picker.FileTypeChoices.Add("RTX Lens recording", new List<string> { ".rtxchart" });
            picker.FileTypeChoices.Add("Interactive HTML chart", new List<string> { ".html" });

            var file = await picker.PickSaveFileAsync();

            if (file is not null)
            {
                await Task.Run(() =>
                {
                    var contents = Path.GetExtension(file.Path).Equals(".html", StringComparison.OrdinalIgnoreCase)
                        ? PerformanceHtmlExporter.Export(session)
                        : PerformanceRecordingFile.Serialize(session);

                    File.WriteAllText(file.Path, contents);
                }, _runtime.StopToken);

                ShowNotice($"Exported {Path.GetFileName(file.Path)}", InfoBarSeverity.Success);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or OperationCanceledException or System.Runtime.InteropServices.COMException)
        {
            ShowNotice($"Could not export the recording. {error.Message}".TrimEnd());
        }
        finally
        {
            _exporting = false;
            if (!_unloaded)
            {
                Refresh();
            }
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs args)
    {
        if (_importing)
        {
            return;
        }

        _importing = true;
        Notice.IsOpen = false;

        Refresh();

        try
        {
            var picker = new FileOpenPicker(Win32Interop.GetWindowIdFromWindow(_runtime.MainWindowHandle))
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };

            picker.FileTypeFilter.Add(".rtxchart");
            picker.FileTypeFilter.Add(".html");
            picker.FileTypeFilter.Add(".json");

            var file = await picker.PickSingleFileAsync();

            if (file is not null)
            {
                var session = await Task.Run(async () =>
                {
                    if (new FileInfo(file.Path).Length > PerformanceRecordingFile.MaximumFileBytes)
                    {
                        throw new InvalidDataException("The recording file exceeds 256 MB.");
                    }

                    var contents = await File.ReadAllTextAsync(file.Path, _runtime.StopToken);
                    return PerformanceRecordingFile.Deserialize(contents);
                }, _runtime.StopToken);

                _recorder.Import(session);

                _choices.Clear();
                _groups.Clear();
                _categoryChoices.Clear();
                _pluginChoices.Clear();
                PluginSelectionsHost.Children.Clear();
                SourcesHost.Children.Clear();

                _sessionId = null;

                ShowNotice($"Imported {Path.GetFileName(file.Path)}", InfoBarSeverity.Success);
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or OperationCanceledException or System.Runtime.InteropServices.COMException)
        {
            ShowNotice($"Could not import the recording. {error.Message}".TrimEnd());
        }
        finally
        {
            _importing = false;

            if (!_unloaded)
            {
                Refresh();
            }
        }
    }

    private void OnThemeChanged(FrameworkElement sender, object args) => SendAppearance();

    private void SendAppearance()
    {
        if (_chartReady)
        {
            Chart.CoreWebView2.PostWebMessageAsJson(ActualTheme == ElementTheme.Light
                ? "{\"type\":\"appearance\",\"light\":true}"
                : "{\"type\":\"appearance\",\"light\":false}");
        }
    }

    private void ShowNotice(string message, InfoBarSeverity severity = InfoBarSeverity.Warning)
    {
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }
}
