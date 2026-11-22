using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using QRCoder;
using RTXLens.App.Views.Shell;
using RTXLens.Contracts.Mobile;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Configuration;

internal sealed class MobilePairingWindow : LensWindow
{
    private static readonly Geometry ApproveIcon = Geometry.Parse("M 4,12 L 6,10 L 9,13 L 18,4 L 20,6 L 9,17 Z");
    private static readonly Geometry RejectIcon =
        Geometry.Parse("M 5,7 L 7,5 L 12,10 L 17,5 L 19,7 L 14,12 L 19,17 L 17,19 L 12,14 L 7,19 L 5,17 L 10,12 Z");

    private readonly Image _qr = new() { Width = 216, Height = 216, Stretch = Stretch.Uniform };
    private readonly Border _qrFrame;
    private readonly TextBlock _codeStatus = Caption();
    private readonly TextBlock _request = DialogText("Waiting for a device to scan the code.");
    private readonly ContentControl _feedback = Alert();
    private readonly Button _approve;
    private readonly Button _reject;
    private readonly StackPanel _decisions;
    private readonly DateTimeOffset? _expiresAt;
    private MobilePairingApproval? _pending;
    private bool _finished;
    private bool _busy;
    private bool _canResolve;

    public MobilePairingWindow(MobilePairingCode? code, Func<MobileAccessOperation, Guid, Task> resolve)
    {
        Title = "Pair a device";
        Width = 690;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        AutomationProperties.SetName(_qr, "Mobile pairing QR code");
        RenderOptions.SetBitmapInterpolationMode(_qr, BitmapInterpolationMode.None);

        _qrFrame = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            HorizontalAlignment = HorizontalAlignment.Center,
            IsVisible = code is not null,
            Child = _qr
        };
        _codeStatus.HorizontalAlignment = HorizontalAlignment.Center;
        _codeStatus.TextAlignment = TextAlignment.Center;
        if (code is not null)
        {
            _expiresAt = code.ExpiresAt;
            _qr.Source = CreateQrCode(code);
            _codeStatus.Text = $"Expires at {code.ExpiresAt.ToLocalTime():t}";
        }
        else
        {
            _codeStatus.Text = "A device has already scanned the pairing code.";
        }

        var codePanel = Frame(new StackPanel
        {
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _qrFrame, _codeStatus }
        });

        var guide = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = "How to pair", Classes = { "heading" } },
                GuideStep(1, "Connect to your local network", "Connect your device to the same network as this PC."),
                GuideStep(2, "Scan the QR code", "Open Settings in RTX Lens on your device, then choose Scan pairing code."),
                GuideStep(3, "Approve the request", "Check the device name below and approve it to finish pairing.")
            }
        };
        var columns = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("248,*"),
            ColumnSpacing = 20,
            Children = { codePanel, guide }
        };
        Grid.SetColumn(guide, 1);

        _reject = CreateDecisionButton("Reject", RejectIcon, "error", async () =>
        {
            if (_pending is { } pending)
            {
                await resolve(MobileAccessOperation.Reject, pending.Id);
            }
        });
        _approve = CreateDecisionButton("Approve", ApproveIcon, "success", async () =>
        {
            if (_pending is { } pending)
            {
                await resolve(MobileAccessOperation.Approve, pending.Id);
            }
        });
        _decisions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _reject, _approve } };
        var requestPanel = Section("Pairing request", new StackPanel { Spacing = 8, Children = { _request, _feedback } });
        requestPanel.HeaderActions = _decisions;

        var body = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(20, 16, 20, 16),
            Children =
            {
                columns,
                Frame(Caption("Each code pairs one device and expires after five minutes. Approve only a device you recognize.")),
                requestPanel
            }
        };

        var close = ActionButton("Close", "cancel-action", Close);
        close.IsCancel = true;
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Margin = new Thickness(20, 0, 20, 16);

        var layout = new DockPanel { Children = { close, Scroll(body) } };
        DockPanel.SetDock(close, Dock.Bottom);
        Body = layout;

        Closed += (_, _) => ClearQr();
        UpdateButtons();
    }

    public void CheckExpiry(DateTimeOffset now)
    {
        if (!_finished && (_expiresAt ?? _pending?.ExpiresAt) is { } expiresAt && expiresAt <= now)
        {
            Finish("The pairing code expired. Close this window and create a new code.", "warning");
        }
    }

    public void UpdateApproval(MobilePairingApproval? approval)
    {
        if (_finished)
        {
            return;
        }

        if (approval is not null && _expiresAt is { } expiresAt && approval.ExpiresAt != expiresAt)
        {
            Finish("A newer pairing code replaced this one. Close this window to review the latest request.", "warning");
            return;
        }

        if (_pending is not null && approval is null)
        {
            Finish("This pairing request is no longer available.", "warning");
            return;
        }

        _pending = approval;
        _request.Text = approval is not null ? $"Pairing request from {approval.DeviceName}." : "Waiting for a device to scan the code.";
        UpdateButtons();
    }

    public void SetRequestAvailability(bool busy, bool canResolve)
    {
        _busy = busy;
        _canResolve = canResolve;
        UpdateButtons();
    }

    public void ReportError(string? message)
    {
        if (!_finished)
        {
            SetAlert(_feedback, message, "warning");
        }
    }

    public void ShowDecision(MobileAccessOperation operation)
    {
        if (operation == MobileAccessOperation.Approve)
        {
            Finish("Device approved. Return to RTX Lens on your device to finish pairing.", "success");
        }
        else
        {
            Finish("Pairing request rejected. Create a new code to try again.", "warning");
        }
    }

    private void Finish(string message, string tone)
    {
        _finished = true;
        _pending = null;
        _request.IsVisible = false;
        ClearQr();
        _codeStatus.Text = "Create a new code to pair another device.";
        SetAlert(_feedback, message, tone);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _decisions.IsVisible = _pending is not null;
        _approve.IsEnabled = _reject.IsEnabled = !_busy && _canResolve && _pending is not null;
    }

    private void ClearQr()
    {
        var bitmap = _qr.Source as Bitmap;
        _qr.Source = null;
        _qrFrame.IsVisible = false;
        bitmap?.Dispose();
    }

    private static Bitmap CreateQrCode(MobilePairingCode code)
    {
        var payload = JsonSerializer.Serialize(code, MobileAccessProtocol.Json);
        using var data = QRCodeGenerator.GenerateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using var renderer = new PngByteQRCode(data);
        using var stream = new MemoryStream(renderer.GetGraphic(4));
        return new Bitmap(stream);
    }

    private static Border Frame(Control content) => new()
    {
        Classes = { "panel-frame" },
        Padding = new Thickness(12),
        Child = content
    };

    private static Control GuideStep(int number, string title, string detail)
    {
        var text = DialogText(title);
        text.FontWeight = FontWeight.SemiBold;
        var step = new TextBlock
        {
            Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Classes = { "guide-step" },
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var badge = Frame(step);
        badge.Width = badge.Height = 28;
        badge.Padding = new Thickness(0);
        badge.VerticalAlignment = VerticalAlignment.Top;
        badge[!Border.BackgroundProperty] = new DynamicResourceExtension("HudAccentSurfaceBrush");

        var description = new StackPanel { Spacing = 4, Children = { text, Caption(detail) } };
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 10,
            Children = { badge, description }
        };
        Grid.SetColumn(description, 1);
        return row;
    }

    private static Button CreateDecisionButton(string label, Geometry geometry, string tone, Func<Task> action)
    {
        var icon = new PathIcon { Data = geometry, Width = 15, Height = 15, [!TemplatedControl.ForegroundProperty] = new DynamicResourceExtension(tone == "success" ? "SuccessBrush" : "ErrorBrush") };
        var button = ActionButton(label, icon, action);
        button.Classes.Add($"{tone}-action");
        return button;
    }
}
