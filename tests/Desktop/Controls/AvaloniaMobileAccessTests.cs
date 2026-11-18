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
        var code = new MobilePairingInvite("test-invite", new string('A', 64), DateTimeOffset.UtcNow.AddMinutes(5));

        Task<MobileAccessResponse> SendAsync(MobileAccessRequest request, CancellationToken token)
        {
            if (request.Operation == MobileAccessOperation.Configure)
            {
                applied = request;
            }

            return Task.FromResult(request.Operation == MobileAccessOperation.CreateInvite
                ? new MobileAccessResponse(true, Invite: code)
                : new MobileAccessResponse(true, Status: status));
        }

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

            var pairing = view.GetVisualDescendants().OfType<SectionPanel>().Single(panel => Equals(panel.Header, "Pairing and phones"));
            TestAssert.Equal(1, Grid.GetColumn(pairing));
            TestAssert.True(pairing.HeaderActions is Button, "The pairing button should sit in the section header.");
            var image = view.GetVisualDescendants().OfType<Image>()
                .Single(control => AutomationProperties.GetName(control) == "Mobile pairing QR code");
            TestAssert.False(image.IsEffectivelyVisible, "The code should take no space before an invite exists.");
            AvaloniaControlsTests.Capture(window, screenshots, "mobile-access.png");

            window.Width = 760;
            Dispatcher.UIThread.RunJobs();
            TestAssert.Equal(0, Grid.GetColumn(pairing));
            TestAssert.Equal(1, Grid.GetRow(pairing));

            var password = view.GetVisualDescendants().OfType<TextBox>()
                .Single(control => AutomationProperties.GetName(control) == "DDNS key password");
            TestAssert.True(password.PasswordChar != default && string.IsNullOrEmpty(password.Text),
                "The DDNS password should be masked and the saved key should not be returned to the UI.");

            Click("Apply mobile access");
            TestAssert.True(applied is { KeepNoIpCredentials: true, Settings: { NoIp: null, LocalAddress: "192.168.1.20" } },
                "Saving without entering a replacement key should preserve the saved credentials on the service.");

            Click("Create pairing code");
            TestAssert.True(image is { IsEffectivelyVisible: true, Source: not null }, "The invite should be rendered as a QR code.");
            window.Width = 1100;
            Dispatcher.UIThread.RunJobs();
            AvaloniaControlsTests.Capture(window, screenshots, "mobile-access-pairing.png");
        }
        finally
        {
            window.Close();
        }

        void Click(string label)
        {
            var button = view.GetVisualDescendants().OfType<Button>()
                .Single(control => AutomationProperties.GetName(control) == label);

            TestAssert.True(button.IsEnabled, $"{label} should be available.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }
    }
}