using System.Threading.Channels;
using RTXLens.Contracts.Warnings;
using RTXLens.Core.Diagnostics;

namespace RTXLens.Service.Mobile;

internal sealed record MobilePushTarget(Guid Id, string Token, CancellationToken Revoked);

internal sealed record MobilePushAlert(string Title, Guid? IncidentId = null, string? IncidentKind = null,
    MobileWarningCategory? Category = null, string? PrivateTitle = null);

internal sealed class MobilePushDelivery(MobilePairing pairing, ExpoMobilePushProvider provider, TimeProvider clock,
    ILogger<MobilePushDelivery> logger) : BackgroundService
{
    private static readonly TimeSpan AlertLifetime = TimeSpan.FromMinutes(5);
    private readonly Channel<PendingAlert> _alerts = Bounded<PendingAlert>(128);
    private readonly Channel<PendingReceipt> _receipts = Bounded<PendingReceipt>(256);
    private readonly Lock _gate = new();
    private string? _instanceId;
    private long _sequence;
    private DateTimeOffset _nextTest;

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
                    var category = MobileNotificationPreferences.CategoryFor(warning.PluginId, warning.Icon);
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
        Task.WhenAll(DeliverAsync(stoppingToken), CheckReceiptsAsync(stoppingToken));

    private bool Enqueue(MobilePushAlert alert, MobilePushTarget[] targets, DateTimeOffset? startedAt = null)
    {
        var now = clock.GetUtcNow();
        var expiresAt = (startedAt ?? now) + AlertLifetime;
        if (targets.Length == 0 || expiresAt <= now)
        {
            return false;
        }

        var title = alert.Title.Length <= 160 ? alert.Title : alert.Title[..160];
        if (_alerts.Writer.TryWrite(new PendingAlert(alert with { Title = title }, targets, expiresAt)))
        {
            return true;
        }

        logger.LogActivity(LogLevel.Warning, "mobile.push-queue-full", "Mobile access", "The mobile notification queue is full.");
        return false;
    }

    private async Task DeliverAsync(CancellationToken stoppingToken)
    {
        await foreach (var pending in _alerts.Reader.ReadAllAsync(stoppingToken))
        {
            foreach (var target in pending.Targets)
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, target.Revoked);
                try
                {
                    await SendAsync(pending, target, cancellation.Token);
                }
                catch (OperationCanceledException) when (target.Revoked.IsCancellationRequested)
                {
                    // A revoked pairing cannot receive new notifications.
                }
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
                _receipts.Writer.TryWrite(new PendingReceipt(target, ticketId, clock.GetUtcNow().AddMinutes(15)));
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

    private sealed record PendingAlert(MobilePushAlert Alert, MobilePushTarget[] Targets, DateTimeOffset ExpiresAt);
    private sealed record PendingReceipt(MobilePushTarget Target, string TicketId, DateTimeOffset CheckAt);
}
