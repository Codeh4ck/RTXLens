using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RTXLens.App.Controls.Charts;
using RTXLens.App.Views.Performance;
using RTXLens.App.Views.Preview;
using RTXLens.Presentation.Performance;
using RTXLens.Tests.Infrastructure;
using RTXLens.UI.Tests.Performance;

namespace RTXLens.UI.Tests.Controls;

internal static class AvaloniaChartsTests
{
    public static void Run(TestRunner runner, string? screenshotDirectory)
    {
        runner.Run(CurveEditsRespectOptions);
        runner.Run(ViewportFollowsLatestAndClampsNavigation);
        runner.Run(SeriesGeometryPreservesGapsAndPeaks);
        runner.Run(PerformanceHoverReadsSamplesOmittedFromThePlot);
        runner.Run(() => PreviewRespondsToPointer(screenshotDirectory));
    }

    private static void CurveEditsRespectOptions()
    {
        var editor = new SimpleCurveControl
        {
            XAxis = new XyAxis(new XyRange(0, 100)),
            YAxis = new XyAxis(new XyRange(0, 100)),
            Points = [new Point(20, 50), new Point(30, 30), new Point(40, 90)],
            EditingOptions = new XyCurveEditingOptions(XStep: 1, YStep: 5, MinimumXSpacing: 10, MaximumPointCount: 4)
        };

        TestAssert.True(editor.TryMovePoint(1, new Point(36, 33)), "The point should move vertically within the neighbor limits.");
        TestAssert.Equal(new Point(30, 35), editor.Points[1]);
        TestAssert.False(editor.TryInsertPoint(new Point(35, 10)), "Insertion cannot violate the minimum spacing.");
        TestAssert.True(editor.TryInsertPoint(new Point(60.1, 22.8)), "A point can be added after the last point.");
        TestAssert.Equal(new Point(60, 25), editor.Points[^1]);
        TestAssert.False(editor.TryInsertPoint(new Point(80, 25)), "Insertion must respect the configured point limit.");

        editor.EditingOptions = editor.EditingOptions with { CanMoveX = false, CanInsertPoints = false, CanRemovePoints = false };
        TestAssert.True(editor.TryMovePoint(1, new Point(15, 43)), "A point on a fixed X grid may still move along Y.");
        TestAssert.Equal(new Point(30, 45), editor.Points[1]);
        TestAssert.False(editor.TryRemovePoint(1), "Removing points from a fixed grid is disabled.");
        TestAssert.False(editor.TryInsertPoint(new Point(80, 25)), "Adding points to a fixed grid is disabled.");

        editor.IsReadOnly = true;
        TestAssert.False(editor.TryMovePoint(1, new Point(30, 55)), "Read-only curves must reject edits.");
    }

    private static void ViewportFollowsLatestAndClampsNavigation()
    {
        var chart = new XyLineChart { XAxis = new XyAxis(new XyRange(0, 100)) };

        chart.Zoom(2, anchor: 50);
        TestAssert.Equal(new XyRange(25, 75), chart.VisibleXRange);

        chart.FollowLatest = true;
        TestAssert.Equal(new XyRange(50, 100), chart.VisibleXRange);
        chart.XAxis = chart.XAxis with { Range = new XyRange(0, 120) };
        TestAssert.Equal(new XyRange(70, 120), chart.VisibleXRange);

        chart.Pan(-0.5);
        TestAssert.Equal(new XyRange(45, 95), chart.VisibleXRange);
        TestAssert.False(chart.FollowLatest, "Manual navigation must pause live following.");

        chart.GoToStart();
        TestAssert.Equal(new XyRange(0, 50), chart.VisibleXRange);
        chart.GoToEnd();
        TestAssert.Equal(new XyRange(70, 120), chart.VisibleXRange);

        chart.XAxis = chart.XAxis with { Range = new XyRange(0, 40) };
        TestAssert.Equal(new XyRange(0, 40), chart.GetPlotLayout().X);
        chart.Fit();
        TestAssert.Equal<XyRange?>(null, chart.VisibleXRange);
    }

    private static void SeriesGeometryPreservesGapsAndPeaks()
    {
        var x = new XyAxis(new XyRange(0, 10));
        var y = new XyAxis(new XyRange(0, 10));
        var layout = new XyPlotLayout(new Size(600, 250), x, y, x.Range, Brushes.White);
        XySample[] samples =
        [
            new(0, 1), new(1, 1), new(1.001, 8), new(1.002, 2), new(2, 1),
            new(4, null), new(6, 9), new(8, 9), new(10, 9)
        ];

        var path = XySeriesPath.Build(samples, layout, 1);
        var pen = new Pen(Brushes.White, 1.6);
        TestAssert.True(path.StrokeContains(pen, layout.ToScreen(new Point(1.001, 7.8))), "A peak within one pixel must remain visible.");
        TestAssert.False(path.StrokeContains(pen, layout.ToScreen(new Point(4, 5))), "Missing samples must break the line.");
        TestAssert.Equal<XySample?>(new XySample(4, null), XySeriesPath.FindSample(samples, 4, x.Range, atOrBefore: false));
        TestAssert.Equal<XySample?>(new XySample(6, 9), XySeriesPath.FindSample(samples, 6.1, x.Range, atOrBefore: true));
    }

    private static void PerformanceHoverReadsSamplesOmittedFromThePlot()
    {
        var fixture = PerformanceRecordingTests.Fixture();
        var samples = Enumerable.Range(0, 20_000)
            .Select(index => new PerformanceSample(index / 500d, index % 17)).ToArray();
        samples[10_000] = new PerformanceSample(20, null);

        var series = fixture.Series[0] with { Selected = true, Count = samples.Length, Samples = samples };
        var recorder = new PerformanceRecorder(TimeProvider.System);
        recorder.Import(fixture with { Series = [series], DurationSeconds = 40, SampleCount = samples.Length - 1 });

        var window = new Window { Width = 1024, Height = 720, Content = new PerformancePage(recorder) };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();

            var chart = window.GetVisualDescendants().OfType<XyLineChart>().Single();
            var visible = chart.GetPlotLayout().X;
            var plotted = chart.Series.Single().Samples.ToArray().Select(sample => sample.X).ToHashSet();
            var omitted = samples.First(sample => sample.Seconds > 1 && sample.Seconds < 19 && !plotted.Contains(sample.Seconds));

            TestAssert.Equal<XySample?>(new XySample(omitted.Seconds, omitted.Value),
                chart.ReadHoverSamples(omitted.Seconds, visible)[0]);
            TestAssert.Equal<XySample?>(null, chart.ReadHoverSamples(19.999, visible)[0]);
            TestAssert.Equal("00:00:01.234", chart.XAxis.FormatReadout(1.234));

            chart.VisibleXRange = new XyRange(8.236, 17.236);
            AssertWholeSecondTicks(chart, [10, 12, 14, 16]);

            chart.Zoom(3, anchor: 11.836);
            AssertWholeSecondTicks(chart, [11, 12, 13]);

            chart.Zoom(0.5, anchor: 11.836);
            AssertWholeSecondTicks(chart, [10, 12, 14]);

            chart.VisibleXRange = new XyRange(11.7, 11.9);
            AssertWholeSecondTicks(chart, []);

            recorder.Clear();
            TestAssert.Equal<XySample?>(null, chart.ReadHoverSamples(omitted.Seconds, visible)[0]);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertWholeSecondTicks(XyLineChart chart, double[] expected)
    {
        var layout = chart.GetPlotLayout();
        TestAssert.True(expected.SequenceEqual(layout.HorizontalTicks.Select(tick => tick.Value)),
            "Axis ticks should land only on the expected whole seconds.");

        foreach (var tick in layout.HorizontalTicks)
        {
            var position = new Point(layout.ToScreenX(tick.Value), layout.Plot.Center.Y);
            var cursorTime = layout.ToData(position).X;

            TestAssert.Equal(chart.XAxis.Format(tick.Value) + ".000", chart.XAxis.FormatReadout(cursorTime));
        }
    }

    private static void PreviewRespondsToPointer(string? screenshotDirectory)
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        var preview = new ChartsPreviewWindow { Height = 910 };
        preview.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();

            var curve = preview.FindControl<SimpleCurveControl>("FanCurve")!;
            var chart = preview.FindControl<XyLineChart>("PerformancePlot")!;
            var origin = ChartPoint(preview, curve, new Point(60, 50));
            var destination = ChartPoint(preview, curve, new Point(64, 62));
            AvaloniaControlsTests.Capture(preview, screenshotDirectory, "charts-preview.png");

            preview.MouseMove(origin, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            AvaloniaControlsTests.Capture(preview, screenshotDirectory, "charts-curve-hover.png");

            preview.MouseDown(origin, MouseButton.Left, RawInputModifiers.None);
            preview.MouseMove(destination, RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            TestAssert.Equal(new Point(60, 50), curve.Points[2]);
            AvaloniaControlsTests.Capture(preview, screenshotDirectory, "charts-drag.png");

            preview.MouseUp(destination, MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            TestAssert.Equal(new Point(64, 62), curve.Points[2]);

            chart.Zoom(2, anchor: 40);
            Dispatcher.UIThread.RunJobs();
            var from = ChartPoint(preview, chart, new Point(40, 30));
            var to = ChartPoint(preview, chart, new Point(35, 30));
            preview.MouseDown(from, MouseButton.Left, RawInputModifiers.None);
            preview.MouseMove(to, RawInputModifiers.LeftMouseButton);
            preview.MouseUp(to, MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            TestAssert.True(Math.Abs(chart.VisibleXRange!.Minimum - 25) < 0.01, "Dragging the line chart should pan the viewport.");

            chart.Fit();
            chart.ShowLegendValues = true;
            Dispatcher.UIThread.RunJobs();
            preview.MouseMove(ChartPoint(preview, chart, new Point(7, 8)), RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            AvaloniaControlsTests.Capture(preview, screenshotDirectory, "charts-hover.png");
        }
        finally
        {
            preview.Close();
        }
    }

    private static Point ChartPoint(Window window, XyPlot plot, Point data) =>
        plot.TranslatePoint(plot.GetPlotLayout().ToScreen(data), window)!.Value;
}