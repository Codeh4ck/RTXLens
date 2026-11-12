using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using RTXLens.App.Controls;
using RTXLens.App.Msi.Afterburner;
using RTXLens.App.ViewModels.Shell;
using RTXLens.App.Views.Shell;
using RTXLens.Presentation.Msi.Afterburner;
using static RTXLens.App.Views.Configuration.ConfigurationPage;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Safeguard;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class AfterburnerSettingsWindow : LensWindow
{
    private readonly ShellViewModel _shell;
    private readonly Func<AfterburnerInstallation[]> _findInstallations;
    private readonly ComboBox _installations = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        PlaceholderText = "Select an Afterburner installation"
    };
    private readonly ToggleSwitch _monitoring = Switch("Enable PSU.dll monitoring in Afterburner");
    private readonly ToggleSwitch _software = Switch("Enable Afterburner Safeguard+ alarms");
    private readonly ValidatedNumberInput _maximum = new("Max per-pin current", 9.2m, 0.1m, 30, 1);
    private readonly ValidatedNumberInput _difference = new("Max pin current difference", 1.2m, 0.1m, 30, 1);
    private readonly TabControl _connectors = new() { Name = "SafeguardConnectorTabs" };
    private readonly SectionPanel _connectorPanel;
    private readonly ScrollViewer _contentScroll;
    private readonly ListBox _backups = new() { Name = "SafeguardBackups", Classes = { "event-rows" }, SelectionMode = SelectionMode.Multiple };
    private readonly TextBlock _noBackups = Caption("No configuration backups yet.");
    private readonly TextBlock _dateSortIndicator = new() { Text = "↓" };
    private readonly TextBlock _nameSortIndicator = new();
    private readonly ContentControl _alert = Alert();
    private readonly ContentControl _runningAlert = Alert();
    private readonly ContentControl _version = Message("Afterburner version unavailable", tone: "info", badge: true);
    private readonly ContentControl _plugin = Message("PSU.dll status unavailable", tone: "info", badge: true);
    private readonly Button _save;
    private readonly Button _restore;
    private readonly Button _delete;
    private readonly Button _apply;
    private readonly Button _restart;
    private readonly Button _backupFolder;
    private readonly TextBlock _restartText = new() { Text = "Start Afterburner" };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private AfterburnerInstallation[] _candidates = [];
    private IReadOnlyList<AfterburnerBackup> _backupEntries = [];
    private BackupSortColumn _backupSortColumn = BackupSortColumn.Date;
    private bool _backupSortAscending;
    private AfterburnerFileStore? _store;
    private AfterburnerChangeSet? _baseline;
    private AfterburnerSettings? _saved;
    private AfterburnerConnectorEditor[] _editors = [];
    private bool _busy;
    private bool _allowClose;
    private bool _confirmingClose;
    private bool _running;
    private bool _processStatusKnown;
    private bool _selecting;

    private static string SelectionPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RTXLens", "afterburner-installation.txt");

    public AfterburnerSettingsWindow(ShellViewModel shell, Func<AfterburnerInstallation[]>? findInstallations = null)
    {
        _shell = shell;
        _findInstallations = findInstallations ?? FindInstallations;
        Title = "RTX Lens - Afterburner Safeguard+ Integration";
        Width = 1120;
        Height = 770;
        MinWidth = 730;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _connectors.Theme = (ControlTheme)Application.Current!.FindResource("ConfigurationTabStripTheme")!;

        _save = ActionButton("Save config", "chart-export", async void () => await RunAsync(SaveAsync));
        _restore = ActionButton("Restore config", "chart-import", async void () => await RunAsync(RestoreAsync));
        _delete = ActionButton("Delete", "chart-clear", async void () => await RunAsync(DeleteAsync));
        _apply = CommitButton("Apply Afterburner settings", async void () => await RunAsync(ApplyAsync));
        _restart = ActionButton("Start Afterburner", "refresh", async void () => await RunAsync(RestartAsync));
        _restart.Content = Row(new Image { Source = Icon("refresh"), Width = 15, Height = 15 }, _restartText);
        _backupFolder = ActionButton("Open backup folder", "folder-action", async void () => await RunAsync(OpenBackupFolderAsync));

        var locate = IconButton("Locate Afterburner", "folder-action", async void () => await RunAsync(LocateAsync));
        var refresh = IconButton("Refresh Afterburner status", "refresh", async void () => await RunAsync(async () =>
        {
            if (await ConfirmDiscardAsync())
            {
                await DiscoverAsync(_store?.Installation);
            }
        }));
        var softwareInfo = IconButton("About software alarms", "info-action", async void () => await ShowInformationAsync(this,
            "Afterburner software alarms", DialogText("Afterburner checks the configured software current limits. The PSU hardware protection remains active.")));
        var limitInfo = IconButton("About current limits", "info-action", async void () => await ShowInformationAsync(this,
            "Software current limits", DialogText("The limits are shared by both connectors. Current difference is the highest pin current minus the lowest.")));
        var limits = Columns(Field("Max per-pin current (A)", _maximum), Field("Max pin current difference (A)", _difference));
        var limitRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Children = { limits, limitInfo } };
        Grid.SetColumn(limitInfo, 1);
        limitInfo.VerticalAlignment = VerticalAlignment.Bottom;
        _backupFolder.HorizontalAlignment = HorizontalAlignment.Left;
        var installationField = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            ColumnSpacing = 8,
            Children = { _installations, locate, refresh }
        };
        Grid.SetColumn(locate, 1);
        Grid.SetColumn(refresh, 2);
        AutomationProperties.SetName(_installations, "Afterburner installation");
        var config = Section("Afterburner configuration", new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Columns(_version, _plugin), Field("Afterburner installation", installationField),
                _monitoring, Row(_software, softwareInfo), limitRow
            }
        });

        var backups = CreateBackupTable();
        var backupPanel = Section("Configuration backups", new StackPanel
        {
            Spacing = 10,
            Children = { Toolbar(_save, _restore, _delete), backups, _backupFolder }
        });
        backupPanel.HeaderActions = IconButton("About configuration backups", "info-action", async void () => await ShowInformationAsync(this,
            "Configuration backups", DialogText("Save config copies both files currently on disk, even while Afterburner is running. "
                + "RTX Lens also makes a backup before applying settings or restoring a backup.")));

        _connectorPanel = Section("Connector configuration", _connectors);
        _connectorPanel.IsVisible = false;
        _connectorPanel.Padding = new Thickness(16, 12);

        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { _restart, _apply } };
        Grid.SetColumn(_apply, 2);
        var left = new StackPanel { Spacing = 12, Children = { config, _connectorPanel } };
        var right = new StackPanel { Spacing = 12, Children = { backupPanel, actions, _alert } };
        var columns = Columns(left, right);
        StackAtNarrowWidth(columns, 840);
        _contentScroll = Scroll(new StackPanel
        {
            Margin = new Thickness(18, 16, 18 + PageScrollbarGutter, 18), Spacing = 12,
            Children = { _runningAlert, columns }
        });
        Body = _contentScroll;

        _connectorPanel.SizeChanged += (_, args) =>
        {
            if (args.NewSize.Height > args.PreviousSize.Height)
            {
                Dispatcher.UIThread.Post(FitConnectorControls, DispatcherPriority.Loaded);
            }
        };

        _installations.SelectionChanged += async (_, _) =>
        {
            if (_selecting || _installations.SelectedIndex < 0)
            {
                return;
            }

            var selected = _candidates[_installations.SelectedIndex];
            await RunAsync(async () =>
            {
                if (await ConfirmDiscardAsync())
                {
                    await ReadAsync(selected);
                }
                else
                {
                    SelectInstallation(_store?.Installation);
                }
            });
        };

        Opened += async (_, _) =>
        {
            if (Screens.ScreenFromWindow(this) is { } screen)
            {
                MaxHeight = screen.WorkingArea.Height / screen.Scaling;
            }

            _statusTimer.Start();
            await RunAsync(() => DiscoverAsync());
        };
        Closed += (_, _) => _statusTimer.Stop();
        Closing += OnClosing;
        _statusTimer.Tick += (_, _) => RefreshProcessState();
        UpdateAvailability();
    }

    private void FitConnectorControls()
    {
        var overflow = _contentScroll.Extent.Height - _contentScroll.Viewport.Height;
        if (!IsVisible || WindowState != WindowState.Normal || _contentScroll.Viewport.Height <= 0 || overflow <= 1)
        {
            return;
        }

        var height = Math.Min(MaxHeight, ClientSize.Height + Math.Ceiling(overflow));
        if (Screens.ScreenFromWindow(this) is { } screen)
        {
            var area = screen.WorkingArea;
            height = Math.Min(height, area.Height / screen.Scaling);
            if (height <= ClientSize.Height)
            {
                return;
            }

            var bottommostY = area.Bottom - (int)Math.Ceiling(height * screen.Scaling);
            if (Position.Y > bottommostY)
            {
                Position = new PixelPoint(Position.X, Math.Max(area.Y, bottommostY));
            }
        }

        Height = height;
    }

    internal static AfterburnerInstallation[] FindInstallations()
    {
        try
        {
            return AfterburnerDiscovery.Find(File.Exists(SelectionPath) ? File.ReadAllText(SelectionPath) : null);
        }
        catch (Exception error) when (SafeguardScriptWindow.IsConfigurationError(error))
        {
            return [];
        }
    }

    internal bool HasChanges => _saved is { } saved && (_monitoring.IsChecked != saved.MonitoringEnabled
        || _software.IsChecked != saved.SoftwareAlarm || _maximum.Value != (decimal)saved.MaxCurrent
        || _difference.Value != (decimal)saved.CurrentDifference || _editors.Any(editor => editor.IsDirty));

    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_allowClose || args.CloseReason != WindowCloseReason.WindowClosing)
        {
            return;
        }

        args.Cancel = _busy || _confirmingClose || HasChanges;
        if (_busy || _confirmingClose || !HasChanges)
        {
            return;
        }

        _confirmingClose = true;
        var discard = await ConfirmDiscardAsync();
        _confirmingClose = false;
        if (discard)
        {
            _allowClose = true;
            Close();
        }
    }

    private Task<bool> ConfirmDiscardAsync() => !HasChanges ? Task.FromResult(true) :
        ConfirmAsync(this, "Discard pending changes?", "You have unapplied Afterburner settings.", "Discard changes");

    private async Task DiscoverAsync(string? preferred = null)
    {
        _candidates = await Task.Run(_findInstallations);
        if (_candidates.Length == 0)
        {
            PresentInstallationStatus(null);
            SetAlert(_alert, "MSI Afterburner was not detected. Use Locate Afterburner to choose its installation.", "warning");
            return;
        }

        var selected = _candidates.FirstOrDefault(item => SameDirectory(item.Directory, preferred)) ?? _candidates[0];
        await ReadAsync(selected);
    }

    private async Task LocateAsync()
    {
        if (!await ConfirmDiscardAsync())
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select the MSI Afterburner installation folder" });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        var installation = await Task.Run(() => AfterburnerDiscovery.Inspect(path));
        AfterburnerDiscovery.RequireSupported(installation);
        Directory.CreateDirectory(Path.GetDirectoryName(SelectionPath)!);
        await File.WriteAllTextAsync(SelectionPath, installation.Directory);
        _candidates = [installation, .. _candidates.Where(item => !SameDirectory(item.Directory, installation.Directory))];
        await ReadAsync(installation);
    }

    internal void PresentInstallationStatus(AfterburnerInstallation? installation)
    {
        var supported = installation is not null && AfterburnerDiscovery.IsSupportedVersion(installation.Version);
        SetAlert(_version, installation is null ? "Afterburner version unavailable" : $"Afterburner {installation.Version}",
            supported ? "success" : "error");

        var pluginPresent = installation?.PluginPresent == true;
        SetAlert(_plugin, pluginPresent ? "PSU.dll present" : "PSU.dll unavailable", pluginPresent ? "success" : "error");
    }

    private async Task ReadAsync(AfterburnerInstallation selected)
    {
        _baseline = null;
        _saved = null;
        _store = null;
        _editors = [];
        var selectedConnector = _connectors.SelectedIndex;
        _connectorPanel.IsVisible = false;
        _connectors.ItemsSource = null;
        PresentBackups([]);
        SelectInstallation(selected.Directory);

        AfterburnerInstallation installation;
        try
        {
            installation = await Task.Run(() => AfterburnerDiscovery.Inspect(selected.Directory));
        }
        catch (Exception error) when (SafeguardScriptWindow.IsConfigurationError(error))
        {
            PresentInstallationStatus(null);
            throw;
        }

        PresentInstallationStatus(installation);
        if (!installation.PluginPresent)
        {
            SetAlert(_alert, "PSU.dll is unavailable in this Afterburner installation.", "warning");
            return;
        }

        AfterburnerDiscovery.RequireSupported(installation, requireFiles: false);

        _store = new AfterburnerFileStore(installation.Directory, AfterburnerFileStore.DefaultBackupRoot,
            AfterburnerFileStore.LegacyBackupDirectoryFor(installation.Directory));
        var store = _store;
        await RefreshBackupsAsync();
        if (!installation.ConfigPresent || store.HasInterruptedChange)
        {
            SetAlert(_alert, store.HasInterruptedChange
                ? "A previous write was interrupted. Restore a configuration backup before applying changes."
                : "Afterburner configuration files are missing. Restore a backup or let Afterburner recreate its configuration.", "warning");
            return;
        }

        var baseline = await Task.Run(() => store.Read());
        PresentConfiguration(baseline, selectedConnector);
    }

    internal void PresentConfiguration(AfterburnerChangeSet baseline, int selectedConnector = 0)
    {
        var inspection = AfterburnerConfiguration.Inspect(baseline.Files[0].Before, baseline.Files[1].Before);
        _monitoring.IsChecked = inspection.Settings.MonitoringEnabled;
        _software.IsChecked = inspection.Settings.SoftwareAlarm;
        _maximum.Value = (decimal)inspection.Settings.MaxCurrent;
        _difference.Value = (decimal)inspection.Settings.CurrentDifference;
        var itemTheme = (ControlTheme)Application.Current!.FindResource("ConfigurationTabTheme")!;
        _editors =
        [
            .. Enumerable.Range(1, 2)
                .Select(connector => new AfterburnerConnectorEditor(connector, baseline.Installation,
                    AfterburnerConfiguration.InspectConnector(baseline.Files[1].Before, connector)))
        ];
        _connectors.ItemsSource = _editors.Select((editor, index) => new TabItem
        {
            Header = $"Connector {index + 1}",
            Content = editor,
            Theme = itemTheme
        }).ToArray();
        _connectors.SelectedIndex = Math.Clamp(selectedConnector, 0, _editors.Length - 1);
        _baseline = baseline;
        _saved = inspection.Settings;
        _connectorPanel.IsVisible = true;
    }

    private void SelectInstallation(string? directory)
    {
        _selecting = true;
        _installations.ItemsSource = _candidates.Select(item => item.Directory).ToArray();
        _installations.SelectedIndex = Array.FindIndex(_candidates, item => SameDirectory(item.Directory, directory));
        _selecting = false;
    }

    private async Task SaveAsync()
    {
        var store = RequireStore();
        var baseline = await Task.Run(() => store.Read());
        await ExecuteAsync(new(store.Installation, "save", null, baseline.Id, null), reload: false);
    }

    private async Task ApplyAsync()
    {
        var store = RequireStore();
        AfterburnerDiscovery.RequireStopped();
        if (_backups.ItemsSource?.OfType<AfterburnerBackup>().Any(item => item.Kind == "Saved working configuration") != true)
        {
            SetAlert(_alert, "Click Save config first to keep a copy of your working configuration.", "warning");
            return;
        }

        var baseline = _baseline ?? throw new InvalidOperationException("Refresh the Afterburner configuration before applying changes.");
        var alarms = _editors.Select(editor => editor.Read()).ToArray();
        var settings = new AfterburnerSettings(_monitoring.IsChecked == true, alarms[0].Enabled, _software.IsChecked == true,
            (double)_maximum.Read(), (double)_difference.Read(), 1, alarms[0].UseLowPowerAction) { ConnectorAlarms = alarms };

        var plan = await Task.Run(() =>
        {
            store.ValidateCurrent(baseline);
            return AfterburnerConfiguration.Plan(store.Installation, baseline.Files[0].Before, baseline.Files[1].Before, settings);
        });
        if (!plan.HasChanges)
        {
            SetAlert(_alert, "The selected settings match the saved configuration.");
            return;
        }

        if (await ConfirmPlanAsync(plan, "Apply Afterburner settings?"))
        {
            await ExecuteAsync(new(store.Installation, "apply", settings, plan.Id, null), reload: true);
        }
    }

    private async Task RestoreAsync()
    {
        var store = RequireStore();
        AfterburnerDiscovery.RequireStopped();
        if (SelectedBackups() is [var backup])
        {
            var plan = await Task.Run(() => store.PlanRestore(backup.Id));
            if (await ConfirmPlanAsync(plan, "Restore configuration backup?"))
            {
                await ExecuteAsync(new(store.Installation, "restore", null, plan.Id, backup.Id), reload: true);
            }
        }
    }

    private async Task DeleteAsync()
    {
        var store = RequireStore();
        var selected = SelectedBackups();
        if (selected.Length == 0 || !await ConfirmAsync(this, "Delete configuration backups?",
                $"Delete the {selected.Length} selected backups?", "Delete"))
        {
            return;
        }

        var deletions = await Task.Run(() => selected.Select(backup => new AfterburnerBackupDeletion(backup.Id, store.BackupFingerprint(backup.Id))).ToArray());
        await ExecuteAsync(new(store.Installation, "delete", null, "", null) { BackupsToDelete = deletions }, reload: false);
    }

    private Task<bool> ConfirmPlanAsync(AfterburnerChangeSet plan, string title)
    {
#if DEBUG
        return AfterburnerChangesWindow.ShowAsync(this, _shell, title, plan);
#else
        return ConfirmAsync(this, title, "RTX Lens will back up both configuration files before writing. Keep Afterburner closed until the write finishes.", "Continue");
#endif
    }

    private async Task ExecuteAsync(AfterburnerOperation operation, bool reload)
    {
        if (_shell.IsSimulation)
        {
            return;
        }

        var result = await AfterburnerElevatedOperation.ExecuteAsync(operation);
        if (result.Success && reload)
        {
            var baseline = _baseline;
            await ReadAsync(AfterburnerDiscovery.Inspect(operation.Installation));
            if (baseline is not null && _baseline is not null)
            {
                await _shell.LocalEvents.RecordAsync("Afterburner Safeguard+", AuditSettings(baseline), AuditSettings(_baseline));
            }
        }
        else
        {
            await RefreshBackupsAsync();
        }

        SetAlert(_alert, result.Message, result.Success ? "success" : "error");
    }

    private static object AuditSettings(AfterburnerChangeSet configuration)
    {
        var profile = configuration.Files[1].Before;
        var settings = AfterburnerConfiguration.Inspect(configuration.Files[0].Before, profile).Settings;
        var connectors = new[] { 1, 2 }.Select(number => AfterburnerConfiguration.InspectConnector(profile, number))
            .Select(connector => new
            {
                connector.SourceEnabled,
                connector.AlarmConfigured,
                connector.Timeout,
                Action = connector.LowPowerAction ? "Emergency low-power profile"
                    : connector.BuiltInScriptFileName ?? (string.IsNullOrWhiteSpace(connector.Application) ? "None" : "Custom action")
            }).ToArray();

        return new { settings.MonitoringEnabled, settings.SoftwareAlarm, settings.MaxCurrent, settings.CurrentDifference, connectors };
    }

    private async Task RestartAsync()
    {
        var store = RequireStore();
        var installation = AfterburnerDiscovery.Inspect(store.Installation);
        AfterburnerDiscovery.RequireSupported(installation);
        var baseline = await Task.Run(() => store.Read());
        if (store.HasInterruptedChange || HasChanges)
        {
            throw new InvalidOperationException("Apply or discard pending changes before restarting Afterburner.");
        }

        if (AfterburnerDiscovery.IsRunning())
        {
            if (!await ConfirmAsync(this, "Restart Afterburner?", "Afterburner will be asked to close normally before it is started again.", "Restart"))
            {
                return;
            }

            var result = await AfterburnerElevatedOperation.ExecuteAsync(new(store.Installation, "stop-for-restart", null, baseline.Id, null));
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Message);
            }
        }

        store.ValidateCurrent(baseline);
        AfterburnerDiscovery.RequireStopped();
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(installation.Directory, "MSIAfterburner.exe"))
        {
            UseShellExecute = true, Verb = "runas"
        }) ?? throw new InvalidOperationException("Afterburner could not be started.");
        SetAlert(_alert, "Afterburner started.", "success");
    }

    private Task OpenBackupFolderAsync()
    {
        var path = RequireStore().BackupDirectory;
        AfterburnerFileStore.EnsureNoLinks(path);
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        {
            UseShellExecute = false, ArgumentList = { path }
        });
        return Task.CompletedTask;
    }

    private AfterburnerFileStore RequireStore() => _store ?? throw new InvalidOperationException("Select a supported Afterburner installation.");
    private AfterburnerBackup[] SelectedBackups() => _backups.SelectedItems?.OfType<AfterburnerBackup>().ToArray() ?? [];

    private async Task RefreshBackupsAsync()
    {
        if (_store is { } store)
        {
            PresentBackups(await Task.Run(store.Backups));
            ToolTip.SetTip(_backupFolder, store.BackupDirectory);
        }
    }

    private void RefreshProcessState()
    {
        try
        {
            _running = AfterburnerDiscovery.IsRunning();
            _processStatusKnown = true;
            SetAlert(_runningAlert, _running ? "Afterburner is running. Close it to modify Safeguard+ settings." : null, "warning");
        }
        catch (Exception error) when (SafeguardScriptWindow.IsConfigurationError(error))
        {
            _processStatusKnown = false;
            SetAlert(_runningAlert, "Afterburner process status is unavailable.", "warning");
        }

        UpdateAvailability();
    }

    private void UpdateAvailability()
    {
        var selected = SelectedBackups();
        var idle = !_busy && !_shell.IsSimulation;
        var stopped = idle && _processStatusKnown && !_running;
        var editable = idle && _baseline is not null;
        _save.IsEnabled = editable;
        _apply.IsEnabled = stopped && _baseline is not null;
        _restore.IsEnabled = stopped && _store is not null && selected.Length == 1;
        _delete.IsEnabled = idle && _store is not null && selected.Length > 0;
        _backupFolder.IsEnabled = idle && _store is { } store && Directory.Exists(store.BackupDirectory);
        _restart.IsEnabled = idle && _processStatusKnown && _baseline is not null;
        _restartText.Text = _running ? "Restart Afterburner" : "Start Afterburner";
        _installations.IsEnabled = idle;
        _monitoring.IsEnabled = _software.IsEnabled = _maximum.IsEnabled = _difference.IsEnabled = _connectors.IsEnabled = editable;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _shell.IsSimulation)
        {
            return;
        }

        _busy = true;
        UpdateAvailability();
        SetAlert(_alert, null);
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
            RefreshProcessState();
        }
    }

    private static StackPanel Row(params Control[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.AddRange(controls);
        return row;
    }

    private static WrapPanel Toolbar(params Button[] buttons)
    {
        var toolbar = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        toolbar.Children.AddRange(buttons);
        return toolbar;
    }

    private Control CreateBackupTable()
    {
        _backups.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        _backups.ItemTemplate = new FuncDataTemplate<AfterburnerBackup>((backup, _) =>
            backup is null ? null : BackupRow(backup.CreatedAt.LocalDateTime.ToString("g"), backup.Kind));
        _backups.SelectionChanged += (_, _) => UpdateAvailability();
        ScrollViewer.SetVerticalScrollBarVisibility(_backups, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(_backups, "Saved Afterburner configuration backups");

        var date = BackupSortButton("Date and time", _dateSortIndicator, BackupSortColumn.Date);
        var name = BackupSortButton("Name", _nameSortIndicator, BackupSortColumn.Name);
        var header = new Border
        {
            Classes = { "event-header" },
            CornerRadius = new CornerRadius(7, 7, 0, 0),
            Child = BackupRow(date, name)
        };
        _noBackups.HorizontalAlignment = HorizontalAlignment.Center;
        _noBackups.VerticalAlignment = VerticalAlignment.Center;
        _noBackups.IsHitTestVisible = false;
        var table = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Children = { header, _backups, _noBackups } };
        Grid.SetRow(_backups, 1);
        Grid.SetRow(_noBackups, 1);

        return new Border
        {
            Classes = { "panel-frame" },
            Height = 254,
            ClipToBounds = true,
            Child = table
        };
    }

    private Button BackupSortButton(string label, TextBlock indicator, BackupSortColumn column)
    {
        indicator[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("IconBrush");
        var button = new Button
        {
            Classes = { "column-sort" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = column == BackupSortColumn.Date ? new CornerRadius(7, 0, 0, 0) : new CornerRadius(0, 7, 0, 0),
            Content = Row(new TextBlock { Text = label }, indicator)
        };
        AutomationProperties.SetName(button, $"Sort by {label.ToLowerInvariant()}");
        button.Click += (_, _) =>
        {
            _backupSortAscending = _backupSortColumn == column ? !_backupSortAscending : column == BackupSortColumn.Name;
            _backupSortColumn = column;
            SortBackups();
        };
        return button;
    }

    internal void PresentBackups(IReadOnlyList<AfterburnerBackup> backups)
    {
        _backupEntries = backups;
        _noBackups.IsVisible = backups.Count == 0;
        SortBackups();
    }

    private void SortBackups()
    {
        var selected = SelectedBackups().Select(backup => backup.Id).ToHashSet(StringComparer.Ordinal);
        var ordered = (_backupSortColumn, _backupSortAscending) switch
        {
            (BackupSortColumn.Name, true) => _backupEntries.OrderBy(backup => backup.Kind, StringComparer.CurrentCultureIgnoreCase)
                .ThenByDescending(backup => backup.CreatedAt),
            (BackupSortColumn.Name, false) => _backupEntries.OrderByDescending(backup => backup.Kind, StringComparer.CurrentCultureIgnoreCase)
                .ThenByDescending(backup => backup.CreatedAt),
            (BackupSortColumn.Date, true) => _backupEntries.OrderBy(backup => backup.CreatedAt),
            _ => _backupEntries.OrderByDescending(backup => backup.CreatedAt)
        };
        var rows = ordered.ToArray();
        _backups.ItemsSource = rows;
        if (_backups.SelectedItems is { } selection)
        {
            selection.Clear();
            foreach (var backup in rows.Where(backup => selected.Contains(backup.Id)))
            {
                selection.Add(backup);
            }
        }

        var direction = _backupSortAscending ? "↑" : "↓";
        _dateSortIndicator.Text = _backupSortColumn == BackupSortColumn.Date ? direction : null;
        _nameSortIndicator.Text = _backupSortColumn == BackupSortColumn.Name ? direction : null;
    }

    private static Grid BackupRow(string date, string name) => BackupRow(
        new TextBlock
        {
            Text = date,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(12, 8, 0, 8)
        },
        new TextBlock
        {
            Text = name,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(12, 8, 12 + PageScrollbarGutter, 8)
        });

    private static Grid BackupRow(Control timestamp, Control kind)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("2*,3*"),
            Children = { timestamp, kind }
        };
        Grid.SetColumn(kind, 1);
        return row;
    }

    private static bool SameDirectory(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private enum BackupSortColumn
    {
        Date,
        Name
    }
}
