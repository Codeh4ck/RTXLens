using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using RTXLens.App.Controls.Charts;
using RTXLens.App.Views.Shell;

namespace RTXLens.App.Views.Preview;

internal sealed partial class ChartsPreviewWindow : LensWindow
{
    private static readonly SolidColorBrush Violet = new(Color.Parse("#C29BFF"));
    private static readonly SolidColorBrush Cyan = new(Color.Parse("#39EED1"));
    private static readonly SolidColorBrush Amber = new(Color.Parse("#FFBA68"));

    public ChartsPreviewWindow()
    {
        InitializeComponent();
        ConfigureFanCurve();
        ConfigureVoltageFrequencyCurve();
        ConfigurePerformanceChart();
    }

    private void ConfigureFanCurve()
    {
        FanCurve.XAxis = new XyAxis(new XyRange(0, 100), "PSU temperature", value => $"{value:0} °C", TickCount: 11);
        FanCurve.YAxis = new XyAxis(new XyRange(0, 100), "Fan duty", value => $"{value:0}%", TickCount: 6);
        FanCurve.Points = [new Point(44, 0), new Point(50, 25), new Point(60, 50), new Point(71, 70), new Point(80, 100)];
        FanCurve.EditingOptions = new XyCurveEditingOptions(XStep: 1, YStep: 1, MinimumXSpacing: 1,
            MaximumPointCount: 6, ExtendEndpoints: true);
        FanCurve.Guides = [new XyGuide(XyAxisDirection.X, 56)];
        FanCurve.Markers = [new XyMarker(new Point(56, 32)), new XyMarker(new Point(56, 40), Violet, Filled: false)];
    }

    private void ConfigureVoltageFrequencyCurve()
    {
        VoltageFrequencyCurve.XAxis = new XyAxis(new XyRange(700, 1100), "Core voltage", value => $"{value:0} mV", TickCount: 9);
        VoltageFrequencyCurve.YAxis = new XyAxis(new XyRange(1200, 3000), "Core clock", value => $"{value:0} MHz", TickCount: 7);
        VoltageFrequencyCurve.Points =
        [
            new Point(725, 1350), new Point(775, 1725), new Point(825, 2070), new Point(875, 2355),
            new Point(925, 2580), new Point(975, 2700), new Point(1025, 2760), new Point(1075, 2760)
        ];

        VoltageFrequencyCurve.EditingOptions = new XyCurveEditingOptions(YStep: 15, CanMoveX: false,
            CanInsertPoints: false, CanRemovePoints: false);
        VoltageFrequencyCurve.Guides = [new XyGuide(XyAxisDirection.X, 925)];
        VoltageFrequencyCurve.Markers = [new XyMarker(new Point(925, 2580), Violet, Filled: false, Radius: 10)];
    }

    private void ConfigurePerformanceChart()
    {
        PerformancePlot.XAxis = new XyAxis(new XyRange(0, 80), "Elapsed time",
            value => TimeSpan.FromSeconds(value).ToString(@"mm\:ss"), TickCount: 9,
            ReadoutFormatter: value => TimeSpan.FromSeconds(value).ToString(@"mm\:ss\.fff"));
        PerformancePlot.YAxis = new XyAxis(new XyRange(0, 60), "Connector current", value => $"{value:0} A", TickCount: 5);
        PerformancePlot.Series =
        [
            new XySeries("ROG Astral", CreateSamples(0), Cyan, value => $"{value:0.0} A"),
            new XySeries("MSI PSU", CreateSamples(1.6), Violet, value => $"{value:0.0} A"),
            new XySeries("WireView Pro II", CreateSamples(3.2), Amber, value => $"{value:0.0} A")
        ];
    }

    private static XySample[] CreateSamples(double phase)
    {
        var samples = new XySample[1201];
        for (var index = 0; index < samples.Length; index++)
        {
            var time = index / 15.0;
            var load = time < 20 || time > 65 ? 0 : 31 + Math.Sin(time * 0.26) * 6;
            var current = 6 + load + phase * 1.4 + Math.Sin(time * 1.8 + phase) * (load > 0 ? 1.6 : 0.45);
            var hasGap = phase > 3 && time is > 32 and < 36;

            samples[index] = new XySample(time, hasGap ? null : current);
        }

        return samples;
    }

    private void ShowStart(object? sender, RoutedEventArgs e) => PerformancePlot.GoToStart();

    private void ShowEnd(object? sender, RoutedEventArgs e) => PerformancePlot.GoToEnd();

    private void ZoomIn(object? sender, RoutedEventArgs e) => PerformancePlot.Zoom(1.5);

    private void ZoomOut(object? sender, RoutedEventArgs e) => PerformancePlot.Zoom(1 / 1.5);

    private void Fit(object? sender, RoutedEventArgs e) => PerformancePlot.Fit();
}