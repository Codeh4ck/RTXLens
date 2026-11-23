using RTXLens.Presentation.Settings;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Security;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using RTXLens.App.ViewModels.Shell;
using RTXLens.Client.Mobile;
using RTXLens.Contracts.Mobile;
using RTXLens.Contracts.Monitoring;
using static RTXLens.App.Views.Configuration.ConfigurationPage;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Configuration;

internal sealed class MobileAccessView : UserControl
{
    private const string PublicIpMode = "Public IPv4";
    private const string NoIpMode = "No-IP hostname";

    private readonly Func<MobileAccessRequest, CancellationToken, Task<MobileAccessResponse>>? _send;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly ContentControl _notice = Alert();
    private readonly ToggleSwitch _enabled = Switch("Enable mobile access");
    private readonly ToggleSwitch _firewall = Switch("Add a Windows Firewall rule");
    private readonly ToggleSwitch _automaticForwarding = Switch("Forward port through UPnP");

    private readonly NumericUpDown _port = new()
    {
        Minimum = 1024, Maximum = 65535, Value = MobileAccessProtocol.DefaultPort, Increment = 1, FormatString = "0"
    };

    private readonly NumericUpDown _historyDays = new()
    {
        Minimum = 1, Maximum = 365, Value = MobileAccessProtocol.DefaultWarningHistoryDays, Increment = 1, FormatString = "0"
    };

    private readonly ComboBox _mode = new() { ItemsSource = new[] { PublicIpMode, NoIpMode }, SelectedIndex = 0 };
    private readonly ComboBox _localAddress = new();
    private readonly TextBox _publicIp = new() { PlaceholderText = "Public IPv4 address" };
    private readonly TextBox _hostname = new() { PlaceholderText = "Hostname from No-IP" };
    private readonly TextBox _keyUsername = new() { PlaceholderText = "DDNS key username" };
    private readonly TextBox _keyPassword = new() { PlaceholderText = "DDNS key password", PasswordChar = '●' };
    private readonly TextBlock _listenerStatus = Caption("Checking the local service…");
    private readonly TextBlock _networkStatus = Caption();
    private readonly TextBlock _noIpStatus = Caption();
    private readonly StackPanel _devices = new() { Spacing = 8 };
    public StackPanel EditActions { get; }
    private readonly Button _cancel;
    private readonly Button _apply;
    private readonly Button _invite;
    private readonly Button _review;
    private readonly CancellationToken _appStop;
    private CancellationTokenSource? _visible;
    private MobileAccessStatus? _status;
    private MobileDevice[]? _shownDevices;
    private MobilePairingWindow? _pairingWindow;
    private bool _busy;

    public MobileAccessView(ShellViewModel shell) : this(CreateClient(shell), shell.StopToken)
    {
    }

    internal MobileAccessView(Func<MobileAccessRequest, CancellationToken, Task<MobileAccessResponse>>? send,
        CancellationToken appStop, string[]? localAddresses = null)
    {
        _send = send;
        _appStop = appStop;

        _localAddress.ItemsSource = send is null ? [] : localAddresses ?? LocalAddresses();
        _localAddress.SelectedIndex = 0;

        var publicField = Field("Public IPv4 address", _publicIp);
        var hostnameField = Field("Hostname", _hostname);

        hostnameField.IsVisible = false;
        _noIpStatus.IsVisible = false;

        var noIpFields = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Columns(Field("DDNS key username", _keyUsername), Field("DDNS key password", _keyPassword)),
                Caption("No-IP requires confirmation every 30 days for free hostnames."),
                _noIpStatus
            }
        };

        _mode.SelectionChanged += (_, _) =>
        {
            var useNoIp = Equals(_mode.SelectedItem, NoIpMode);
            noIpFields.IsVisible = hostnameField.IsVisible = useNoIp;
            publicField.IsVisible = !useNoIp;
        };

        noIpFields.IsVisible = false;

        var reload = IconButton("Reload settings", "refresh", () => RefreshAsync(reloadSettings: true));
        _cancel = ActionButton("Cancel", "cancel-action", () =>
        {
            if (_status is not { } status)
            {
                return;
            }

            SetAlert(_notice, null);
            RenderStatus(status, loadSettings: true);
        });
        _apply = CommitButton("Apply", ApplyAsync);
        EditActions = Actions(_cancel, _apply);

        _listenerStatus.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(_firewall, "On Public networks, Windows Firewall allows RTX Lens connections only from the local subnet.");

        var status = new WrapPanel { ItemSpacing = 12, LineSpacing = 6, Children = { _enabled, _listenerStatus } };
        var address = new Panel { Children = { publicField, hostnameField } };

        var settings = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                status,
                Caption("Keep this PC on and the RTX Lens service running to connect from your phone."),
                Columns(Field("Local IPv4 address", _localAddress), Field("TCP port", _port)),
                Columns(Field("Connect using", _mode), address),
                noIpFields,
                _firewall,
                _automaticForwarding,
                _networkStatus,
                Field("Warning history retention (days)", _historyDays)
            }
        };

        var connection = Section("Connection", settings);
        connection.HeaderActions = reload;

        _port.ValueChanged += (_, _) => UpdateForwardingInstructions();
        _localAddress.SelectionChanged += (_, _) => UpdateForwardingInstructions();
        _automaticForwarding.IsCheckedChanged += (_, _) => UpdateForwardingInstructions();

        UpdateForwardingInstructions();

        _invite = ActionButton("Create pairing code", "qr-code-action", CreateInviteAsync);

        var pairingInfo = IconButton("Pairing and remote access information", DesktopIcons.Info, ShowPairingInfoAsync);
        _review = ActionButton("Review pairing request", "qr-code-action", () => OpenPairingWindowAsync(code: null));
        RenderDevices([]);

        var pairing = Section("Pairing & devices", new StackPanel { Spacing = 12, Children = { _review, _devices } });
        pairing.HeaderActions = Actions(_invite, pairingInfo);

        var panels = Columns(connection, pairing);
        StackAtNarrowWidth(panels, 940, firstColumnWeight: 1.4);

        Content = new StackPanel { Spacing = 14, Children = { _notice, panels } };

        if (send is null)
        {
            reload.IsEnabled = false;
            settings.IsEnabled = false;
            _listenerStatus.Text = "Mobile Access is available for the local Windows service.";
        }

        UpdateButtons();

        _refresh.Tick += async (_, _) => await RefreshAsync(reloadSettings: false);

        AttachedToVisualTree += async (_, _) =>
        {
            _visible = CancellationTokenSource.CreateLinkedTokenSource(_appStop);
            if (_send is null)
            {
                return;
            }

            _refresh.Start();

            await RefreshAsync(reloadSettings: _status is null);
        };

        DetachedFromVisualTree += (_, _) =>
        {
            _refresh.Stop();
            _visible?.Cancel();
            _visible?.Dispose();
            _visible = null;

            _pairingWindow?.Close();

            _keyPassword.Text = string.Empty;
        };
    }

    private async Task RefreshAsync(bool reloadSettings)
    {
        _pairingWindow?.CheckExpiry(DateTimeOffset.UtcNow);
        await SendAsync(new MobileAccessRequest(MobileAccessOperation.Status), reloadSettings);
    }

    private async Task ApplyAsync()
    {
        if (_status is not { } status || !TryCreateSettings(status, out var settings, out var keepCredentials))
        {
            return;
        }

        var opensPort = settings.Enabled && (settings.OpenFirewall || settings.PortForwarding is not null)
            && (!status.Enabled || settings.Port != status.Port || settings.OpenFirewall != status.OpenFirewall
                || settings.PortForwarding != status.PortForwarding);

        if (opensPort)
        {
            var message = settings.PortForwarding is not null
                ? $"Allow TCP port {settings.Port} through the router to {settings.LocalAddress}?"
                : $"Allow inbound TCP connections to RTX Lens on port {settings.Port} through Windows Firewall?";
            if (settings.OpenFirewall)
            {
                message += " On Public networks, the Windows Firewall rule only allows devices on the same subnet.";
            }

            if (TopLevel.GetTopLevel(this) is not Window owner || !await ConfirmAsync(owner, "Allow mobile access", message, "Allow"))
            {
                return;
            }
        }

        var request = new MobileAccessRequest(MobileAccessOperation.Configure, settings, KeepNoIpCredentials: keepCredentials);
        var response = await SendAsync(request, reloadSettings: true);

        if (response is { Success: true })
        {
            SetAlert(_notice, "Mobile access settings saved.", "success");
        }
    }

    private bool TryCreateSettings(MobileAccessStatus status, out MobileAccessSettings settings, out bool keepCredentials)
    {
        var localAddress = _localAddress.SelectedItem as string;
        var port = _port.Value;

        keepCredentials = false;
        settings = new MobileAccessSettings();

        if (_historyDays.Value is not { } days || days != decimal.Truncate(days) || days is < 1 or > 365)
        {
            SetAlert(_notice, "Enter a whole number between 1 and 365 for warning history retention.", "warning");
            return false;
        }

        if (_enabled.IsChecked != true)
        {
            keepCredentials = status.NoIpHostname is not null;
            settings = new MobileAccessSettings(Port: status.Port, OpenFirewall: status.OpenFirewall,
                PortForwarding: status.PortForwarding, LocalAddress: status.LocalAddress, PublicAddress: status.PublicAddress,
                WarningHistoryDays: (int)days);

            return true;
        }

        if (port is null || port != decimal.Truncate(port.Value) || localAddress is null)
        {
            SetAlert(_notice, "Choose a local IPv4 address and enter a whole TCP port number.", "warning");
            return false;
        }

        NoIpSettings? noIp = null;
        string? publicAddress = null;

        if (Equals(_mode.SelectedItem, NoIpMode))
        {
            var hostname = _hostname.Text?.Trim() ?? string.Empty;
            var username = _keyUsername.Text?.Trim() ?? string.Empty;
            var password = _keyPassword.Text ?? string.Empty;

            keepCredentials = hostname.Length > 0 && string.Equals(status.NoIpHostname, hostname, StringComparison.OrdinalIgnoreCase)
                && username.Length == 0 && password.Length == 0;

            if (!keepCredentials)
            {
                noIp = new NoIpSettings(hostname, username, password);
            }
        }
        else
        {
            publicAddress = _publicIp.Text?.Trim() ?? string.Empty;
        }

        var forwarding = _automaticForwarding.IsChecked == true ? new MobilePortForwarding(localAddress, (int)port.Value) : null;
        settings = new MobileAccessSettings(
            true, (int)port.Value, noIp, _firewall.IsChecked == true, forwarding, localAddress, publicAddress, (int)days);

        if (!settings.IsValid)
        {
            SetAlert(_notice, "Check the port and enter a public IPv4 address or a No-IP hostname with its DDNS key.", "warning");
            return false;
        }

        return true;
    }

    private async Task CreateInviteAsync()
    {
        if (_pairingWindow is not null || TopLevel.GetTopLevel(this) is not Window
            || _status is not { Listening: true, LocalAddress: { } localAddress, PendingApproval: null } status
            || (status.NoIpHostname ?? status.PublicAddress) is not { } remoteAddress)
        {
            return;
        }

        var response = await SendAsync(new MobileAccessRequest(MobileAccessOperation.CreateInvite));
        if (response?.Invite is not { } invite)
        {
            return;
        }

        var code = new MobilePairingCode(1, new UriBuilder("https", localAddress, status.Port).Uri,
            new UriBuilder("https", remoteAddress, status.Port).Uri, invite.PublicKeyFingerprint, invite.Secret, invite.ExpiresAt);
        await OpenPairingWindowAsync(code);
    }

    private async Task OpenPairingWindowAsync(MobilePairingCode? code)
    {
        if (_pairingWindow is not null || TopLevel.GetTopLevel(this) is not Window owner
            || (code is null && _status?.PendingApproval is null))
        {
            return;
        }

        var dialog = new MobilePairingWindow(code, ResolvePairingAsync);
        if (owner.Screens.ScreenFromWindow(owner) is { } screen)
        {
            dialog.Width = Math.Min(dialog.Width, screen.WorkingArea.Width / screen.Scaling);
            dialog.MaxHeight = screen.WorkingArea.Height / screen.Scaling;
        }

        _pairingWindow = dialog;
        if (code is null)
        {
            dialog.UpdateApproval(_status?.PendingApproval);
        }
        UpdateButtons();

        try
        {
            var closed = dialog.ShowDialog(owner);
            await RefreshAsync(reloadSettings: false);
            await closed;
        }
        finally
        {
            _pairingWindow = null;
            UpdateButtons();
        }
    }

    private async Task ResolvePairingAsync(MobileAccessOperation operation, Guid id)
    {
        if (_status?.PendingApproval?.Id != id || _pairingWindow is not { } dialog)
        {
            return;
        }

        dialog.ReportError(null);
        var response = await SendAsync(new MobileAccessRequest(operation, Id: id));
        if (response is { Success: true })
        {
            dialog.ShowDecision(operation);
        }

        await RefreshAsync(reloadSettings: false);
    }

    private async Task ShowPairingInfoAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                DialogText("Pair your device on the same local network as this PC. "
                    + "The pairing code is valid for five minutes and pairs one device."),
                DialogText("For remote access, keep this PC and the RTX Lens service running. "
                    + "Switch the device to mobile data, then use Test remote access in its RTX Lens settings.")
            }
        };

        if (_status?.Devices.Max(device => device.LastExternalConnectionAt) is { } lastExternal)
        {
            content.Children.Add(Caption($"Last external connection at {lastExternal.ToLocalTime():g}."));
        }

        await ShowInformationAsync(owner, "Pairing and remote access", content);
    }

    private async Task RevokeAsync(MobileDevice device)
    {
        if (TopLevel.GetTopLevel(this) is Window owner
            && await ConfirmAsync(owner, "Revoke device access", $"Disconnect {device.Name} and revoke its access?", "Revoke")
            && await SendAsync(new MobileAccessRequest(MobileAccessOperation.Revoke, Id: device.Id)) is { Success: true })
        {
            await RefreshAsync(reloadSettings: false);
        }
    }

    private async Task SendTestNotificationAsync(MobileDevice device)
    {
        if (await SendAsync(new MobileAccessRequest(MobileAccessOperation.SendTestNotification, Id: device.Id)) is { Success: true })
        {
            SetAlert(_notice, "Test notification queued.", "success");
        }
    }

    private async Task<MobileAccessResponse?> SendAsync(MobileAccessRequest request, bool reloadSettings = false)
    {
        if (_send is null || _visible is not { } visible || _busy)
        {
            return null;
        }

        _busy = true;
        UpdateButtons();
        var cancellationToken = visible.Token;

        try
        {
            var response = await _send(request, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            if (response.Status is { } status)
            {
                var firstLoad = _status is null;
                _status = status;

                RenderStatus(status, reloadSettings || firstLoad);
            }

            if (!response.Success)
            {
                var message = response.Error ?? "The service could not complete the request.";
                SetAlert(_notice, message, "warning");
                _pairingWindow?.ReportError(message);
            }
            else if (request.Operation == MobileAccessOperation.Status && reloadSettings && response.Status?.Error is null)
            {
                SetAlert(_notice, null);
            }

            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException
            or JsonException or OperationCanceledException or InvalidOperationException or ArgumentException)
        {
            _status = null;
            _listenerStatus.IsVisible = false;

            var message = error is OperationCanceledException ? "The local service did not respond in time." : error.Message;
            SetAlert(_notice, message, "warning");
            _pairingWindow?.ReportError(message);

            return null;
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    private void RenderStatus(MobileAccessStatus status, bool loadSettings)
    {
        _listenerStatus.IsVisible = true;
        _listenerStatus.Text = status.Listening ? $"Listening on TCP port {status.Port}." : status.Enabled ? "The listener could not start." : "Mobile access is off.";
        _noIpStatus.Text = status.NoIp?.Message;
        _noIpStatus.IsVisible = status.NoIp is not null;

        if (status.Error is not null)
        {
            SetAlert(_notice, status.Error, "warning");
            _pairingWindow?.ReportError(status.Error);
        }

        if (loadSettings)
        {
            _enabled.IsChecked = status.Enabled;
            _port.Value = status.Port;
            _historyDays.Value = status.WarningHistoryDays;

            _localAddress.SelectedIndex = status.LocalAddress is { } localAddress && _localAddress.Items.Contains(localAddress)
                ? _localAddress.Items.IndexOf(localAddress) : (_localAddress.Items.Count > 0 ? 0 : -1);

            _firewall.IsChecked = status.OpenFirewall;
            _automaticForwarding.IsChecked = status.PortForwarding is not null;
            _mode.SelectedItem = status.NoIpHostname is null ? PublicIpMode : NoIpMode;
            _publicIp.Text = status.PublicAddress;
            _hostname.Text = status.NoIpHostname;
            _keyUsername.Text = string.Empty;
            _keyPassword.Text = string.Empty;
            _keyUsername.PlaceholderText = status.NoIpHostname is null ? "DDNS key username" : "Leave blank to keep the saved key";
            _keyPassword.PlaceholderText = status.NoIpHostname is null ? "DDNS key password" : "Leave blank to keep the saved key";

            UpdateForwardingInstructions();
        }

        _pairingWindow?.UpdateApproval(status.PendingApproval);
        RenderDevices(status.Devices);
    }

    private void RenderDevices(MobileDevice[] devices)
    {
        if (_shownDevices?.SequenceEqual(devices) == true)
        {
            return;
        }

        _shownDevices = devices;
        _devices.Children.Clear();

        if (devices.Length == 0)
        {
            _devices.Children.Add(Alert("No paired devices.", "warning"));
            return;
        }

        foreach (var device in devices)
        {
            if (_devices.Children.Count > 0)
            {
                var divider = new Border { Height = 1, [!Border.BackgroundProperty] = new DynamicResourceExtension(DesktopBrushes.PanelBorder) };
                _devices.Children.Add(divider);
            }

            var details = device.LastConnectedAt is { } connectedAt ? $"Last seen {connectedAt.ToLocalTime():g}" : "No connections yet";
            var title = DialogText(device.Name);
            title.FontWeight = FontWeight.SemiBold;
            var name = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = { title, Caption(details) } };
            var revoke = ActionButton("Revoke", "cancel-action", () => RevokeAsync(device));
            Control actions = revoke;
            if (device.LockScreenNotifications)
            {
                name.Children.Add(Caption("Lock-screen notifications enabled"));
                var test = ActionButton("Send test", "mail-action", () => SendTestNotificationAsync(device));
                actions = Actions(test, revoke);
            }

            actions.VerticalAlignment = VerticalAlignment.Center;
            actions.HorizontalAlignment = HorizontalAlignment.Right;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12, Children = { name, actions } };
            Grid.SetColumn(actions, 1);

            _devices.Children.Add(row);
        }
    }

    private void UpdateButtons()
    {
        _cancel.IsEnabled = _apply.IsEnabled = !_busy && _status is not null;
        _invite.IsEnabled = !_busy && _pairingWindow is null
            && _status is { Listening: true, LocalAddress: not null, PendingApproval: null }
            && (_status.NoIpHostname is not null || _status.PublicAddress is not null);
        _review.IsVisible = _pairingWindow is null && _status?.PendingApproval is not null;
        _review.IsEnabled = !_busy;
        _pairingWindow?.SetRequestAvailability(_busy, _status?.PendingApproval is not null);
    }

    private void UpdateForwardingInstructions()
    {
        var host = _localAddress.SelectedItem as string ?? "the PC’s local IPv4 address";
        var port = _port.Value?.ToString("0", CultureInfo.InvariantCulture) ?? "the selected port";

        _networkStatus.Text = _automaticForwarding.IsChecked == true
            ? $"RTX Lens will use UPnP to forward TCP {port} to {host}:{port}."
            : $"For manual setup, forward TCP {port} to {host}:{port} on the router. Reserve the PC’s local IP in your router settings.";
    }

    private static Func<MobileAccessRequest, CancellationToken, Task<MobileAccessResponse>>? CreateClient(ShellViewModel shell)
    {
        if (!OperatingSystem.IsWindows() || shell.IsSimulation
            || !Uri.TryCreate(shell.Preferences.ServiceAddress, UriKind.Absolute, out var uri)
            || !uri.IsLoopback)
        {
            return null;
        }

        var send = new MobileAccessClient(uri.Port == ServiceEndpoint.DebugPort).SendAsync;

        return (request, token) => shell.IsSimulation
            ? Task.FromResult(new MobileAccessResponse(false, Error: "Mobile Access is unavailable during simulation."))
            : send(request, token);
    }

    private static string[] LocalAddresses() =>
    [
        .. NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .Select(adapter => adapter.GetIPProperties())
            .OrderByDescending(properties => properties.GatewayAddresses.Count > 0)
            .SelectMany(properties => properties.UnicastAddresses)
            .Select(unicast => unicast.Address.ToString())
            .Where(address => MobileAccessSettings.TryGetLocalIpv4(address, out _))
            .Distinct(StringComparer.Ordinal)
    ];

    private static StackPanel Actions(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var button in buttons)
        {
            panel.Children.Add(button);
        }

        return panel;
    }
}
