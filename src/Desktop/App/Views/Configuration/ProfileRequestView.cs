using System.Text.Json;
using Avalonia.Controls;
using RTXLens.App.ViewModels.Shell;
using RTXLens.Client.Http;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Configuration;

internal abstract class ProfileRequestView(ShellViewModel shell) : UserControl
{
    protected ShellViewModel Shell { get; } = shell;
    protected ContentControl Status { get; } = Alert();
    protected virtual IMonitoringApi ProfileApi => Shell.Api;
    protected bool IsBusy { get; private set; }

    protected void Report(string? message, string tone = "info") => SetAlert(Status, message, tone);

    protected async Task RunAsync(Func<IMonitoringApi, Task> operation)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        UpdateAvailability();

        try
        {
            await operation(ProfileApi);
        }
        catch (Exception error) when (error is ArgumentException or MonitoringApiException or HttpRequestException
            or IOException or InvalidDataException or NotSupportedException or OperationCanceledException or JsonException)
        {
            Report(error.Message, error is ArgumentException ? "warning" : "error");
        }
        finally
        {
            IsBusy = false;
            UpdateAvailability();
        }
    }

    protected abstract void UpdateAvailability();
}
