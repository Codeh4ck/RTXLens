using RTXLens.Contracts.Discovery;
using RTXLens.Contracts.Warnings;
using RTXLens.Core.Plugins;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using RTXLens.Contracts.Sensors;
using RTXLens.Core.Diagnostics;
using RTXLens.Plugins.Msi.Contracts.Safeguard;
using RTXLens.SafeguardReporting;
using RTXLens.Service.Mobile;

namespace RTXLens.Service.Services;

internal sealed class SafeguardJournalService(TimeProvider clock, ILogger<SafeguardJournalService> logger,
    MobilePushDelivery? mobilePush = null, IEnumerable<ISensorPluginFactory>? plugins = null) : BackgroundService
{
    private readonly SafeguardTelemetryBuffer _telemetry = new(plugins?.ToDictionary(plugin => plugin.Id, plugin => new PluginHardwareMetadata(plugin.Hardware.DisplayName, plugin.Hardware.Association)));
    private readonly Lock _telemetryFiles = new();
    private SafeguardExecutionNotice[]? _notices;
    private readonly DateTimeOffset _startedAt = clock.GetUtcNow();
    private readonly Dictionary<Guid, string> _loggedStatus = new();

    private readonly Dictionary<Guid, CachedHistory> _historyCache = new();
    private readonly HashSet<Guid> _readFailures = [];
    private DateTimeOffset _nextMailCleanup;

    public SafeguardExecutionNotice[]? Notices => Volatile.Read(ref _notices);

    public void Observe(SensorSnapshot snapshot) => _telemetry.Observe(snapshot);

    internal WarningIncident? ReadMobileIncident(Guid id)
    {
        if (!OperatingSystem.IsWindows() || Notices?.Any(notice => notice.Id == id) != true)
        {
            return null;
        }

        try
        {
            var storage = new SafeguardStorage();
            if (storage.Read<SafeguardExecution>(SafeguardArea.History, id) is not { } execution || execution.Id != id)
            {
                return null;
            }

            var now = clock.GetUtcNow();
            if (execution.StartedAt < now.AddDays(-storage.RetentionDays) && !SafeguardStorage.IsRunning(execution, now))
            {
                return null;
            }

            var telemetry = storage.Read<SafeguardTelemetry>(SafeguardArea.Telemetry, id);
            var delivery = storage.Read<SafeguardDelivery>(SafeguardArea.Delivery, id);
            var status = SafeguardStorage.DisplayStatus(execution, now);

            return new WarningIncident(id, "safeguard", execution.Name, "Safeguard+", execution.StartedAt, execution.FinishedAt, status)
            {
                Connector = execution.Connector, DelayMs = execution.DelayMs, Steps = execution.Steps?.Select(s => new IncidentStep(s.Timestamp, s.Name, s.Result, s.ExitCode)).ToArray() ?? [], DeliveryStatus = delivery?.Status,
                CapturedAt = telemetry?.CapturedAt, Readings = telemetry?.Readings ?? []
            };
        }
        catch (Exception error) when (IsRecordError(error))
        {
            logger.LogWarning("Could not read Safeguard+ mobile report {ExecutionId} ({Type})", id, error.GetType().Name);
            return null;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        if (!OperatingSystem.IsWindows() || cancellationToken.IsCancellationRequested || !Directory.Exists(SafeguardStorage.DefaultRoot))
        {
            return;
        }

        try
        {
            // The script can request shutdown before the next journal poll.
            await RefreshNoticesAsync(new SafeguardStorage(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host reached its shutdown deadline.
        }
        catch (Exception error) when (IsRecordError(error))
        {
            logger.LogWarning("Could not save Safeguard+ telemetry during service shutdown ({Type})", error.GetType().Name);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await Task.WhenAll(
            PollAsync(RefreshNoticesAsync, TimeSpan.FromSeconds(1), stoppingToken),
            PollAsync(ProcessMailAsync, TimeSpan.FromSeconds(1), stoppingToken));
    }

    [SupportedOSPlatform("windows")]
    private async Task PollAsync(Func<SafeguardStorage, CancellationToken, Task> poll, TimeSpan interval, CancellationToken token)
    {
        SafeguardStorage? storage = null;
        using var timer = new PeriodicTimer(interval, clock);

        do
        {
            try
            {
                if (Directory.Exists(SafeguardStorage.DefaultRoot))
                {
                    storage ??= new SafeguardStorage();
                    await poll(storage, token);
                }
            }
            catch (Exception error) when (IsRecordError(error))
            {
                logger.LogWarning("Safeguard+ journal could not be read or updated ({Type})", error.GetType().Name);
                storage = null;

                await Task.Delay(TimeSpan.FromSeconds(15), clock, token);
            }
        }
        while (await timer.WaitForNextTickAsync(token));
    }

    [SupportedOSPlatform("windows")]
    internal Task RefreshNoticesAsync(ISafeguardJournalStorage storage, CancellationToken token)
    {
        CapturePendingTelemetry(storage, token);

        var now = clock.GetUtcNow();
        var cutoff = now.AddDays(-storage.RetentionDays);
        var notices = new List<SafeguardExecutionNotice>();
        var newExecutions = new List<SafeguardExecutionNotice>();

        var retained = storage.Ids(SafeguardArea.History).ToHashSet();

        foreach (var id in retained)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                var execution = ReadHistory(storage, id);
                _readFailures.Remove(id);

                if (execution is null || execution.Id != id || execution.Connector is not (1 or 2))
                {
                    continue;
                }

                if (execution.StartedAt < cutoff && !SafeguardStorage.IsRunning(execution, now))
                {
                    storage.DeleteExecutionHistory(id);

                    _historyCache.Remove(id);
                    _loggedStatus.Remove(id);

                    continue;
                }

                if (now - execution.StartedAt <= SafeguardTelemetryBuffer.MaximumCaptureAge && !File.Exists(storage.Location(SafeguardArea.Telemetry, id)))
                {
                    CaptureTelemetry(storage, id, execution.StartedAt);
                }

                var status = SafeguardStorage.DisplayStatus(execution, now);
                var notice = new SafeguardExecutionNotice(id, execution.Name, execution.Connector, execution.StartedAt, status, execution.ScriptId == SafeguardScripts.Simulation);
                notices.Add(notice);

                if (mobilePush is not null && !notice.IsSimulation && notice.StartedAt >= _startedAt && !_loggedStatus.ContainsKey(id))
                {
                    newExecutions.Add(notice);
                }

                ObserveExecution(execution, status);
            }
            catch (Exception error) when (IsRecordError(error))
            {
                if (_readFailures.Add(id))
                {
                    logger.LogWarning("Could not read Safeguard+ execution {ExecutionId} ({Type})", id, error.GetType().Name);
                }
            }
        }

        foreach (var id in _loggedStatus.Keys.Where(id => !retained.Contains(id)).ToArray())
        {
            _loggedStatus.Remove(id);
        }

        foreach (var id in _historyCache.Keys.Where(id => !retained.Contains(id)).ToArray())
        {
            _historyCache.Remove(id);
        }

        _readFailures.RemoveWhere(id => !retained.Contains(id));

        Volatile.Write(ref _notices, [.. notices.OrderBy(notice => notice.StartedAt).ThenBy(notice => notice.Id)]);
        foreach (var notice in newExecutions)
        {
            mobilePush?.SendSafeguard(notice.Id, notice.Connector, notice.StartedAt);
        }
        return Task.CompletedTask;
    }

    private sealed record CachedHistory(DateTime LastWrite, long Length, SafeguardExecution? Execution);

    private SafeguardExecution? ReadHistory(ISafeguardJournalStorage storage, Guid id)
    {
        var file = new FileInfo(storage.Location(SafeguardArea.History, id));
        var lastWrite = file.LastWriteTimeUtc;
        var length = file.Length;

        if (_historyCache.TryGetValue(id, out var cached) && cached.LastWrite == lastWrite && cached.Length == length)
        {
            return cached.Execution;
        }

        _historyCache.Remove(id);
        try
        {
            var record = storage.Read<SafeguardExecution>(SafeguardArea.History, id);

            if (record?.FinishedAt is not null)
            {
                // The notice only uses the summary. Full reports load their own steps on demand.
                _historyCache[id] = new(lastWrite, length, record with { Steps = [] });
            }

            return record;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            _historyCache[id] = new(lastWrite, length, null);
            throw;
        }
    }

    private void ObserveExecution(SafeguardExecution execution, string status)
    {
        var seen = _loggedStatus.TryGetValue(execution.Id, out var previous);
        if (seen && previous == status)
        {
            return;
        }

        _loggedStatus[execution.Id] = status;
        if (!seen && execution.StartedAt < _startedAt && execution.FinishedAt is { } finished && finished < _startedAt)
        {
            return;
        }

        var detail = $"Connector {execution.Connector}. Execution {execution.Id:N}.";
        if (!seen && execution.ScriptId != SafeguardScripts.Simulation)
        {
            logger.LogActivity(LogLevel.Warning, "safeguard.triggered", "Safeguard+",
                "An Afterburner Safeguard+ action was triggered.", detail, eventViewer: true);
        }

        switch (status)
        {
            case SafeguardStatus.Simulated:
                logger.LogActivity(LogLevel.Information, "safeguard.simulated", "Safeguard+", "Simulation completed.", detail);
                break;
            case SafeguardStatus.ShutdownRequested:
                logger.LogActivity(LogLevel.Information, "safeguard.shutdown-requested", "Safeguard+", "The action requested Windows shutdown.", detail);
                break;
            case SafeguardStatus.ShutdownRequestFailed:
            case SafeguardStatus.ShutdownRequestUnconfirmed:
            case SafeguardStatus.ScriptVerificationFailed:
            case SafeguardStatus.SimulationEmailQueueFailed:
            case SafeguardStatus.Interrupted:
                logger.LogActivity(LogLevel.Error, "safeguard.action-failed", "Safeguard+", status + ".", detail, eventViewer: true);
                break;
        }
    }

    [SupportedOSPlatform("windows")]
    private void CapturePendingTelemetry(ISafeguardJournalStorage storage, CancellationToken token)
    {
        foreach (var id in storage.Ids(SafeguardArea.Outbox))
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            try
            {
                if (!File.Exists(storage.Location(SafeguardArea.Telemetry, id)) && storage.Read<SafeguardMailEvent>(SafeguardArea.Outbox, id) is { } job && job.ExecutionId == id)
                {
                    CaptureTelemetry(storage, id, job.StartedAt);
                }
            }
            catch (Exception error) when (IsRecordError(error))
            {
                logger.LogWarning("Could not read the Safeguard+ mail job {ExecutionId} ({Type})", id, error.GetType().Name);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private SafeguardTelemetry? CaptureTelemetry(ISafeguardJournalStorage storage, Guid id, DateTimeOffset startedAt)
    {
        SafeguardTelemetry? frame = null;
        try
        {
            lock (_telemetryFiles)
            {
                var saved = storage.Read<SafeguardTelemetry>(SafeguardArea.Telemetry, id);
                if (saved is not null)
                {
                    return _telemetry.WithSources(saved);
                }

                frame = _telemetry.CaptureAt(startedAt);
                if (frame is not null && HasExecutionOrMail())
                {
                    storage.Write(SafeguardArea.Telemetry, id, frame);
                    if (!HasExecutionOrMail())
                    {
                        storage.Delete(SafeguardArea.Telemetry, id);
                    }
                }
            }
        }
        catch (Exception error) when (IsRecordError(error))
        {
            logger.LogWarning("Could not store or read Safeguard+ telemetry for {ExecutionId} ({Type})",
                id, error.GetType().Name);
        }

        return frame;

        bool HasExecutionOrMail() => File.Exists(storage.Location(SafeguardArea.History, id)) || File.Exists(storage.Location(SafeguardArea.Outbox, id));
    }

    [SupportedOSPlatform("windows")]
    internal async Task ProcessMailAsync(ISafeguardJournalStorage storage, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        foreach (var id in storage.Ids(SafeguardArea.Requests))
        {
            token.ThrowIfCancellationRequested();
            await HandleMailRequestAsync(storage, id, now, token);
        }

        foreach (var id in storage.Ids(SafeguardArea.Outbox))
        {
            token.ThrowIfCancellationRequested();
            await DeliverAlarmAsync(storage, id, now, token);
        }

        if (now < _nextMailCleanup)
        {
            return;
        }

        foreach (var id in storage.Ids(SafeguardArea.Results))
        {
            if (File.GetLastWriteTimeUtc(storage.Location(SafeguardArea.Results, id)) < now.UtcDateTime.AddHours(-1))
            {
                storage.Delete(SafeguardArea.Results, id);
            }
        }

        var cutoff = now.AddDays(-storage.RetentionDays);
        foreach (var id in storage.Ids(SafeguardArea.Delivery))
        {
            if (File.GetLastWriteTimeUtc(storage.Location(SafeguardArea.Delivery, id)) < cutoff.UtcDateTime
                && !File.Exists(storage.Location(SafeguardArea.History, id)) && !File.Exists(storage.Location(SafeguardArea.Outbox, id)))
            {
                storage.Delete(SafeguardArea.Delivery, id);
            }
        }
        _nextMailCleanup = now.AddMinutes(1);
    }

    [SupportedOSPlatform("windows")]
    private async Task HandleMailRequestAsync(ISafeguardJournalStorage storage, Guid id, DateTimeOffset now, CancellationToken token)
    {
        SafeguardMailResult result;

        try
        {
            var request = storage.Read<SafeguardMailRequest>(SafeguardArea.Requests, id);
            if (request is null)
            {
                return;
            }

            if (request.CreatedAt < now.AddMinutes(-2) || File.Exists(storage.Location(SafeguardArea.Results, id)))
            {
                storage.Delete(SafeguardArea.Requests, id);
                return;
            }

            var previous = request.PreviousMailId is { } previousId ? storage.Read<SafeguardMailCredential>(SafeguardArea.Mail, previousId) : null;

            string? encryptedPassword = null;

            if (request.Settings.Authenticate)
            {
                if (request.EncryptedInput is { } encryptedInput)
                {
                    var bytes = ProtectedData.Unprotect(Convert.FromBase64String(encryptedInput), null, DataProtectionScope.LocalMachine);
                    try
                    {
                        encryptedPassword = Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(bytes);
                    }
                }
                else if (previous?.Settings is { } old && string.Equals(old.Host, request.Settings.Host, StringComparison.OrdinalIgnoreCase)
                    && old.Username == request.Settings.Username)
                {
                    encryptedPassword = previous.EncryptedPassword;
                }
            }

            SafeguardScripts.ValidateMail(request.Settings, encryptedPassword is not null);
            var credentials = new SafeguardMailCredential(request.Settings, encryptedPassword);

            if (request.SendTest)
            {
                var preview = request.Preview;
                var script = SafeguardScripts.Get(preview?.ScriptId ?? SafeguardScripts.Notify);

                if (!script.SendsMail || (preview is not null && preview.Connector is not (1 or 2)))
                {
                    throw new ArgumentException("Select a Safeguard+ email action and connector.");
                }

                if (preview is not null)
                {
                    SafeguardScripts.ValidateDelay(preview.DelayMs);
                }

                var report = new SafeguardReport(id, script.Id, request.CreatedAt, preview?.Connector, preview?.DelayMs, "SMTP test", _telemetry.CaptureAt(request.CreatedAt))
                {
                    IsTestEmail = true,
                    ScriptVersion = SafeguardScriptDefinition.Version
                };

                await SendAsync(credentials, "RTX Lens Safeguard+ test email", CreateReportBody(report), null, token);

                result = new(true, "SMTP server accepted the test email.");
                logger.LogActivity(LogLevel.Information, "safeguard.email-test-accepted", "Safeguard+", result.Message);
            }
            else
            {
                storage.Write(SafeguardArea.Mail, id, credentials);
                result = new(true, "SMTP settings validated and saved.", id);
            }
        }
        catch (Exception error) when (!token.IsCancellationRequested && IsMailError(error))
        {
            result = new(false, DescribeMailError(error));
        }

        storage.Write(SafeguardArea.Results, id, result);
        storage.Delete(SafeguardArea.Requests, id);
    }

    [SupportedOSPlatform("windows")]
    private async Task DeliverAlarmAsync(ISafeguardJournalStorage storage, Guid id, DateTimeOffset now, CancellationToken token)
    {
        var attempt = 1;
        try
        {
            var delivery = storage.Read<SafeguardDelivery>(SafeguardArea.Delivery, id);
            if (delivery?.Status is SafeguardStatus.AcceptedBySmtpServer or SafeguardStatus.DeliveryExpired)
            {
                DeleteMailJob(storage, id);
                return;
            }

            var job = storage.Read<SafeguardMailEvent>(SafeguardArea.Outbox, id);
            if (job is null)
            {
                return;
            }

            attempt = (delivery?.Attempts ?? 0) + 1;

            if (job.StartedAt < now.AddDays(-7))
            {
                storage.Write(SafeguardArea.Delivery, id, new SafeguardDelivery(SafeguardStatus.DeliveryExpired, attempt - 1, now));

                logger.LogActivity(LogLevel.Error, "safeguard.email-expired", "Safeguard+",
                    "Email delivery expired after retrying.", $"Execution {id:N}.", eventViewer: true);

                DeleteMailJob(storage, id);
                return;
            }

            if (delivery?.NextAttempt > now)
            {
                return;
            }

            var binding = storage.Read<SafeguardBinding>(SafeguardArea.Bindings, job.BindingId);
            if (job.ExecutionId != id || binding is null || binding.Id != job.BindingId || binding.MailId is not { } mailId)
            {
                throw new InvalidDataException("The email request has no saved notification binding.");
            }

            binding.Validate();

            var script = SafeguardScripts.Get(binding.ScriptId);

            var mail = storage.Read<SafeguardMailCredential>(SafeguardArea.Mail, mailId) ?? throw new InvalidDataException("Saved SMTP configuration is unavailable.");
            var execution = storage.Read<SafeguardExecution>(SafeguardArea.History, id);
            var status = execution is null ? "Triggered" : SafeguardStorage.DisplayStatus(execution, now);

            var telemetry = CaptureTelemetry(storage, id, job.StartedAt);

            var report = execution is not null
                ? SafeguardReport.ForExecution(execution, status, telemetry)
                : new SafeguardReport(id, script.Id, job.StartedAt, binding.Connector, binding.DelayMs, status, telemetry) { ScriptVersion = SafeguardScriptDefinition.Version };

            var subject = script.IsSimulation ? "RTX Lens Safeguard+ simulation" : "RTX Lens Safeguard+ triggered";
            await SendAsync(mail, subject, CreateReportBody(report), id, token);

            storage.Write(SafeguardArea.Delivery, id, new SafeguardDelivery(SafeguardStatus.AcceptedBySmtpServer, attempt, now));

            logger.LogActivity(LogLevel.Information, "safeguard.email-accepted", "Safeguard+", "SMTP server accepted the Safeguard+ email.",
                $"Execution {id:N}.");

            if (telemetry is null)
            {
                logger.LogActivity(LogLevel.Warning, "safeguard.telemetry-missing", "Safeguard+",
                    "No sensor snapshot was available for the email report.", $"Execution {id:N}.");
            }

            DeleteMailJob(storage, id);
        }
        catch (Exception error) when (!token.IsCancellationRequested && IsMailError(error))
        {
            var wait = TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Min(attempt - 1, 6))));
            storage.Write(SafeguardArea.Delivery, id, new SafeguardDelivery("Retry pending", attempt, now + wait, DescribeMailError(error)));

            if (attempt == 1)
            {
                logger.LogActivity(LogLevel.Warning, "safeguard.email-retry", "Safeguard+",
                    "Safeguard+ email could not be sent. The service will retry.", $"Execution {id:N}.");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private void DeleteMailJob(ISafeguardJournalStorage storage, Guid id)
    {
        try
        {
            lock (_telemetryFiles)
            {
                storage.Delete(SafeguardArea.Outbox, id);
                if (!File.Exists(storage.Location(SafeguardArea.History, id)))
                {
                    storage.Delete(SafeguardArea.Telemetry, id);
                }
            }
        }
        catch (Exception error) when (IsRecordError(error))
        {
            logger.LogWarning("Could not clean up Safeguard+ mail job {ExecutionId} ({Type})", id, error.GetType().Name);
        }
    }

    internal static MimeEntity CreateReportBody(SafeguardReport report)
    {
        var (html, plain) = SafeguardReportRenderer.Render(report);
        return new BodyBuilder { TextBody = plain, HtmlBody = html }.ToMessageBody();
    }

    [SupportedOSPlatform("windows")]
    private static async Task SendAsync(SafeguardMailCredential mail, string subject, MimeEntity body, Guid? id, CancellationToken token)
    {
        var settings = mail.Settings;

        SafeguardScripts.ValidateMail(settings, mail.EncryptedPassword is not null);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));

        using var message = new MimeMessage();
        message.Subject = subject;
        message.Body = body;

        if (id is { } executionId)
        {
            message.MessageId = $"safeguard-{executionId:N}@rtxlens.com";
        }

        message.From.Add(MailboxAddress.Parse(settings.Sender));

        foreach (var recipient in settings.Recipients)
        {
            message.To.Add(MailboxAddress.Parse(recipient));
        }

        using var client = new SmtpClient();

        var mode = settings.Security == SafeguardMailMode.ImplicitTls ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
        await client.ConnectAsync(settings.Host, settings.Port, mode, deadline.Token);

        if (settings.Authenticate)
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(mail.EncryptedPassword!), null, DataProtectionScope.CurrentUser);
            try
            {
                await client.AuthenticateAsync(settings.Username, Encoding.UTF8.GetString(bytes), deadline.Token);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        await client.SendAsync(message, deadline.Token);
    }

    private static bool IsRecordError(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException;

    private static bool IsMailError(Exception error) => IsRecordError(error)
        || error is ArgumentException or FormatException or CryptographicException or OperationCanceledException
            or AuthenticationException or SslHandshakeException
            or SmtpCommandException or System.Net.Sockets.SocketException or InvalidOperationException;

    private static string DescribeMailError(Exception error) => error switch
    {
        AuthenticationException => "SMTP authentication failed. Check the username and password.",
        SslHandshakeException => "SMTP TLS negotiation failed. Check the server name, certificate and TLS mode.",
        SmtpCommandException smtp => "SMTP rejected the request (" + (int)smtp.StatusCode + ").",
        OperationCanceledException => "SMTP operation timed out.",
        CryptographicException => "The Windows service could not decrypt the saved SMTP password.",
        ArgumentException => "SMTP configuration is incomplete or invalid.",
        _ => "SMTP request failed (" + error.GetType().Name + ")."
    };
}
