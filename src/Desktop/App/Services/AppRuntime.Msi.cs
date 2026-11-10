using Microsoft.UI.Windowing;
using RTXLens.App.Msi.Windows;

namespace RTXLens.App.Services;

internal sealed partial class AppRuntime
{
    internal MsiFanCurveWindow? FanCurveWindow { get; private set; }

    internal async Task OpenMsiFanCurveAsync()
    {
        var configuration = Configuration;
        var msi = configuration.Plugins.FirstOrDefault(plugin => plugin.IsMsi)?.Msi;

        if (msi?.IsCurve != true)
        {
            return;
        }

        if (FanCurveWindow is not null)
        {
            FanCurveWindow.Activate();
            return;
        }

        var window = new MsiFanCurveWindow(CurrentApi, Preferences,
            configuration.ReceiveMsiCurveUpdate,
            RecordConfigurationChangeAsync,
            () =>
            {
                var current = configuration.Plugins.FirstOrDefault(plugin => plugin.IsMsi)?.Msi;

                if (current is not null)
                {
                    current.FanModeIndex = (int)Plugins.Msi.Contracts.Configuration.MsiFanControl.Automatic;
                }
            });
        FanCurveWindow = window;
        window.Closed += (_, _) => FanCurveWindow = null;
        TrackWindow(window, "msi-fan-curve", 1000, 720);
        window.Activate();
        await window.InitializeAsync();
    }
}
