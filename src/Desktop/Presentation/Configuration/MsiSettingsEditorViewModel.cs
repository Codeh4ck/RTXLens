using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using RTXLens.Plugins.Msi.Contracts.Configuration;
using RTXLens.Plugins.Msi.Contracts.Devices;
using RTXLens.Presentation.Infrastructure;

namespace RTXLens.Presentation.Configuration;

public sealed class MsiSettingsEditorViewModel : EditableObject
{
    private PsuChoice _selectedDevice;
    private bool _refreshingDevices;
    private int _fanMode;
    private double _fanDuty;
    private double _responseDelay;

    public ObservableCollection<MsiFanCurvePointViewModel> CurvePoints { get; } = [];
    public double ResponseDelaySeconds { get => _responseDelay; set => Edit(ref _responseDelay, value); }
    public bool CanAddCurvePoint => CurvePoints.Count < 6;
    public bool CanRemoveCurvePoint => CurvePoints.Count > 2;
    public bool IsCurve => FanModeIndex == (int)MsiFanControl.Curve;

    public ObservableCollection<PsuChoice> Devices { get; } = [];

    [AllowNull]
    public PsuChoice SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            // ComboBox clears selection while its items are being refreshed.
            if (value is not null && !_refreshingDevices)
            {
                Edit(ref _selectedDevice, value);
            }
        }
    }

    public int FanModeIndex
    {
        get => _fanMode;
        set
        {
            if (value < 0)
            {
                return;
            }

            if (_fanMode == value)
            {
                return;
            }

            Edit(ref _fanMode, value);
            Raise(nameof(IsManual));
            Raise(nameof(IsCurve));
        }
    }

    public bool IsManual => FanModeIndex == (int)MsiFanControl.Manual;
    public double FanDutyPercent { get => _fanDuty; set => Edit(ref _fanDuty, value); }

    public MsiSettingsEditorViewModel(MsiOptions options)
    {
        _fanMode = (int)options.FanControl;
        _fanDuty = options.FanDutyPercent;
        _responseDelay = options.FanCurve.ResponseDelaySeconds;

        foreach (var point in options.FanCurve.Points)
        {
            var editor = new MsiFanCurvePointViewModel(point.TemperatureC, point.DutyPercent);
            editor.Edited += NotifyEdited;
            CurvePoints.Add(editor);
        }
        CurvePoints.CollectionChanged += (_, _) =>
        {
            Raise(nameof(CanAddCurvePoint));
            Raise(nameof(CanRemoveCurvePoint));
            NotifyEdited();
        };

        _selectedDevice = new PsuChoice(options.DevicePath, options.DevicePath ?? "Automatic · single connected MPG TS PSU");
        SetDevices([]);
    }

    public void SetDevices(MsiDeviceInfo[] devices)
    {
        var id = _selectedDevice.Id;
        _refreshingDevices = true;
        try
        {
            Devices.Clear();
            Devices.Add(new PsuChoice(null, "Automatic · single connected MPG TS PSU"));
            for (var index = 0; index < devices.Length; index++)
            {
                Devices.Add(new PsuChoice(devices[index].DeviceId, devices[index].Name + " · device " + (index + 1)));
            }

            if (id is not null && Devices.All(device => device.Id != id))
            {
                Devices.Add(new PsuChoice(id, "Selected PSU · currently unavailable"));
            }

            _selectedDevice = Devices.First(device => device.Id == id);
            Raise(nameof(SelectedDevice));
        }
        finally
        {
            _refreshingDevices = false;
        }
    }

    public MsiOptions Build()
    {
        var options = new MsiOptions
        {
            DevicePath = SelectedDevice.Id,
            FanControl = (MsiFanControl)FanModeIndex,
            FanDutyPercent = NumericInput.Integer(FanDutyPercent, "MSI fan duty"),
            FanCurve = BuildCurve()
        };

        options.Validate();
        return options;
    }

    public MsiFanCurve BuildCurve() => new(
        [.. CurvePoints.Select(point => point.Build())],
        NumericInput.Integer(ResponseDelaySeconds, "Fan curve response delay"));

    public void AddCurvePoint(double? temperature = null, double? duty = null)
    {
        if (!CanAddCurvePoint)
        {
            return;
        }

        try
        {
            BuildCurve().Validate();
        }
        catch (ArgumentException)
        {
            return;
        }

        var temperatures = new[] { 0d }.Concat(CurvePoints.Select(point => point.TemperatureC)).Append(100d).ToArray();
        var gap = Enumerable.Range(1, temperatures.Length - 1).MaxBy(index => temperatures[index] - temperatures[index - 1]);
        var temp = Math.Round(temperature ?? (temperatures[gap - 1] + temperatures[gap]) / 2, MidpointRounding.AwayFromZero);

        if (!double.IsFinite(temp) || temp is < 0 or > 100 || CurvePoints.Any(point => point.TemperatureC == temp))
        {
            return;
        }

        var index = CurvePoints.TakeWhile(point => point.TemperatureC < temp).Count();
        var left = CurvePoints[Math.Max(0, index - 1)];
        var right = CurvePoints[Math.Min(CurvePoints.Count - 1, index)];
        var interpolated = left.TemperatureC == right.TemperatureC ? left.DutyPercent
            : left.DutyPercent + (temp - left.TemperatureC) / (right.TemperatureC - left.TemperatureC) * (right.DutyPercent - left.DutyPercent);
        var value = Math.Clamp(Math.Round(duty ?? interpolated, MidpointRounding.AwayFromZero), 0, 100);

        if (!double.IsFinite(value))
        {
            return;
        }

        var added = new MsiFanCurvePointViewModel(temp, value);
        added.Edited += NotifyEdited;
        CurvePoints.Insert(index, added);
    }

    public void SetCurve(MsiFanCurve curve)
    {
        curve.Validate();

        foreach (var point in CurvePoints)
        {
            point.Edited -= NotifyEdited;
        }

        CurvePoints.Clear();

        foreach (var point in curve.Points)
        {
            var editor = new MsiFanCurvePointViewModel(point.TemperatureC, point.DutyPercent);
            editor.Edited += NotifyEdited;
            CurvePoints.Add(editor);
        }

        ResponseDelaySeconds = curve.ResponseDelaySeconds;
    }

    public void RemoveCurvePoint(MsiFanCurvePointViewModel point)
    {
        if (!CanRemoveCurvePoint)
        {
            return;
        }

        point.Edited -= NotifyEdited;
        CurvePoints.Remove(point);
        NotifyEdited();
    }

    public bool Matches(MsiOptions options) => SelectedDevice.Id == options.DevicePath
                                               && FanModeIndex == (int)options.FanControl && Math.Abs(FanDutyPercent - options.FanDutyPercent) < 0.0001
                                               && double.IsFinite(ResponseDelaySeconds) && ResponseDelaySeconds == options.FanCurve.ResponseDelaySeconds
                                               && CurvePoints.Count == options.FanCurve.Points.Length
                                               && CurvePoints.Select((point, index) => point.TemperatureC == options.FanCurve.Points[index].TemperatureC
                                                   && point.DutyPercent == options.FanCurve.Points[index].DutyPercent).All(equal => equal);
}