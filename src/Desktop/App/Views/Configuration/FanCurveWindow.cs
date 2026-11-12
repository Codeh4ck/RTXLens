using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RTXLens.Client.Http;
using RTXLens.App.Controls;
using RTXLens.App.Controls.Charts;
using RTXLens.App.ViewModels.Shell;
using RTXLens.App.Views.Shell;
using RTXLens.Plugins.Msi.Contracts.Configuration;
using RTXLens.Plugins.Msi.Contracts.Telemetry;
using RTXLens.Presentation.Configuration;
using RTXLens.Presentation.Telemetry;
using static RTXLens.App.Views.Configuration.ConfigurationPage;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Configuration;

internal sealed class FanCurveWindow : LensWindow
{
    public FanCurveWindow(MsiSettingsEditorViewModel settings, ShellViewModel shell)
    {
        var original = settings.BuildCurve();
        var editor = new MsiSettingsEditorViewModel(new MsiOptions { FanCurve = original });

        Title = "RTX Lens - MSI PSU fan curve";
        Width = 1280;
        Height = 694;
        MinWidth = 800;
        MinHeight = 554;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var curve = new SimpleCurveControl
        {
            MinHeight = 180,
            XAxis = new XyAxis(new XyRange(0, 100), "PSU temperature (°C)", value => $"{value:0} °C", 11),
            YAxis = new XyAxis(new XyRange(0, 100), "Fan duty", value => $"{value:0}%", 11),
            EditingOptions = new XyCurveEditingOptions(1, 1, 1, MaximumPointCount: 6, ExtendEndpoints: true)
        };
        curve[!XyPlot.AccentBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("PrimaryBrush");
        curve[!XyPlot.GridBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("PanelBorderBrush");
        curve[!XyPlot.AxisBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("MutedTextBrush");

        var pointList = new ItemsControl
        {
            ItemsSource = editor.CurvePoints,
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Spacing = 7 }),
            ItemTemplate = new FuncDataTemplate<MsiFanCurvePointViewModel>((point, _) =>
                point is null ? null : CreatePointRow(point, editor))
        };

        var add = IconButton("Add point", "add-action", () => editor.AddCurvePoint());
        BindTo(add, IsEnabledProperty, editor, nameof(editor.CanAddCurvePoint));
        var labels = PointColumns(Caption("Temperature (°C)"), Caption("Duty (%)"));
        labels.Margin = new Thickness(0, 0, 14, 0);
        var scroll = Scroll(new Border { Padding = new Thickness(0, 0, 14, 0), Child = pointList });
        scroll.Name = "CurvePointScroll";
        scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var list = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 7, Children = { labels, scroll } };
        Grid.SetRow(scroll, 1);
        var pointsPanel = new SectionPanel { Header = "Curve points", HeaderActions = add, Padding = new Thickness(12), Content = list };

        var delay = Section("Fan control", NumberField("Fan response delay (seconds)", editor, nameof(editor.ResponseDelaySeconds), 0, 60));
        delay.Padding = new Thickness(12);
        var validation = Message(tone: "error");
        validation.IsVisible = false;
        var done = CommitButton("Done", () =>
        {
            try
            {
                var updated = editor.BuildCurve();
                updated.Validate();
                if (!updated.Matches(original))
                {
                    settings.SetCurve(updated);
                }

                Close();
            }
            catch (ArgumentException error)
            {
                validation.Content = error.Message;
                validation.IsVisible = true;
            }
        });
        var restoreDefaults = ActionButton("Restore defaults", "refresh", () =>
        {
            settings.FanModeIndex = (int)MsiFanControl.Automatic;
            Close();
        });
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { restoreDefaults, ActionButton("Cancel", "cancel-action", Close), done }
        };

        var actions = new StackPanel { Spacing = 8, Children = { validation, footer } };
        var right = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 12,
            Children = { delay, pointsPanel, actions }
        };
        Grid.SetRow(pointsPanel, 1);
        Grid.SetRow(actions, 2);

        var guide = Caption("Double-click to add a point.");
        var left = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 10, Children = { curve, guide } };
        Grid.SetRow(curve, 1);
        Grid.SetRow(guide, 2);

        var controllerAlert = Alert();
        var requested = new MetricViewModel("Requested fan duty") { Value = "Unavailable" };
        var requestedReadout = Hardware.HardwarePage.Readout(requested);
        var telemetry = CancellationTokenSource.CreateLinkedTokenSource(shell.StopToken);
        var telemetryToken = telemetry.Token;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        var readingDuty = false;
        MsiSnapshot? liveSnapshot = null;
        var live = shell.Dashboard.Panels.FirstOrDefault(panel => panel.IsMsi)?.Msi;
        if (live is not null)
        {
            var readings = AdaptiveCards(width => width >= 780 ? 3 : width >= 540 ? 2 : 1,
                Hardware.HardwarePage.Readout(live.Temperature), Hardware.HardwarePage.Readout(live.FanSpeed), requestedReadout);
            left.Children.Add(readings);
            live.Temperature.PropertyChanged += LiveChanged;
            live.FanDuty.PropertyChanged += LiveChanged;
        }
        else
        {
            left.Children.Add(requestedReadout);
        }

        var chartPanel = new SectionPanel { Header = "Temperature / fan duty", Content = left };
        var chartColumn = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            RowSpacing = 8,
            Children = { chartPanel, controllerAlert }
        };
        Grid.SetRow(controllerAlert, 1);
        var columns = Columns(chartColumn, right);
        columns.ColumnDefinitions[0].Width = new GridLength(2.2, GridUnitType.Star);
        columns.ColumnDefinitions[1].MinWidth = 340;
        columns.Margin = new Thickness(20);
        Body = columns;

        var synchronizing = false;
        var subscribed = new HashSet<MsiFanCurvePointViewModel>();

        SyncPointSubscriptions();
        SyncChart();
        LiveChanged(null, new PropertyChangedEventArgs(string.Empty));

        editor.CurvePoints.CollectionChanged += PointsChanged;
        shell.PropertyChanged += OnShellPropertyChanged;
        curve.PropertyChanged += (_, args) =>
        {
            if (args.Property != SimpleCurveControl.PointsProperty || synchronizing)
            {
                return;
            }

            synchronizing = true;
            try
            {
                var points = curve.Points.ToArray();
                var existing = editor.CurvePoints.OrderBy(point => point.TemperatureC).ToArray();
                if (points.Length > existing.Length)
                {
                    var added = points.First(point => existing.All(item => item.TemperatureC != point.X));
                    editor.AddCurvePoint(added.X, added.Y);
                }
                else if (points.Length < existing.Length)
                {
                    editor.RemoveCurvePoint(existing.First(point => points.All(item => item.X != point.TemperatureC)));
                }
                else
                {
                    for (var index = 0; index < points.Length; index++)
                    {
                        existing[index].TemperatureC = points[index].X;
                        existing[index].DutyPercent = points[index].Y;
                    }
                }
            }
            finally
            {
                synchronizing = false;
            }

            SyncChart();
        };

        timer.Tick += async (_, _) => await RefreshRequestedDutyAsync();
        Opened += async (_, _) =>
        {
            timer.Start();
            shell.ShowFanCurve = true;
            shell.RefreshPresentation(force: true);
            await RefreshRequestedDutyAsync();
        };
        Closed += (_, _) =>
        {
            timer.Stop();
            telemetry.Cancel();
            telemetry.Dispose();
            shell.ShowFanCurve = false;
            editor.CurvePoints.CollectionChanged -= PointsChanged;
            shell.PropertyChanged -= OnShellPropertyChanged;
            foreach (var point in subscribed)
            {
                point.PropertyChanged -= PointChanged;
            }

            if (live is not null)
            {
                live.Temperature.PropertyChanged -= LiveChanged;
                live.FanDuty.PropertyChanged -= LiveChanged;
            }
        };
        return;

        void SyncChart()
        {
            if (synchronizing)
            {
                return;
            }

            synchronizing = true;
            try
            {
                curve.Points =
                [
                    .. editor.CurvePoints.OrderBy(point => point.TemperatureC)
                        .Select(point => new Point(point.TemperatureC, point.DutyPercent))
                ];
            }
            finally
            {
                synchronizing = false;
            }
        }

        void SyncPointSubscriptions()
        {
            foreach (var removed in subscribed.Except(editor.CurvePoints).ToArray())
            {
                removed.PropertyChanged -= PointChanged;
                subscribed.Remove(removed);
            }

            foreach (var point in editor.CurvePoints)
            {
                if (subscribed.Add(point))
                {
                    point.PropertyChanged += PointChanged;
                }
            }
        }

        void PointChanged(object? sender, PropertyChangedEventArgs args) => SyncChart();

        void PointsChanged(object? sender, NotifyCollectionChangedEventArgs args)
        {
            SyncPointSubscriptions();
            SyncChart();
        }

        void LiveChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (liveSnapshot is { } snapshot)
            {
                curve.Guides = [new XyGuide(XyAxisDirection.X, snapshot.TemperatureC)];
                var reported = new XyMarker(new Point(snapshot.TemperatureC, snapshot.FanDutyPercent));
                curve.Markers = snapshot.TargetFanDutyPercent is { } target
                    ? [new XyMarker(new Point(snapshot.TemperatureC, target), Filled: false, Radius: 8), reported]
                    : [reported];
                return;
            }

            if (live is null || !TryRead(live.Temperature.Value, out var temperature))
            {
                curve.Guides = [];
                curve.Markers = [];
                return;
            }

            curve.Guides = [new XyGuide(XyAxisDirection.X, temperature)];
            curve.Markers = TryRead(live.FanDuty.Value, out var duty) ? [new XyMarker(new Point(temperature, duty))] : [];
        }

        void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(ShellViewModel.Api))
            {
                ClearControllerReadings();
            }
        }

        void ClearControllerReadings()
        {
            liveSnapshot = null;
            requested.Value = "Unavailable";
            UpdateControllerAlert(null);
            LiveChanged(null, new PropertyChangedEventArgs(string.Empty));
        }

        void UpdateControllerAlert(string? message)
        {
            if (!Equals(controllerAlert.Content, message))
            {
                SetAlert(controllerAlert, message, "warning");
            }
        }

        async Task RefreshRequestedDutyAsync()
        {
            if (readingDuty || telemetryToken.IsCancellationRequested || shell.IsSimulation)
            {
                return;
            }

            readingDuty = true;
            var api = shell.Api;
            var token = telemetryToken;
            try
            {
                var snapshot = await api.GetMsiSnapshotAsync(token);
                if (!token.IsCancellationRequested && ReferenceEquals(shell.Api, api) && !shell.IsSimulation)
                {
                    liveSnapshot = DateTimeOffset.UtcNow - snapshot.ObservedAt <= TimeSpan.FromSeconds(5) ? snapshot : null;
                    requested.Value = liveSnapshot?.TargetFanDutyPercent is { } duty ? $"{duty} %" : "Unavailable";
                    UpdateControllerAlert(liveSnapshot?.FanControllerMessage);
                    LiveChanged(null, new PropertyChangedEventArgs(string.Empty));
                }
            }
            catch (Exception error) when (error is HttpRequestException or MonitoringApiException or IOException
                or OperationCanceledException or NotSupportedException or System.Text.Json.JsonException)
            {
                if (!token.IsCancellationRequested && ReferenceEquals(shell.Api, api))
                {
                    ClearControllerReadings();
                }
            }
            finally
            {
                readingDuty = false;
            }
        }
    }

    private static Grid CreatePointRow(MsiFanCurvePointViewModel point, MsiSettingsEditorViewModel editor)
    {
        var temperature = CreatePointInput(point, nameof(point.TemperatureC), false);
        var duty = CreatePointInput(point, nameof(point.DutyPercent), true);
        AutomationProperties.SetName(temperature, "Temperature (°C)");
        AutomationProperties.SetName(duty, "Duty (%)");

        var remove = IconButton("Remove point", "chart-clear", () => editor.RemoveCurvePoint(point));
        BindTo(remove, IsEnabledProperty, editor, nameof(editor.CanRemoveCurvePoint));
        var row = PointColumns(temperature, duty);
        Grid.SetColumn(remove, 2);
        row.Children.Add(remove);
        return row;
    }

    private static NumericUpDown CreatePointInput(MsiFanCurvePointViewModel point, string property, bool isDuty)
    {
        var input = new CurvePointInput(isDuty)
        {
            Minimum = 0,
            Maximum = 100,
            Increment = 1,
            ClipValueToMinMax = true,
            ParsingNumberStyle = NumberStyles.None,
            FormatString = "0",
            TextAlignment = TextAlignment.Center,
            Classes = { "curve-point" }
        };
        BindTo(input, NumericUpDown.ValueProperty, point, property, Avalonia.Data.BindingMode.TwoWay);
        return input;
    }

    private sealed class CurvePointInput(bool isDuty) : NumericUpDown
    {
        protected override Type StyleKeyOverride => typeof(NumericUpDown);

        protected override void OnApplyTemplate(TemplateAppliedEventArgs args)
        {
            base.OnApplyTemplate(args);

            if (args.NameScope.Find<TextBox>("PART_TextBox") is { } input)
            {
                input.MaxLength = 3;
                input.AddHandler(TextInputEvent, (_, change) =>
                {
                    if (change.Text?.Any(character => character is < '0' or > '9') == true)
                    {
                        change.Handled = true;
                    }
                }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            }
        }

        protected override void OnSpin(SpinEventArgs args)
        {
            if (isDuty && args.Direction == SpinDirection.Increase && Value is null or < 25)
            {
                SetCurrentValue(ValueProperty, 25m);
                return;
            }

            if (isDuty && args.Direction == SpinDirection.Decrease && Value is > 0 and <= 25)
            {
                SetCurrentValue(ValueProperty, 0m);
                return;
            }

            base.OnSpin(args);
        }

        protected override void OnLostFocus(Avalonia.Input.FocusChangedEventArgs args)
        {
            base.OnLostFocus(args);

            if (isDuty && Value is > 0 and < 25)
            {
                SetCurrentValue(ValueProperty, 0m);
            }
        }
    }

    private static Grid PointColumns(Control temperature, Control duty)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,36"),
            ColumnSpacing = 8,
            Children = { temperature, duty }
        };
        Grid.SetColumn(duty, 1);
        return row;
    }

    private static bool TryRead(string value, out double reading) => double.TryParse(value.Split(' ', 2)[0].TrimEnd('%'), out reading);
}
