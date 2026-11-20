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
    public static void SavesSettingsAndGeneratesPairingCode(string? screenshots)
    {
        MobileAccessRequest? applied = null;
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
            Content = new Border { Padding = new Thickness(18), Child = view }
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
            TestAssert.True(password.PasswordChar != default && string.IsNullOrEmpty(password.Text),
                "The DDNS password should be masked and the saved key should not be returned to the UI.");

            Click(view, "Apply mobile access");
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
            TestAssert.Equal(1, Grid.GetColumn(revokes[0]));
            TestAssert.Equal(1, Grid.GetColumn((Control)revokes[1].Parent!));

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
                    break;

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
