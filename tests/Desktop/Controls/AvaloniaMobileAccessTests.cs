using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RTXLens.App.Controls;
using RTXLens.App.Views.Configuration;
using RTXLens.Contracts.Mobile;
using RTXLens.Tests.Infrastructure;

namespace RTXLens.UI.Tests.Controls;

internal static class AvaloniaMobileAccessTests
{
    public static void PublicIpv4ApplyTracksEdits()
    {
        const string localAddress = "192.168.1.20";
        const string publicAddress = "203.0.113.10";
        var port = MobileAccessProtocol.DefaultPort;
        var status = new MobileAccessStatus(true, true, port, null, null, true,
            new MobilePortForwarding(localAddress, port), null, [new MobileDevice(Guid.NewGuid(), "Test phone", null)],
            null, localAddress, publicAddress, 30);
        var view = new MobileAccessView((_, _) => Task.FromResult(new MobileAccessResponse(true, Status: status)),
            CancellationToken.None, [localAddress]);
        var window = new Window { Content = new StackPanel { Children = { view.EditActions, view } } };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var apply = window.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Apply");
            var address = view.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Public IPv4 address");
            TestAssert.Equal(publicAddress, address.Text);
            TestAssert.False(apply.IsEnabled, "Opening a configured Public IPv4 connection must leave Apply disabled.");
            address.Text = "203.0.113.11";
            TestAssert.True(apply.IsEnabled, "Editing the saved public address should enable Apply.");
            address.Text = publicAddress;
            TestAssert.False(apply.IsEnabled, "Restoring the public address should disable Apply.");
        }
        finally
        {
            window.Close();
        }
    }

    public static void SavesSettingsAndGeneratesPairingCode(string? screenshots)
    {
        MobileAccessRequest? applied = null;
        MobileAccessRequest? savedWarningActions = null;
        var status = new MobileAccessStatus(true, true, MobileAccessProtocol.DefaultPort, "example.ddns.net", null,
            false, null, null, [], null, "192.168.1.20");
        MobileAccessRequest? decision = null;
        var requestId = Guid.NewGuid();
        var code = new MobilePairingInvite("test-invite", new string('A', 64), DateTimeOffset.UtcNow.AddMinutes(5));

        var view = new MobileAccessView(SendAsync, CancellationToken.None, ["192.168.1.20"]);
        var window = new Window
        {
            Width = 1100,
            Height = 720,
            Content = new Border { Padding = new Thickness(18), Child = new StackPanel { Children = { view.EditActions, view } } }
        };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();

            var pairing = view.GetVisualDescendants().OfType<SectionPanel>().Single(panel => Equals(panel.Header, "Pairing & devices"));
            TestAssert.Equal(1, Grid.GetColumn(pairing));
            var header = (StackPanel)pairing.HeaderActions!;
            var headerButtons = header.Children.OfType<Button>().Select(AutomationProperties.GetName).ToArray();
            TestAssert.True(headerButtons.SequenceEqual(["Create pairing code", "Pairing and remote access information"]),
                "The information button should follow the create button in the section header.");
            TestAssert.True(pairing.GetVisualDescendants().OfType<ContentControl>()
                .Any(alert => Equals(alert.Content, "No paired devices.") && alert.Classes.Contains("warning")),
                "The empty device state should use a warning panel.");
            TestAssert.True(view.GetVisualDescendants().OfType<ToggleSwitch>()
                .Any(toggle => AutomationProperties.GetName(toggle) == "Forward port through UPnP"),
                "The forwarding toggle should identify the protocol used by the service.");
            AvaloniaControlsTests.Capture(window, screenshots, "mobile-access.png");

            Click(view, "Pairing and remote access information");
            var information = window.OwnedWindows.Single();
            TestAssert.Equal("Pairing and remote access", information.Title);
            TestAssert.True(information.GetVisualDescendants().OfType<TextBlock>()
                .Any(text => text.Text?.Contains("Test remote access", StringComparison.Ordinal) == true),
                "Remote access verification instructions belong in the information dialog.");
            information.Close();
            Dispatcher.UIThread.RunJobs();

            window.Width = 760;
            Dispatcher.UIThread.RunJobs();
            TestAssert.Equal(0, Grid.GetColumn(pairing));
            TestAssert.Equal(1, Grid.GetRow(pairing));

            var password = view.GetVisualDescendants().OfType<TextBox>()
                .Single(control => AutomationProperties.GetName(control) == "DDNS key password");
            TestAssert.True(password.PasswordChar != 0 && string.IsNullOrEmpty(password.Text),
                "The DDNS password should be masked and the saved key should not be returned to the UI.");

            var port = view.GetVisualDescendants().OfType<NumericUpDown>()
                .Single(control => AutomationProperties.GetName(control) == "TCP port");
            var apply = FindButton(window, "Apply");
            TestAssert.False(apply.IsEnabled, "Freshly loaded settings must be clean.");
            port.Value = status.Port + 1;
            TestAssert.True(apply.IsEnabled, "Changing the port should enable Apply.");
            port.Value = status.Port;
            TestAssert.False(apply.IsEnabled, "Restoring the saved port should disable Apply.");

            password.Text = "unsaved";
            TestAssert.True(apply.IsEnabled, "A replacement DDNS password is an edit.");
            password.Text = string.Empty;
            TestAssert.False(apply.IsEnabled, "Clearing the replacement keeps the saved credentials.");
            password.Text = "unsaved";
            status = status with { Port = status.Port + 2 };
            Click(window, "Reload");
            TestAssert.True(applied is null && port.Value == status.Port && string.IsNullOrEmpty(password.Text),
                "Reload must fetch the updated service settings and clear unsaved credentials without configuring the service.");

            TestAssert.False(apply.IsEnabled, "Reload must leave the form clean.");
            port.Value = status.Port + 1;
            Click(window, "Apply");
            TestAssert.True(applied?.Settings?.Port == port.Value && !apply.IsEnabled,
                "A successful save must adopt the new port and disable Apply.");
            TestAssert.True(applied is { KeepNoIpCredentials: true, Settings: { NoIp: null, LocalAddress: "192.168.1.20" } },
                "Saving without entering a replacement key should preserve the saved credentials on the service.");

            Click(view, "Create pairing code");
            var dialog = window.OwnedWindows.Single();
            TestAssert.True(dialog.GetVisualDescendants().OfType<Image>()
                .Any(image => AutomationProperties.GetName(image) == "Mobile pairing QR code"
                    && image is { IsEffectivelyVisible: true, Source: not null }),
                "The invite should be rendered as a QR code in the dialog.");
            TestAssert.True(dialog.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "Pairing request from Test iPhone."),
                "The incoming request should appear in the QR dialog.");
            TestAssert.True(FindButton(dialog, "Approve").Classes.Contains("success-action"), "Approve should use the success style.");
            TestAssert.True(FindButton(dialog, "Reject").Classes.Contains("error-action"), "Reject should use the error style.");
            AvaloniaControlsTests.Capture(dialog, screenshots, "mobile-access-pairing.png");

            Click(dialog, "Approve");
            TestAssert.True(decision is { Operation: MobileAccessOperation.Approve } && decision.Id == requestId,
                "Approval should apply to the request shown in the pairing dialog.");
            TestAssert.True(dialog.GetVisualDescendants().OfType<ContentControl>()
                .Any(alert => alert.Classes.Contains("success") && alert.Content is string text
                    && text.Contains("Device approved", StringComparison.Ordinal)),
                "The pairing dialog should show the approval result.");
            Click(dialog, "Close");

            var revokes = pairing.GetVisualDescendants().OfType<Button>()
                .Where(button => AutomationProperties.GetName(button) == "Revoke").ToArray();
            TestAssert.Equal(2, revokes.Length);
            TestAssert.True(revokes.All(button => button.Parent is StackPanel panel && Grid.GetColumn(panel) == 1),
                "Both paired-device action rows should align to the right.");
            var notificationRow = (StackPanel)revokes[1].Parent!;
            TestAssert.True(notificationRow.Children.OfType<Button>().Select(AutomationProperties.GetName)
                    .SequenceEqual(["Send test", "Configure warning actions", "Revoke"]),
                "The phone with lock-screen notifications should render all three actions in one panel.");
            TestAssert.False(pairing.GetVisualDescendants().OfType<ContentControl>()
                .Any(alert => alert.Classes.Contains("warning") && alert.Content is string message && message.Contains("StackPanel")),
                "Rendering paired-device buttons must not show a logical-parent error.");

            Click(notificationRow, "Configure warning actions");
            var actionWindow = (MobileWarningActionsWindow)window.OwnedWindows.Single();
            var slider = actionWindow.GetVisualDescendants().OfType<Slider>()
                .Single(control => AutomationProperties.GetName(control) == "Remote GPU power limit (watts)");
            TestAssert.True(slider.IsEffectivelyVisible && !slider.IsEffectivelyEnabled,
                "Show the slider as disabled while remote power reduction is off.");
            TestAssert.True(slider.Minimum == 400 && slider.Maximum == 825.125 && slider.IsSnapToTickEnabled,
                "Use the warning profile slider’s bounds and tick behavior, including XOC ranges.");
            var lowPower = actionWindow.GetVisualDescendants().OfType<ToggleSwitch>()
                .Single(control => AutomationProperties.GetName(control) == "Allow remote GPU power reduction");
            lowPower.IsChecked = true;
            TestAssert.True(slider.IsEffectivelyEnabled, "Enabling remote power reduction unlocks the slider.");

            slider.Value = 500;
            var mismatch = actionWindow.GetVisualDescendants().OfType<ContentControl>().Single(control =>
                control.IsVisible && control.Content is string text && text.Contains("• Lower limit", StringComparison.Ordinal));
            TestAssert.True(mismatch.Content is string warnings && warnings.Contains("• Higher limit", StringComparison.Ordinal)
                && !warnings.Contains("• Matching limit", StringComparison.Ordinal) && !warnings.Contains("• Other GPU", StringComparison.Ordinal),
                "List higher and lower limits on the chosen GPU, skipping matching limits and other GPUs.");
            var shutdown = actionWindow.GetVisualDescendants().OfType<ToggleSwitch>()
                .Single(control => AutomationProperties.GetName(control) == "Allow remote shutdown");
            TestAssert.False(shutdown.IsChecked == true, "Enabling low power leaves shutdown off.");
            Click(actionWindow, "Save");
            TestAssert.True(savedWarningActions?.Id == status.Devices[1].Id
                && savedWarningActions?.WarningActions is { EnableShutdown: false, EnableLowPower: true, GpuId: "test-gpu", PowerMilliwatts: 500000 },
                "Save only the selected phone’s settings.");

            Click(view, "Create pairing code");
            dialog = window.OwnedWindows.Single();
            Click(dialog, "Close");
            Click(view, "Review pairing request");
            dialog = window.OwnedWindows.Single();
            Click(dialog, "Reject");
            TestAssert.True(decision is { Operation: MobileAccessOperation.Reject } && decision.Id == requestId,
                "The same dialog should let users reject a pending request after closing and reopening it.");
            Click(dialog, "Close");
        }
        finally
        {
            window.Close();
        }

        return;

        Task<MobileAccessResponse> SendAsync(MobileAccessRequest request, CancellationToken token)
        {
            switch (request.Operation)
            {
                case MobileAccessOperation.Configure:
                    applied = request;
                    status = status with { Port = request.Settings!.Port };
                    break;

                case MobileAccessOperation.GetWarningActions:
                    return Task.FromResult(new MobileAccessResponse(true, WarningActions: new(new(),
                        [new("test-gpu", "Test GPU", 400000, 825125)],
                        [
                            new("Lower limit", "test-gpu", 425000),
                            new("Higher limit", "test-gpu", 575000),
                            new("Matching limit", "test-gpu", 500000),
                            new("Other GPU", "another-gpu", 600000)
                        ])));

                case MobileAccessOperation.ConfigureWarningActions:
                    savedWarningActions = request;
                    return Task.FromResult(new MobileAccessResponse(true));

                case MobileAccessOperation.CreateInvite:
                    status = status with { PendingApproval = new MobilePairingApproval(requestId, "Test iPhone", code.ExpiresAt) };
                    return Task.FromResult(new MobileAccessResponse(true, Invite: code));

                case MobileAccessOperation.Approve:
                    decision = request;
                    status = status with
                    {
                        PendingApproval = null,
                        Devices = [
                            new MobileDevice(Guid.NewGuid(), "Test iPhone", null),
                            new MobileDevice(Guid.NewGuid(), "Test Android", null, LockScreenNotifications: true)
                        ]
                    };
                    return Task.FromResult(new MobileAccessResponse(true));

                case MobileAccessOperation.Reject:
                    decision = request;
                    status = status with { PendingApproval = null };
                    return Task.FromResult(new MobileAccessResponse(true));
            }

            return Task.FromResult(new MobileAccessResponse(true, Status: status));
        }

        static Button FindButton(Visual root, string label) => root.GetVisualDescendants().OfType<Button>()
            .Single(control => AutomationProperties.GetName(control) == label);

        static void Click(Visual root, string label)
        {
            var button = FindButton(root, label);
            TestAssert.True(button.IsEnabled, $"{label} should be available.");
            AvaloniaControlsTests.InvokeButton(button);
        }
    }
}
