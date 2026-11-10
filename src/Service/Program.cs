using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging.EventLog;
using RTXLens.Contracts.Monitoring;
using RTXLens.Core.Diagnostics;
using RTXLens.Service.Diagnostics;
using RTXLens.Plugins.Msi;
using RTXLens.Plugins.GenericNvidia;
using RTXLens.Plugins.GenericNvidia.Endpoints;
using RTXLens.Plugins.Msi.Endpoints;
using RTXLens.Plugins.Asus;
using RTXLens.Plugins.Asus.Endpoints;
using RTXLens.Plugins.WireView;
using RTXLens.Plugins.WireView.Endpoints;
using RTXLens.Service;
using RTXLens.Service.Endpoints;

var builder = WebApplication.CreateSlimBuilder(args);
#if DEBUG
var debugInstance = true;
#else
var debugInstance = false;
#endif

if (!debugInstance)
{
    builder.Host.UseWindowsService(options => options.ServiceName = "RTXLensService");
}

builder.Logging.ClearProviders();
if (!debugInstance && OperatingSystem.IsWindows())
{
    var eventViewer = new ActivityEventLogProvider(new EventLogSettings
    {
        SourceName = "RTXLensService",
        Filter = (_, level) => level >= LogLevel.Warning
    });
    builder.Services.AddSingleton(eventViewer);
    builder.Logging.AddProvider(eventViewer);
}

if (!WindowsServiceHelpers.IsWindowsService())
{
    builder.Logging.AddConsole();
}

builder.Services.AddMonitoringCore(builder.Configuration, debugInstance);

builder.Services.AddGenericNvidiaPlugin();
builder.Services.AddWireViewPlugin();
builder.Services.AddAsusPlugin();
builder.Services.AddMsiPlugin();

builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().WithMethods("GET", "POST", "PUT").AllowAnyHeader()));

var port = debugInstance ? ServiceEndpoint.DebugPort : ServiceEndpoint.LivePort;
builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port));

await using var app = builder.Build();

app.UseCors().UseWebSockets();
app.MapMonitoringEndpoints();
app.MapWireViewEndpoints();
app.MapAsusEndpoints();
app.MapGenericNvidiaEndpoints();
app.MapMsiEndpoints();

app.Lifetime.ApplicationStarted.Register(() =>
    app.Logger.LogActivity(LogLevel.Information, "service.ready", "Service", "RTX Lens backend is ready."));
app.Lifetime.ApplicationStopping.Register(() =>
    app.Logger.LogActivity(LogLevel.Information, "service.stopping", "Service", "RTX Lens backend is stopping."));

await app.RunAsync();
