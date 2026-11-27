using System.Net;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Media;
using RTXLens.App.ViewModels.Shell;
using RTXLens.App.Views.Shell;
using RTXLens.Client.Http;
using RTXLens.Contracts.Warnings;
using RTXLens.Presentation.Settings;
using RTXLens.WarningUI;
using static RTXLens.App.Views.Shell.DesktopControls;
using static RTXLens.App.Views.Diagnostics.WarningVisuals;

namespace RTXLens.App.Views.Diagnostics;

internal sealed class WarningHistoryView : UserControl
{
    private sealed record Row(WarningIncident Incident, string Source, string Title)
    {
        public string Label => $"{Source} - {Title}";
    }

    private readonly ShellViewModel _shell;
    private readonly ListBox _list = new() { Classes = { "event-rows" } };
    private readonly ComboBox _source = new();
    private readonly ComboBox _status = new();
    private readonly ComboBox _sort = new() { ItemsSource = new[] { "Newest first", "Oldest first" }, SelectedIndex = 0 };
    private readonly TextBox _search = new() { PlaceholderText = "Search warnings", Width = 180 };
    private readonly TextBlock _message = Caption();
    private readonly ContentControl _empty = new();
    private readonly ContentControl _error = Alert();
    private readonly Grid _frame;
    private readonly Button _inspect;
    private readonly Button _refresh;
    private Row[] _rows = [];
    private CancellationTokenSource? _lifetime;
    private bool _loading;
    private bool _inspecting;

    public WarningHistoryView(ShellViewModel shell)
    {
        _shell = shell;
        _inspect = ActionButton("View warning info", DesktopIcons.Info, InspectAsync);
        _inspect.IsEnabled = false;
        _inspect.Classes.Add("primary-action");
        _refresh = ActionButton("Refresh", "refresh", LoadAsync);
        AutomationProperties.SetName(_list, "Warning history");
        AutomationProperties.SetName(_source, "Filter by source");
        AutomationProperties.SetName(_status, "Filter by status");
        AutomationProperties.SetName(_sort, "Sort by date");
        AutomationProperties.SetName(_search, "Search warnings");
        Options(_source, "All sources", []);
        Options(_status, "All statuses", []);
        _list.ItemTemplate = new FuncDataTemplate<Row>((row, _) => row is null ? null : IncidentRow(row));
        _list.AddHandler(InputElement.DoubleTappedEvent, async (_, args) =>
        {
            if (args.Source is not Visual hit || hit.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not Row row)
            {
                return;
            }

            args.Handled = true;
            await ShowAsync(row);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        _list.KeyDown += async (_, args) =>
        {
            if (args.Key != Avalonia.Input.Key.Enter || _list.SelectedItem is not Row row)
            {
                return;
            }

            args.Handled = true;
            await ShowAsync(row);
        };
        _list.SelectionChanged += (_, _) => _inspect.IsEnabled = _list.SelectedItem is Row;
        _source.SelectionChanged += (_, _) => Filter();
        _status.SelectionChanged += (_, _) => Filter();
        _sort.SelectionChanged += (_, _) => Filter();
        _search.TextChanged += (_, _) => Filter();

        var actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 6, Children = { _refresh, _inspect } };
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        title.Children.Add(Glyph("info", 38, history: true));
        var titleCopy = new StackPanel { Spacing = 2, Children =
            { new TextBlock { Text = "Warning history", Classes = { "heading" }, TextWrapping = TextWrapping.Wrap }, _message } };
        Grid.SetColumn(titleCopy, 1);
        title.Children.Add(titleCopy);
        var toolbar = new WrapPanel
        {
            ItemSpacing = 12, LineSpacing = 8,
            Children = { FilterField("Source", _source, 190), FilterField("Status", _status, 150),
                FilterField("Date order", _sort, 150), FilterField("Search", _search, 180) }
        };
        var filters = new Border { Classes = { "panel-frame" }, Padding = new Thickness(14), Child = toolbar };
        var heading = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = 12, Children = { title, actions }
        };
        Grid.SetColumn(actions, 1);
        heading.SizeChanged += (_, args) =>
        {
            var compact = args.NewSize.Width < 560;
            Grid.SetColumn(actions, compact ? 0 : 1);
            Grid.SetColumnSpan(actions, compact ? 2 : 1);
            Grid.SetRow(actions, compact ? 1 : 0);
            heading.RowSpacing = compact ? 8 : 0;
        };
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"), RowSpacing = 10 };
        grid.Children.Add(heading);
        Grid.SetRow(filters, 1);
        grid.Children.Add(filters);
        Grid.SetRow(_error, 2);
        grid.Children.Add(_error);
        var table = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var header = TableColumns(Caption("Date"), Caption("Warning"));
        header.MinHeight = 23;
        table.Children.Add(new Border { Classes = { "event-header" }, Child = header });
        Grid.SetRow(_list, 1);
        table.Children.Add(_list);
        _frame = TableFrame(table);
        _frame.VerticalAlignment = VerticalAlignment.Top;
        _frame.IsVisible = false;
        Grid.SetRow(_frame, 3);
        grid.Children.Add(_frame);
        Grid.SetRow(_empty, 3);
        _empty.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(_empty);
        Content = grid;
        AttachedToVisualTree += async (_, _) =>
        {
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(shell.StopToken);
            await LoadAsync();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _lifetime?.Cancel();
            _lifetime?.Dispose();
            _lifetime = null;
        };
    }

    private static Grid IncidentRow(Row row)
    {
        var incident = row.Incident;
        var local = incident.StartedAt.LocalDateTime;
        var dateLabel = new TextBlock { Text = local.ToString("d"), FontWeight = FontWeight.SemiBold };
        var date = new StackPanel { Spacing = 2, Children = { dateLabel, Caption(local.ToString("T")) } };
        var label = Field(row.Source, row.Title, 15);
        var status = Badge(incident.Status);
        if (incident.Status == "Interrupted")
        {
            ToolTip.SetTip(status, InterruptedExplanation);
        }

        var isSafeguard = incident.Kind == "safeguard";
        var iconTone = isSafeguard || incident.Status == "Active" ? Tone(incident.Status) : "warning";
        var name = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10,
            Children = { Glyph(iconTone, 32, isSafeguard), label, status }
        };
        Grid.SetColumn(label, 1);
        Grid.SetColumn(status, 2);
        ToolTip.SetTip(name, row.Label);
        return TableColumns(date, name);
    }

    private static Grid TableColumns(Control date, Control warning)
    {
        date.VerticalAlignment = VerticalAlignment.Center;
        warning.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("128,*"), ColumnSpacing = 12,
            Margin = new Thickness(14, 6), Children = { date, warning }
        };
        Grid.SetColumn(warning, 1);
        return row;
    }

    private static Control FilterField(string label, Control input, double width)
    {
        input.Width = width;
        return new StackPanel { Spacing = 4, Children = { Caption(label), input } };
    }

    private static void Options(ComboBox box, string all, IEnumerable<string> values)
    {
        var previous = box.SelectedItem as string;
        var options = values.Distinct().Order(StringComparer.CurrentCultureIgnoreCase).Prepend(all).ToArray();
        box.ItemsSource = options;
        box.SelectedItem = previous is not null && options.Contains(previous) ? previous : all;
    }

    private async Task LoadAsync()
    {
        if (_lifetime is not { } owner)
        {
            return;
        }

        var token = owner.Token;
        _loading = true;
        _refresh.IsEnabled = false;
        _message.Text = "Loading warning history…";
        _empty.Content = null;
        SetAlert(_error, null);
        try
        {
            var items = await _shell.HistoryApi.GetWarningHistoryAsync(token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            _rows = items.Select(i =>
            {
                var (source, title) = PopupWarning.Describe(i.Title, i.Source);
                return new Row(i, source, title);
            }).ToArray();
            Options(_source, "All sources", _rows.Select(r => r.Source));
            Options(_status, "All statuses", _rows.Select(r => r.Incident.Status));
            _loading = false;
            Filter();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (IsReadError(error))
        {
            if (!token.IsCancellationRequested)
            {
                _message.Text = "History unavailable";
                SetAlert(_error, $"Could not load warning history. {error.Message}", "warning");
            }
        }
        finally
        {
            if (_lifetime == owner) { _loading = false; _refresh.IsEnabled = true; }
        }
    }

    private void Filter()
    {
        if (_loading)
        {
            return;
        }

        var previous = _list.SelectedItem as Row;
        var matching = _rows.Where(r => (_source.SelectedIndex <= 0 || Equals(_source.SelectedItem, r.Source))
            && (_status.SelectedIndex <= 0 || Equals(_status.SelectedItem, r.Incident.Status))
            && r.Label.Contains(_search.Text?.Trim() ?? "", StringComparison.CurrentCultureIgnoreCase));
        var visible = (_sort.SelectedIndex == 1 ? matching.OrderBy(r => r.Incident.StartedAt) : matching.OrderByDescending(r => r.Incident.StartedAt)).ToArray();
        _list.ItemsSource = visible;
        if (previous is not null)
        {
            _list.SelectedItem = visible.FirstOrDefault(r => r.Incident.Id == previous.Incident.Id && r.Incident.Kind == previous.Incident.Kind);
        }

        _frame.IsVisible = visible.Length != 0;
        _message.Text = visible.Length == _rows.Length
            ? $"{_rows.Length} {(_rows.Length == 1 ? "incident" : "incidents")}"
            : $"{visible.Length} of {_rows.Length} incidents";
        _empty.Content = visible.Length != 0 ? null : _rows.Length == 0
            ? StatePanel("No saved warnings", "Warning episodes will appear here when the service records them.", "info")
            : StatePanel("No matching warnings", "Change the source, status, or search filters.", "info");
    }

    private Task InspectAsync() => _list.SelectedItem is Row row ? ShowAsync(row) : Task.CompletedTask;

    private async Task ShowAsync(Row row)
    {
        if (_inspecting || _lifetime is null || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var token = _lifetime.Token;
        _inspecting = true;
        SetAlert(_error, null);
        try
        {
            var incident = await _shell.HistoryApi.GetWarningIncidentAsync(row.Incident.Kind, row.Incident.Id, token);
            if (!token.IsCancellationRequested)
            {
                await new WarningIncidentWindow(row.Source, row.Title, incident, _shell.Preferences.Fahrenheit).ShowDialog(owner);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (IsReadError(error))
        {
            if (!token.IsCancellationRequested)
            {
                SetAlert(_error, error is MonitoringApiException { StatusCode: HttpStatusCode.NotFound }
                    ? "This incident is no longer in history. Refresh to update the list." : $"Could not open the incident. {error.Message}", "warning");
            }
        }
        finally { _inspecting = false; }
    }

    private static bool IsReadError(Exception e) => e is HttpRequestException or IOException or JsonException
        or MonitoringApiException or OperationCanceledException or ObjectDisposedException or InvalidDataException;
}
