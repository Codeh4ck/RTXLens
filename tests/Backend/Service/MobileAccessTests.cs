using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Contracts.Warnings;
using RTXLens.Service.Diagnostics;
using RTXLens.Service.Mobile;
using RTXLens.Service.Services;
using RTXLens.Tests.Infrastructure;
using RTXLens.Tests.TestDoubles;
using RTXLens.Contracts.Mobile;

namespace RTXLens.Tests.Service;

internal static class MobileAccessTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync(PairedClientReusesTheSensorStreamAsync);
        await runner.RunAsync(RestartKeepsIdentityAndDisableRemovesRulesAsync);
        if (OperatingSystem.IsWindows())
        {
            runner.Run(PublicFirewallScopeIsLimitedToTheLocalSubnet);
        }
        await runner.RunAsync(DebugSimulationWarningsStayOffTheLocalSensorStreamAsync);
        await runner.RunAsync(ReportsPortConflictAndRecoversAsync);
        await runner.RunAsync(NetworkSetupFailureKeepsTheListenerAvailableAsync);
        await runner.RunAsync(PairedPhoneReadsPersistedWarningHistoryAsync);
        await runner.RunAsync(PairedPhoneReceivesOnlyNewWarningNotificationsAsync);
        await runner.RunAsync(BlockedRecipientDoesNotHoldUpAnotherPhoneAsync);
        await runner.RunAsync(FullReceiptQueueReportsMissedChecksAsync);
        runner.Run(ExpiredInviteCannotBeUsed);
        runner.Run(CertificateRenewalKeepsThePublicKey);
        await runner.RunAsync(NoIpRetriesAndSuspendsRejectedCredentialsAsync);
    }

    private static MobilePairing CreatePushPairing(params string[] tokens)
    {
        var store = new TestStore();
        store.Save(MobileAccessData.Empty with
        {
            Settings = new MobileAccessSettings(Enabled: true),
            Devices = tokens.Select(token => new MobileDeviceCredential(Guid.NewGuid(), token, new byte[32], token)).ToArray()
        });
        var pairing = new MobilePairing(store, TimeProvider.System, NullLogger.Instance);
        pairing.Load();
        pairing.SetListening(true);
        return pairing;
    }

    private static async Task BlockedRecipientDoesNotHoldUpAnotherPhoneAsync()
    {
        var pairing = CreatePushPairing("ExpoPushToken[phone_one]", "ExpoPushToken[phone_two]");
        var blockedToken = pairing.GetPushTargets()[0].Token;
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fast = Channel.CreateUnbounded<string>();
        var slow = Channel.CreateUnbounded<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var handler = new RecordingPushHandler(async (message, ct) =>
        {
            using var body = JsonDocument.Parse(message);
            var title = body.RootElement.GetProperty("title").GetString()!;
            if (body.RootElement.GetProperty("to").GetString() == blockedToken)
            {
                blocked.TrySetResult();
                await release.Task.WaitAsync(ct);
                await slow.Writer.WriteAsync(title, ct);
            }
            else
            {
                await fast.Writer.WriteAsync(title, ct);
            }
        });
        using var client = new HttpClient(handler);
        using var delivery = new MobilePushDelivery(pairing, new ExpoMobilePushProvider(client), TimeProvider.System,
            NullLogger<MobilePushDelivery>.Instance);
        await delivery.StartAsync(default);
        try
        {
            delivery.SendSimulation("first");
            await blocked.Task.WaitAsync(timeout.Token);
            delivery.SendSimulation("second");
            TestAssert.Equal("Simulation · first", await fast.Reader.ReadAsync(timeout.Token));
            TestAssert.Equal("Simulation · second", await fast.Reader.ReadAsync(timeout.Token));
            release.TrySetResult();
            TestAssert.Equal("Simulation · first", await slow.Reader.ReadAsync(timeout.Token));
            TestAssert.Equal("Simulation · second", await slow.Reader.ReadAsync(timeout.Token));
        }
        finally
        {
            release.TrySetResult();
            await delivery.StopAsync(default);
        }
    }

    private static async Task FullReceiptQueueReportsMissedChecksAsync()
    {
        var pairing = CreatePushPairing("ExpoPushToken[receipt_test]");
        using var input = new ActivityLoggerProvider(TimeProvider.System);
        using var logs = LoggerFactory.Create(builder => builder.AddProvider(input));
        using var handler = new RecordingPushHandler();
        using var client = new HttpClient(handler);
        using var delivery = new MobilePushDelivery(pairing, new ExpoMobilePushProvider(client), TimeProvider.System,
            logs.CreateLogger<MobilePushDelivery>());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var receiptWarning = false;
        await delivery.StartAsync(default);
        try
        {
            // One receipt can be waiting on its due time while another 256 occupy the queue.
            for (var i = 0; i < 260; i++)
            {
                delivery.SendSimulation("queue pressure");
                while (true)
                {
                    var record = await input.Reader.ReadAsync(timeout.Token);
                    receiptWarning |= IsReceiptWarning(record);
                    if (record.Entry.Code == "mobile.push-accepted")
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            await delivery.StopAsync(default);
        }
        while (input.Reader.TryRead(out var record))
        {
            receiptWarning |= IsReceiptWarning(record);
        }
        TestAssert.True(receiptWarning, "260 accepted pushes filled the receipt queue without reporting a missed check.");

        static bool IsReceiptWarning(ActivityRecord record) => record.Level >= LogLevel.Warning &&
            (record.Entry.Code.Contains("receipt", StringComparison.OrdinalIgnoreCase)
             || record.Entry.Message.Contains("receipt", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task PairedPhoneReceivesOnlyNewWarningNotificationsAsync()
    {
        await using var local = await MonitoringTestHost.StartWithTestPluginsAsync();
        var clock = TimeProvider.System;
        var store = new TestStore();
        var pairing = new MobilePairing(store, clock, NullLogger.Instance);
        pairing.Load();
        pairing.SaveSettings(new MobileAccessSettings(Enabled: true), privateKey: null, rules: null);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var cert = new MobileAccessCertificate(key.ExportPkcs8PrivateKey(), clock);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var listener = await MobileAccessListener.StartAsync(IPAddress.Loopback, GetUnusedPort(), cert, pairing, local.SensorHub, ct);
        pairing.SetListening(true);
        using var phone = CreateClient(MobileAccessListener.GetAddress(listener), cert.Fingerprint);

        const string token = "ExpoPushToken[test_phone_registration]";
        using var denied = await phone.PostAsJsonAsync("/notification-registration", new { token }, ct);
        TestAssert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        var credential = await PairAsync(phone, pairing, pairing.CreateInvite(cert.Fingerprint)!, ct);
        phone.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential.Token);
        using var invalid = await phone.PostAsJsonAsync("/notification-registration", new { token = "invalid" }, ct);
        TestAssert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var registered = await phone.PostAsJsonAsync("/notification-registration", new { token }, ct);
        TestAssert.Equal(HttpStatusCode.NoContent, registered.StatusCode);
        TestAssert.True(pairing.GetDevices().Single().LockScreenNotifications, "The paired phone should have a push registration.");

        using var handler = new RecordingPushHandler();
        using var client = new HttpClient(handler);
        using var delivery = new MobilePushDelivery(pairing, new ExpoMobilePushProvider(client), clock, NullLogger<MobilePushDelivery>.Instance);
        await delivery.StartAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            var id = Guid.NewGuid();
            var warning = new WarningState("warning", "wireview-pro2", "test-device", "overcurrent", "Wire overcurrent",
                "Local diagnostic details", WarningPhase.Active, id.ToString("N"), now, new WarningPolicy(), 1, now);
            delivery.Observe(new WarningSnapshot("test", 1, true, [warning]));
            delivery.Observe(new WarningSnapshot("test", 1, true, [warning]));
            delivery.Observe(new WarningSnapshot("test", 2, false, [warning with { NotificationSequence = 2 }]));
            TestAssert.True(delivery.SendTest(credential.DeviceId), "The first test notification should be queued.");
            TestAssert.False(delivery.SendTest(credential.DeviceId), "Repeated test notifications should be throttled.");

            using var alert = JsonDocument.Parse(await handler.Messages.Reader.ReadAsync(ct));
            TestAssert.Equal(token, alert.RootElement.GetProperty("to").GetString());
            TestAssert.Equal("Wire overcurrent", alert.RootElement.GetProperty("title").GetString());
            TestAssert.Equal("rtxlens-warnings", alert.RootElement.GetProperty("channelId").GetString());
            TestAssert.Equal(id, alert.RootElement.GetProperty("data").GetProperty("incidentId").GetGuid());
            TestAssert.False(alert.RootElement.GetRawText().Contains(warning.Detail, StringComparison.Ordinal),
                "Diagnostic details should stay on the PC.");

            using var test = JsonDocument.Parse(await handler.Messages.Reader.ReadAsync(ct));
            TestAssert.Equal("RTX Lens test notification", test.RootElement.GetProperty("title").GetString());
            TestAssert.Equal(JsonValueKind.Null, test.RootElement.GetProperty("data").GetProperty("incidentId").ValueKind);

            var preferences = new MobileNotificationPreferences(MobileWarningCategory.Safeguard, ShowTitle: false);
            using var badFilter = await phone.PostAsJsonAsync("/notification-registration/v2",
                new { token, preferences = new { categories = 16, showTitle = true } }, ct);
            TestAssert.Equal(HttpStatusCode.BadRequest, badFilter.StatusCode);
            using var updated = await phone.PostAsJsonAsync("/notification-registration/v2", new { token, preferences }, ct);
            TestAssert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
            TestAssert.Equal(preferences, store.Load().Devices.Single().NotificationPreferences);

            delivery.Observe(new WarningSnapshot("test", 3, true, [warning with { NotificationSequence = 3 }]));
            var safeguardId = Guid.NewGuid();
            delivery.SendSafeguard(safeguardId, connector: 2, now);

            using var privateAlert = JsonDocument.Parse(await handler.Messages.Reader.ReadAsync(ct));
            TestAssert.Equal("RTX Lens warning", privateAlert.RootElement.GetProperty("title").GetString());
            var data = privateAlert.RootElement.GetProperty("data");
            TestAssert.Equal("safeguard", data.GetProperty("kind").GetString());
            TestAssert.Equal(safeguardId, data.GetProperty("incidentId").GetGuid());
            TestAssert.False(privateAlert.RootElement.GetRawText().Contains("connector 2", StringComparison.Ordinal),
                "The private push payload must omit the warning title.");

            using var removed = await phone.PostAsJsonAsync("/notification-registration/v2", new { token = (string?)null, preferences }, ct);
            TestAssert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
            TestAssert.False(pairing.GetDevices().Single().LockScreenNotifications, "The phone should no longer have a push registration.");
            TestAssert.True(pairing.Revoke(credential.DeviceId), "The phone should be revocable.");
            using var revoked = await phone.PostAsJsonAsync("/notification-registration/v2", new { token, preferences }, ct);
            TestAssert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        }
        finally
        {
            await delivery.StopAsync(CancellationToken.None);
        }
    }

    private static async Task PairedPhoneReadsPersistedWarningHistoryAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "rtxlens-mobile-history-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = TimeProvider.System;
            var now = clock.GetUtcNow();
            var reading = new SensorReading(new SensorIdentity("wireview-pro2", "test-device", SensorComponent.GpuPin, SensorMetric.Current, 2),
                SensorUnit.Amperes, 9.2, now);
            var history = new MobileWarningHistory(root, NullLogger<MobileWarningHistory>.Instance);
            var ids = new List<Guid>();

            for (var i = 0; i < 26; i++)
            {
                var id = Guid.NewGuid();
                ids.Add(id);
                var start = now.AddMinutes(i - 27);
                var warning = new WarningState("warning", "wireview-pro2", "test-device", "overcurrent", "Wire overcurrent",
                    "Pin 2 exceeded its limit", WarningPhase.Active, id.ToString("N"), start, new WarningPolicy(), 1, start);
                history.Observe(Frame(start, warning), 30);
                history.Observe(Frame(start.AddSeconds(1), warning with { Phase = WarningPhase.Recovering }), 30);
                history.Observe(Frame(start.AddSeconds(3)), 30);
            }

            var restarted = new MobileWarningHistory(root, NullLogger<MobileWarningHistory>.Instance);
            restarted.Observe(Frame(now), 30);
            var safeguard = new SafeguardJournalService(clock, NullLogger<SafeguardJournalService>.Instance);
            await using var local = await MonitoringTestHost.StartWithTestPluginsAsync();
            var pairing = new MobilePairing(new TestStore(), clock, NullLogger.Instance);
            pairing.Load();
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var cert = new MobileAccessCertificate(key.ExportPkcs8PrivateKey(), clock);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var ct = timeout.Token;
            var retention = 30;
            var endpoints = new MobileHistoryEndpoints(restarted, safeguard, clock, () => retention);
            await using var listener = await MobileAccessListener.StartAsync(IPAddress.Loopback, GetUnusedPort(), cert, pairing,
                local.SensorHub, ct, history: endpoints);
            pairing.SetListening(true);
            using var remote = CreateClient(MobileAccessListener.GetAddress(listener), cert.Fingerprint);
            using var denied = await remote.GetAsync("/incidents", ct);
            TestAssert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

            var credential = await PairAsync(remote, pairing, pairing.CreateInvite(cert.Fingerprint)!, ct);
            remote.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential.Token);
            using var session = await remote.GetAsync("/session", ct);
            TestAssert.Equal(HttpStatusCode.NoContent, session.StatusCode);
            using var page = JsonDocument.Parse(await remote.GetStringAsync("/incidents", ct));
            var items = page.RootElement.GetProperty("items");
            TestAssert.Equal(25, items.GetArrayLength());
            TestAssert.Equal(ids[^1], items[0].GetProperty("id").GetGuid());
            TestAssert.Equal("Cleared", items[0].GetProperty("status").GetString());
            TestAssert.Equal(0, items[0].GetProperty("readings").GetArrayLength());
            var cursor = page.RootElement.GetProperty("next").GetString();
            using var older = JsonDocument.Parse(await remote.GetStringAsync($"/incidents?before={cursor}", ct));
            TestAssert.Equal(ids[0], older.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());

            using var detail = JsonDocument.Parse(await remote.GetStringAsync($"/incidents/warning/{ids[^1]}", ct));
            TestAssert.Equal("Pin 2 exceeded its limit", detail.RootElement.GetProperty("detail").GetString());
            TestAssert.Equal(9.2, detail.RootElement.GetProperty("readings")[0].GetProperty("value").GetDouble());
            restarted.Observe(Frame(now.AddDays(2)), 1);
            TestAssert.False(Directory.EnumerateFiles(root, "*.json").Any(), "The retention change should remove expired incidents.");
            TestAssert.True(pairing.Revoke(credential.DeviceId), "The test phone should be revoked.");
            using var revoked = await remote.GetAsync("/session", ct);
            TestAssert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);

            SensorSnapshot Frame(DateTimeOffset time, params WarningState[] states) => new(1, 1, time, [reading], [], [])
            {
                Warnings = new WarningSnapshot("test", 1, true, states)
            };
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task DebugSimulationWarningsStayOffTheLocalSensorStreamAsync()
    {
        await using var local = await MonitoringTestHost.StartWithTestPluginsAsync();
        var clock = TimeProvider.System;
        var pairing = new MobilePairing(new TestStore(), clock, NullLogger.Instance);
        using var providerClient = new HttpClient();
        var updater = new NoIpUpdater(providerClient, clock, NullLogger.Instance);
        using var service = new MobileAccessService(pairing, local.SensorHub, clock, updater, new TestNetwork(),
            NullLogger<MobileAccessService>.Instance, IPAddress.Loopback, debugInstance: true);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var cancellationToken = timeout.Token;
        var warning = new MobileSimulationWarning("Current imbalance", "Simulation from the desktop warning engine");
        TestAssert.False(await service.PublishSimulationWarningAsync(warning, cancellationToken), "The listener is off.");
        await service.StartAsync(cancellationToken);

        try
        {
            var settings = new MobileAccessSettings(Enabled: true, Port: GetUnusedPort());
            var status = await service.ConfigureAsync(settings, cancellationToken);
            TestAssert.True(status.Listening, status.Error ?? "The Debug listener did not start.");

            var invite = (await service.CreateInviteAsync(cancellationToken))!;
            var address = service.ListenerAddress!;
            using var client = CreateClient(address, invite.PublicKeyFingerprint);
            var credential = await PairAsync(client, pairing, invite, cancellationToken);
            using var localSocket = await local.ConnectSensorClientAsync(cancellationToken);
            using var remoteSocket = await ConnectAsync(address, invite.PublicKeyFingerprint, credential.Token, cancellationToken);
            TestAssert.True(await service.PublishSimulationWarningAsync(warning, cancellationToken), "The Debug listener should accept the warning.");
            while (true)
            {
                using var frame = await WebSocketMessageReader.ReadJsonAsync(remoteSocket, cancellationToken);
                if (frame.RootElement.TryGetProperty("simulationWarnings", out var warnings))
                {
                    TestAssert.Equal(warning.Title, warnings[0].GetProperty("title").GetString());
                    break;
                }
            }

            using var localFrame = await WebSocketMessageReader.ReadJsonAsync(localSocket, cancellationToken);
            TestAssert.False(localFrame.RootElement.TryGetProperty("simulationWarnings", out _), "The local stream should contain real readings only.");
            remoteSocket.Abort();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static async Task ReportsPortConflictAndRecoversAsync()
    {
        await using var local = await MonitoringTestHost.StartWithTestPluginsAsync();
        var clock = TimeProvider.System;
        var pairing = new MobilePairing(new TestStore(), clock, NullLogger.Instance);
        using var providerClient = new HttpClient();
        var updater = new NoIpUpdater(providerClient, clock, NullLogger.Instance);
        using var service = new MobileAccessService(pairing, local.SensorHub, clock, updater, new TestNetwork(),
            NullLogger<MobileAccessService>.Instance, IPAddress.Loopback);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var cancellationToken = timeout.Token;
        await service.StartAsync(cancellationToken);
        using var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        var port = ((IPEndPoint)busy.LocalEndpoint).Port;
        try
        {
            var settings = new MobileAccessSettings(Enabled: true, Port: port);
            var status = await service.ConfigureAsync(settings, cancellationToken);
            TestAssert.False(status.Listening, "The occupied port should prevent the mobile listener from starting.");
            TestAssert.Equal($"TCP port {port} is already in use. Choose another Mobile Access port.", status.Error);
            busy.Stop();

            status = await service.ConfigureAsync(settings, cancellationToken);
            TestAssert.True(status.Listening && status.Error is null, status.Error ?? "The listener should recover when the port is released.");
            TestAssert.False(await service.PublishSimulationWarningAsync(new MobileSimulationWarning("Test", ""), cancellationToken),
                "The release listener should never accept simulated warnings.");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static async Task NetworkSetupFailureKeepsTheListenerAvailableAsync()
    {
        await using var local = await MonitoringTestHost.StartWithTestPluginsAsync();
        var clock = TimeProvider.System;
        var pairing = new MobilePairing(new TestStore(), clock, NullLogger.Instance);
        using var providerClient = new HttpClient();
        var updater = new NoIpUpdater(providerClient, clock, NullLogger.Instance);
        const string message = "The router could not set up automatic forwarding.";
        var network = new TestNetwork { ApplyError = new MobileNetworkException(message, new IOException()) };
        using var service = new MobileAccessService(pairing, local.SensorHub, clock, updater, network,
            NullLogger<MobileAccessService>.Instance, IPAddress.Loopback);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var cancellationToken = timeout.Token;
        await service.StartAsync(cancellationToken);
        try
        {
            var port = GetUnusedPort();
            var settings = new MobileAccessSettings(Enabled: true, Port: port,
                PortForwarding: new MobilePortForwarding("192.168.1.40", port));
            var status = await service.ConfigureAsync(settings, cancellationToken);
            TestAssert.True(status.Listening, "A router failure should leave the HTTPS listener up.");
            TestAssert.Equal(message, status.Error);
            TestAssert.True(await service.CreateInviteAsync(cancellationToken) is not null, "The service should still support pairing.");

            status = await service.ConfigureAsync(settings with { PortForwarding = null }, cancellationToken);
            TestAssert.True(status.Listening && status.Error is null, status.Error ?? "Local pairing should be available.");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static async Task PairedClientReusesTheSensorStreamAsync()
    {
        await using var local = await MonitoringTestHost.StartWithWireViewAsync(new WireViewTestState(), initialPollMs: 100);
        var store = new TestStore();
        var clock = TimeProvider.System;
        var pairing = new MobilePairing(store, clock, NullLogger.Instance);
        using var providerClient = new HttpClient();
        var updater = new NoIpUpdater(providerClient, clock, NullLogger.Instance);
        using var service = new MobileAccessService(pairing, local.SensorHub, clock, updater, new TestNetwork(),
            NullLogger<MobileAccessService>.Instance, IPAddress.Loopback);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var cancellationToken = timeout.Token;
        await service.StartAsync(cancellationToken);

        try
        {
            var settings = new MobileAccessSettings(Enabled: true, Port: GetUnusedPort());
            var status = await service.ConfigureAsync(settings, cancellationToken);
            TestAssert.True(status.Listening && status.Error is null, status.Error ?? "The mobile listener did not start.");

            var invite = (await service.CreateInviteAsync(cancellationToken))!;
            var address = service.ListenerAddress!;
            using var remote = CreateClient(address, invite.PublicKeyFingerprint);
            using var wrongHost = CreateClient(address, new string('0', 64));

            try
            {
                using var response = await wrongHost.GetAsync("/sensors", cancellationToken);
                throw new InvalidOperationException("A mismatched host key should reject TLS.");
            }
            catch (HttpRequestException)
            {
                // The simulated mobile client rejected the unexpected host key.
            }

            using var denied = await remote.GetAsync("/sensors", cancellationToken);
            TestAssert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            await AssertSocketRejectedAsync(address, invite.PublicKeyFingerprint, "invalid", cancellationToken);

            var credential = await PairAsync(remote, pairing, invite, cancellationToken);
            remote.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential.Token);

            using var configuration = await remote.GetAsync("/configuration", cancellationToken);
            TestAssert.Equal(HttpStatusCode.NotFound, configuration.StatusCode);
            using var plugin = await remote.PutAsJsonAsync("/plugins/wireview-pro2/settings", new { enabled = false }, cancellationToken);
            TestAssert.Equal(HttpStatusCode.NotFound, plugin.StatusCode);

            using var localSocket = await local.ConnectSensorClientAsync(cancellationToken);
            using var remoteSocket = await ConnectAsync(address, invite.PublicKeyFingerprint, credential.Token, cancellationToken);

            using var firstLocal = await WebSocketMessageReader.ReadJsonAsync(localSocket, cancellationToken);
            using var firstRemote = await WebSocketMessageReader.ReadJsonAsync(remoteSocket, cancellationToken);
            var device = pairing.GetDevices().Single();
            TestAssert.True(device.LastConnectedAt.HasValue, "The successful upgrade should be visible in pairing status.");
            TestAssert.Equal<DateTimeOffset?>(null, device.LastExternalConnectionAt);
            TestAssert.Equal(firstLocal.RootElement.GetProperty("schemaVersion").GetInt32(),
                firstRemote.RootElement.GetProperty("schemaVersion").GetInt32());

            var sequence = Math.Max(firstLocal.RootElement.GetProperty("sequence").GetInt64(),
                firstRemote.RootElement.GetProperty("sequence").GetInt64());
            var nextSequence = await AssertMatchingWarningFrameAsync(localSocket, remoteSocket, sequence, cancellationToken);
            TestAssert.True(pairing.Revoke(credential.DeviceId), "Revocation should remove the paired phone.");

            try
            {
                await WebSocketMessageReader.WaitForCloseAsync(remoteSocket, cancellationToken);
            }
            catch (WebSocketException)
            {
                // Revocation can abort the connection before a close frame.
            }

            await AssertSocketRejectedAsync(address, invite.PublicKeyFingerprint, credential.Token, cancellationToken);
            using var continued = await WebSocketMessageReader.ReadJsonAsync(localSocket, cancellationToken);
            TestAssert.True(continued.RootElement.GetProperty("sequence").GetInt64() > nextSequence,
                "The local stream should continue after remote revocation.");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static async Task RestartKeepsIdentityAndDisableRemovesRulesAsync()
    {
        await using var local = await MonitoringTestHost.StartWithTestPluginsAsync();
        var store = new TestStore();
        var clock = TimeProvider.System;
        var network = new TestNetwork();
        using var providerClient = new HttpClient();
        var updater = new NoIpUpdater(providerClient, clock, NullLogger.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var cancellationToken = timeout.Token;

        var pairing = new MobilePairing(store, clock, NullLogger.Instance);
        using var first = new MobileAccessService(pairing, local.SensorHub, clock, updater, network,
            NullLogger<MobileAccessService>.Instance, IPAddress.Loopback);
        await first.StartAsync(cancellationToken);
        TestAssert.False((await first.GetStatusAsync(cancellationToken)).Listening, "Mobile access should start off.");

        var settings = new MobileAccessSettings(Enabled: true, Port: GetUnusedPort(), OpenFirewall: true,
            PortForwarding: new MobilePortForwarding("192.168.1.40", 59275));

        var enabled = await first.ConfigureAsync(settings, cancellationToken);
        TestAssert.True(enabled.Listening && enabled.Error is null, enabled.Error ?? "Mobile access should be listening.");
        var rules = network.Owned!;
        var invite = (await first.CreateInviteAsync(cancellationToken))!;
        var address = first.ListenerAddress!;
        using var client = CreateClient(address, invite.PublicKeyFingerprint);

        MobilePairingCredential credential;
        try
        {
            credential = await PairAsync(client, pairing, invite, cancellationToken);
        }
        finally
        {
            await first.StopAsync(CancellationToken.None);
        }

        var restartedPairing = new MobilePairing(store, clock, NullLogger.Instance);
        using var second = new MobileAccessService(restartedPairing, local.SensorHub, clock, updater, network,
            NullLogger<MobileAccessService>.Instance, IPAddress.Loopback);
        await second.StartAsync(cancellationToken);

        try
        {
            TestAssert.Equal(rules, network.Owned);
            TestAssert.Equal(2, network.ApplyCount);
            using var socket = await ConnectAsync(
                second.ListenerAddress!, invite.PublicKeyFingerprint, credential.Token, cancellationToken);
            using var frame = await WebSocketMessageReader.ReadJsonAsync(socket, cancellationToken);
            socket.Abort();

            var disabled = await second.ConfigureAsync(settings with { Enabled = false }, cancellationToken);
            TestAssert.False(disabled.Listening, "Disabling mobile access should stop the listener.");
            TestAssert.Equal(rules, network.Removed);
            TestAssert.Equal<MobileNetworkRules?>(null, store.Data.NetworkRules);
        }
        finally
        {
            await second.StopAsync(CancellationToken.None);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void PublicFirewallScopeIsLimitedToTheLocalSubnet()
    {
        var id = Guid.NewGuid();
        var rules = MobileNetworkAccess.GetFirewallRules(new MobileNetworkRules(id, 59275, OpenFirewall: true, PortForwarding: null));
        var legacyName = $"RTX Lens Mobile Access ({id:N})";

        TestAssert.Equal(2, rules.Length);
        TestAssert.Equal((legacyName, 3, "*"), rules[0]);
        TestAssert.Equal(($"{legacyName} - Public LAN", 4, "LocalSubnet"), rules[1]);
    }

    private static void ExpiredInviteCannotBeUsed()
    {
        var clock = new TestTimeProvider();
        var pairing = new MobilePairing(new TestStore(), clock, NullLogger.Instance);
        pairing.Load();
        pairing.SetListening(true);
        var invite = pairing.CreateInvite("fingerprint")!;

        clock.Advance(TimeSpan.FromMinutes(6));

        TestAssert.Equal<MobilePairingClaim?>(null, pairing.Begin(new MobilePairingRequest(invite.Secret, "Phone")));
    }

    private static void CertificateRenewalKeepsThePublicKey()
    {
        var clock = new TestTimeProvider();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var identity = new MobileAccessCertificate(key.ExportPkcs8PrivateKey(), clock);
        var first = identity.GetCertificate();

        clock.Advance(TimeSpan.FromDays(360));
        var renewed = identity.GetCertificate();

        TestAssert.True(renewed.NotAfter > first.NotAfter, "The certificate should be renewed.");
        TestAssert.True(HasFingerprint(first, identity.Fingerprint), "The original certificate should match the invite.");
        TestAssert.True(HasFingerprint(renewed, identity.Fingerprint), "The renewed certificate should match the same invite.");
    }

    private static async Task NoIpRetriesAndSuspendsRejectedCredentialsAsync()
    {
        var clock = new TestTimeProvider();
        var transport = new TestNoIpHandler();
        using var client = new HttpClient(transport);
        var updater = new NoIpUpdater(client, clock, NullLogger.Instance);
        var settings = new NoIpSettings("example.ddns.net", "test-key", "test-secret");

        await updater.CheckAsync(null, CancellationToken.None);
        TestAssert.Equal(0, transport.UpdateCount);

        await updater.CheckAsync(settings, CancellationToken.None);
        TestAssert.Equal(NoIpUpdateState.Current, updater.GetStatus(settings)?.State);
        TestAssert.Equal(1, transport.UpdateCount);

        clock.Advance(TimeSpan.FromMinutes(5));
        await updater.CheckAsync(settings, CancellationToken.None);
        TestAssert.Equal(1, transport.UpdateCount);

        transport.Address = "203.0.113.2";
        transport.Reply = "911";
        clock.Advance(TimeSpan.FromMinutes(5));
        await updater.CheckAsync(settings, CancellationToken.None);
        TestAssert.Equal(NoIpUpdateState.RetryScheduled, updater.GetStatus(settings)?.State);

        clock.Advance(TimeSpan.FromMinutes(29));
        await updater.CheckAsync(settings, CancellationToken.None);
        TestAssert.Equal(2, transport.UpdateCount);

        transport.Reply = "badauth";
        clock.Advance(TimeSpan.FromMinutes(1));
        await updater.CheckAsync(settings, CancellationToken.None);
        TestAssert.Equal(NoIpUpdateState.ActionRequired, updater.GetStatus(settings)?.State);

        clock.Advance(TimeSpan.FromHours(1));
        await updater.CheckAsync(settings, CancellationToken.None);
        TestAssert.Equal(3, transport.UpdateCount);
    }

    private static async Task<MobilePairingCredential> PairAsync(HttpClient client, MobilePairing pairing,
        MobilePairingInvite invite, CancellationToken cancellationToken)
    {
        var request = new MobilePairingRequest(invite.Secret, "Test phone");
        using var start = await client.PostAsJsonAsync("/pairing", request, cancellationToken);
        start.EnsureSuccessStatusCode();
        var claim = (await start.Content.ReadFromJsonAsync<MobilePairingClaim>(cancellationToken))!;

        using var duplicate = await client.PostAsJsonAsync("/pairing", request, cancellationToken);
        TestAssert.Equal(HttpStatusCode.NotFound, duplicate.StatusCode);

        var proof = new MobilePairingCompletion(claim.Id, claim.Secret);
        using var pending = await client.PostAsJsonAsync("/pairing/complete", proof, cancellationToken);
        TestAssert.Equal(HttpStatusCode.Accepted, pending.StatusCode);
        TestAssert.True(pairing.Approve(claim.Id), "The local approval should accept the pending claim.");

        using var completed = await client.PostAsJsonAsync("/pairing/complete", proof, cancellationToken);
        completed.EnsureSuccessStatusCode();
        return (await completed.Content.ReadFromJsonAsync<MobilePairingCredential>(cancellationToken))!;
    }

    private static async Task<long> AssertMatchingWarningFrameAsync(ClientWebSocket local, ClientWebSocket remote,
        long afterSequence, CancellationToken cancellationToken)
    {
        var remoteFrame = await WebSocketMessageReader.ReadUntilAsync(remote,
            frame => frame.GetProperty("sequence").GetInt64() > afterSequence
                && frame.GetProperty("warnings").GetProperty("states").GetArrayLength() > 0, cancellationToken);

        var nextSequence = remoteFrame.GetProperty("sequence").GetInt64();

        while (true)
        {
            var localFrame = await WebSocketMessageReader.ReadUntilAsync(local,
                frame => frame.GetProperty("sequence").GetInt64() >= nextSequence, cancellationToken);

            var localSequence = localFrame.GetProperty("sequence").GetInt64();
            if (localSequence == nextSequence)
            {
                TestAssert.Equal(remoteFrame.GetRawText(), localFrame.GetRawText());
                return nextSequence;
            }

            remoteFrame = await WebSocketMessageReader.ReadUntilAsync(remote,
                frame => frame.GetProperty("sequence").GetInt64() >= localSequence, cancellationToken);
            nextSequence = remoteFrame.GetProperty("sequence").GetInt64();
        }
    }

    private static HttpClient CreateClient(Uri address, string fingerprint)
    {
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) => HasFingerprint(certificate, fingerprint)
        };

        return new HttpClient(handler) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(5) };
    }

    private static async Task<ClientWebSocket> ConnectAsync(
        Uri address, string fingerprint, string token, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            HasFingerprint(certificate as X509Certificate2, fingerprint);

        try
        {
            var stream = new UriBuilder(address) { Scheme = "wss", Path = "/sensors" }.Uri;
            await socket.ConnectAsync(stream, cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task AssertSocketRejectedAsync(Uri address, string fingerprint, string token, CancellationToken cancellationToken)
    {
        try
        {
            using var socket = await ConnectAsync(address, fingerprint, token, cancellationToken);
        }
        catch (WebSocketException)
        {
            return;
        }

        throw new InvalidOperationException("The remote listener accepted an unauthorized WebSocket upgrade.");
    }

    private static bool HasFingerprint(X509Certificate2? certificate, string fingerprint) =>
        certificate is not null && Convert.ToHexString(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo())) == fingerprint;

    private static int GetUnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class RecordingPushHandler(Func<string, CancellationToken, Task>? onSend = null) : HttpMessageHandler
    {
        public Channel<string> Messages { get; } = Channel.CreateUnbounded<string>();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            TestAssert.Equal("https://exp.host/--/api/v2/push/send", request.RequestUri?.AbsoluteUri);
            var message = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (onSend is not null)
            {
                await onSend(message, cancellationToken);
            }

            await Messages.Writer.WriteAsync(message, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { data = new { status = "ok", id = Guid.NewGuid().ToString("N") } })
            };
        }
    }

    private sealed class TestStore : IMobileAccessStore
    {
        public MobileAccessData Data { get; private set; } = MobileAccessData.Empty;

        public MobileAccessData Load() => Data;
        public void Save(MobileAccessData data) => Data = data;
    }

    private sealed class TestNetwork : IMobileNetworkAccess
    {
        public MobileNetworkRules? Owned { get; private set; }
        public MobileNetworkRules? Removed { get; private set; }
        public Exception? ApplyError { get; init; }
        public int ApplyCount { get; private set; }

        public void Apply(MobileNetworkRules rules)
        {
            ApplyCount++;
            if (ApplyError is not null)
            {
                throw ApplyError;
            }

            Owned = rules;
        }

        public void Remove(MobileNetworkRules rules)
        {
            Removed = rules;
            Owned = null;
        }
    }

    private sealed class TestNoIpHandler : HttpMessageHandler
    {
        public int UpdateCount { get; private set; }
        public string Address { get; set; } = "203.0.113.1";
        public string Reply { get; set; } = "good 203.0.113.1";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "ip1.dynupdate.no-ip.com")
            {
                TestAssert.Equal<AuthenticationHeaderValue?>(null, request.Headers.Authorization);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Address) });
            }

            TestAssert.Equal("https", request.RequestUri.Scheme);
            TestAssert.Equal("Basic", request.Headers.Authorization?.Scheme);
            UpdateCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Reply) });
        }
    }
}
