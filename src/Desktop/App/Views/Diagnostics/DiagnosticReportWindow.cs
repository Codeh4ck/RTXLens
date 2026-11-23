using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using RTXLens.App.Platform;
using RTXLens.App.Views.Shell;
using RTXLens.Presentation.Settings;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Diagnostics;

internal sealed class DiagnosticReportWindow : LensWindow
{
    private readonly ComboBox _period = new()
    {
        ItemsSource = new[] { "Last hour", "Last 24 hours", "Last 48 hours", "Last 7 days", "Custom range" },
        SelectedIndex = 2, HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly CalendarDatePicker _fromDate = new(), _throughDate = new();
    private readonly TimePicker _fromTime = new(), _throughTime = new();
    private readonly TextBlock _status = Caption();
    private readonly StackPanel _inputs;
    private CancellationTokenSource? _operation;

    public DiagnosticReportWindow(string serviceAddress, CancellationToken stopToken)
    {
        Title = "Collect diagnostic information";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var now = DateTime.Now;
        var from = now.AddDays(-2);
        _fromDate.SelectedDate = from.Date;
        _throughDate.SelectedDate = now.Date;
        _fromTime.SelectedTime = new TimeSpan(from.Hour, from.Minute, 0);
        _throughTime.SelectedTime = new TimeSpan(now.Hour, now.Minute, 0);
        var custom = new StackPanel
        {
            Spacing = 8, IsVisible = false,
            Children = { Caption("From (local time)"), Columns(_fromDate, _fromTime),
                Caption("Through (local time)"), Columns(_throughDate, _throughTime) }
        };
        _period.SelectionChanged += (_, _) => custom.IsVisible = _period.SelectedIndex == 4;
        _inputs = new StackPanel { Spacing = 8, Children = { Caption("Time range"), _period, custom } };
        _status.IsVisible = false;
        var configured = new Uri(serviceAddress);
        var useConfigured = configured.IsLoopback && configured.AbsolutePath == "/";
        var localService = useConfigured ? configured : new Uri(new AppPreferences().ServiceAddress);
        var description = "The time range applies to crash reports and events. Hardware models and sensor readings are captured now. Personal identifiers, paths and raw log messages are excluded.";
        if (!useConfigured)
            description += " This report uses the local RTX Lens service on this PC.";
        var collect = CommitButton("Collect", () => CollectAsync(localService, stopToken));
        Body = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 16,
            Children =
            {
                DialogText(description),
                _inputs, _status,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
                    Children = { ActionButton("Cancel", "cancel-action", Close), collect }
                }
            }
        };
        Closing += (_, args) =>
        {
            if (_operation is null) return;
            args.Cancel = true;
            _operation.Cancel();
        };
    }

    private async Task CollectAsync(Uri serviceAddress, CancellationToken stopToken)
    {
        var through = DateTimeOffset.Now;
        var from = _period.SelectedIndex switch
        {
            0 => through.AddHours(-1), 1 => through.AddDays(-1), 2 => through.AddDays(-2), 3 => through.AddDays(-7),
            _ => default
        };
        if (_period.SelectedIndex == 4)
        {
            var first = LocalTime(_fromDate, _fromTime);
            var last = LocalTime(_throughDate, _throughTime);
            if (first is null || last is null || last < first || last > through)
            {
                SetStatus("Choose valid start and end times in the past. The end must be at or after the start.");
                return;
            }
            from = first.Value;
            // Include the entire selected end minute, up to the time of collection.
            through = new DateTimeOffset(Math.Min(through.UtcTicks, last.Value.UtcTicks + TimeSpan.TicksPerMinute - 1), TimeSpan.Zero);
        }

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        var workspace = Directory.CreateTempSubdirectory("RTXLens-Report-");
        _operation = operation;
        _inputs.IsEnabled = false;
        try
        {
            var archive = await DiagnosticReport.CollectAsync(workspace, serviceAddress, from, through,
                new Progress<string>(SetStatus), operation.Token);
            while (!operation.IsCancellationRequested)
            {
                SetStatus("Collection complete. Choose where to save the ZIP file.");
                using var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save diagnostic information", DefaultExtension = "zip",
                    SuggestedFileName = $"RTXLens-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
                    FileTypeChoices = [new FilePickerFileType("ZIP archive") { Patterns = ["*.zip"] }],
                    ShowOverwritePrompt = true
                });
                if (target is null) break;
                try
                {
                    var path = target.TryGetLocalPath() ?? throw new IOException("Choose a Windows file location.");
                    await DiagnosticReport.SaveAsync(archive, path, operation.Token);
                    break;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    if (!await ConfirmAsync(this, "Could not save diagnostic information", error.Message, "Choose another location"))
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!operation.IsCancellationRequested)
            {
                SetStatus($"Collection failed. {error.Message}");
                return;
            }
        }
        finally
        {
            _operation = null;
            _inputs.IsEnabled = true;
            workspace.Delete(recursive: true);
        }
        Close();
    }

    private static DateTimeOffset? LocalTime(CalendarDatePicker date, TimePicker time)
    {
        if (date.SelectedDate is not { } day || time.SelectedTime is not { } clock) return null;
        var local = DateTime.SpecifyKind(day.Date + clock, DateTimeKind.Unspecified);
        return TimeZoneInfo.Local.IsInvalidTime(local) ? null : new DateTimeOffset(local);
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
        _status.IsVisible = true;
    }
}
