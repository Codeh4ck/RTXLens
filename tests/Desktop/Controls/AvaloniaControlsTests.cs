using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using RTXLens.App.Controls;
using RTXLens.App.Themes;
using RTXLens.App.ViewModels.Appearance;
using RTXLens.App.ViewModels.Preview;
using RTXLens.App.Views.Preview;
using RTXLens.Tests.Infrastructure;

namespace RTXLens.UI.Tests.Controls;

internal static class AvaloniaControlsTests
{
    public static int Run(string? screenshotDirectory)
    {
        AppBuilder.Configure<RTXLens.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        var runner = new TestRunner();
        runner.Run(() => RunPreviewChecks(screenshotDirectory));
        runner.Run(SavedAppearanceRoundTrips);
        runner.Run(() => DiagnosticPickersFollowPalette(screenshotDirectory));
        runner.Run(PendingActionsDisableTheButtonAndRecoverAfterCancellation);
        runner.Run(ErrorReportsRetainSeparateCrashAndActionHistories);
        runner.Run(EditableListVirtualizesRows);
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        runner.Run(AvaloniaDashboardTests.UpdatesRealHardwareViewsFromFixtureFrames);
#if TELEMETRY_DEBUG
        runner.Run(AvaloniaDashboardTests.SimulationCanEditWireViewAndReturnsToLiveOnClose);
        runner.Run(() => AvaloniaDashboardTests.DebugConsoleScrollsAndKeepsOverrides(screenshotDirectory));
#if !DEBUG
        runner.Run(AvaloniaDashboardTests.ReleaseDebugConsoleRequiresShortcut);
#endif
#endif
        runner.Run(() => AvaloniaDashboardTests.MatchesDashboardLayoutAtWideAndCompactWidths(screenshotDirectory));
        runner.Run(() => AvaloniaDashboardTests.EditsExistingPluginConfigurationModel(screenshotDirectory));
        runner.Run(() => AvaloniaDashboardTests.MatchesEventTableAndDialogLayouts(screenshotDirectory));
        runner.Run(() => AvaloniaDashboardTests.FanCurveKeepsActionsVisible(screenshotDirectory));
        runner.Run(AvaloniaDashboardTests.FanCurveRestoreStagesAutomaticMode);
        runner.Run(AvaloniaDashboardTests.EventLogRecoversAfterAnInvalidStreamPage);
        runner.Run(AvaloniaDashboardTests.SecondInstanceQueuesActivationDuringStartup);
        runner.Run(AvaloniaDashboardTests.SafeguardReportDisplaysPinCharts);
        runner.Run(() => AvaloniaDashboardTests.SafeguardLoadsAndReloadsConnectors(screenshotDirectory));
        runner.Run(AvaloniaDashboardTests.SafeguardScriptDialogsFitTheirParameters);
        runner.Run(() => AvaloniaSettingsParityTests.ExercisesSettingsAndProfiles(screenshotDirectory));
        runner.Run(AvaloniaSettingsParityTests.RepairsLegacyAstralWarningProfile);
        runner.Run(AvaloniaMobileAccessTests.PublicIpv4ApplyTracksEdits);
        runner.Run(() => AvaloniaMobileAccessTests.SavesSettingsAndGeneratesPairingCode(screenshotDirectory));

        runner.Run(() => RunGalleryChecks(screenshotDirectory));
        AvaloniaChartsTests.Run(runner, screenshotDirectory);

        runner.PrintSummary();

        return runner.FailedCount == 0 ? 0 : 1;
    }

    internal static void PumpUntil(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!ready() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }

        TestAssert.True(ready(), "The UI operation did not complete.");
    }

    internal static void InvokeButton(Button button)
    {
        var peer = new Avalonia.Automation.Peers.ButtonAutomationPeer(button);
        ((Avalonia.Automation.Provider.IInvokeProvider)peer).Invoke();
        Dispatcher.UIThread.RunJobs();
    }

    private static void PendingActionsDisableTheButtonAndRecoverAfterCancellation()
    {
        var completion = new TaskCompletionSource();
        var calls = 0;
        var button = RTXLens.App.Views.Shell.DesktopControls.CommitButton("Apply", () =>
        {
            calls++;
            return completion.Task;
        });
        var window = new Window { Content = button };
        window.Show();
        try
        {
            InvokeButton(button);
            TestAssert.Equal(1, calls);
            TestAssert.False(button.IsEffectivelyEnabled, "A pending action must disable its button.");
            TestAssert.False(button.Command!.CanExecute(null), "A second invocation must be unavailable.");
            completion.SetCanceled(new CancellationToken(canceled: true));
            Dispatcher.UIThread.RunJobs();
            TestAssert.True(button.IsEffectivelyEnabled, "Cancellation must release the button.");
        }
        finally
        {
            window.Close();
        }
    }

    private static void ErrorReportsRetainSeparateCrashAndActionHistories()
    {
        var directory = Directory.CreateTempSubdirectory("rtxlens-diagnostics-");
        try
        {
            var error = new InvalidOperationException("fixture failure");
            var crash = RTXLens.App.DesktopErrorReports.Write(error, "startup", crash: true, directory: directory.FullName);
            for (var i = 0; i < RTXLens.App.DesktopErrorReports.RetainedPerKind + 2; i++)
            {
                RTXLens.App.DesktopErrorReports.Write(error, $"action {i}", directory: directory.FullName);
            }
            var actions = directory.GetFiles("action-*.txt").OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
            TestAssert.Equal(RTXLens.App.DesktopErrorReports.RetainedPerKind, actions.Length);
            TestAssert.True(File.Exists(crash) && File.ReadAllText(crash!).Contains("startup"), "Action reports must preserve the crash report.");
            var latest = File.ReadAllText(actions[^1].FullName);
            TestAssert.True(latest.Contains("fixture failure") && latest.Contains("RTX Lens"), "Reports must identify the build and error.");
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static void RunPreviewChecks(string? screenshotDirectory)
    {
        var store = new AppearanceStore(Path.Combine(Path.GetTempPath(), $"rtxlens-{Guid.NewGuid():N}.json"));
        var appearance = new AppearanceViewModel(Application.Current!, store);
        var model = new ControlsPreviewViewModel(appearance);
        var window = new ControlsPreviewWindow { DataContext = model, Height = 1150 };

        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();

            var metrics = window.GetVisualDescendants().OfType<MetricDisplay>().ToArray();
            TestAssert.Equal(6, metrics.Length);
            TestAssert.True(metrics.All(metric => metric.Bounds.Width > 0), "All preview metrics should be laid out.");
            TestAssert.True(metrics.All(metric => metric.GetVisualDescendants().OfType<Image>().Any(image => image.Source is not null)),
                "Metric icons should use their existing SVG artwork.");
            var tracks = window.GetVisualDescendants().OfType<PinTrack>().ToArray();
            TestAssert.Equal(6, tracks.Length);
            TestAssert.Equal(4, window.GetVisualDescendants().OfType<ColorPicker>().Count());

            var header = window.FindControl<ColumnHeader>("TimeColumn")!;
            header.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            var resizeGrip = header.GetVisualDescendants().OfType<Thumb>().Single();
            var sortButton = header.GetVisualDescendants().OfType<Button>().Single();
            Click(window, sortButton);
            TestAssert.Equal("09:31:44", model.FirstEventTime);

            var start = CenterInWindow(resizeGrip, window);
            window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
            window.MouseMove(start + new Vector(28, 0), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            window.MouseMove(start + new Vector(40, 0), RawInputModifiers.LeftMouseButton);
            window.MouseUp(start + new Vector(40, 0), MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            TestAssert.Equal(172.0, header.Width);

            var scrollViewer = window.FindControl<ScrollViewer>("PreviewScroll")!;
            scrollViewer.Offset = default;
            Dispatcher.UIThread.RunJobs();

            var defaultBackground = ReadColor(window.Background);
            var defaultPrimary = ReadColor(metrics[0].BorderBrush);
            var defaultIconSurface = ReadColor(metrics[0].IconBackground);
            TestAssert.Equal(Color.Parse("#090E17"), defaultBackground);
            TestAssert.Equal(Color.Parse("#39EED1"), defaultPrimary);
            Capture(window, screenshotDirectory, "controls-dark.png");
            CheckEditableListActions(window, model.SourcePriority);

            appearance.Primary = Color.Parse("#FFB14D");
            appearance.Secondary = Color.Parse("#F1A7D5");
            appearance.Tertiary = Color.Parse("#ABE787");
            appearance.Background = Color.Parse("#221637");
            Dispatcher.UIThread.RunJobs();

            TestAssert.Equal(appearance.Background, ReadColor(window.Background));
            TestAssert.Equal(appearance.Primary, ReadColor(metrics[0].BorderBrush));
            TestAssert.Equal(defaultIconSurface, ReadColor(metrics[0].IconBackground));
            TestAssert.Equal(appearance.Secondary, ReadColor(tracks[0].BaselineBrush));
            TestAssert.Equal(appearance.Tertiary, ReadColor(tracks[0].PeakBrush));
            TestAssert.True(ReferenceEquals(metrics[0], window.GetVisualDescendants().OfType<MetricDisplay>().First()),
                "Previewing a theme should preserve the existing metric control.");
            Capture(window, screenshotDirectory, "controls-custom.png");

            appearance.ResetPaletteCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            TestAssert.Equal(defaultBackground, ReadColor(window.Background));
            TestAssert.Equal(defaultPrimary, ReadColor(metrics[0].BorderBrush));

            appearance.LightTheme = true;
            Dispatcher.UIThread.RunJobs();
            TestAssert.Equal(Color.Parse("#F3F6FA"), ReadColor(window.Background));
            Capture(window, screenshotDirectory, "controls-light.png");

            window.Width = 800;
            window.Height = 830;
            Dispatcher.UIThread.RunJobs();

            Capture(window, screenshotDirectory, "controls-narrow.png");
            var headroom = window.GetVisualDescendants().OfType<TextBlock>().First(block => block.Text == "+8.69 A");
            TestAssert.True(headroom.Bounds.Width >= headroom.TextLayout.Width, "The headroom badge should fit at the minimum width.");
        }
        finally
        {
            window.Close();
        }
    }

    private static void RunGalleryChecks(string? screenshotDirectory)
    {
        var application = Application.Current!;
        var paletteResources = new PaletteResources(application);
        application.RequestedThemeVariant = ThemeVariant.Dark;

        var gallery = new ControlGalleryWindow();
        gallery.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();

            var button = gallery.FindControl<Button>("ApplySettingsButton")!;
            var textBox = gallery.FindControl<TextBox>("DeviceNameTextBox")!;
            var checkBox = gallery.FindControl<CheckBox>("ConnectorCurrentCheckBox")!;
            var toggle = gallery.FindControl<ToggleSwitch>("RememberWindowSizeToggle")!;
            var radioButton = gallery.FindControl<RadioButton>("AutomaticFanModeRadioButton")!;

            var textBoxFrame = FindPart<Border>(textBox, "PART_BorderElement");
            var checkBoxFrame = FindPart<Border>(checkBox, "NormalRectangle");
            var switchKnob = FindPart<Ellipse>(toggle, "SwitchKnobOn");
            var switchTrack = FindPart<Border>(toggle, "SwitchKnobBounds");
            var offKnob = FindPart<Ellipse>(toggle, "SwitchKnobOff");
            var offTrack = FindPart<Border>(toggle, "OuterBorder");
            var radioFrame = FindPart<Ellipse>(radioButton, "CheckOuterEllipse");
            var darkPalette = paletteResources.ReadDefaults(ThemeVariant.Dark);

            TestAssert.Equal(Color.Parse("#111927"), ReadColor(button.Background));
            TestAssert.Equal(darkPalette.Primary, ReadColor(button.Foreground));
            TestAssert.Equal(darkPalette.Primary, ReadColor(button.BorderBrush));

            var icon = button.GetVisualDescendants().OfType<Image>().Single();
            var label = button.GetVisualDescendants().OfType<TextBlock>().Single();
            TestAssert.True(Math.Abs(CenterInWindow(icon, gallery).Y - CenterInWindow(label, gallery).Y) <= 0.5,
                "The action icon and label should share a vertical center.");
            TestAssert.Equal(Color.Parse("#3A606D"), ReadColor(textBoxFrame.BorderBrush));
            TestAssert.Equal(darkPalette.Primary, ReadColor(checkBoxFrame.Background));
            TestAssert.Equal(darkPalette.Primary, ReadColor(switchKnob.Fill));
            TestAssert.Equal(darkPalette.Primary, ReadColor(radioFrame.Stroke));
            var numberInput = gallery.FindControl<NumericUpDown>("PollingIntervalInput")!;
            var spinner = FindPart<RepeatButton>(numberInput, "PART_DecreaseButton");
            var spinnerSurface = FindPart<ContentPresenter>(spinner, "PART_ContentPresenter");
            var spinnerIcon = spinner.GetVisualDescendants().OfType<PathIcon>().Single();
            TestAssert.Equal(Color.Parse("#24BCAA"), ReadColor(spinnerIcon.Foreground));
            TestAssert.Equal(new CornerRadius(0, 4, 3, 0), spinnerSurface.CornerRadius);
            TestAssert.Equal(Colors.Transparent, ReadColor(spinnerSurface.Background));

            CheckToggleAlignment();
            Capture(gallery, screenshotDirectory, "control-gallery.png");
            gallery.SetRenderScaling(1.25);
            Dispatcher.UIThread.RunJobs();
            CheckToggleAlignment();
            Capture(gallery, screenshotDirectory, "control-gallery-125.png");
            gallery.SetRenderScaling(1.5);
            Dispatcher.UIThread.RunJobs();
            CheckToggleAlignment();
            Capture(gallery, screenshotDirectory, "control-gallery-150.png");
            gallery.SetRenderScaling(1);
            Dispatcher.UIThread.RunJobs();

            gallery.MouseMove(CenterInWindow(spinner, gallery), RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            TestAssert.Equal(Color.Parse("#173E46"), ReadColor(spinnerSurface.Background));
            Capture(gallery, screenshotDirectory, "control-gallery-stepper-hover.png");

            Click(gallery, spinner);
            TestAssert.Equal<decimal?>(900, numberInput.Value);
            numberInput.Value = 1000;
            gallery.MouseMove(new Point(0, 0), RawInputModifiers.None);

            TestAssert.True(textBox.Focus(NavigationMethod.Tab), "The device name field should accept keyboard focus.");
            Dispatcher.UIThread.RunJobs();
            TestAssert.Equal(darkPalette.Primary, ReadColor(textBoxFrame.BorderBrush));
            Capture(gallery, screenshotDirectory, "control-gallery-focus.png");

            var customPalette = darkPalette with { Primary = Color.Parse("#FFB14D") };
            paletteResources.Apply(customPalette);
            Dispatcher.UIThread.RunJobs();

            TestAssert.Equal(customPalette.Primary, ReadColor(button.BorderBrush));
            TestAssert.Equal(customPalette.Primary, ReadColor(textBoxFrame.BorderBrush));
            TestAssert.Equal(customPalette.Primary, ReadColor(checkBoxFrame.Background));
            TestAssert.Equal(customPalette.Primary, ReadColor(switchKnob.Fill));
            TestAssert.Equal(customPalette.Primary, ReadColor(radioFrame.Stroke));
            TestAssert.Equal(Color.Parse("#24BCAA"), ReadColor(spinnerIcon.Foreground));
            Capture(gallery, screenshotDirectory, "control-gallery-custom.png");

            paletteResources.Apply(null);
            application.RequestedThemeVariant = ThemeVariant.Light;
            Dispatcher.UIThread.RunJobs();

            var lightPalette = paletteResources.ReadDefaults(ThemeVariant.Light);
            TestAssert.Equal(lightPalette.Primary, ReadColor(checkBoxFrame.Background));
            TestAssert.Equal(lightPalette.Primary, ReadColor(switchKnob.Fill));
            TestAssert.Equal(Color.Parse("#007F76"), ReadColor(spinnerIcon.Foreground));
            Capture(gallery, screenshotDirectory, "control-gallery-light.png");

            void CheckToggleAlignment()
            {
                TestAssert.True(Math.Abs(CenterInWindow(switchTrack, gallery).Y - CenterInWindow(switchKnob, gallery).Y) < 0.01,
                    "The on-state knob should match the track's vertical center.");
                TestAssert.True(Math.Abs(CenterInWindow(offTrack, gallery).Y - CenterInWindow(offKnob, gallery).Y) < 0.01,
                    "The off-state knob should match the track's vertical center.");
            }
        }
        finally
        {
            paletteResources.Apply(null);
            gallery.Close();
        }
    }

    private static T FindPart<T>(Control control, string name) where T : Control =>
        control.GetVisualDescendants().OfType<T>().Single(part => part.Name == name);

    private static void CheckEditableListActions(Window window, SourcePriorityPreviewViewModel model)
    {
        var editor = window.FindControl<EditableList>("SourcePriorityList")!;
        var list = FindPart<ListBox>(editor, "PART_List");
        var add = FindPart<Button>(editor, "PART_Add");
        var moveUp = FindPart<Button>(editor, "PART_MoveUp");
        var moveDown = FindPart<Button>(editor, "PART_MoveDown");
        var remove = FindPart<Button>(editor, "PART_Remove");

        TestAssert.False(add.IsVisible, "The source list hides the inline Add button.");
        TestAssert.Equal("ROG Astral", list.SelectedItem as string);
        TestAssert.True(moveUp.Bounds.X > list.Bounds.X, "The actions should be beside the list.");

        Click(window, moveUp);
        TestAssert.Equal("ROG Astral", model.Sources[0]);
        TestAssert.Equal("ROG Astral", list.SelectedItem as string);
        TestAssert.False(moveUp.IsEffectivelyEnabled, "The first row cannot move up.");

        Click(window, moveDown);
        TestAssert.Equal("ROG Astral", model.Sources[1]);

        Click(window, remove);
        TestAssert.False(model.Sources.Contains("ROG Astral"), "The Remove action should remove the selected item.");
        TestAssert.Equal("WireView Pro II", model.SelectedSource);

        editor.ShowAddButton = true;
        editor.ShowMoveUpButton = false;
        editor.ShowMoveDownButton = false;
        editor.ShowRemoveButton = false;
        Dispatcher.UIThread.RunJobs();
        TestAssert.True(add is { IsVisible: true, IsEffectivelyEnabled: true }, "The Add action can be enabled independently.");
        TestAssert.False(moveUp.IsVisible || moveDown.IsVisible || remove.IsVisible, "Each selection action can be hidden.");
        Click(window, add);
        TestAssert.Equal("ROG Astral", model.Sources[^1]);
        TestAssert.Equal("ROG Astral", model.SelectedSource);

        editor.ShowAddButton = false;
        Dispatcher.UIThread.RunJobs();
        TestAssert.True(Math.Abs(editor.Bounds.Width - list.Bounds.Width) < 0.01, "A hidden toolbar should take no space.");

        // Restore the fixture for the subsequent preview captures.
        model.Sources.Move(model.Sources.Count - 1, 1);
        editor.ShowMoveUpButton = true;
        editor.ShowMoveDownButton = true;
        editor.ShowRemoveButton = true;
    }

    private static void EditableListVirtualizesRows()
    {
        var editor = new EditableList
        {
            ItemsSource = Enumerable.Range(0, 1_000).ToArray(),
            ShowAddButton = false,
            ShowMoveUpButton = false,
            ShowMoveDownButton = false,
            ShowRemoveButton = false
        };
        var window = new Window { Width = 350, Height = 500, Content = new StackPanel { Children = { editor } } };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            var list = FindPart<ListBox>(editor, "PART_List");
            var realized = list.GetVisualDescendants().OfType<ListBoxItem>().Count();
            TestAssert.True(realized is > 0 and < 40, $"Expected a virtualized viewport, but {realized} rows were realized.");
        }
        finally
        {
            window.Close();
        }
    }

    private static void SavedAppearanceRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rtxlens-{Guid.NewGuid():N}.json");
        var store = new AppearanceStore(path);
        var palette = new ThemePalette(Colors.Cyan, Colors.Pink, Colors.Gold, Colors.Black);
        var expected = AppearanceSettings.FromPalette(lightTheme: true, palette);

        try
        {
            Task.Run(() => store.SaveAsync(expected)).GetAwaiter().GetResult();
            var actual = Task.Run(store.LoadAsync).GetAwaiter().GetResult();
            TestAssert.Equal(expected, actual);
            TestAssert.Equal<ThemePalette?>(palette, actual?.ReadPalette());

            var defaults = AppearanceSettings.FromPalette(lightTheme: false, null);
            Task.Run(() => store.SaveAsync(defaults)).GetAwaiter().GetResult();
            TestAssert.Equal(defaults, Task.Run(store.LoadAsync).GetAwaiter().GetResult());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void DiagnosticPickersFollowPalette(string? screenshots)
    {
        var app = Application.Current!;
        var previousTheme = app.RequestedThemeVariant;
        var palette = new PaletteResources(app);
        var dialog = new RTXLens.App.Views.Diagnostics.DiagnosticReportWindow("http://localhost:59273/", CancellationToken.None);
        dialog.Show();
        try
        {
            dialog.GetVisualDescendants().OfType<ComboBox>().Single().SelectedItem = "Custom range";
            foreach (var mode in new[] { "dark", "light", "custom" })
            {
                app.RequestedThemeVariant = mode == "light" ? ThemeVariant.Light : ThemeVariant.Dark;
                palette.Apply(mode == "custom" ? ThemePreset.All.Single(p => p.Id == "ultraviolet").Dark : null);
                dialog.MouseMove(new Point(0, 0), RawInputModifiers.None);
                dialog.GetVisualDescendants().OfType<ComboBox>().Single().Focus();
                Dispatcher.UIThread.RunJobs();
                TestAssert.True(app.TryGetResource("HudInputColor", dialog.ActualThemeVariant, out var inputColor), "Missing input color.");
                TestAssert.True(app.TryGetResource("HudInputStrokeColor", dialog.ActualThemeVariant, out var borderColor), "Missing input border.");
                var fill = (Color)inputColor!;
                var stroke = (Color)borderColor!;
                var date = dialog.GetVisualDescendants().OfType<CalendarDatePicker>().First();
                var time = dialog.GetVisualDescendants().OfType<TimePicker>().First();
                var dateFrame = date.GetTemplateDescendants().OfType<Border>().Single(b => b.Name == "Background" && b.TemplatedParent == date);
                var timeButton = FindPart<Button>(time, "PART_FlyoutButton");
                TestAssert.Equal(fill, ReadColor(dateFrame.Background));
                TestAssert.Equal(stroke, ReadColor(dateFrame.BorderBrush));
                TestAssert.Equal(fill, ReadColor(timeButton.Background));
                TestAssert.Equal(stroke, ReadColor(timeButton.BorderBrush));
                Capture(dialog, screenshots, $"diagnostic-range-{mode}.png");

                date.IsDropDownOpen = true;
                Dispatcher.UIThread.RunJobs();
                var calendar = (Calendar)date.GetTemplateDescendants().OfType<Popup>().Single().Child!;
                TestAssert.Equal(fill, ReadColor(calendar.Background));
                TestAssert.Equal(stroke, ReadColor(calendar.BorderBrush));
                date.IsDropDownOpen = false;

                Click(dialog, timeButton);
                var popup = time.GetTemplateDescendants().OfType<Popup>().Single();
                TestAssert.True(popup.IsOpen, "Time flyout should be open.");
                var presenter = (TimePickerPresenter)popup.Child!;
                TestAssert.Equal(fill, ReadColor(presenter.Background));
                TestAssert.Equal(stroke, ReadColor(presenter.BorderBrush));
                popup.IsOpen = false;
            }
        }
        finally
        {
            dialog.Close();
            palette.Apply(null);
            app.RequestedThemeVariant = previousTheme;
        }
    }

    private static void Click(Window window, Control control)
    {
        var position = CenterInWindow(control, window);
        window.MouseDown(position, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(position, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static Point CenterInWindow(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static Color ReadColor(IBrush? brush) =>
        brush is ISolidColorBrush solid ? solid.Color : throw new InvalidOperationException("Expected a solid theme brush.");

    internal static void Capture(Window window, string? directory, string fileName)
    {
        if (directory is null)
        {
            return;
        }

        Directory.CreateDirectory(directory);

        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The preview did not render a frame.");
        frame.Save(Path.Combine(directory, fileName), PngBitmapEncoderOptions.Default);
    }
}