namespace RTXLens.Plugins.WireView.Contracts.Common;

public static class WireViewSettingsValidation
{
    public const int MinimumWritableFirmware = 3;
    public const int MaximumPercentage = 100;
    public const int MaximumDeviceNameLength = 32;
    public const double MinimumTemperatureC = short.MinValue / 10d;
    public const double MaximumTemperatureC = short.MaxValue / 10d;
    public const double MaximumPinCurrentA = byte.MaxValue / 10d;

    private const ushort KnownFaults = 0x3F;
    private const byte KnownScreens = 0x1F;

    public static int AveragingOptionCount(int layout, int? firmware) =>
        layout is 1 or 2 ? firmware >= 5 ? DeviceConfig.AveragingOptions.Count : 7 : 0;

    public static bool IsNumberValid(double value, double minimum, double maximum, int decimalPlaces) =>
        double.IsFinite(value) && value >= minimum && value <= maximum && value == Math.Round(value, decimalPlaces);

    public static void Validate(DeviceConfig current, DeviceConfig changes, int firmware)
    {
        if (firmware < MinimumWritableFirmware || current.Version is < 0 or > 2
            || current.Version != changes.Version || (changes.FirmwareVersion is { } requested && requested != firmware)
            || current.Display is not { } before || changes.Display is not { } display)
        {
            throw new ArgumentException("These settings do not match the detected WireView firmware and configuration layout.");
        }

        if (changes.FriendlyName != current.FriendlyName && (changes.FriendlyName is null
            || changes.FriendlyName.Length > MaximumDeviceNameLength || changes.FriendlyName.Any(c => c is < ' ' or > '~')))
        {
            throw new ArgumentException("Use up to 32 printable ASCII characters for the device name.");
        }

        if (changes.Averaging != current.Averaging && !DeviceConfig.AveragingOptions
                .Take(AveragingOptionCount(current.Version, firmware)).Contains(changes.Averaging))
        {
            throw new ArgumentException("This firmware does not support the selected averaging mode.");
        }

        if (changes.FanMode != current.FanMode && changes.FanMode is not ("Curve" or "Fixed"))
        {
            throw new ArgumentException("Select a valid fan mode.");
        }

        if (changes.FanTempSource != current.FanTempSource && changes.FanTempSource is not ("TsIn" or "TsOut" or "Ts1" or "Ts2" or "Tmax"))
        {
            throw new ArgumentException("Select a valid fan temperature source.");
        }

        ValidateNumber(current.BacklightDuty, changes.BacklightDuty, 0, MaximumPercentage, 0, "Display brightness");
        ValidateNumber(current.FanDutyMin, changes.FanDutyMin, 0, MaximumPercentage, 0, "Minimum fan duty");
        ValidateNumber(current.FanDutyMax, changes.FanDutyMax, 0, MaximumPercentage, 0, "Maximum fan duty");
        ValidateNumber(current.FanTempMinC, changes.FanTempMinC, MinimumTemperatureC, MaximumTemperatureC, 1, "Minimum fan temperature");
        ValidateNumber(current.FanTempMaxC, changes.FanTempMaxC, MinimumTemperatureC, MaximumTemperatureC, 1, "Maximum fan temperature");
        ValidateNumber(current.TsFaultThresholdC, changes.TsFaultThresholdC, MinimumTemperatureC, MaximumTemperatureC, 1, "Overtemperature threshold");
        ValidateNumber(current.WireOcpFaultThresholdA, changes.WireOcpFaultThresholdA, 0, MaximumPinCurrentA, 1, "Per-pin overcurrent threshold");
        ValidateNumber(current.CurrentImbalanceThreshold, changes.CurrentImbalanceThreshold, 0, MaximumPercentage, 0, "Current imbalance threshold");
        // Other numeric fields use byte or ushort in DeviceConfig. JSON deserialization enforces their full ranges.

        if (changes.FanMode == "Curve" && (changes.FanMode != current.FanMode || changes.FanDutyMin != current.FanDutyMin
            || changes.FanDutyMax != current.FanDutyMax || changes.FanTempMinC != current.FanTempMinC || changes.FanTempMaxC != current.FanTempMaxC)
            && (changes.FanDutyMin > changes.FanDutyMax || changes.FanTempMinC >= changes.FanTempMaxC))
        {
            throw new ArgumentException("For a fan curve, minimum duty must not exceed maximum duty, and minimum temperature must be below maximum temperature.");
        }

        if ((((changes.FaultDisplayEnable ^ current.FaultDisplayEnable) | (changes.FaultBuzzerEnable ^ current.FaultBuzzerEnable)
            | (changes.FaultSoftPowerEnable ^ current.FaultSoftPowerEnable) | (changes.FaultHardPowerEnable ^ current.FaultHardPowerEnable)) & ~KnownFaults) != 0)
        {
            throw new ArgumentException("Unrecognized WireView fault flags cannot be changed.");
        }

        ValidateNumber(before.CurrentScale, display.CurrentScale, 0, 3, 0, "Current scale");
        ValidateNumber(before.PowerScale, display.PowerScale, 0, 2, 0, "Power scale");
        ValidateNumber(before.Rotation, display.Rotation, 0, 1, 0, "Rotation");
        ValidateNumber(before.TimeoutMode, display.TimeoutMode, 0, 2, 0, "Timeout action");

        if (((display.CycleScreens ^ before.CycleScreens) & ~KnownScreens) != 0)
        {
            throw new ArgumentException("Unrecognized screen flags cannot be changed.");
        }

        if (current.Version < 2)
        {
            if (display.Theme is not { } theme || display.DefaultScreen is not null || display.PrimaryColor is not null
                || display.SecondaryColor is not null || display.HighlightColor is not null || display.BackgroundColor is not null
                || display.BackgroundBitmapId is not null || display.FanBitmapId is not null || display.Inversion is not null)
            {
                throw new ArgumentException("This firmware uses the legacy display layout.");
            }

            ValidateNumber(before.Theme.GetValueOrDefault(), theme, 0, 2, 0, "Theme");
            return;
        }

        if (display.Theme is not null || display.DefaultScreen is not { } screen || display.PrimaryColor is null
            || display.SecondaryColor is null || display.HighlightColor is null || display.BackgroundColor is null
            || display.BackgroundBitmapId is not { } background || display.FanBitmapId is not { } fan || display.Inversion is not { } inversion)
        {
            throw new ArgumentException("This firmware requires the color and graphics display settings.");
        }

        ValidateNumber(before.DefaultScreen.GetValueOrDefault(), screen, 0, 4, 0, "Startup screen");
        ValidateNumber(before.Inversion.GetValueOrDefault(), inversion, 0, 1, 0, "Invert display");

        if (background != before.BackgroundBitmapId && background is not (1 or 2 or 255))
        {
            throw new ArgumentException("Select a supported background image.");
        }

        if (fan != before.FanBitmapId && fan is not (0x64 or 0x75 or 0x98))
        {
            throw new ArgumentException("Select a supported fan graphic.");
        }
    }

    private static void ValidateNumber(double previous, double value, double minimum, double maximum, int decimalPlaces, string label)
    {
        // An unrecognized value already on the device can be preserved, but never entered as a new value.
        if (value != previous && !IsNumberValid(value, minimum, maximum, decimalPlaces))
        {
            throw new ArgumentException($"{label} must be between {minimum} and {maximum}, with "
                + (decimalPlaces == 0 ? "no decimal places." : "at most one decimal place."));
        }
    }
}

