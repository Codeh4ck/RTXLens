using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using RTXLens.App.Controls;
using RTXLens.App.ViewModels.Shell;
using RTXLens.App.Views.Shell;
using RTXLens.Plugins.Msi.Contracts.Safeguard;
using RTXLens.SafeguardReporting;
using static RTXLens.App.Views.Configuration.ConfigurationPage;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Safeguard;

[SupportedOSPlatform("windows")]
internal sealed class SafeguardHistoryWindow : LensWindow
{
    private readonly ShellViewModel _shell;
    private readonly SafeguardStorage _storage = new();
    private readonly ContentControl _alert = Alert();
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Multiple };
    private readonly ValidatedNumberInput _retention;
    private readonly Button _inspect;
    private readonly Button _delete;
    private bool _busy;
    private Row[] _rows = [];

    public SafeguardHistoryWindow(ShellViewModel shell)
    {
        _shell = shell;
        Title = "RTX Lens - Safeguard+ History";
        Width = 840;
        Height = 580;
        MinWidth = 650;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _list.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        _list.ItemTemplate = new FuncDataTemplate<Row>((row, _) => CreateRow(row!.Name, row.Date, row.Status));
        _list.SelectionChanged += (_, _) => UpdateActions();
        _list.DoubleTapped += (_, _) => Inspect();
        _inspect = ActionButton("Inspect", "info-action", Inspect);
        _delete = ActionButton("Delete selected", "chart-clear", async void () => await RunAsync(DeleteAsync));
        _retention = new ValidatedNumberInput("History retention", _storage.RetentionDays, 1, 365) { MinWidth = 115 };
        var save = CommitButton("Save retention", async void () => await RunAsync(async () =>
        {
            _storage.RetentionDays = (int)_retention.Read();
            await RefreshAsync();
            SetAlert(_alert, "History retention saved.", "success");
        }));
        var refresh = ActionButton("Refresh", "refresh", async void () => await RunAsync(RefreshAsync));
        var actions = new WrapPanel
        {
            ItemSpacing = 8, LineSpacing = 8, Children = { Field("History retention (days)", _retention), save, refresh, _inspect, _delete }
        };

        var layout = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), RowSpacing = 10 };
        layout.Children.Add(actions);
        var headings = CreateRow("Script", "Date and time", "Status");
        Grid.SetRow(headings, 1);
        layout.Children.Add(headings);
        Grid.SetRow(_list, 2);
        layout.Children.Add(_list);
        Grid.SetRow(_alert, 3);
        layout.Children.Add(_alert);
        Body = layout;

        Opened += async (_, _) => await RunAsync(RefreshAsync);
        _shell.SafeguardHistoryChanged += HistoryChanged;
        Closed += (_, _) => _shell.SafeguardHistoryChanged -= HistoryChanged;
        UpdateActions();
    }

    private async void HistoryChanged() => await RunAsync(RefreshAsync);

    private async Task RefreshAsync()
    {
        _rows = await Task.Run(() =>
        {
            var now = DateTimeOffset.UtcNow;
            var cutoff = now.AddDays(-_storage.RetentionDays);
            return _storage.Ids("history").Select(id => _storage.Read<SafeguardExecution>("history", id)).OfType<SafeguardExecution>()
                .Where(run => run.StartedAt >= cutoff).OrderByDescending(run => run.StartedAt).ThenBy(run => run.Id)
                .Select(run => new Row(run.Id, run.Name, run.StartedAt.LocalDateTime.ToString("g"),
                    SafeguardStorage.DisplayStatus(run, now), SafeguardStorage.IsRunning(run, now))).ToArray();
        });

        var selected = Selected().Select(row => row.Id).ToHashSet();
        _list.ItemsSource = _rows;
        foreach (var row in _rows.Where(row => selected.Contains(row.Id)))
        {
            _list.SelectedItems?.Add(row);
        }

        _shell.SetSafeguardHistoryAvailability(_rows.Length > 0);
        SetAlert(_alert, _rows.Length == 0 ? "No executions within the retention period." : null);
    }

    private void Inspect()
    {
        _ = RunAsync(async () =>
        {
            if (Selected() is not [var row])
            {
                return;
            }

            var report = await Task.Run(() => ReadReport(row.Id));
            if (report is null)
            {
                await RefreshAsync();
                return;
            }

            var window = new SafeguardExecutionReportWindow(report);
            _shell.TrackToolWindow(window, "safeguard-execution", this);
            window.Show(this);
        });
    }

    private SafeguardReport? ReadReport(Guid id)
    {
        if (_storage.Read<SafeguardExecution>("history", id) is not { } run)
        {
            return null;
        }

        var telemetry = _storage.Read<SafeguardTelemetry>("telemetry", id);
        var binding = _storage.Read<SafeguardBinding>("bindings", run.BindingId);
        return SafeguardReport.ForExecution(run, SafeguardStorage.DisplayStatus(run, DateTimeOffset.UtcNow), telemetry) with
        {
            Delivery = _storage.Read<SafeguardDelivery>("delivery", id),
            MailSettings = binding?.MailId is { } mailId ? _storage.Read<SafeguardMailCredential>("mail", mailId)?.Settings : null
        };
    }

    private async Task DeleteAsync()
    {
        var selected = Selected();
        if (selected.Length > 0 && await ConfirmAsync(this, "Delete executions?", "The selected records will be removed from Safeguard+ History.", "Delete"))
        {
            await Task.Run(() => _storage.DeleteExecutions(selected.Select(row => row.Id), DateTimeOffset.UtcNow));
            await RefreshAsync();
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _shell.IsSimulation)
        {
            return;
        }

        _busy = true;
        UpdateActions();
        try
        {
            await action();
        }
        catch (Exception error) when (SafeguardScriptWindow.IsConfigurationError(error))
        {
            SetAlert(_alert, error.Message, "error");
        }
        finally
        {
            _busy = false;
            UpdateActions();
        }
    }

    private Row[] Selected() => _list.SelectedItems?.OfType<Row>().ToArray() ?? [];

    private void UpdateActions()
    {
        var selection = Selected();
        _inspect.IsEnabled = !_busy && selection.Length == 1;
        _delete.IsEnabled = !_busy && selection.Length > 0 && selection.All(row => !row.Running);
    }

    private static Control CreateRow(string script, string date, string status)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("300,190,160"), ColumnSpacing = 8 };
        row.Children.Add(new TextBlock { Text = script, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis });
        var time = new TextBlock { Text = date };
        Grid.SetColumn(time, 1);
        row.Children.Add(time);
        var result = new TextBlock { Text = status };
        Grid.SetColumn(result, 2);
        row.Children.Add(result);
        return row;
    }

    private sealed record Row(Guid Id, string Name, string Date, string Status, bool Running);
}
