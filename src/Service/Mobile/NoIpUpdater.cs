using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using RTXLens.Core.Diagnostics;
using RTXLens.Contracts.Mobile;

namespace RTXLens.Service.Mobile;

internal sealed class NoIpUpdater(HttpClient client, TimeProvider clock, ILogger logger)
{
    private static readonly Uri DetectionAddress = new("http://ip1.dynupdate.no-ip.com/");
    private const string UpdateAddress = "https://dynupdate.no-ip.com/nic/update";

    private NoIpSettings? _settings;
    private IPAddress? _lastAddress;
    private DateTimeOffset _nextCheck;
    private volatile NoIpUpdateStatus? _status;

    public NoIpUpdateStatus? GetStatus(NoIpSettings? settings) => _settings == settings ? _status : null;

    public async Task CheckAsync(NoIpSettings? settings, CancellationToken cancellationToken)
    {
        if (settings != _settings)
        {
            _status = null;
            _settings = settings;
            _lastAddress = null;
            _nextCheck = default;
        }

        var now = clock.GetUtcNow();
        if (settings is null || now < _nextCheck)
        {
            return;
        }

        _nextCheck = now.AddMinutes(5);
        try
        {
            var contents = await client.GetStringAsync(DetectionAddress, cancellationToken);
            if (!IPAddress.TryParse(contents.Trim(), out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            {
                ScheduleRetry("No-IP could not determine the public IPv4 address.");
                return;
            }

            if (address.Equals(_lastAddress))
            {
                SetStatus(NoIpUpdateState.Current, "The No-IP hostname is current.");
                return;
            }

            var hostname = Uri.EscapeDataString(settings.Hostname);
            var credentials = Encoding.UTF8.GetBytes($"{settings.KeyUsername}:{settings.KeyPassword}");

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{UpdateAddress}?hostname={hostname}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(credentials));

            using var response = await client.SendAsync(request, cancellationToken);

            var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
            var parts = body.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var code = parts.FirstOrDefault();

            if (response.IsSuccessStatusCode && code is ("good" or "nochg") && parts.Length == 2 && IPAddress.TryParse(parts[1], out var updated) &&
                updated.AddressFamily == AddressFamily.InterNetwork)
            {
                _lastAddress = updated;
                SetStatus(NoIpUpdateState.Current, "The No-IP hostname is current.");
            }
            else if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ||
                     code is "badauth" or "nohost" or "badagent" or "abuse" or "!donator")
            {
                _nextCheck = DateTimeOffset.MaxValue;

                SetStatus(NoIpUpdateState.ActionRequired, "No-IP rejected the update. Check the hostname and DDNS key.");
            }
            else
            {
                ScheduleRetry("No-IP could not update the hostname. The service will retry in 30 minutes.");
            }
        }
        catch (Exception error) when (error is HttpRequestException or IOException || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            ScheduleRetry("No-IP could not be reached. The service will retry in 30 minutes.");
        }
    }

    public static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = ConnectIpv4Async
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10),
            MaxResponseContentBufferSize = 1024
        };

        var version = typeof(NoIpUpdater).Assembly.GetName().Version;
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"RTX-Lens RTXLens/Windows{Environment.OSVersion.Version}-{version}");

        return client;
    }

    private void ScheduleRetry(string message)
    {
        _nextCheck = clock.GetUtcNow().AddMinutes(30);
        SetStatus(NoIpUpdateState.RetryScheduled, message);
    }

    private void SetStatus(NoIpUpdateState state, string message)
    {
        if (_status?.State != state || _status.Message != message)
        {
            var level = state == NoIpUpdateState.Current ? LogLevel.Information : LogLevel.Warning;
            logger.LogActivity(level, "mobile.no-ip", "Mobile access", message);
        }

        _status = new NoIpUpdateStatus(state, message, clock.GetUtcNow());
    }

    private static async ValueTask<Stream> ConnectIpv4Async(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint;
        var addresses = await Dns.GetHostAddressesAsync(host.Host, AddressFamily.InterNetwork, cancellationToken);
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(addresses, host.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}