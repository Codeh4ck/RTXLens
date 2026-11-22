using RTXLens.Contracts.Monitoring;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.RateLimiting;
using RTXLens.Core.Streaming;
using RTXLens.Service.Endpoints;

namespace RTXLens.Service.Mobile;

internal static class MobileAccessListener
{
    private static readonly object DeviceIdentity = new();

    public static async Task<WebApplication> StartAsync(IPAddress address, int port, MobileAccessCertificate certificate,
        MobilePairing pairing, SensorStreamHub hub, CancellationToken cancellationToken, Func<byte[], byte[]>? formatFrame = null,
        MobileHistoryEndpoints? history = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });

        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(3));

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxConcurrentConnections = 32;
            options.Limits.MaxConcurrentUpgradedConnections = 16;
            options.Limits.MaxRequestBodySize = 1024;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Listen(address, port, endpoint => endpoint.UseHttps(https => https.ServerCertificateSelector = (_, _) => certificate.GetCertificate()));
        });

        builder.Services.AddSingleton(hub);
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddFixedWindowLimiter("invite", limiter => ConfigureLimiter(limiter, 8));
            options.AddFixedWindowLimiter("claim", limiter => ConfigureLimiter(limiter, 60));
            options.AddFixedWindowLimiter("mobile-read", limiter => ConfigureLimiter(limiter, 240));
            options.AddFixedWindowLimiter("mobile-registration", limiter => ConfigureLimiter(limiter, 30));
        });

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20),
            KeepAliveTimeout = TimeSpan.FromSeconds(15)
        });

        app.UseRateLimiter();
        var paired = app.MapGroup("").AddEndpointFilter(async (context, next) =>
        {
            var httpContext = context.HttpContext;
            var headers = httpContext.Request.Headers.Authorization;

            if (headers.Count != 1 || !AuthenticationHeaderValue.TryParse(headers[0], out var header)
                || !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
                || !pairing.TryAuthenticate(header.Parameter, out var deviceId, out var revoked))
            {
                return Results.Unauthorized();
            }

            if (httpContext.WebSockets.IsWebSocketRequest)
            {
                httpContext.Response.OnStarting(() =>
                {
                    if (httpContext.Response.StatusCode == StatusCodes.Status101SwitchingProtocols)
                    {
                        pairing.RecordConnection(deviceId, httpContext.Connection.RemoteIpAddress);
                    }

                    return Task.CompletedTask;
                });
            }

            await using var registration = revoked.Register(httpContext.Abort);
            httpContext.Items[DeviceIdentity] = deviceId;
            httpContext.Response.Headers.CacheControl = "no-store";

            return await next(context);
        });

        paired.MapSensorsEndpoint(formatFrame);
        paired.MapGet(MobileProtocol.Session, Results.NoContent).RequireRateLimiting("mobile-read");

        paired.MapPost(MobileProtocol.Registration, (HttpContext context, MobileNotificationRegistration request) =>
        {
            if (request.Token is not null && !ExpoMobilePushProvider.IsToken(request.Token))
            {
                return Results.BadRequest();
            }

            return context.Items[DeviceIdentity] is Guid id && pairing.SetPushToken(id, request.Token) ? Results.NoContent() : Results.Unauthorized();
        }).RequireRateLimiting("mobile-registration");

        paired.MapPost(MobileProtocol.RegistrationV2, (HttpContext context, MobileNotificationRegistrationV2 request) =>
        {
            if ((request.Token is not null && !ExpoMobilePushProvider.IsToken(request.Token)) || request.Preferences is not { IsValid: true })
            {
                return Results.BadRequest();
            }

            return context.Items[DeviceIdentity] is Guid id && pairing.SetPushToken(id, request.Token, preferences: request.Preferences)
                ? Results.NoContent()
                : Results.Unauthorized();
        }).RequireRateLimiting("mobile-registration");

        if (history is not null)
        {
            paired.MapGet(MobileProtocol.Incidents, history.List).RequireRateLimiting("mobile-read");
            paired.MapGet(MobileProtocol.Incidents + "/{kind}/{id:guid}", history.Detail).RequireRateLimiting("mobile-read");
        }

        app.MapPost(MobileProtocol.Pairing, (HttpContext context, MobilePairingRequest request) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return pairing.Begin(request) is { } claim ? Results.Ok(claim) : Results.NotFound();
        }).RequireRateLimiting("invite");

        app.MapPost(MobileProtocol.CompletePairing, (HttpContext context, MobilePairingCompletion request) =>
        {
            context.Response.Headers.CacheControl = "no-store";

            var (valid, credential) = pairing.Complete(request);
            if (!valid)
            {
                return Results.NotFound();
            }

            return credential is null ? Results.StatusCode(StatusCodes.Status202Accepted) : Results.Ok(credential);
        }).RequireRateLimiting("claim");

        try
        {
            await app.StartAsync(cancellationToken);
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    public static Uri GetAddress(WebApplication app) => new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());

    private static void ConfigureLimiter(FixedWindowRateLimiterOptions options, int permits)
    {
        options.PermitLimit = permits;
        options.Window = TimeSpan.FromMinutes(1);
    }

    private sealed record MobileNotificationRegistration([property: System.Text.Json.Serialization.JsonRequired] string? Token);
    private sealed record MobileNotificationRegistrationV2([property: System.Text.Json.Serialization.JsonRequired] string? Token, MobileNotificationPreferences? Preferences);
}
