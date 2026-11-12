using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace RTXLens.App.Controls.Charts;

public sealed record XyRange
{
    public XyRange(double minimum, double maximum)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum || !double.IsFinite(maximum - minimum))
        {
            throw new ArgumentOutOfRangeException(nameof(maximum), "The axis range must be finite and have a positive span.");
        }

        Minimum = minimum;
        Maximum = maximum;
    }

    public double Minimum { get; }
    public double Maximum { get; }
    public double Span => Maximum - Minimum;

    public bool Contains(double value) => value >= Minimum && value <= Maximum;

    internal double Clamp(double value) => Math.Clamp(value, Minimum, Maximum);
}

public sealed record XyAxis(
    XyRange Range,
    string Title = "",
    Func<double, string>? Formatter = null,
    int TickCount = 6,
    Func<double, string>? ReadoutFormatter = null,
    double? TickGranularity = null)
{
    internal static XyAxis Default { get; } = new(new XyRange(0, 1));

    public string Format(double value) => Formatter?.Invoke(value) ?? value.ToString("0.##", CultureInfo.CurrentCulture);

    public string FormatReadout(double value) => ReadoutFormatter?.Invoke(value) ?? Format(value);
}

public enum XyAxisDirection
{
    X,
    Y
}

public sealed record XyGuide(XyAxisDirection Axis, double Value, IBrush? Brush = null);

public sealed record XyMarker(Point Position, IBrush? Brush = null, bool Filled = true, double Radius = 5);