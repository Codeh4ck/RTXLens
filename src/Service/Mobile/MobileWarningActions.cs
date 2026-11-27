using RTXLens.Contracts.Mobile;
using RTXLens.Service.Configuration;
using RTXLens.Service.Services;

namespace RTXLens.Service.Mobile;

internal sealed class MobileWarningActions(MobilePairing pairing, WarningPowerService power, WarningShutdownCoordinator shutdown, DeviceWarningProfileStore profiles)
{
    public MobileWarningActionAvailability GetAvailability(Guid phone)
    {
        var settings = pairing.GetWarningActions(phone);
        return new(true, settings?.EnableShutdown == true, settings?.EnableLowPower == true, settings?.GpuId, settings?.PowerMilliwatts);
    }

    public MobileAccessResponse GetDesktopOptions(Guid phone)
    {
        if (pairing.GetWarningActions(phone, requireListening: false) is not { } settings)
        {
            return new(false, Error: "This phone is no longer paired.");
        }

        var options = new MobileWarningActionOptions(settings, power.Targets(),
            [.. profiles.PowerReactionProfiles().Select(profile =>
                new MobileWarningPowerProfile(profile.Name, profile.PowerReduction!.GpuId, profile.PowerReduction.TargetMilliwatts))]);
        return new(true, WarningActions: options);
    }

    public MobileAccessResponse ConfigureDesktop(Guid phone, MobileWarningActionSettings settings)
    {
        if (pairing.GetWarningActions(phone, requireListening: false) is not { } previous)
        {
            return new(false, Error: "This phone is no longer paired.");
        }

        var keepsPreviousPower = previous.EnableLowPower && previous.GpuId == settings.GpuId
            && previous.PowerMilliwatts == settings.PowerMilliwatts;
        if (settings.GpuId is { Length: > 128 } || (settings.EnableLowPower && !keepsPreviousPower && !power.Targets().Any(target =>
                target.Id == settings.GpuId && settings.PowerMilliwatts >= target.Minimum && settings.PowerMilliwatts <= target.Maximum)))
        {
            return new(false, Error: "Select an available GPU and a supported power limit.");
        }

        return pairing.SetWarningActions(phone, settings)
            ? new(true)
            : new(false, Error: "This phone is no longer paired.");
    }

    public IResult Execute(Guid phone, MobileWarningActionRequest request)
    {
        if (request.InstanceId is not { Length: > 0 and <= 128 } || !Guid.TryParseExact(request.EpisodeId, "N", out _)
            || request.Action is not ("shutdown" or "low-power") || (request.StatusOnly && request.Action != "low-power"))
        {
            return Results.BadRequest();
        }

        if (pairing.GetWarningActions(phone) is not { } settings
            || (request.Action == "shutdown" ? !settings.EnableShutdown : !settings.EnableLowPower))
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
                ? new MobileWarningActionResult("unavailable", Message: "Set this phone’s GPU power limit in RTX Lens on the PC.")
                : power.RequestMobilePower(phone, request.InstanceId, request.EpisodeId!, gpu, milliwatts, request.StatusOnly));
        }

        var outcome = shutdown.RequestMobile(warning, () => pairing.GetWarningActions(phone) is { EnableShutdown: true }
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
