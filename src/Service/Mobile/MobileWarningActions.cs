using RTXLens.Contracts.Mobile;
using RTXLens.Service.Configuration;
using RTXLens.Service.Services;

namespace RTXLens.Service.Mobile;

internal sealed class MobileWarningActions(MobilePairing pairing, WarningPowerService power, WarningShutdownCoordinator shutdown, DeviceWarningProfileStore profiles)
{
    public MobileWarningActionOptions GetOptions(Guid phone)
    {
        var (allowed, settings) = pairing.GetWarningActions(phone);
        if (!allowed)
        {
            return new(false, settings, [], []);
        }

        var targets = power.Targets();
        var configured = profiles.PowerReactionProfiles();

        return new(true, settings, targets,
            [.. configured.Select(profile => new MobileWarningPowerProfile(profile.Name, profile.PowerReduction!.GpuId, profile.PowerReduction.TargetMilliwatts))]);
    }

    public IResult Save(Guid phone, MobileWarningActionSettings settings)
    {
        if (!pairing.GetWarningActions(phone).Allowed)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (settings.GpuId is { Length: > 128 } || (settings.EnableLowPower && !power.Targets().Any(target =>
                target.Id == settings.GpuId && settings.PowerMilliwatts >= target.Minimum && settings.PowerMilliwatts <= target.Maximum)))
        {
            return Results.BadRequest(new MobileWarningActionResult("unavailable", Message: "Select an available GPU and a supported power limit."));
        }

        return pairing.SetWarningActions(phone, settings) ? Results.NoContent() : Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    public IResult Execute(Guid phone, MobileWarningActionRequest request)
    {
        if (request.InstanceId is not { Length: > 0 and <= 128 } || !Guid.TryParseExact(request.EpisodeId, "N", out _)
            || request.Action is not ("shutdown" or "low-power") || (request.StatusOnly && request.Action != "low-power"))
        {
            return Results.BadRequest();
        }

        var (allowed, settings) = pairing.GetWarningActions(phone);
        if (!allowed || (request.Action == "shutdown" && !settings.EnableShutdown) || (request.Action == "low-power" && !settings.EnableLowPower))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var warning = power.ActiveMobileWarning(request.InstanceId, request.EpisodeId);
        if (warning is null)
        {
            return Results.Ok(new MobileWarningActionResult("inactive", Message: "This warning is no longer active."));
        }

        if (request.Action == "low-power")
        {
            return Results.Ok(settings is not { GpuId: { } gpu, PowerMilliwatts: { } milliwatts }
                ? new MobileWarningActionResult("unavailable", Message: "Set a GPU power limit in Settings.")
                : power.RequestMobilePower(phone, request.InstanceId, request.EpisodeId!, gpu, milliwatts, request.StatusOnly));
        }

        var outcome = shutdown.RequestMobile(warning, () => pairing.GetWarningActions(phone) is { Allowed: true, Settings.EnableShutdown: true }
                                                            && power.ActiveMobileWarning(request.InstanceId, request.EpisodeId) is not null);

        if (outcome is ShutdownOutcome.Accepted or ShutdownOutcome.Unconfirmed)
        {
            return Results.Ok(new MobileWarningActionResult("requested"));
        }

        return Results.Ok(outcome is null
            ? new MobileWarningActionResult("inactive", Message: "Shutdown is no longer available.")
            : new MobileWarningActionResult("failed", Message: "Windows could not accept the shutdown request."));
    }
}
