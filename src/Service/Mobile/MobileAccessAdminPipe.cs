using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using RTXLens.Core.Diagnostics;
using RTXLens.Contracts.Mobile;

namespace RTXLens.Service.Mobile;

[SupportedOSPlatform("windows")]
internal sealed class MobileAccessAdminPipe(string pipeName, MobileAccessService service, MobilePairing pairing,
    MobileWarningActions warningActions, ILogger<MobileAccessAdminPipe> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var security = CreateSecurity();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);

                while (!stoppingToken.IsCancellationRequested)
                {
                    await pipe.WaitForConnectionAsync(stoppingToken);
                    await ServeAsync(pipe, stoppingToken);

                    pipe.Disconnect();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                logger.LogActivity(LogLevel.Warning, "mobile.admin-pipe-unavailable", "Mobile access",
                    "The mobile access setup channel is unavailable. The service will retry.");

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ServeAsync(Stream pipe, CancellationToken stoppingToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));

        var header = new byte[sizeof(int)];
        byte[]? contents = null;

        try
        {
            await pipe.ReadExactlyAsync(header, deadline.Token);

            var size = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (size is <= 0 or > MobileAccessProtocol.MaxRequestSize)
            {
                return;
            }

            contents = new byte[size];
            await pipe.ReadExactlyAsync(contents, deadline.Token);

            var request = JsonSerializer.Deserialize<MobileAccessRequest>(contents, MobileAccessProtocol.Json);

            var response = request is null
                ? new MobileAccessResponse(false, Error: "A mobile access command is required.")
                : await HandleAsync(request, stoppingToken);

            var reply = JsonSerializer.SerializeToUtf8Bytes(response, MobileAccessProtocol.Json);
            try
            {
                BinaryPrimitives.WriteInt32LittleEndian(header, reply.Length);

                await pipe.WriteAsync(header, deadline.Token);
                await pipe.WriteAsync(reply, deadline.Token);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(reply);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or JsonException)
        {
        }
        finally
        {
            if (contents is not null)
            {
                CryptographicOperations.ZeroMemory(contents);
            }
        }
    }

    private async Task<MobileAccessResponse> HandleAsync(MobileAccessRequest request, CancellationToken cancellationToken)
    {
        try
        {
            switch (request)
            {
                case { Operation: MobileAccessOperation.Status }:
                    return new MobileAccessResponse(true, Status: await service.GetStatusAsync(cancellationToken));

                case { Operation: MobileAccessOperation.Configure, Settings: { } settings }:
                    var status = await service.ConfigureAsync(settings, cancellationToken, request.KeepNoIpCredentials);
                    return new MobileAccessResponse(status.Error is null, Status: status, Error: status.Error);

                case { Operation: MobileAccessOperation.CreateInvite }:
                    var invite = await service.CreateInviteAsync(cancellationToken);
                    return invite is null
                        ? new MobileAccessResponse(false, Error: "Mobile access is off or the phone limit has been reached.")
                        : new MobileAccessResponse(true, Invite: invite);

                case { Operation: MobileAccessOperation.Approve, Id: { } approvalId } when pairing.Approve(approvalId):
                case { Operation: MobileAccessOperation.Reject, Id: { } rejectionId } when pairing.Reject(rejectionId):
                case { Operation: MobileAccessOperation.Revoke, Id: { } deviceId } when pairing.Revoke(deviceId):
                    return new MobileAccessResponse(true);

                case { Operation: MobileAccessOperation.GetWarningActions, Id: { } optionsId }:
                    return warningActions.GetDesktopOptions(optionsId);

                case { Operation: MobileAccessOperation.ConfigureWarningActions, Id: { } settingsId, WarningActions: { } actions }:
                    return warningActions.ConfigureDesktop(settingsId, actions);

                case { Operation: MobileAccessOperation.PublishSimulationWarning, SimulationWarning: { IsValid: true } warning }:
                    var published = await service.PublishSimulationWarningAsync(warning, cancellationToken);
                    return new MobileAccessResponse(published, Error: published ? null : "Simulation warnings require the Debug mobile listener.");

                case { Operation: MobileAccessOperation.SendTestNotification, Id: { } phoneId }:
                    var queued = service.SendTestNotification(phoneId);
                    return new MobileAccessResponse(queued, Error: queued ? null : "Enable lock-screen notifications on this phone and wait before retrying.");

                default:
                    return new MobileAccessResponse(false, Error: "The command or phone identifier is invalid or expired.");
            }
        }
        catch (ArgumentException)
        {
            return new MobileAccessResponse(false, Error: "The mobile access settings are invalid.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException)
        {
            logger.LogActivity(LogLevel.Warning, "mobile.admin-command-failed", "Mobile access", "A mobile access setup command failed.");
            return new MobileAccessResponse(false, Error: "The service could not complete the mobile access command.");
        }
    }

    private static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        using var identity = WindowsIdentity.GetCurrent();
        security.SetOwner(identity.User!);

        var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        security.AddAccessRule(new PipeAccessRule(network, PipeAccessRights.FullControl, AccessControlType.Deny));

        foreach (var type in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
        {
            var sid = new SecurityIdentifier(type, null);
            security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }
}
