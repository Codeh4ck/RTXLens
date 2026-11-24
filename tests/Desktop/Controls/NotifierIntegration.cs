extern alias Notifier;

using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Threading;
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
    public static int Run()
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
                    var warning = new WarningState("pin", "asus-astral", "gpu", "current", "Pin overcurrent", "P1 · 9.2 A",
                        WarningPhase.Active, "episode", now, new(), 1, now);
                    var feed = new WarningSnapshot("service", 1, true, [warning]);
                    frames.Writer.TryWrite(feed);
                    await Until(() => lifetime.Windows.OfType<WarningPopup>().Any(w => w.IsVisible));
                    var popup = lifetime.Windows.OfType<WarningPopup>().Single();
                    TestAssert.False(popup.ShowActivated || popup.ShowInTaskbar, "A warning must open without taking focus or adding a taskbar entry.");
                    TestAssert.Equal(Avalonia.Styling.ThemeVariant.Light, popup.ActualThemeVariant);
                    var detail = popup.FindControl<TextBlock>("Details")!;
                    frames.Writer.TryWrite(feed with { States = [warning with { Detail = "P1 · 9.6 A" }] });
                    await Until(() => detail.Text == "P1 · 9.6 A");
                    TestAssert.Equal(1, lifetime.Windows.Count);
                    frames.Writer.TryWrite(feed with { States = [] });
                    await Until(() => !popup.IsVisible);
                    preview.Set();
                    await Until(() => lifetime.Windows.OfType<WarningPopup>().Any(w => w.FindControl<TextBlock>("Heading")?.Text == "Test warning"));
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
