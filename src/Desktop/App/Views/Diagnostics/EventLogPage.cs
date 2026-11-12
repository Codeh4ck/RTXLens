using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using RTXLens.App.Controls;
using RTXLens.App.Services;
using RTXLens.Client.Http;
using RTXLens.Contracts.Diagnostics;
using RTXLens.Presentation.Diagnostics;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Diagnostics;

internal sealed class EventLogPage : UserControl
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);
    private readonly ServiceLogClient _client;
    private readonly DesktopEventLog? _localEvents;
    private readonly List<ServiceLogEntry> _entries = [];
    private readonly ListBox _list = new() { Classes = { "event-rows" } };
    private readonly TextBlock _status = Caption("Connecting to the backend…");
    private readonly TextBlock _empty = Caption("No events to display.");
    private readonly ComboBox _level = new()
    {
        ItemsSource = new[] { "All levels", "Information", "Warning", "Error" },
        SelectedIndex = 0
    };
    private readonly ComboBox _source = new() { ItemsSource = new[] { "All sources" }, SelectedIndex = 0 };
    private readonly ComboBox _sort = new() { ItemsSource = new[] { "Newest first", "Oldest first", "Level" }, SelectedIndex = 0 };
    private CancellationTokenSource? _stop;
    private string? _instance;
    private long _latest;
    private bool _updatingSources;
    private readonly ColumnHeader[] _columns;

    public EventLogPage(ServiceLogClient client, CancellationToken applicationStop, DesktopEventLog? localEvents = null)
    {
        _client = client;
        _localEvents = localEvents;

        AutomationProperties.SetName(_level, "Filter events by level");
        AutomationProperties.SetName(_source, "Filter events by source");
        AutomationProperties.SetName(_sort, "Sort events");
        AutomationProperties.SetName(_list, "RTX Lens events");
        _source.MaxWidth = 190;

        var filters = ToolbarGroup("Filters", _level, _source, _sort);
        var actions = ToolbarGroup("Actions",
            ActionButton("Export logs", "chart-export", async void () => await ExportAsync()),
            ActionButton("Clear events", "chart-clear", async void () => await ClearAsync()),
            ActionButton("Load older", "refresh", async void () => await LoadOlderAsync()));
        var toolbar = new WrapPanel
        {
            ItemSpacing = 20,
            LineSpacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { filters, actions }
        };
        var title = new StackPanel
        {
            Spacing = 4,
            Children = { new TextBlock { Text = "Event Log", Classes = { "heading" } }, _status }
        };
        var heading = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = 16,
            Children = { title, toolbar }
        };
        Grid.SetColumn(toolbar, 1);
        heading.SizeChanged += (_, args) =>
        {
            var compact = args.NewSize.Width < 1120;
            heading.RowSpacing = compact ? 8 : 0;
            Grid.SetColumn(toolbar, compact ? 0 : 1);
            Grid.SetRow(toolbar, compact ? 1 : 0);
            Grid.SetColumnSpan(toolbar, compact ? 2 : 1);
        };

        _columns =
        [
            new ColumnHeader { Label = "Time", Width = 155, MinWidth = 95, MaxWidth = 300, SortIndicator = "↓" },
            new ColumnHeader { Label = "Level", Width = 100, MinWidth = 75, MaxWidth = 225 },
            new ColumnHeader { Label = "Source", Width = 175, MinWidth = 100, MaxWidth = 400 },
            new ColumnHeader { Label = "Message", MinWidth = 170, MaxWidth = 1800 }
        ];
        _columns[0].SortCommand = new RelayCommand(() => _sort.SelectedIndex = _sort.SelectedIndex == 0 ? 1 : 0);
        _columns[1].SortCommand = new RelayCommand(() => _sort.SelectedIndex = 2);

        var header = new Border { Classes = { "event-header" }, Child = Row(_columns) };
        _list.ItemTemplate = new FuncDataTemplate<ServiceLogEntry>((entry, _) => entry is null ? null : Row(
            Cell(entry.Timestamp.ToLocalTime().ToString("G", CultureInfo.CurrentCulture)), LevelCell(entry.Level),
            Cell(entry.Source), Cell(entry.Message)));
        _list.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());

        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        var table = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Children = { header, _list, _empty } };
        Grid.SetRow(_list, 1);
        Grid.SetRow(_empty, 1);
        var scroll = Scroll(table);
        scroll.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        scroll.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        var surface = new Border { CornerRadius = new CornerRadius(7), Margin = new Thickness(1), ClipToBounds = true, Child = scroll };
        surface[!Border.BackgroundProperty] = new DynamicResourceExtension("PanelBrush");
        var outline = new Border { Classes = { "panel-frame" }, Background = Brushes.Transparent, IsHitTestVisible = false };
        var frame = new Grid { Children = { surface, outline } };
        surface.SizeChanged += (_, _) => UpdateTableWidth();
        foreach (var column in _columns)
        {
            column.PropertyChanged += (_, args) =>
            {
                if (args.Property == WidthProperty)
                {
                    UpdateTableWidth();
                }
            };
        }

        var layout = new Grid
        {
            Margin = new Thickness(0, 12, 0, 12),
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 12,
            Children = { heading, frame }
        };
        Grid.SetRow(frame, 1);
        Content = layout;

        _level.SelectionChanged += (_, _) => Render();
        _source.SelectionChanged += (_, _) =>
        {
            if (!_updatingSources)
            {
                Render();
            }
        };
        _sort.SelectionChanged += (_, _) =>
        {
            _columns[0].SortIndicator = _sort.SelectedIndex switch { 0 => "↓", 1 => "↑", _ => null };
            _columns[1].SortIndicator = _sort.SelectedIndex == 2 ? "↓" : null;
            Render();
        };

        AttachedToVisualTree += async (_, _) =>
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(applicationStop);
            _stop = stop;

            if (_localEvents is not null)
            {
                _localEvents.Changed += OnLocalEventsChanged;
                RefreshSourcesAndRender();
            }

            try
            {
                await FollowAsync(stop.Token);
            }
            finally
            {
                if (_stop == stop)
                {
                    _stop = null;
                }
            }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _stop?.Cancel();
            if (_localEvents is not null)
            {
                _localEvents.Changed -= OnLocalEventsChanged;
            }
        };
        return;

        void UpdateTableWidth()
        {
            var columnWidth = _columns.Sum(column => double.IsNaN(column.Width) ? column.MinWidth : column.Width);
            table.Width = Math.Max(surface.Bounds.Width, columnWidth + 56);
        }
    }

    private async Task FollowAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var initial = await _client.ReadHistoryAsync(0, token);
                token.ThrowIfCancellationRequested();
                Receive(initial);
                _status.Text = "Following backend events";

                await foreach (var page in _client.FollowAsync(initial.InstanceId, initial.LatestSequence, token))
                {
                    token.ThrowIfCancellationRequested();
                    Receive(page);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or JsonException)
            {
                _status.Text = error.Message;
            }

            try
            {
                await Task.Delay(ReconnectDelay, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private void Receive(ServiceLogPage page)
    {
        if (_instance != page.InstanceId || page.LatestSequence < _latest)
        {
            _entries.Clear();
            _instance = page.InstanceId;
        }

        _latest = page.LatestSequence;
        var known = _entries.Select(entry => entry.Sequence).ToHashSet();
        _entries.RemoveAll(entry => entry.Sequence < page.FirstSequence);
        _entries.AddRange(page.Entries.Where(entry => known.Add(entry.Sequence)));

        RefreshSourcesAndRender();
    }

    private void OnLocalEventsChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (_stop is { IsCancellationRequested: false })
        {
            RefreshSourcesAndRender();
        }
    });

    private IEnumerable<ServiceLogEntry> AllEntries() => _entries.Concat(_localEvents?.Entries ?? []);

    private void RefreshSourcesAndRender()
    {
        var sources = AllEntries().Select(entry => entry.Source)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.CurrentCulture).ToArray();
        var options = new[] { "All sources" }.Concat(sources).ToArray();
        if (_source.ItemsSource is not string[] current || !current.SequenceEqual(options, StringComparer.Ordinal))
        {
            var selection = _source.SelectedItem as string;
            _updatingSources = true;
            _source.ItemsSource = options;
            _source.SelectedItem = selection is not null && options.Contains(selection) ? selection : options[0];
            _updatingSources = false;
        }

        Render();
    }

    private void Render()
    {
        var visible = AllEntries().Where(entry => (_level.SelectedIndex <= 0 || entry.Level == (ServiceLogLevel)(_level.SelectedIndex - 1))
            && (_source.SelectedIndex <= 0 || entry.Source == _source.SelectedItem as string));

        var rows = (_sort.SelectedIndex switch
        {
            1 => visible.OrderBy(entry => entry.Timestamp),
            2 => visible.OrderByDescending(entry => entry.Level).ThenByDescending(entry => entry.Timestamp),
            _ => visible.OrderByDescending(entry => entry.Timestamp)
        }).ToArray();
        _list.ItemsSource = rows;
        _empty.IsVisible = rows.Length == 0;
    }

    private async Task LoadOlderAsync()
    {
        if (_stop is null || _entries.Count == 0)
        {
            return;
        }

        try
        {
            Receive(await _client.ReadHistoryAsync(_entries.Min(entry => entry.Sequence), _stop.Token));
        }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or JsonException)
        {
            _status.Text = error.Message;
        }
    }

    private async Task ClearAsync()
    {
        if (_stop is null || TopLevel.GetTopLevel(this) is not Window owner
            || !await ConfirmAsync(owner, "Clear events", "Permanently remove all retained desktop and backend events?", "Clear events"))
        {
            return;
        }

        try
        {
            var page = await _client.ClearAsync(_stop.Token);
            if (_localEvents is not null)
            {
                await _localEvents.ClearAsync(_stop.Token);
            }

            _entries.Clear();
            Receive(page);
            _status.Text = "Events cleared.";
        }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or JsonException)
        {
            _status.Text = error.Message;
        }
    }

    private async Task ExportAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage
            || _list.ItemsSource is not ServiceLogEntry[] { Length: > 0 } entries)
        {
            return;
        }

        try
        {
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Event Log",
                SuggestedFileName = "RTX Lens Event Log.html",
                FileTypeChoices = [new FilePickerFileType("HTML report") { Patterns = ["*.html"] }]
            });

            if (file is null)
            {
                return;
            }

            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek)
            {
                stream.SetLength(0);
            }

            using var writer = new StreamWriter(stream);
            foreach (var line in EventLogHtmlExporter.Lines(entries, complete: false, lightTheme: false))
            {
                await writer.WriteLineAsync(line);
            }

            _status.Text = "Event Log exported.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _status.Text = error.Message;
        }
    }

    private static StackPanel ToolbarGroup(string label, params Control[] controls)
    {
        var buttons = new WrapPanel { ItemSpacing = 7, LineSpacing = 5 };
        foreach (var control in controls)
        {
            if (control is Button)
            {
                control.Classes.Add("desktop-action");
            }

            control.VerticalAlignment = VerticalAlignment.Center;
            buttons.Children.Add(control);
        }

        return new StackPanel { Spacing = 4, Children = { Caption(label), buttons } };
    }

    private static TextBlock Cell(string text)
    {
        var cell = new TextBlock
        {
            Text = text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(cell, text);
        return cell;
    }

    private static Control LevelCell(ServiceLogLevel level)
    {
        var cell = Cell(level.ToString());
        var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };
        var brush = level switch
        {
            ServiceLogLevel.Warning => "WarningBrush",
            ServiceLogLevel.Error => "ErrorBrush",
            _ => "PrimaryBrush"
        };
        dot[!Avalonia.Controls.Shapes.Shape.FillProperty] = new DynamicResourceExtension(brush);
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { dot, cell } };
    }

    private Grid Row(params Control[] cells)
    {
        var row = new Grid { ColumnSpacing = 8, Margin = new Thickness(12, 0, 20, 0), MinHeight = 40 };
        foreach (var header in _columns)
        {
            var column = new ColumnDefinition();
            column.Bind(ColumnDefinition.WidthProperty, new Binding(nameof(Width))
            {
                Source = header,
                Converter = new FuncValueConverter<double, GridLength>(width => double.IsNaN(width)
                    ? new GridLength(1, GridUnitType.Star) : new GridLength(width))
            });
            row.ColumnDefinitions.Add(column);
        }
        for (var index = 0; index < cells.Length; index++)
        {
            Grid.SetColumn(cells[index], index);
            row.Children.Add(cells[index]);
        }

        return row;
    }
}
