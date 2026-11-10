using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using RTXLens.App.Services;
using RTXLens.Presentation.Msi.Afterburner;
using Windows.Globalization.NumberFormatting;

namespace RTXLens.App.Msi.Afterburner;

public sealed partial class AfterburnerSettingsView : UserControl
{
    private readonly AppRuntime _runtime;
    private readonly WindowId _windowId;
    private readonly DispatcherQueueTimer _statusTimer;
    private readonly string _selectionPath;
    private AfterburnerInstallation? _installation;
    private AfterburnerFileStore? _store;
    private AfterburnerChangeSet? _baseline;
    private AfterburnerBackup[] _backups = [];
    private bool _busy;
    private bool _populating;
    private bool _loaded;
    private bool _gutterUpdateQueued;
    private bool _invalidNumericInput;
    private (bool MonitoringEnabled, bool SoftwareAlarm, double MaxCurrent, double CurrentDifference)? _savedFields;
    private readonly AfterburnerConnectorView _connector1 = new(1);
    private readonly AfterburnerConnectorView _connector2 = new(2);
    private bool _processStateKnown;
    private DateTimeOffset _startPendingUntil;

    internal event Action? CloseRequested;
    internal bool IsBusy => _busy;
    internal bool HasPendingChanges => _savedFields is { } saved
        && (_invalidNumericInput || !saved.Equals(CurrentFields()) || _connector1.HasPendingChanges || _connector2.HasPendingChanges);

    private (bool, bool, double, double) CurrentFields() =>
        (MonitoringEnabled.IsChecked == true, SoftwareAlarmsEnabled.IsChecked == true, MaxCurrent.Value, CurrentDifference.Value);

    internal void CommitPendingEdits()
    {
        // Read the active input before changing focus: a title-bar close can bypass NumberBox's normal text commit.
        var validCurrent = AfterburnerNumericInput.Commit(MaxCurrent);
        var validDifference = AfterburnerNumericInput.Commit(CurrentDifference);
        _invalidNumericInput = !validCurrent || !validDifference;
        _connector1.CommitPendingEdits();
        _connector2.CommitPendingEdits();
    }

    internal AfterburnerSettingsView(AppRuntime runtime, WindowId windowId)
    {
        _runtime = runtime;
        _windowId = windowId;
        _selectionPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RTXLens", "afterburner-installation.txt");
        InitializeComponent();
        Connector1Host.Content = _connector1;
        Connector2Host.Content = _connector2;

        foreach (var field in new[] { MaxCurrent, CurrentDifference })
        {
            field.NumberFormatter = new DecimalFormatter
            {
                IntegerDigits = 1,
                FractionDigits = 1,
                IsGrouped = false,
                IsDecimalPointAlwaysDisplayed = true,
                NumberRounder = new IncrementNumberRounder
                {
                    Increment = 0.1,
                    RoundingAlgorithm = RoundingAlgorithm.RoundHalfAwayFromZero
                }
            };
        }

        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(2);
        _statusTimer.Tick += (_, _) => RefreshProcessState();
        Unloaded += (_, _) => _statusTimer.Stop();

        Loaded += async (_, _) =>
        {
            _statusTimer.Start();
            RefreshProcessState();

            if (!_loaded)
            {
                _loaded = true;
                await PerformAsync(DiscoverAsync);
            }
        };
    }

    private async Task DiscoverAsync()
    {
        var remembered = File.Exists(_selectionPath) ? await File.ReadAllTextAsync(_selectionPath) : null;
        var installations = await Task.Run(() => AfterburnerDiscovery.Find(remembered));
        _populating = true;
        InstallationPicker.ItemsSource = installations;
        InstallationPicker.SelectedItem = installations.FirstOrDefault(item =>
            item.Directory.Equals(remembered, StringComparison.OrdinalIgnoreCase))
            ?? (installations.Length == 1 ? installations[0] : null);
        _populating = false;

        if (InstallationPicker.SelectedItem is AfterburnerInstallation installation)
        {
            await ReadInstallationAsync(installation);
        }
        else
        {
            _installation = null;
            _store = null;
            _baseline = null;
            _savedFields = null;
            UpdateInstallationStatus(null, installations.Length > 0);
            ShowMessage(installations.Length == 0
                ? "Afterburner was not found. Locate its executable to configure Safeguard+."
                : "Multiple Afterburner installations found. Select the installation to configure.", true);
        }
    }

    private async Task ReadInstallationAsync(AfterburnerInstallation installation)
    {
        installation = AfterburnerDiscovery.Inspect(installation.Directory);
        _installation = installation;
        _baseline = null;
        _savedFields = null;
        _store = new AfterburnerFileStore(installation.Directory, AfterburnerFileStore.DefaultBackupRoot, AfterburnerFileStore.LegacyBackupDirectoryFor(installation.Directory));
        UpdateInstallationStatus(installation);

        if (!installation.ConfigPresent)
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = "Afterburner configuration is missing",
                Content = "PSU.cfg and Profiles\\MSIAfterburner.cfg must both exist. Configure Afterburner once, or restore its configuration outside this window, then reopen the integration.",
                CloseButtonText = "Close"
            }.ShowAsync();

            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => CloseRequested?.Invoke());
            return;
        }

        AfterburnerDiscovery.RequireSupported(installation, false);
        RefreshBackups();

        if (!installation.PluginPresent)
        {
            ShowMessage("PSU.dll is missing. Install an Afterburner version with the PSU monitoring plugin before configuring Safeguard+.", true);
            return;
        }

        var baseline = await Task.Run(() => _store.Read());
        var inspection = AfterburnerConfiguration.Inspect(baseline.Files[0].Before, baseline.Files[1].Before);
        _baseline = baseline;
        MonitoringEnabled.IsChecked = inspection.Settings.MonitoringEnabled;
        SoftwareAlarmsEnabled.IsChecked = inspection.Settings.SoftwareAlarm;
        MaxCurrent.Value = inspection.Settings.MaxCurrent;
        CurrentDifference.Value = inspection.Settings.CurrentDifference;
        _connector1.SetInspection(AfterburnerConfiguration.InspectConnector(baseline.Files[1].Before, 1), installation.Directory);
        _connector2.SetInspection(AfterburnerConfiguration.InspectConnector(baseline.Files[1].Before, 2), installation.Directory);
        _savedFields = CurrentFields();
        _invalidNumericInput = false;

        if (_store.HasInterruptedChange)
        {
            ShowMessage("A previous update was interrupted. Keep Afterburner closed and restore a saved configuration backup.", true);
        }

        RefreshBackups();
        RefreshProcessState();
    }

    private void RefreshBackups()
    {
        _backups = _store?.Backups() ?? [];
        var selectedIds = BackupPicker.SelectedItems.OfType<AfterburnerBackupChoice>()
            .Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var choices = _backups.Select(item => new AfterburnerBackupChoice(item.Id,
            item.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), item.Kind)).ToArray();
        BackupPicker.ItemsSource = choices;
        BackupPicker.SelectedItems.Clear();
        foreach (var item in choices.Where(item => selectedIds.Contains(item.Id)))
        {
            BackupPicker.SelectedItems.Add(item);
        }

        if (BackupPicker.SelectedItems.Count == 0 && choices.Length > 0)
        {
            BackupPicker.SelectedItems.Add(choices[0]);
        }
        BackupLocation.Content = new TextBlock { Text = _store is null ? "" : "Backup location: " + _store.BackupDirectory, TextWrapping = TextWrapping.Wrap };
        BackupLocation.IsEnabled = _store is not null && Directory.Exists(_store.BackupDirectory);
    }

    private async Task SaveAsync()
    {
        RequireReady();
        AfterburnerDiscovery.RequireStopped();
        var current = await Task.Run(() => _store!.Read());
        var result = await AfterburnerElevatedOperation.ExecuteAsync(new(_installation!.Directory, "save", null, current.Id, null));
        ShowMessage(result.Message, !result.Success);
        RefreshBackups();
    }

    private async Task ApplyAsync()
    {
        RequireReady();
        AfterburnerDiscovery.RequireStopped();
        if (_backups.All(item => item.Kind != "Saved working configuration"))
        {
            ShowMessage("Click Save config first to preserve your existing working setup.", true);
            return;
        }

        var alarms = new[] { _connector1.Draft, _connector2.Draft };
        var selected = alarms[0];
        var settings = new AfterburnerSettings(MonitoringEnabled.IsChecked == true, selected.Enabled,
            SoftwareAlarmsEnabled.IsChecked == true, MaxCurrent.Value, CurrentDifference.Value, selected.Connector, selected.UseLowPowerAction)
        {
            ConnectorAlarms = alarms
        };
        var baseline = _baseline!;
        _store!.ValidateCurrent(baseline);

        var plan = AfterburnerConfiguration.Plan(_installation!.Directory, baseline.Files[0].Before, baseline.Files[1].Before, settings);

        if (!plan.HasChanges)
        {
            ShowMessage("Both connector configurations and shared settings match the saved configuration.", false);
            return;
        }

        if (!await ConfirmPlanAsync(plan, "Apply Afterburner settings?"))
        {
            return;
        }

        var result = await AfterburnerElevatedOperation.ExecuteAsync(new(_installation.Directory, "apply", settings, plan.Id, null));
        if (result.Success)
        {
            await RecordAfterburnerChangesAsync(plan);
            await ReadInstallationAsync(_installation);
        }

        ShowMessage(result.Message, !result.Success);
    }

    private async Task RestoreAsync()
    {
        RequireReady(false);
        AfterburnerDiscovery.RequireStopped();
        if (BackupPicker.SelectedItems.Count != 1 || BackupPicker.SelectedItems[0] is not AfterburnerBackupChoice backup)
        {
            throw new InvalidOperationException("Select exactly one backup to restore.");
        }

        var plan = await Task.Run(() => _store!.PlanRestore(backup.Id));
        if (!await ConfirmPlanAsync(plan, "Restore Afterburner configuration backup?"))
        {
            return;
        }

        var result = await AfterburnerElevatedOperation.ExecuteAsync(new(_installation!.Directory, "restore", null, plan.Id, backup.Id));
        if (result.Success)
        {
            await RecordAfterburnerChangesAsync(plan);
            await ReadInstallationAsync(_installation);
        }
        ShowMessage(result.Message, !result.Success);
    }

    private Task RecordAfterburnerChangesAsync(AfterburnerChangeSet plan) =>
        _runtime.EventLog.RecordAsync("Afterburner Safeguard+", ReadAuditFields(plan.Files[0].Before, plan.Files[1].Before),
            ReadAuditFields(plan.Files[0].After, plan.Files[1].After));

    private static object? ReadAuditFields(byte[] plugin, byte[] profile)
    {
        try
        {
            var settings = AfterburnerConfiguration.Inspect(plugin, profile).Settings;
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
        catch (Exception error) when (error is IOException or ArgumentException)
        {
            // A restored backup may replace a damaged file with no reliable previous values.
            System.Diagnostics.Debug.WriteLine(error);
            return null;
        }
    }

    private async Task<bool> ConfirmPlanAsync(AfterburnerChangeSet plan, string title)
    {
#if DEBUG || TELEMETRY_DEBUG
        return await AfterburnerDiffWindow.ShowAsync(plan, _runtime);
#else
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = title,
            Content = "Both configuration files will be backed up before writing. Afterburner must remain closed and needs restarting afterwards.",
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
#endif
    }

    private void RequireReady(bool requireBaseline = true)
    {
        if (_installation is null || _store is null || (requireBaseline && _baseline is null))
        {
            throw new InvalidOperationException("Select a supported installation and refresh its configuration first.");
        }
    }

    private async Task PerformAsync(Func<Task> action)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        SetEnabled();
        try
        {
            await action();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException
            or System.Runtime.InteropServices.COMException or System.Security.SecurityException or OperationCanceledException)
        {
            var message = string.IsNullOrWhiteSpace(error.Message)
                ? "The Afterburner operation could not be completed."
                : error.Message;

            ShowMessage(message, true);
        }
        finally
        {
            _busy = false;
            SetEnabled();
        }
    }

    private void SetEnabled()
    {
        var ready = !_busy && _baseline is not null;
        InstallationPicker.IsEnabled = !_busy;
        SettingsPanel.IsEnabled = ready && _store?.HasInterruptedChange != true;
        ConnectorSettingsPanel.IsEnabled = SettingsPanel.IsEnabled;
        SaveButton.IsEnabled = ready;
        var backupSelected = BackupPicker.SelectedItems.Count;
        RestoreButton.IsEnabled = !_busy && _store is not null && backupSelected == 1;
        DeleteBackupButton.IsEnabled = !_busy && _store is not null && backupSelected > 0;
        ApplyButton.IsEnabled = ready && _store?.HasInterruptedChange != true;
        RestartButton.IsEnabled = ready && _processStateKnown && DateTimeOffset.UtcNow >= _startPendingUntil && _store?.HasInterruptedChange != true;
    }

    private void ShowMessage(string message, bool error)
    {
        OperationMessage.Severity = error ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        OperationMessage.Message = message;
        OperationMessage.IsOpen = true;
        if (IsLoaded)
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, BringMessageIntoView);
        }
    }

    private void BringMessageIntoView()
    {
        if (!IsLoaded || !OperationMessage.IsOpen)
        {
            return;
        }

        OperationMessage.UpdateLayout();
        DependencyObject? ancestor = VisualTreeHelper.GetParent(OperationMessage);
        while (ancestor is not null && ancestor is not ScrollViewer)
        {
            ancestor = VisualTreeHelper.GetParent(ancestor);
        }

        if (ancestor is ScrollViewer { Content: FrameworkElement content } scroll)
        {
            var top = OperationMessage.TransformToVisual(content).TransformPoint(new global::Windows.Foundation.Point(0, 0)).Y;
            var offset = Math.Clamp(top + OperationMessage.ActualHeight + 12 - scroll.ViewportHeight, 0, scroll.ScrollableHeight);
            scroll.ChangeView(null, offset, null, true);
        }
        else
        {
            OperationMessage.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 1 });
        }
    }

    private async void Installation_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (!_populating && InstallationPicker.SelectedItem is AfterburnerInstallation installation)
        {
            await PerformAsync(async () =>
            {
                await ReadInstallationAsync(installation);
                Directory.CreateDirectory(Path.GetDirectoryName(_selectionPath)!);
                await File.WriteAllTextAsync(_selectionPath, installation.Directory);
            });
        }
    }

    private async void Locate_Click(object sender, RoutedEventArgs args) => await PerformAsync(async () =>
    {
        var picker = new FileOpenPicker(_windowId);
        picker.FileTypeFilter.Add(".exe");

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        if (!Path.GetFileName(file.Path).Equals("MSIAfterburner.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Select MSIAfterburner.exe.");
        }

        var installation = AfterburnerDiscovery.Inspect(Path.GetDirectoryName(file.Path)!);
        _populating = true;
        InstallationPicker.ItemsSource = new[] { installation };
        InstallationPicker.SelectedItem = installation;
        _populating = false;
        await ReadInstallationAsync(installation);
        Directory.CreateDirectory(Path.GetDirectoryName(_selectionPath)!);
        await File.WriteAllTextAsync(_selectionPath, installation.Directory);
    });

    private async void Refresh_Click(object sender, RoutedEventArgs args) => await PerformAsync(DiscoverAsync);
    private async void Save_Click(object sender, RoutedEventArgs args) => await PerformAsync(SaveAsync);
    private async void Restore_Click(object sender, RoutedEventArgs args) => await PerformAsync(RestoreAsync);
    private async void Apply_Click(object sender, RoutedEventArgs args) => await PerformAsync(ApplyAsync);

    private void PageLayout_Changed(object sender, SizeChangedEventArgs args)
    {
        if (_gutterUpdateQueued)
        {
            return;
        }

        _gutterUpdateQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _gutterUpdateQueued = false;
            if (!IsLoaded)
            {
                return;
            }

            var warningWidth = Math.Max(1, (HeaderLayout.ActualWidth - HeaderLayout.ColumnSpacing) / 2
                - HeaderInfoButton.ActualWidth - 12 - 18 - 8);
            ProcessWarningText.MaxWidth = warningWidth;

            // Reserve equal side gutters only when the page actually needs its overlay scrollbar.
            var gutter = PageScroll.ScrollableHeight > 1 ? 20 : 0;
            var panelMargin = new Thickness(gutter, 0, gutter, 8);
            if (Panels.Margin != panelMargin)
            {
                Panels.Margin = panelMargin;
                HeaderLayout.Margin = new Thickness(gutter, 0, gutter, 0);
            }
        });
    }
    private void CurrentValue_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!double.IsFinite(args.NewValue))
        {
            return;
        }

        var rounded = Math.Round(args.NewValue, 1, MidpointRounding.AwayFromZero);
        if (Math.Abs(args.NewValue - rounded) > 0.001)
        {
            sender.Value = rounded;
        }
    }

    private void SoftwareAlarms_Changed(object sender, RoutedEventArgs args)
    {
        if (ThresholdsPanel is not null)
        {
            ThresholdsPanel.IsEnabled = SoftwareAlarmsEnabled.IsChecked == true;
        }
    }

    private async void Restart_Click(object sender, RoutedEventArgs args) => await PerformAsync(async () =>
    {
        RequireReady();

        var installation = AfterburnerDiscovery.Inspect(_installation!.Directory);
        AfterburnerDiscovery.RequireSupported(installation);

        if (AfterburnerDiscovery.IsRunning())
        {
            var current = await Task.Run(() => _store!.Read());

            var result = await AfterburnerElevatedOperation
                .ExecuteAsync(new(installation.Directory, "stop-for-restart", null, current.Id, null));

            if (!result.Success)
            {
                ShowMessage(result.Message, true);
                return;
            }
        }

        AfterburnerDiscovery.RequireStopped();

        _ = Process.Start(new ProcessStartInfo(Path.Combine(installation.Directory, "MSIAfterburner.exe")) { UseShellExecute = true, Verb = "runas" })
            ?? throw new InvalidOperationException("Afterburner could not be started.");

        _startPendingUntil = DateTimeOffset.UtcNow.AddSeconds(5);
        RefreshProcessState();

        ShowMessage("Afterburner launch requested. Its saved configuration will be loaded at startup.", false);
    });

    private async void BackupInfo_Click(object sender, RoutedEventArgs args) => await ShowInfoAsync(
        "Configuration backups",
        "Close Afterburner, then use Save config to keep your existing working configuration before making changes. "
        + "Explicitly saved working backups are kept until you delete them. A paired backup of both files is also made automatically before each apply or restore. "
        + "Select one backup to restore, or multiple backups to delete together. Restoring requires Afterburner to be closed and restarted afterwards.");

    private async void SoftwareAlarmInfo_Click(object sender, RoutedEventArgs args) => await ShowInfoAsync(
        "Afterburner software alarms",
        "Enables Afterburner's software current checks. The PSU's hardware protection remains active. "
        + "When unchecked, the plugin reports the PSU firmware alarm status. Afterburner evaluates its alarms and runs the action selected for each connector.");

    private async void ThresholdInfo_Click(object sender, RoutedEventArgs args) => await ShowInfoAsync(
        "Current thresholds",
        "Software current limits are shared by both connectors. Current difference is highest minus lowest pin current in amperes. "
        + "PSU firmware limits are not changed.");

    private Task ShowInfoAsync(string title, string message) => PerformAsync(async () =>
    {
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = title,
            Content = message,
            CloseButtonText = "Close"
        }.ShowAsync();
    });

    private async void Info_Click(object sender, RoutedEventArgs args) => await PerformAsync(async () =>
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = "Afterburner Safeguard+ Integration",
            Content = "This is one-way configuration of Afterburner. RTX Lens does not consume its alarms or run alarm commands.\n\n"
                + "Close Afterburner and save a working configuration backup first. Apply changes, then restart Afterburner. "
                + "Safeguard+ configured describes the files; it does not guarantee monitoring is running.\n\n"
                + "Software mode alarms when any pin exceeds Maximum per-pin current, or highest minus lowest exceeds Maximum pin-current difference. "
                + "Both connectors share these limits. When software checks are unchecked, the plugin uses the PSU's firmware alarm status. Hardware protection stays active in either case and the PSU buzzer is not configured here.\n\n"
                + "The low-power preset asks Afterburner to apply its emergency profile. Native sound flags are preserved. Each connector has its own editable alarm timeout. "
                + "Other PSU controllers can interfere with USB access; simultaneous polling and fan control compatibility must be checked separately.",
            CloseButtonText = "Close"
        };

        await dialog.ShowAsync();
    });

    internal void StopMonitoring() => _statusTimer.Stop();

    private async void BackupLocation_Click(object sender, RoutedEventArgs args) => await PerformAsync(() =>
    {
        RequireReady(false);

        var directory = _store!.BackupDirectory;
        AfterburnerFileStore.EnsureNoLinks(directory);

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("Save a configuration backup first to create the backup folder.");
        }

        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        {
            UseShellExecute = true
        };

        start.ArgumentList.Add(directory);
        Process.Start(start);

        return Task.CompletedTask;
    });

    private void RefreshProcessState()
    {
        try
        {
            var running = AfterburnerDiscovery.IsRunning();
            var starting = !running && DateTimeOffset.UtcNow < _startPendingUntil;

            _processStateKnown = true;

            var scriptArmed = new[] { _connector1.Saved, _connector2.Saved }.Any(connector => connector is { AlarmConfigured: true, ScriptBindingId: not null });

            ProcessWarningText.Text = running && scriptArmed && _installation is { } installation
                && !AfterburnerProcessControl.HasElevatedInstance(installation.Directory)
                ? "Safeguard+ scripts require the selected Afterburner installation to run as administrator."
                : "Afterburner is running. Close Afterburner to modify Safeguard+ settings!";

            ProcessWarning.Visibility = running || starting ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _processStateKnown = false;

            ProcessWarningText.Text = "Afterburner process status is unavailable. Refresh status before modifying Safeguard+ settings!";
            ProcessWarning.Visibility = Visibility.Visible;

            Debug.WriteLine(error);
        }

        SetEnabled();
    }

    private void UpdateInstallationStatus(AfterburnerInstallation? installation, bool hasCandidates = false)
    {
        VersionStatus.Text = installation?.Version ?? (hasCandidates ? "Select installation" : "Not found");
        PluginStatus.Text = installation is null ? (hasCandidates ? "-" : "Missing") : installation.PluginPresent ? "Present" : "Missing";

        VisualStateManager.GoToState(this, installation is not null || hasCandidates ? "VersionPresent" : "VersionMissing", false);
        VisualStateManager.GoToState(this, installation?.PluginPresent == true || hasCandidates ? "PluginPresent" : "PluginMissing", false);
    }

    private void Backup_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (IsLoaded)
        {
            SetEnabled();
        }
    }

    private async Task DeleteBackupAsync()
    {
        RequireReady(false);

        var selected = BackupPicker.SelectedItems.OfType<AfterburnerBackupChoice>().ToArray();
        if (selected.Length == 0)
        {
            throw new InvalidOperationException("Select one or more backups to delete.");
        }

        var deletions = selected.Select(item => new AfterburnerBackupDeletion(item.Id, _store!.BackupFingerprint(item.Id))).ToArray();
        var labels = string.Join("\n", selected.Take(6).Select(item => item.Label));

        if (selected.Length > 6)
        {
            labels += "\n... and " + (selected.Length - 6) + " more.";
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = selected.Length == 1 ? "Delete configuration backup?" : "Delete " + selected.Length + " configuration backups?",
            Content = labels + "\n\nBoth files in each selected backup will be permanently deleted. Your active Afterburner configuration will not be changed.",
            PrimaryButtonText = selected.Length == 1 ? "Delete backup" : "Delete backups",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = await AfterburnerElevatedOperation
            .ExecuteAsync(new(_installation!.Directory, "delete", null, "", null) { BackupsToDelete = deletions });

        RefreshBackups();
        ShowMessage(result.Message, !result.Success);
    }
    private async void DeleteBackup_Click(object sender, RoutedEventArgs args) => await PerformAsync(DeleteBackupAsync);

}