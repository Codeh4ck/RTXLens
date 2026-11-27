extern alias Notifier;

using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using RTXLens.Client.Serialization;
using RTXLens.Contracts.Warnings;
using RTXLens.Presentation.Settings;
using RTXLens.Tests.Infrastructure;
using RTXLens.WarningUI;

namespace RTXLens.UI.Tests.Controls;

internal static class NotifierIntegration
{
    // Start the real helper without a dashboard, startup registration, audible output or user preferences.
    public static int Run(string? screenshotPath)
    {
        var root = Directory.CreateTempSubdirectory("rtxlens-notifier-");
        using var preview = new EventWaitHandle(false, EventResetMode.AutoReset);
        var frames = Channel.CreateUnbounded<WarningSnapshot>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var server = builder.Build();
        server.UseWebSockets();
        server.MapGet("/warnings/stream", async (HttpContext context) =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await foreach (var frame in frames.Reader.ReadAllAsync(context.RequestAborted))
            {
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(frame, ClientJsonContext.Default.WarningSnapshot),
                    WebSocketMessageType.Text, true, context.RequestAborted);
            }
        });
        try
        {
            server.StartAsync().GetAwaiter().GetResult();
            var settings = new PreferencesStore(Path.Combine(root.FullName, "preferences.json"));
            var prefs = new AppPreferences { ServiceAddress = server.Urls.Single(), WarningSoundEnabled = false, Theme = AppTheme.Light };
            settings.SaveAsync(prefs).GetAwaiter().GetResult();
            var result = 1;
            var app = AppBuilder.Configure(() => new Notifier::RTXLens.Notifier.NotifierApp(root.FullName, preview))
                .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
            app.AfterSetup(_ => Dispatcher.UIThread.Post(async () =>
            {
                var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    var warning = new WarningState("pin", "asus-astral", "gpu", "current", "Pin overcurrent", "P3: 9.2 A",
                        WarningPhase.Active, "episode", now, new(), 1, now);
                    var feed = new WarningSnapshot("service", 1, true, [warning]);
                    frames.Writer.TryWrite(feed);
                    await Until(() => lifetime.Windows.OfType<WarningPopup>().Any(w => w.IsVisible));
                    var popup = lifetime.Windows.OfType<WarningPopup>().Single();
                    TestAssert.False(popup.ShowActivated || popup.ShowInTaskbar, "A warning must open without taking focus or adding a taskbar entry.");
                    TestAssert.Equal(Avalonia.Styling.ThemeVariant.Light, popup.ActualThemeVariant);
                    static bool Shows(WarningPopup window, string text) => window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == text);
                    frames.Writer.TryWrite(feed with { States = [warning with { Detail = "P3: 9.6 A" }] });
                    await Until(() => Shows(popup, "9.6 A"));

                    var imbalance = warning with { Id = "spread", EpisodeId = "second", NotificationSequence = 2,
                        Title = "Debug · ROG Astral · Current imbalance", StartedAt = now.AddSeconds(1),
                        Detail = "P1: 6 A / P5: 0.52 A - 168.1% difference (Profile: Astral)" };
                    frames.Writer.TryWrite(feed with { Sequence = 2, States = [warning, imbalance] });
                    await Until(() => Shows(popup, "2 warnings") && Shows(popup, "168.1%"));
                    foreach (var text in new[] { "Debug · ROG Astral", "Current imbalance", "P1", "6 A", "P5", "0.52 A", "Profile: Astral", "Pin overcurrent" })
                    {
                        TestAssert.True(Shows(popup, text), text);
                    }

                    TestAssert.Equal(1, lifetime.Windows.Count);

                    if (screenshotPath is not null)
                    {
                        popup.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
                        using var image = popup.CaptureRenderedFrame() ?? throw new InvalidOperationException("Warning popup did not render.");
                        image.Save(screenshotPath, PngBitmapEncoderOptions.Default);
                    }

                    preview.Set();
                    await Until(() => lifetime.Windows.OfType<WarningPopup>().Any(w => Shows(w, "Test warning")));
                    var example = lifetime.Windows.OfType<WarningPopup>().Single(w => w != popup);
                    TestAssert.True(example.Position != popup.Position, "A preview must not cover the live popup.");
                    example.Close();
                    frames.Writer.TryWrite(feed with { Sequence = 2, States = [imbalance] });
                    await Until(() => Shows(popup, "Current imbalance") && !Shows(popup, "Pin overcurrent"));
                    frames.Writer.TryWrite(feed with { Sequence = 2, States = [] });
                    await Until(() => !popup.IsVisible);
                    Console.WriteLine("PASS: Standalone notifier opened, updated, cleared and previewed a themed warning without a dashboard.");
                    result = 0;
                }
                catch (Exception error) { Console.Error.WriteLine(error); }
                finally { lifetime.Shutdown(result); }
            }));
            app.StartWithClassicDesktopLifetime([]);
            return result;
        }
        finally
        {
            frames.Writer.TryComplete();
            // Avalonia's dispatcher has stopped. Finish asynchronous host teardown on the pool.
            Task.Run(async () =>
            {
                await server.StopAsync();
                await server.DisposeAsync();
            }).GetAwaiter().GetResult();
            root.Delete(recursive: true);
        }
    }

    private static async Task Until(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!ready())
        {
            if (DateTime.UtcNow >= until)
            {
                throw new TimeoutException("Timed out waiting for the warning window.");
            }

            await Task.Delay(20);
        }
    }
}
