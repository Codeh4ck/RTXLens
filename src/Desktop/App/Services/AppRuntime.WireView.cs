using Microsoft.UI.Windowing;
using RTXLens.App.WireView.Windows;
using RTXLens.Plugins.WireView.Contracts.Common;
using RTXLens.Presentation.Telemetry;

namespace RTXLens.App.Services;

internal sealed partial class AppRuntime
{
    internal WireViewDeviceWindow? WireViewSettingsWindow { get; private set; }

    internal bool IsWireViewDeviceSimulated =>
#if TELEMETRY_DEBUG
        Debug.Enabled && Debug.Plugins[0].IsSimulated;
#else
        false;
#endif

    internal bool CanApplyWireViewDeviceSettings =>
#if TELEMETRY_DEBUG
        Debug.Enabled ? IsWireViewDeviceSimulated :
#endif
        !IsDemo;

    internal async Task<bool> ApplyWireViewDeviceSettingsAsync(HardwarePanelViewModel panel, DeviceConfig changes, bool simulation)
    {
        var api = CurrentApi;
        var device = panel.DeviceText;

        if (simulation != IsWireViewDeviceSimulated || !CanApplyWireViewDeviceSettings || !panel.IsReporting
            || !ReferenceEquals(panel, Dashboard.Panels.FirstOrDefault(item => item.IsWireView)))
        {
            throw new InvalidOperationException("WireView changed or disconnected. Refresh before applying settings.");
        }

        var applied = await api.ApplyWireViewConfigurationAsync(changes, StopToken);
        if (panel.IsReporting && panel.DeviceText == device && simulation == IsWireViewDeviceSimulated && ReferenceEquals(api, CurrentApi)
            && ReferenceEquals(panel, Dashboard.Panels.FirstOrDefault(item => item.IsWireView)))
        {
#if TELEMETRY_DEBUG
            if (!Debug.Enabled)
            {
                _liveWireViewConfiguration = applied;
            }
#endif
            var status = simulation ? "Saved to this simulation." : "Saved to WireView and verified.";
            panel.SetDeviceConfiguration(applied, simulation ? "Simulated WireView settings loaded." : null);
            OperationCompleted?.Invoke(status);
            return true;
        }

        return false;
    }

    internal async Task OpenWireViewDeviceSettingsAsync()
    {
        if (WireViewSettingsWindow is null)
        {
            WireViewSettingsWindow = new WireViewDeviceWindow(this);
            WireViewSettingsWindow.Closed += (_, _) => WireViewSettingsWindow = null;
            TrackWindow(WireViewSettingsWindow, "wireview-settings", 660, 850, besideOwner: true);

        }
        var window = WireViewSettingsWindow;
        window.SetPanel(Dashboard.Panels.FirstOrDefault(panel => panel.IsWireView));
        window.AppWindow.Show();
        window.Activate();
        await window.RefreshAsync();
    }
}
