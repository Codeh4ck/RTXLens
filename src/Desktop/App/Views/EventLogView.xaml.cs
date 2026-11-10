using System.Collections.ObjectModel;
using System.Net;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Windows.Storage.Pickers;
using RTXLens.App.Services;
using RTXLens.Client.Http;
using RTXLens.Contracts.Diagnostics;
using RTXLens.Presentation.Diagnostics;

namespace RTXLens.App.Views;

public sealed partial class EventLogView : UserControl
{
    private readonly AppRuntime _runtime;
    private readonly Dictionary<long, ServiceLogEntry> _entries = new();
    private readonly ObservableCollection<EventLogRow> _rows = [];
    private bool _exporting;
    private bool _clearing;
    public EventLogColumnWidths Widths { get; } = new();
    private HttpClient? _http;
    private CancellationTokenSource? _stop;
    private string? _instance;
    private long _first;
    private long _latest;
    private bool _olderLoading;
    private bool _updatingSources;
    private bool _sortByLevel;
    private bool _descending = true;
    private string _connection = "Connecting to the backend...";

    internal EventLogView(AppRuntime runtime)
    {
        _runtime = runtime;
        InitializeComponent();
        Events.ItemsSource = _rows;
        RefreshSources();
        UpdateSortHeader();
        RebuildRows();
    }

    private async void View_Loaded(object sender, RoutedEventArgs args)
    {
        if (_stop is not null)
        {
            return;
        }

        _runtime.EventLog.Changed += DesktopEvents_Changed;
        RebuildRows();
        if (_runtime.IsDemo)
        {
            SetConnection("Desktop events · Demo mode");
            return;
        }

        using var http = new HttpClient
        {
            BaseAddress = new Uri(_runtime.Preferences.ServiceAddress),
            Timeout = TimeSpan.FromSeconds(8)
        };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_runtime.StopToken);
        _http = http;
        _stop = stop;
        RefreshStatus();

        try
        {
            await FollowAsync(new ServiceLogClient(http), stop.Token);
        }
        finally
        {
            if (ReferenceEquals(_stop, stop))
            {
                _stop = null;
                _http = null;
            }
        }
    }

    private void View_Unloaded(object sender, RoutedEventArgs args)
    {
        _runtime.EventLog.Changed -= DesktopEvents_Changed;
        var stop = _stop;
        _stop = null;
        _http = null;
        stop?.Cancel();
    }

    private async Task FollowAsync(ServiceLogClient client, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_instance is null)
                {
                    ReplaceHistory(await client.ReadHistoryAsync(0, token));
                }

                await foreach (var page in client.FollowAsync(_instance!, _latest, token))
                {
                    if (page.InstanceId != _instance)
                    {
                        ReplaceHistory(await client.ReadHistoryAsync(0, token));
                        break;
                    }

                    if (page.FirstSequence > _latest + 1)
                    {
                        ReplaceHistory(await client.ReadHistoryAsync(0, token));
                        break;
                    }

                    _connection = "Live backend events";
                    ApplyPage(page);
                    var received = page.Entries.Length == 0 ? page.LatestSequence : page.Entries[^1].Sequence;
                    _latest = Math.Max(_latest, received);
                }
            }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
            {
                SetConnection("The connected backend version does not provide an Event Log.");
            }
            catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                SetConnection("Event log disconnected. Reconnecting...");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async void LoadOlder_Click(object sender, RoutedEventArgs args)
    {
        if (_http is not { } http || _stop is not { } stop || _entries.Count == 0 || _olderLoading)
        {
            return;
        }

        var token = stop.Token;
        var instance = _instance;
        _olderLoading = true;
        RefreshStatus();

        try
        {
            var page = await new ServiceLogClient(http).ReadHistoryAsync(_entries.Keys.Min(), token);
            if (_instance == instance && page.InstanceId == instance)
            {
                ApplyPage(page);
            }
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            if (!token.IsCancellationRequested)
            {
                SetConnection("Older events could not be loaded. Try again.");
            }
        }
        finally
        {
            _olderLoading = false;
            RefreshStatus();
        }
    }

    private void ReplaceHistory(ServiceLogPage page)
    {
        if (page.InstanceId == _instance && page.FirstSequence < _first)
        {
            return;
        }

        _instance = page.InstanceId;
        _first = page.FirstSequence;
        _latest = page.LatestSequence;
        _entries.Clear();
        foreach (var entry in page.Entries)
        {
            _entries[entry.Sequence] = entry with { Source = SourceName(entry.Source) };
        }

        RebuildRows();
    }

    private void ApplyPage(ServiceLogPage page)
    {
        if (page.FirstSequence > _first)
        {
            _first = page.FirstSequence;
            foreach (var entry in _entries.Values.Where(entry => entry.Sequence < _first).ToArray())
            {
                _entries.Remove(entry.Sequence);
                _rows.Remove(new EventLogRow(entry, Widths));
            }
        }

        foreach (var entry in page.Entries)
        {
            var item = entry with { Source = SourceName(entry.Source) };
            if (item.Sequence >= _first && _entries.TryAdd(item.Sequence, item) && IsVisible(item))
            {
                InsertRow(item);
            }
        }

        RefreshSources();
        RefreshStatus();
    }

    private void InsertRow(ServiceLogEntry entry)
    {
        var start = 0;
        var end = _rows.Count;
        while (start < end)
        {
            var middle = start + (end - start) / 2;
            if (CompareEntries(_rows[middle].Entry, entry) < 0)
            {
                start = middle + 1;
            }
            else
            {
                end = middle;
            }
        }

        _rows.Insert(start, new EventLogRow(entry, Widths));
    }

    private int CompareEntries(ServiceLogEntry left, ServiceLogEntry right)
    {
        if (_sortByLevel)
        {
            var level = left.Level.CompareTo(right.Level);
            if (level != 0)
            {
                return _descending ? -level : level;
            }
        }

        var time = left.Timestamp.CompareTo(right.Timestamp);
        var order = time == 0 ? left.Sequence.CompareTo(right.Sequence) : time;
        return _sortByLevel || _descending ? -order : order;
    }

    private bool IsVisible(ServiceLogEntry entry) =>
        (LevelFilter.SelectedIndex <= 0 || LevelFilter.SelectedIndex == (int)entry.Level + 1)
        && (SourceFilter.SelectedIndex <= 0 || Equals(SourceFilter.SelectedItem, entry.Source));

    private IEnumerable<ServiceLogEntry> AllLoaded => _entries.Values.Concat(_runtime.EventLog.Entries);

    private void RefreshSources()
    {
        string[] sources = ["All sources", .. AllLoaded.Select(entry => entry.Source)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.CurrentCultureIgnoreCase)];

        if (SourceFilter.ItemsSource is string[] existing && existing.SequenceEqual(sources))
        {
            return;
        }

        var selected = SourceFilter.SelectedItem as string;
        _updatingSources = true;
        SourceFilter.ItemsSource = sources;
        SourceFilter.SelectedIndex = Math.Max(0, Array.IndexOf(sources, selected));
        _updatingSources = false;

        if (selected is not null && !Equals(selected, SourceFilter.SelectedItem))
        {
            RebuildRows();
        }
    }

    private void RebuildRows()
    {
        RefreshSources();
        var sorted = AllLoaded.Where(IsVisible).ToList();
        sorted.Sort(CompareEntries);

        _rows.Clear();
        foreach (var entry in sorted)
        {
            _rows.Add(new EventLogRow(entry, Widths));
        }

        RefreshStatus();
    }

    private void SetConnection(string status)
    {
        _connection = status;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var more = _entries.Count > 0 && _entries.Keys.Min() > _first;
        LoadOlderButton.IsEnabled = more && !_olderLoading && !_clearing && _http is not null;
        var total = _entries.Count + _runtime.EventLog.Entries.Length;
        ExportButton.IsEnabled = !_exporting && !_clearing && (total > 0 || _http is not null);
        ClearEventsButton.IsEnabled = !_clearing && !_exporting
            && (_http is not null || _runtime.IsDemo && _runtime.EventLog.Entries.Length > 0);
        StatusText.Text = total == 0 ? _connection : $"{_connection} · {_rows.Count:N0} of {total:N0} loaded events";
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (!_updatingSources && Events is not null)
        {
            RebuildRows();
        }
    }

    private void TimeSort_Click(object sender, RoutedEventArgs args) => Sort(byLevel: false);
    private void LevelSort_Click(object sender, RoutedEventArgs args) => Sort(byLevel: true);

    private void Sort(bool byLevel)
    {
        _descending = _sortByLevel != byLevel || !_descending;
        _sortByLevel = byLevel;
        UpdateSortHeader();
        RebuildRows();
    }

    private void UpdateSortHeader()
    {
        var arrow = _descending ? " ▼" : " ▲";
        var direction = _descending ? "descending" : "ascending";
        TimeHeader.Content = _sortByLevel ? "Time" : "Time" + arrow;
        LevelHeader.Content = _sortByLevel ? "Level" + arrow : "Level";
        AutomationProperties.SetName(TimeHeader, _sortByLevel ? "Sort by time" : "Sort by time, " + direction);
        AutomationProperties.SetName(LevelHeader, _sortByLevel ? "Sort by level, " + direction : "Sort by level");
    }

    private void DesktopEvents_Changed() => DispatcherQueue.TryEnqueue(RebuildRows);

    private void Table_SizeChanged(object sender, SizeChangedEventArgs args) => Widths.FitMessage(args.NewSize.Width - 2);

    private void Column_DragDelta(object sender, DragDeltaEventArgs args)
    {
        if (sender is Thumb { Tag: string tag } && int.TryParse(tag, out var column))
        {
            Widths.Resize(column, args.HorizontalChange);
        }
    }

    private async void ClearEvents_Click(object sender, RoutedEventArgs args)
    {
        if (_clearing || _exporting || (!_runtime.IsDemo && _http is null))
        {
            return;
        }

        _clearing = true;
        ActionStatus.Text = string.Empty;
        RefreshStatus();

        var backendCleared = false;
        try
        {
            var confirmed = await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Clear all events?",
                Content = _runtime.IsDemo
                    ? "This permanently deletes all desktop events."
                    : "This permanently deletes all RTX Lens desktop and backend events, including events still within the retention period.",
                PrimaryButtonText = "Clear events",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            }.ShowAsync();

            if (confirmed != ContentDialogResult.Primary)
            {
                return;
            }

            var token = _runtime.StopToken;
            if (!_runtime.IsDemo)
            {
                if (_http is not { } http)
                {
                    ActionStatus.Text = "The backend is unavailable. Reconnect before clearing events.";
                    return;
                }

                var instance = _instance;
                var page = await new ServiceLogClient(http).ClearAsync(token);
                backendCleared = true;

                if (_instance == instance && (page.InstanceId != _instance || page.LatestSequence >= _latest))
                {
                    ReplaceHistory(page);
                }
                else if (page.InstanceId == _instance)
                {
                    ApplyPage(page);
                }
            }

            await _runtime.EventLog.ClearAsync(token);
            RebuildRows();
            ActionStatus.Text = "Event history cleared.";
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException
            or JsonException or OperationCanceledException or ObjectDisposedException or System.Runtime.InteropServices.COMException)
        {
            ActionStatus.Text = backendCleared
                ? "Backend events were cleared, but desktop events could not be cleared."
                : "Could not confirm that event history was cleared.";
        }
        finally
        {
            _clearing = false;
            RefreshStatus();
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs args)
    {
        if (_exporting || _clearing)
        {
            return;
        }

        _exporting = true;
        ActionStatus.Text = string.Empty;
        RefreshStatus();
        var lightTheme = ActualTheme == ElementTheme.Light;

        try
        {
            var picker = new FileSavePicker(Win32Interop.GetWindowIdFromWindow(_runtime.MainWindowHandle))
            {
                SuggestedFileName = $"RTX-Lens-events-{DateTime.Now:yyyy-MM-dd}"
            };
            picker.FileTypeChoices.Add("HTML report", [".html"]);

            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            var backend = _entries.Values.ToArray();
            var complete = false;

            if (_http is { } http)
            {
                try
                {
                    backend = await ReadFullHistoryAsync(new ServiceLogClient(http), _runtime.StopToken);
                    complete = true;
                }
                catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException or ObjectDisposedException)
                {
                    System.Diagnostics.Debug.WriteLine(error);
                }
            }

            var entries = backend.Concat(_runtime.EventLog.Entries).OrderByDescending(entry => entry.Timestamp)
                .ThenByDescending(entry => entry.Sequence);
            await File.WriteAllLinesAsync(file.Path, EventLogHtmlExporter.Lines(entries, complete, lightTheme),
                _runtime.StopToken);
            ActionStatus.Text = complete ? "Exported retained events and desktop configuration changes."
                : "Exported loaded events and desktop configuration changes.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException
            or System.Runtime.InteropServices.COMException)
        {
            ActionStatus.Text = "The logs could not be exported.";
        }
        finally
        {
            _exporting = false;
            RefreshStatus();
        }
    }

    private static async Task<ServiceLogEntry[]> ReadFullHistoryAsync(ServiceLogClient client, CancellationToken token)
    {
        var page = await client.ReadHistoryAsync(0, token);
        var instance = page.InstanceId;
        var entries = new List<ServiceLogEntry>(page.Entries);

        while (page.Entries.LastOrDefault() is { } oldest && oldest.Sequence > page.FirstSequence)
        {
            page = await client.ReadHistoryAsync(oldest.Sequence, token);
            if (page.InstanceId != instance)
            {
                throw new IOException("The backend restarted during export.");
            }

            entries.AddRange(page.Entries);
        }

        return [.. entries.Select(entry => entry with { Source = SourceName(entry.Source) })];
    }

    private static string SourceName(string source) => source is "GenericNvidia" or "NVIDIA" ? "Nvidia" : source;
}
