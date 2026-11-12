using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RTXLens.App.Themes;
using RTXLens.App.ViewModels.Appearance;
using RTXLens.App.ViewModels.Shell;
using RTXLens.App.Views.Configuration;
using RTXLens.App.Views.Settings;
using RTXLens.App.Views.Shell;
using RTXLens.Client.Serialization;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Warnings;
using RTXLens.Plugins.Asus.Contracts.Common;
using RTXLens.Plugins.GenericNvidia.Contracts.Devices;
using RTXLens.Plugins.Msi.Contracts.Devices;
using RTXLens.Presentation.Settings;
using RTXLens.Tests.Infrastructure;

namespace RTXLens.UI.Tests.Controls;

internal static class AvaloniaSettingsParityTests
{
    public static void ExercisesSettingsAndProfiles(string? screenshots)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rtxlens-{Guid.NewGuid():N}");
        var store = new PreferencesStore(Path.Combine(directory, "preferences.json"));
        var fixture = new SettingsService();
        var appearanceStore = new AppearanceStore(Path.Combine(directory, "appearance.json"));
        var appearance = new AppearanceViewModel(Application.Current!, appearanceStore);
        using var shell = new ShellViewModel(store, appearance, address => new HttpClient(fixture, false) { BaseAddress = address });
        shell.SetPreferences(shell.Preferences with { Theme = AppTheme.Dark });
        shell.Configuration.Receive(new ConfigurationSnapshot(1, new ServiceConfiguration
        {
            Plugins = new Dictionary<string, PluginSettings>
            {
                ["generic-nvidia"] = new(), ["asus-astral"] = new(), ["msi-psu"] = new()
            }
        }, Version: "test-1"));

        var window = new Window { Width = 1280, Height = 1160, RequestedThemeVariant = ThemeVariant.Dark };
        window.Content = new SettingsPage(shell);
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            var settingsTabs = window.GetVisualDescendants().OfType<TabControl>().Single();
            TestAssert.Equal(Dock.Left, settingsTabs.TabStripPlacement);
            foreach (var name in new[] { "Appearance", "Desktop behavior", "Software updates", "Service connection" })
            {
                OpenTab(name, settingsTabs);
                TestAssert.True(window.GetVisualDescendants().OfType<RTXLens.App.Controls.SectionPanel>()
                    .Any(section => Equals(section.Header, name)), $"Missing {name} settings.");
            }

            OpenTab("Appearance", settingsTabs);
            var scale = window.GetVisualDescendants().OfType<NumericUpDown>()
                .Single(input => AutomationProperties.GetName(input) == "Text scale");
            var primary = window.GetVisualDescendants().OfType<ColorPicker>()
                .Single(input => AutomationProperties.GetName(input) == "Primary accent");
            var initialColor = appearance.Primary;
            var paletteTheme = window.GetVisualDescendants().OfType<ComboBox>()
                .Single(input => AutomationProperties.GetName(input) == "Theme");
            var colorMode = window.GetVisualDescendants().OfType<ComboBox>()
                .Single(input => AutomationProperties.GetName(input) == "Color mode");
            TestAssert.Equal(7, paletteTheme.ItemCount);
            TestAssert.False(window.GetVisualDescendants().OfType<ToggleSwitch>()
                .Any(input => AutomationProperties.GetName(input) == "Reduce visual effects"), "The header glow setting should be gone.");

            var ultraviolet = ThemePreset.All.Single(preset => preset.Id == "ultraviolet");
            paletteTheme.SelectedItem = ultraviolet.Name;
            TestAssert.Equal(ultraviolet.Dark.Primary, primary.Color);
            TestAssert.Equal(initialColor, appearance.Primary);
            Click("Apply");
            PumpUntil(() => appearance.SelectedPalette.Preset == ultraviolet);
            var savedTheme = appearanceStore.LoadAsync();
            Wait(savedTheme);
            TestAssert.Equal(ultraviolet.Id, savedTheme.Result!.PresetId);
            AvaloniaControlsTests.Capture(window, screenshots, "settings-ultraviolet.png");

            colorMode.SelectedItem = "Light";
            TestAssert.Equal(ultraviolet.Light.Primary, primary.Color);
            TestAssert.Equal(ultraviolet.Dark.Primary, appearance.Primary);
            Click("Apply");
            PumpUntil(() => appearance.Primary == ultraviolet.Light.Primary);
            colorMode.SelectedItem = "Dark";

            var newColor = Colors.Orange;
            scale.SetCurrentValue(NumericUpDown.ValueProperty, 1.2m);
            primary.Color = newColor;
            TestAssert.Equal("Custom", (string)paletteTheme.SelectedItem!);

            OpenTab("Desktop behavior", settingsTabs);
            SetToggle("Close to system tray", true);
            SetToggle("Windows warning notifications on this PC", false);

            OpenTab("Service connection", settingsTabs);
            var address = window.GetVisualDescendants().OfType<TextBox>()
                .Single(input => AutomationProperties.GetName(input) == "Service address");
            var originalAddress = shell.Preferences.ServiceAddress;
            address.Text = "invalid";
            Click("Apply");
            TestAssert.True(window.GetVisualDescendants().OfType<ContentControl>().Any(alert =>
                alert.Classes.Contains("error") && alert is { IsEffectivelyVisible: true, Content: "Enter a valid HTTP or HTTPS service address." }),
                "An invalid service address should block Apply and show an error.");
            TestAssert.Equal(originalAddress, shell.Preferences.ServiceAddress);
            TestAssert.Equal(ultraviolet.Light.Primary, appearance.Primary);
            TestAssert.Equal(1d, store.Load().TextScale);
            TestAssert.False(shell.Preferences.CloseToTray, "Desktop behavior should remain unchanged before Apply.");

            address.Text = "http://localhost:5000";
            var previousApi = shell.Api;
            Click("Apply");
            PumpUntil(() => appearance.Primary == newColor && shell.Preferences is { TextScale: 1.2, CloseToTray: true, WarningNotifications: false });
            TestAssert.Equal("http://localhost:5000/", store.Load().ServiceAddress);
            TestAssert.False(ReferenceEquals(previousApi, shell.Api), "Applying a new service address should reconnect.");
            TestAssert.Equal(1.2, store.Load().TextScale);
            TestAssert.Equal(14 * 1.2, (double)Application.Current!.Resources["DesktopFontSize"]!);
            TestAssert.True(store.Load().CloseToTray && !store.Load().WarningNotifications, "Desktop behavior should persist after Apply.");

            OpenTab("Appearance", settingsTabs);
            scale.SetCurrentValue(NumericUpDown.ValueProperty, 1m);
            Click("Reset colors");
            TestAssert.Equal(newColor, appearance.Primary);
            AvaloniaControlsTests.Capture(window, screenshots, "settings-parity.png");

            OpenTab("Desktop behavior", settingsTabs);
            SetToggle("Close to system tray", false);
            SetToggle("Windows warning notifications on this PC", true);
            Click("Apply");
            PumpUntil(() => appearance.Primary == initialColor && shell.Preferences is { TextScale: 1, CloseToTray: false, WarningNotifications: true });

            var nvidia = shell.Configuration.Plugins.Single(plugin => plugin.IsNvidia);
            var asus = shell.Configuration.Plugins.Single(plugin => plugin.IsAsus);
            var msi = shell.Configuration.Plugins.Single(plugin => plugin.IsMsi);
            Wait(Task.WhenAll(shell.RefreshDeviceListAsync(nvidia, true), shell.RefreshDeviceListAsync(asus, true),
                shell.RefreshDeviceListAsync(msi, true)));
            TestAssert.Equal(3, nvidia.Nvidia.Devices.Count);
            TestAssert.Equal(2, asus.Asus.Devices.Count);
            TestAssert.Equal(2, msi.Msi.Devices.Count);
            nvidia.Nvidia.SelectedDevice = nvidia.Nvidia.Devices.Last();
            fixture.ExtraGpu = true;
            Wait(shell.RefreshDeviceListAsync(nvidia, true));
            TestAssert.Equal("gpu-2", nvidia.Nvidia.SelectedDevice.Id);
            TestAssert.Equal(4, nvidia.Nvidia.Devices.Count);
            TestAssert.True(fixture.CachedDiscovery, "Background discovery should use the backend cache.");

            window.Height = 840;
            window.Content = new ConfigurationPage(shell);
            Dispatcher.UIThread.RunJobs();
            var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
            OpenTab("Warnings");
            var profilePanel = window.GetVisualDescendants().OfType<RTXLens.App.Controls.SectionPanel>()
                .Single(panel => Equals(panel.Header, "Device warning profiles"));
            var profileActions = (WrapPanel)profilePanel.HeaderActions!;
            TestAssert.True(profileActions.Children.OfType<ComboBox>()
                .Any(input => AutomationProperties.GetName(input) == "Warning profile"), "The profile selector belongs in the header.");
            var enabled = profileActions.Children.OfType<ToggleSwitch>().Single();
            TestAssert.True(profileActions.Children[0] == enabled && enabled.Margin.Right >= 12 &&
                AutomationProperties.GetName(profileActions.Children[^1]) == "How warning profiles work",
                "Enabled should sit on the left with room before the profile picker.");
            var warningTabs = window.GetVisualDescendants().OfType<TabControl>().Single(tab => tab.TabStripPlacement == Dock.Left);
            TestAssert.Equal("Device warning profiles", (string)((TabItem)warningTabs.SelectedItem!).Header!);
            TestAssert.True(!enabled.IsEnabled, "The checkbox should be disabled until a profile is selected.");
            var profileSections = window.GetVisualDescendants().OfType<RTXLens.App.Controls.SectionPanel>().ToArray();
            TestAssert.True(profileSections.Any(panel => Equals(panel.Header, "Warning thresholds"))
                && profileSections.Any(panel => Equals(panel.Header, "Notifications and shutdown")),
                "Profile thresholds and notification settings should have their own panels.");
            Click("How warning profiles work");
            var warningGuide = window.OwnedWindows.Single();
            TestAssert.True(warningGuide.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("RTX 5090 preset") == true),
                "The usage guide should show the detected preset.");
            TestAssert.Equal(4, warningGuide.GetVisualDescendants().OfType<TextBlock>().Count(text => text.Classes.Contains("guide-step")));
            AvaloniaControlsTests.Capture(warningGuide, screenshots, "warning-profiles-guide.png");
            Click("Close", warningGuide);
            NameProfile("Add warning profile", "PSU protection");
            TestAssert.True(enabled is { IsEnabled: true, IsChecked: true }, "The header checkbox should reflect the active draft.");
            var saveProfile = window.GetVisualDescendants().OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == "Save profile configuration");
            TestAssert.True(saveProfile.Classes.Contains("unsaved-profile"), "A new profile should show an unsaved cue.");
            var useDefaults = window.GetVisualDescendants().OfType<ToggleSwitch>()
                .Single(input => AutomationProperties.GetName(input) == "Use detected GPU defaults");
            var overcurrent = window.GetVisualDescendants().OfType<NumericUpDown>()
                .Single(input => AutomationProperties.GetName(input) == "Per-pin overcurrent (A)");
            TestAssert.True(!overcurrent.IsEnabled, "Detected thresholds should stay read-only.");
            useDefaults.IsChecked = false;
            TestAssert.True(overcurrent.IsEnabled, "Turning off detected defaults should enable custom thresholds.");
            useDefaults.IsChecked = true;
            var source = window.GetVisualDescendants().OfType<ComboBox>()
                .Single(input => input.Items.Contains("MSI PSU") && input.Items.Contains("WireView Pro II"));
            source.SelectedItem = "MSI PSU";
            var connector = window.GetVisualDescendants().OfType<ComboBox>().Single(input => input.Items.Contains("Connector 2"));
            connector.SelectedItem = "Connector 2";
            Dispatcher.UIThread.RunJobs();
            var device = window.GetVisualDescendants().OfType<ComboBox>().Single(input => AutomationProperties.GetName(input) == "Device");
            TestAssert.True(connector.TranslatePoint(default, window)!.Value.X > device.TranslatePoint(default, window)!.Value.X + device.Bounds.Width,
                "The connector selector should have its own column.");
            Click("Save profile configuration");
            PumpUntil(() => fixture.WarningProfiles.Any(profile => profile.Name == "PSU protection"));
            TestAssert.Equal("msi-psu", fixture.WarningProfiles.Single().PluginId);
            TestAssert.Equal("2", fixture.WarningProfiles.Single().ConnectorId);
            PumpUntil(() => window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "Saved PSU protection."));
            TestAssert.True(!saveProfile.IsEnabled && !saveProfile.Classes.Contains("unsaved-profile"), "Saving should stop the cue.");
            enabled.IsChecked = false;
            TestAssert.True(saveProfile.IsEnabled && saveProfile.Classes.Contains("unsaved-profile"), "Changing Enabled should start the cue.");
            enabled.IsChecked = true;
            TestAssert.True(!saveProfile.IsEnabled && !saveProfile.Classes.Contains("unsaved-profile"), "Reverting the edit should stop the cue.");
            AvaloniaControlsTests.Capture(window, screenshots, "warning-profiles.png");

            window.Width = 720;
            Dispatcher.UIThread.RunJobs();
            TestAssert.True(profilePanel.HeaderActions is null && profileActions.IsEffectivelyVisible,
                "Profile controls should wrap below the header at narrow widths.");
            window.Width = 1280;
            Dispatcher.UIThread.RunJobs();

            warningTabs.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            TestAssert.True(window.GetVisualDescendants().OfType<RTXLens.App.Controls.SectionPanel>()
                .Any(panel => Equals(panel.Header, "Warning notifications")), "The notification panel should open in the side tabs.");
            TestAssert.True(!window.GetVisualDescendants().OfType<Button>()
                .Any(button => AutomationProperties.GetName(button) == "Save profile configuration" && button.IsEffectivelyVisible),
                "Shared notification settings use the global Apply button.");
            TestAssert.True(!window.GetVisualDescendants().OfType<TextBlock>()
                .Any(text => text.Text == "Changes are applied live to the service and all consumers."), "Remove the default configuration banner.");

            ChangeSharedNotificationSettings();
            Click("Apply");
            PumpUntil(ApplyAlertVisible);
            shell.Configuration.Receive(fixture.LastConfiguration!);
            Dispatcher.UIThread.RunJobs();
            TestAssert.True(ApplyAlertVisible(), "A background configuration snapshot should preserve the apply confirmation.");
            TestAssert.Equal("Notifications", (string)((TabItem)warningTabs.SelectedItem!).Header!);
            var confirmation = window.GetVisualDescendants().OfType<ContentControl>().Single(IsApplyAlert);
            var tabStrip = tabs.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "TabStripFrame");
            TestAssert.Equal(tabStrip.TranslatePoint(new Point(tabStrip.Bounds.Width, 0), window)!.Value.X,
                confirmation.TranslatePoint(new Point(confirmation.Bounds.Width, 0), window)!.Value.X);
            AvaloniaControlsTests.Capture(window, screenshots, "warning-notifications.png");
            Click("Dismiss alert");
            PumpUntil(() => !ApplyAlertVisible());

            ChangeSharedNotificationSettings();
            Click("Apply");
            PumpUntil(ApplyAlertVisible);
            using (var expiry = new CancellationTokenSource(TimeSpan.FromSeconds(6)))
            {
                Dispatcher.UIThread.MainLoop(expiry.Token);
            }

            TestAssert.True(!ApplyAlertVisible(), "The apply confirmation should expire automatically.");

            OpenTab("Source selections");
            TestAssert.True(!window.GetVisualDescendants().OfType<RTXLens.App.Controls.EditableList>().Any(),
                "Source selections should open without priority lists when the service has no connector profiles.");
            Click("How source selections work");
            var sourcesGuide = window.OwnedWindows.Single();
            TestAssert.True(sourcesGuide.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("WireView Pro II first") == true),
                "The source guide should explain how to use different sources for each metric.");
            Click("Close", sourcesGuide);

            OpenTab("GPU chart profiles");
            var chartProfilePanel = window.GetVisualDescendants().OfType<RTXLens.App.Controls.SectionPanel>()
                .Single(panel => Equals(panel.Header, "GPU chart profiles"));
            var chartProfileActions = (StackPanel)chartProfilePanel.HeaderActions!;
            TestAssert.True(chartProfileActions.Children.OfType<ComboBox>()
                .Any(input => AutomationProperties.GetName(input) == "GPU chart profile"), "The chart profile selector belongs in the header.");
            TestAssert.True(chartProfileActions.Children.OfType<Button>().Select(AutomationProperties.GetName)
                .SequenceEqual(["Add GPU chart profile", "Delete GPU chart profile", "Use selected profile"]),
                "Profile actions belong in the header.");
            TestAssert.True(!window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.StartsWith("Active chart profile:") == true),
                "The selector should show the profile without a separate active-profile notice.");
            Click("Save configuration");
            PumpUntil(() => fixture.ChartRequest is not null);
            TestAssert.Equal("auto", fixture.ChartRequest!.ProfileId);
            TestAssert.Equal(10d, fixture.ChartRequest.Settings!.CurrentCeiling);
            TestAssert.True(!fixture.ChartRequest.Save, "Applying for the session should not create a persistent profile.");
            PumpUntil(() => window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "Applied for this service session."));
            NameProfile("Add GPU chart profile", "My chart");
            PumpUntil(() => fixture.CustomChart is not null);
            var customId = fixture.CustomChart!.Id;
            PumpUntil(() => window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "Created My chart."));
            var current = window.GetVisualDescendants().OfType<NumericUpDown>()
                .Single(input => AutomationProperties.GetName(input) == "Current chart maximum per pin (A)");
            current.Value = 8;
            Click("Save configuration");
            PumpUntil(() => fixture.CustomChart?.CurrentCeiling == 8);
            TestAssert.Equal(customId, fixture.CustomChart!.Id);
            PumpUntil(() => window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "Saved My chart."));
            AvaloniaControlsTests.Capture(window, screenshots, "gpu-chart-profiles.png");

            fixture.LegacyChartEndpoints = true;
            current.Value = 9;
            Click("Save configuration");
            PumpUntil(() => fixture.ChartRequest is { Save: false, Settings.CurrentCeiling: 9 });
            PumpUntil(() => window.GetVisualDescendants().OfType<ContentControl>().Any(alert => alert.Classes.Contains("warning")
                && alert.Content is string message && message.StartsWith("Applied for this session.", StringComparison.Ordinal)));
            TestAssert.Equal(8d, fixture.CustomChart!.CurrentCeiling);
            fixture.LegacyChartEndpoints = false;

            Click("Delete GPU chart profile");
            var deletePrompt = window.OwnedWindows.Single();
            var cancel = deletePrompt.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Cancel");
            var cancelIcon = cancel.GetVisualDescendants().OfType<Image>().Single();
            TestAssert.Equal(DesktopControls.Icon("cancel-action"), cancelIcon.Source!);
            Click("Delete", deletePrompt);
            PumpUntil(() => fixture.CustomChart is null);
            PumpUntil(() => window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "Deleted My chart."));

            bool ApplyAlertVisible() => window.GetVisualDescendants().OfType<ContentControl>().Any(IsApplyAlert);

            static bool IsApplyAlert(ContentControl alert) => alert.Classes.Contains("success") && alert is { IsEffectivelyVisible: true, Content: "Your configuration has been applied." };

            void ChangeSharedNotificationSettings()
            {
                var notifications = window.GetVisualDescendants().OfType<ToggleSwitch>()
                    .Single(input => AutomationProperties.GetName(input) == "Enable warning notifications");
                notifications.SetCurrentValue(ToggleSwitch.IsCheckedProperty, notifications.IsChecked != true);
                Dispatcher.UIThread.RunJobs();
            }

            void NameProfile(string action, string name)
            {
                Click(action);
                var prompt = window.OwnedWindows.Single();
                var input = prompt.GetVisualDescendants().OfType<TextBox>().Single();
                AvaloniaControlsTests.Capture(prompt, screenshots, "profile-name.png");
                input.Text = name;
                Dispatcher.UIThread.RunJobs();
                Click("Create profile", prompt);
            }

            void SetToggle(string title, bool enabled)
            {
                var toggle = window.GetVisualDescendants().OfType<ToggleSwitch>()
                    .Single(input => AutomationProperties.GetName(input) == title);
                toggle.IsChecked = enabled;
            }

            void OpenTab(string title, TabControl? tabControl = null)
            {
                tabControl ??= window.GetVisualDescendants().OfType<TabControl>().First();
                tabControl.SelectedItem = tabControl.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, title));
                Dispatcher.UIThread.RunJobs();
            }

            void Click(string title, Window? target = null)
            {
                var button = (target ?? window).GetVisualDescendants().OfType<Button>()
                    .Single(item => AutomationProperties.GetName(item) == title);
                TestAssert.True(button.IsEffectivelyEnabled, $"{title} should be enabled.");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static void Wait(Task task)
    {
        PumpUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void PumpUntil(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!ready() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }

        TestAssert.True(ready(), "The UI operation did not complete.");
    }

    private sealed class SettingsService : HttpMessageHandler
    {
        public bool ExtraGpu { get; set; }
        public bool CachedDiscovery { get; private set; }
        public List<DeviceWarningProfile> WarningProfiles { get; } = [];
        public SettingsProfileRequest? ChartRequest { get; private set; }
        public SettingsProfile? CustomChart { get; private set; }
        public bool LegacyChartEndpoints { get; set; }
        public ConfigurationSnapshot? LastConfiguration { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            switch (uri.AbsolutePath)
            {
                case "/plugins/generic-nvidia/devices":
                    CachedDiscovery = uri.Query == "?refresh=false";
                    NvidiaDeviceInfo[] gpus = [new("gpu-1", "GPU One", "legacy-1"), new("gpu-2", "GPU Two", "legacy-2")];
                    return Respond(ExtraGpu ? [..gpus, new NvidiaDeviceInfo("gpu-3", "GPU Three", "legacy-3")] : gpus);
                case "/plugins/asus-astral/devices":
                    return Respond(new[] { new AsusDeviceInfo("gpu-1", "ROG Astral") });
                case "/plugins/msi-psu/devices":
                    return Respond(new[] { new MsiDeviceInfo("psu-1", "MSI MPG Ai1300TS") });
                case "/configuration" when request.Method == HttpMethod.Put:
                    var configuration = (await request.Content!.ReadFromJsonAsync(ClientJsonContext.Default.ServiceConfiguration,
                        cancellationToken))!;
                    var revision = (LastConfiguration?.Revision ?? 1) + 1;
                    LastConfiguration = new ConfigurationSnapshot(revision, configuration, Version: $"test-{revision}");
                    return Respond(LastConfiguration);
                case "/warning-profiles":
                    if (request.Method == HttpMethod.Put)
                    {
                        var profile = (await request.Content!.ReadFromJsonAsync(ClientJsonContext.Default.DeviceWarningProfile, cancellationToken))!;
                        WarningProfiles.RemoveAll(saved => saved.Id == profile.Id);
                        WarningProfiles.Add(profile);
                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                    }

                    var defaults = ConnectorWarningLimits.From(SettingsProfile.Rtx5090);
                    return Respond(new DeviceWarningProfileCatalog([.. WarningProfiles], [], defaults, defaults, "5090", new WarningPolicy()));
                case "/settings-profiles":
                    if (request.Method == HttpMethod.Put)
                    {
                        ChartRequest = await request.Content!.ReadFromJsonAsync(ClientJsonContext.Default.SettingsProfileRequest, cancellationToken);
                        if (ChartRequest is { Save: true, Settings: { } settings })
                        {
                            CustomChart = settings with { Id = Guid.NewGuid().ToString("N") };
                        }
                    }

                    return RespondChartCatalog();
                default:
                    if (!LegacyChartEndpoints && CustomChart is { } custom && uri.AbsolutePath == $"/settings-profiles/{custom.Id}")
                    {
                        CustomChart = request.Method == HttpMethod.Delete ? null :
                            await request.Content!.ReadFromJsonAsync(ClientJsonContext.Default.SettingsProfile, cancellationToken);
                        return RespondChartCatalog();
                    }

                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private HttpResponseMessage RespondChartCatalog()
        {
            SettingsProfile[] profiles = [SettingsProfile.Rtx5090, SettingsProfile.Rtx5080];
            if (CustomChart is { } custom)
            {
                profiles = [.. profiles, custom];
            }

            return Respond(new SettingsProfileCatalog(CustomChart?.Id ?? "auto", CustomChart ?? SettingsProfile.Rtx5090,
                profiles, SettingsProfile.Rtx5090));
        }

        private static HttpResponseMessage Respond<T>(T data) => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(data, options: new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
    }
}
