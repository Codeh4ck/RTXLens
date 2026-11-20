using RTXLens.Core.Plugins;
using System.Threading.Channels;
using RTXLens.Contracts.Warnings;
using RTXLens.Core.Diagnostics;

namespace RTXLens.Service.Mobile;

internal sealed record MobilePushTarget(Guid Id, string Token, CancellationToken Revoked);

internal sealed record MobilePushAlert(string Title, Guid? IncidentId = null, string? IncidentKind = null,
    MobileWarningCategory? Category = null, string? PrivateTitle = null);

internal sealed class MobilePushDelivery(MobilePairing pairing, ExpoMobilePushProvider provider, TimeProvider clock,
    ILogger<MobilePushDelivery> logger, HardwareCatalog? hardware = null) : BackgroundService
{
    private static readonly TimeSpan AlertLifetime = TimeSpan.FromMinutes(5);
    private readonly Channel<RecipientQueue> _newRecipients = Bounded<RecipientQueue>(MobilePairing.DeviceLimit);
    private readonly Dictionary<Guid, RecipientQueue> _recipients = new();
    private readonly Channel<PendingReceipt> _receipts = Bounded<PendingReceipt>(256);
    private readonly Lock _gate = new();
    private string? _instanceId;
    private long _sequence;
    private DateTimeOffset _nextTest;
    private long _missedReceipts;

    public void Observe(WarningSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_instanceId != snapshot.InstanceId)
            {
                _instanceId = snapshot.InstanceId;
                _sequence = 0;
            }

            var previous = _sequence;
            _sequence = Math.Max(_sequence, snapshot.Sequence);
            if (!snapshot.NotificationsEnabled || _sequence == previous)
            {
                return;
            }

            var targets = pairing.GetPushTargets();
            foreach (var warning in snapshot.States)
            {
                if (warning.Phase == WarningPhase.Active && warning.NotificationSequence > previous
                    && Guid.TryParseExact(warning.EpisodeId, "N", out var incidentId))
                {
                    var category = MobileNotificationPreferences.CategoryFor(hardware?.Find(warning.PluginId), warning.Icon);
                    Enqueue(new MobilePushAlert(warning.Title, incidentId, "warning", category), targets);
                }
            }
        }
    }

    public void SendSimulation(string title) =>
        Enqueue(new MobilePushAlert("Simulation · " + title, PrivateTitle: "RTX Lens simulation"), pairing.GetPushTargets());

    public void SendSafeguard(Guid id, int connector, DateTimeOffset startedAt) => Enqueue(
        new MobilePushAlert($"Safeguard+ triggered on connector {connector}", id, "safeguard", MobileWarningCategory.Safeguard),
        pairing.GetPushTargets(), startedAt);

    public bool SendTest(Guid id)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            if (now < _nextTest || !Enqueue(new MobilePushAlert("RTX Lens test notification"), pairing.GetPushTargets(id)))
            {
                return false;
            }

            _nextTest = now.AddSeconds(15);
            return true;
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Parallel.ForEachAsync(_newRecipients.Reader.ReadAllAsync(stoppingToken),
            new ParallelOptions { MaxDegreeOfParallelism = MobilePairing.DeviceLimit, CancellationToken = stoppingToken },
            DeliverToRecipientAsync), CheckReceiptsAsync(stoppingToken));

    private bool Enqueue(MobilePushAlert alert, MobilePushTarget[] targets, DateTimeOffset? startedAt = null)
    {
        var now = clock.GetUtcNow();
        var expiresAt = (startedAt ?? now) + AlertLifetime;
        if (targets.Length == 0 || expiresAt <= now)
        {
            return false;
        }

        var title = alert.Title.Length <= 160 ? alert.Title : alert.Title[..160];
        alert = alert with { Title = title };
        var queued = 0;
        var missed = 0;
        lock (_gate)
        {
            foreach (var target in targets)
            {
                if (target.Revoked.IsCancellationRequested)
                {
                    continue;
                }

                if (!_recipients.TryGetValue(target.Id, out var recipient))
                {
                    recipient = new RecipientQueue(target.Id, target.Revoked, Bounded<PendingAlert>(128));
                    if (!_newRecipients.Writer.TryWrite(recipient))
                    {
                        missed++;
                        continue;
                    }
                    _recipients.Add(target.Id, recipient);
                }

                if (recipient.Alerts.Writer.TryWrite(new PendingAlert(alert, target, expiresAt)))
                {
                    queued++;
                }
                else
                {
                    missed++;
                }
            }
        }
        if (missed > 0)
        {
            logger.LogActivity(LogLevel.Warning, "mobile.push-queue-full", "Mobile access",
                "The mobile notification queue is full.", $"Skipped recipients: {missed}.");
        }
        return queued > 0;
    }

    private async ValueTask DeliverToRecipientAsync(RecipientQueue recipient, CancellationToken stoppingToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, recipient.Revoked);
        try
        {
            await foreach (var pending in recipient.Alerts.Reader.ReadAllAsync(cancellation.Token))
            {
                await SendAsync(pending, pending.Target, cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Revocation drops this recipient's pending notifications.
        }
        finally
        {
            lock (_gate)
            {
                _recipients.Remove(recipient.Id);
                recipient.Alerts.Writer.TryComplete();
            }
        }
    }

    private async Task SendAsync(PendingAlert pending, MobilePushTarget target, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (clock.GetUtcNow() >= pending.ExpiresAt || pairing.GetPushPreferences(target) is not { } preferences
                || pending.Alert.Category is { } category && !preferences.Allows(category))
            {
                return;
            }

            var alert = pending.Alert;
            if (!preferences.ShowTitle)
            {
                alert = alert with { Title = alert.PrivateTitle ?? (alert.IncidentId is null ? alert.Title : "RTX Lens warning") };
            }

            var result = await provider.SendAsync(target.Token, alert, cancellationToken);
            if (result is { Outcome: MobilePushOutcome.Accepted, TicketId: { } ticketId })
            {
                QueueReceipt(new PendingReceipt(target, ticketId, clock.GetUtcNow().AddMinutes(15)));
                logger.LogActivity(LogLevel.Information, "mobile.push-accepted", "Mobile access", "Expo accepted a mobile notification.");
                return;
            }
            if (result.Outcome != MobilePushOutcome.Retry || attempt == 2)
            {
                HandleOutcome(target, result.Outcome);
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2 << attempt), clock, cancellationToken);
        }
    }

    private void QueueReceipt(PendingReceipt receipt)
    {
        lock (_gate)
        {
            if (!_receipts.Writer.TryWrite(receipt))
            {
                _missedReceipts++;
                if ((_missedReceipts & (_missedReceipts - 1)) == 0)
                {
                    logger.LogActivity(LogLevel.Warning, "mobile.push-receipt-queue-full", "Mobile access",
                        "The mobile receipt queue is full.", $"Skipped checks since saturation: {_missedReceipts}.");
                }
            }
            else if (_missedReceipts > 0)
            {
                logger.LogActivity(LogLevel.Information, "mobile.push-receipt-queue-recovered", "Mobile access",
                    "The mobile receipt queue has space again.", $"Skipped checks: {_missedReceipts}.");
                _missedReceipts = 0;
            }
        }
    }

    private async Task CheckReceiptsAsync(CancellationToken stoppingToken)
    {
        await foreach (var receipt in _receipts.Reader.ReadAllAsync(stoppingToken))
        {
            var delay = receipt.CheckAt - clock.GetUtcNow();
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, clock, stoppingToken);
            }
            if (pairing.GetPushPreferences(receipt.Target) is not null)
            {
                var result = await provider.CheckAsync(receipt.TicketId, stoppingToken);
                HandleOutcome(receipt.Target, result.Outcome);
            }
        }
    }

    private void HandleOutcome(MobilePushTarget target, MobilePushOutcome outcome)
    {
        if (outcome == MobilePushOutcome.Unregistered)
        {
            try
            {
                pairing.SetPushToken(target.Id, null, expected: target.Token);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                logger.LogActivity(LogLevel.Warning, "mobile.push-cleanup-failed", "Mobile access",
                    "Could not clear an expired mobile notification registration.");
            }
            return;
        }

        if (outcome is MobilePushOutcome.InvalidCredentials or MobilePushOutcome.Rejected or MobilePushOutcome.Retry)
        {
            var detail = outcome == MobilePushOutcome.InvalidCredentials
                ? "Check the Apple or Firebase push credentials configured for this build." : null;
            logger.LogActivity(LogLevel.Warning, "mobile.push-delivery-failed", "Mobile access", "A mobile notification could not be delivered.", detail);
        }
    }

    private static Channel<T> Bounded<T>(int capacity) =>
        Channel.CreateBounded<T>(new BoundedChannelOptions(capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    private sealed record RecipientQueue(Guid Id, CancellationToken Revoked, Channel<PendingAlert> Alerts);
    private sealed record PendingAlert(MobilePushAlert Alert, MobilePushTarget Target, DateTimeOffset ExpiresAt);
    private sealed record PendingReceipt(MobilePushTarget Target, string TicketId, DateTimeOffset CheckAt);
}
