using Avalonia.Media;

namespace RTXLens.App.Controls.Charts;

public readonly record struct XySample(double X, double? Y);

/// <summary>A snapshot of samples in nondecreasing X order. A null or non-finite Y ends the current line segment.</summary>
/// <remarks>Replace the series when publishing updates and keep its backing memory stable while it is displayed.</remarks>
public sealed record XySeries(string Label, ReadOnlyMemory<XySample> Samples, IBrush Brush, Func<double, string>? FormatValue = null);