using Microsoft.Extensions.Logging;
using RTXLens.Core.Diagnostics;
using RTXLens.Plugins.Msi.Contracts.Configuration;
using RTXLens.Plugins.Msi.Protocol;

namespace RTXLens.Plugins.Msi.Control;

internal sealed class MsiFanControlSession(ILogger logger)
{
    internal const string ActivitySource = "MSI MPG Ai1300TS / Ai1600TS";
    private readonly MsiFanCurveController _curve = new();
    private bool _ownsFan;
    private bool _curveInitialized;
    private MsiFanControl _appliedControl;
    private int _appliedDuty;
    private bool _restoreFailed;

    public int? TargetDuty { get; private set; }
    public string? Failure { get; private set; }
    public string State(MsiOptions options) => Failure is null ? options.FanControl.ToString() : "Automatic fallback";

    public void Update(MsiOptions options, double temperatureC, DateTimeOffset now, Func<byte[], int, byte[]> exchange)
    {
        var mode = Failure is null ? options.FanControl : MsiFanControl.Automatic;

        if (mode == MsiFanControl.Unmanaged)
        {
            Release(exchange);
            return;
        }

        if (mode == MsiFanControl.Curve && !_curveInitialized)
        {
            InitializeCurve(exchange);
        }

        var duty = mode switch
        {
            MsiFanControl.Curve => _curve.Evaluate(options.FanCurve, temperatureC, now),
            MsiFanControl.Manual => options.FanDutyPercent,
            _ => 0
        };

        duty = MsiFanCurve.EffectiveDutyPercent(duty);

        TargetDuty = mode is MsiFanControl.Curve or MsiFanControl.Manual ? duty : null;
        var hardwareMode = mode == MsiFanControl.Automatic || duty == 0
            ? MsiFanControl.Automatic : MsiFanControl.Manual;

        // Check an owned policy before sending a new request, so another controller is not
        // silently overwritten just because the temperature changed on this tick.
        if (_ownsFan)
        {
            Verify(_appliedControl, _appliedDuty, exchange);
        }

        if (!_ownsFan || _appliedControl != hardwareMode || _appliedDuty != duty)
        {
            var returningToAutomatic = mode == MsiFanControl.Curve && _ownsFan && _appliedControl == MsiFanControl.Manual
                && hardwareMode == MsiFanControl.Automatic;
            _ownsFan = true;

            exchange(MsiProtocol.FanRequest(hardwareMode == MsiFanControl.Automatic, duty), 1);

            _appliedControl = hardwareMode;
            _appliedDuty = duty;

            Verify(hardwareMode, duty, exchange);
            if (hardwareMode == MsiFanControl.Automatic)
            {
                LogAutomaticRecovery();
            }

            if (returningToAutomatic)
            {
                logger.LogActivity(LogLevel.Information, "msi.fan-automatic", ActivitySource,
                    "The fan curve handed fan control back to the PSU.");
            }
        }
    }

    public void Fail(Exception exception, Func<byte[], int, byte[]> exchange)
    {
        var firstFailure = Failure is null;
        Failure ??= exception.Message;
        TargetDuty = null;

        _curve.Reset();
        _curveInitialized = false;

        try
        {
            RestoreAutomatic(exchange);
            if (firstFailure && exception is not OperationCanceledException)
            {
                logger.LogActivity(LogLevel.Warning, "msi.fan-fallback", ActivitySource,
                    "Fan control encountered an error. The PSU is back in Automatic mode.", exception.GetType().Name,
                    eventViewer: true);
            }
        }
        catch (Exception restoreError)
        {
            LogRestoreFailure(restoreError);
        }
    }

    internal void LogRestoreFailure(Exception error)
    {
        if (!_restoreFailed)
        {
            _restoreFailed = true;
            logger.LogActivity(LogLevel.Error, "msi.fan-restore-failed", ActivitySource,
                "Could not restore Automatic PSU fan control.", eventViewer: true, exception: error);
        }
    }

    public void Release(Func<byte[], int, byte[]> exchange)
    {
        if (_ownsFan)
        {
            RestoreAutomatic(exchange);
        }

        TargetDuty = null;
        _curve.Reset();
        _curveInitialized = false;
    }

    public void ResetFailure()
    {
        Failure = null;
        TargetDuty = null;
        _curve.Reset();
        _curveInitialized = false;
    }

    public void Disconnected()
    {
        _ownsFan = false;
        _appliedControl = MsiFanControl.Unmanaged;
        TargetDuty = null;
        _curve.Reset();
        _curveInitialized = false;
    }

    private void InitializeCurve(Func<byte[], int, byte[]> exchange)
    {
        var mode = exchange(MsiProtocol.ReadRequest(0x41), 1)[3];
        var duty = exchange(MsiProtocol.ReadRequest(0x42), 4)[3];

        if (mode is not (1 or 3) || duty > 100)
        {
            throw new IOException("Unknown MSI fan mode or duty response.");
        }

        _curve.Reset(mode == 1 ? 0 : duty);
        _curveInitialized = true;
    }

    private void RestoreAutomatic(Func<byte[], int, byte[]> exchange)
    {
        _ownsFan = true;

        exchange(MsiProtocol.FanRequest(true, 0), 1);
        Verify(MsiFanControl.Automatic, 0, exchange);

        _ownsFan = false;
        _appliedControl = MsiFanControl.Unmanaged;
        LogAutomaticRecovery();
    }

    private void LogAutomaticRecovery()
    {
        if (_restoreFailed)
        {
            _restoreFailed = false;
            logger.LogActivity(LogLevel.Information, "msi.fan-automatic-restored", ActivitySource, "Automatic PSU fan control was restored.");
        }
    }

    private static void Verify(MsiFanControl control, int duty, Func<byte[], int, byte[]> exchange)
    {
        var reportedMode = exchange(MsiProtocol.ReadRequest(0x41), 1)[3];
        var reportedDuty = exchange(MsiProtocol.ReadRequest(0x42), 4)[3];

        var automatic = control == MsiFanControl.Automatic;
        var expectedMode = automatic ? 1 : 3;

        // Automatic hands duty selection to the PSU, which can report a nonzero duty.
        // Manual control must still match the exact requested percentage.
        if (reportedMode != expectedMode || reportedDuty > 100 || (!automatic && reportedDuty != duty))
        {
            var expected = automatic ? "Automatic mode (1)" : $"Manual mode (3), duty {duty}%";

            throw new IOException($"MSI fan readback mismatch: expected {expected}; received mode {reportedMode}, duty {reportedDuty}%. "
                + "Another controller or a firmware override may be active. Select Automatic before retrying the curve.");
        }
    }
}
