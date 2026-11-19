using System.Text.Json;
using RTXLens.Client.Serialization;
using RTXLens.Contracts.Configuration;
using RTXLens.Plugins.Asus.Contracts.Common;
using RTXLens.Plugins.Asus.Contracts.Configuration;
using RTXLens.Plugins.Msi.Contracts.Configuration;
using RTXLens.Plugins.Msi.Contracts.Devices;
using RTXLens.Presentation.Configuration;
using RTXLens.Tests.Infrastructure;
using RTXLens.UI.Tests.TestDoubles;

namespace RTXLens.UI.Tests.Configuration;

internal static class DeviceRefreshTests
{
    public static void Run(TestRunner runner)
    {
        runner.Run(GpuRefreshPreservesSelectionDuringBindingCallbacks);
        runner.Run(PsuRefreshPreservesSelectionDuringBindingCallbacks);
        runner.Run(RealDeviceSelectionsStillTrackChanges);
    }

    private static void GpuRefreshPreservesSelectionDuringBindingCallbacks()
    {
        foreach (var id in new string?[] { null, "gpu0", "missing-gpu" })
        {
            var editor = CreateEditor(id, null);
            var asus = editor.Plugins.Single(plugin => plugin.IsAsus).Asus;
            asus.Devices.CollectionChanged += (_, _) =>
            {
                // Clearing the collection can reset the bound selection before SetDevices finishes.
                asus.SelectedDevice = null!;
                if (asus.Devices.Count > 0)
                {
                    asus.SelectedDevice = asus.Devices[0];
                }
            };
            for (var refresh = 0; refresh < 3; refresh++)
            {
                editor.SetDevices(refresh == 1 ? [] : [new AsusDeviceInfo("gpu0", "RTX 5090")]);
                TestAssert.Equal(id, asus.Build().DeviceId);
                TestAssert.True(asus.Devices.Contains(asus.SelectedDevice), "The restored GPU selection must belong to the refreshed list.");
                TestAssert.False(editor.IsDirty, "Binding callbacks during discovery must not create configuration changes.");
            }
            asus.SelectedDevice = null!;
            TestAssert.Equal(id, asus.Build().DeviceId);
        }
    }

    private static void PsuRefreshPreservesSelectionDuringBindingCallbacks()
    {
        foreach (var id in new string?[] { null, "psu0", "missing-psu" })
        {
            var editor = CreateEditor(null, id);
            var msi = editor.Plugins.Single(plugin => plugin.IsMsi).Msi;
            msi.Devices.CollectionChanged += (_, _) =>
            {
                msi.SelectedDevice = null!;
                if (msi.Devices.Count > 0)
                {
                    msi.SelectedDevice = msi.Devices[0];
                }
            };
            for (var refresh = 0; refresh < 3; refresh++)
            {
                editor.SetMsiDevices(refresh == 1 ? [] : [new MsiDeviceInfo("psu0", "MPG Ai1300TS")]);
                TestAssert.Equal(id, msi.Build().DevicePath);
                TestAssert.True(msi.Devices.Contains(msi.SelectedDevice), "The restored PSU selection must belong to the refreshed list.");
                TestAssert.False(editor.IsDirty, "PSU discovery must not change the saved configuration.");
            }
            msi.SelectedDevice = null!;
            TestAssert.Equal(id, msi.Build().DevicePath);
        }
    }

    private static void RealDeviceSelectionsStillTrackChanges()
    {
        var editor = CreateEditor(null, null);
        editor.SetDevices([new AsusDeviceInfo("gpu0", "RTX 5090")]);
        editor.SetMsiDevices([new MsiDeviceInfo("psu0", "MPG Ai1300TS")]);
        var asus = editor.Plugins.Single(plugin => plugin.IsAsus).Asus;
        var msi = editor.Plugins.Single(plugin => plugin.IsMsi).Msi;
        asus.SelectedDevice = asus.Devices.Single(device => device.Id == "gpu0");
        msi.SelectedDevice = msi.Devices.Single(device => device.Id == "psu0");
        TestAssert.True(editor.IsDirty, "User device selections must remain editable.");
        editor.SetDevices([]);
        editor.SetMsiDevices([]);
        TestAssert.Equal("gpu0", asus.Build().DeviceId);
        TestAssert.Equal("psu0", msi.Build().DevicePath);
        TestAssert.True(editor.IsDirty, "Refresh must preserve existing device-selection drafts.");
        asus.SelectedDevice = asus.Devices.Single(device => device.Id is null);
        msi.SelectedDevice = msi.Devices.Single(device => device.Id is null);
        TestAssert.False(editor.IsDirty, "Selecting the Automatic option must still restore the saved configuration.");
    }

    private static ConfigurationEditorViewModel CreateEditor(string? gpu, string? psu)
    {
        var editor = new ConfigurationEditorViewModel(() => new FakeMonitoringApi());
        editor.Receive(new ConfigurationSnapshot(1, new ServiceConfiguration
        {
            Plugins = new Dictionary<string, PluginSettings>
            {
                ["asus-astral"] = new() { Options = JsonSerializer.SerializeToElement(new AsusOptions { DeviceId = gpu }, ClientJsonContext.Default.AsusOptions) },
                ["msi-psu"] = new() { Options = JsonSerializer.SerializeToElement(new MsiOptions { DevicePath = psu }, ClientJsonContext.Default.MsiOptions) }
            }
        }));
        return editor;
    }
}
