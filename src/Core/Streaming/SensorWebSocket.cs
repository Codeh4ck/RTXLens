using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace RTXLens.Core.Streaming;

public static class SensorWebSocket
{
    public static async Task StreamAsync(HttpContext context, SensorStreamHub hub, IHostApplicationLifetime lifetime,
        Func<byte[], byte[]>? formatFrame = null)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var subscription = hub.Subscribe();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);

        var send = SendAsync(socket, subscription, formatFrame, stop.Token);
        var receive = ReceiveAsync(socket, stop.Token);

        await Task.WhenAny(send, receive);

        // Completing a subscription is a server-initiated close. Canceling ReceiveAsync first would abort the socket.
        if (send.IsCompletedSuccessfully && !receive.IsCompleted && socket.State == WebSocketState.Open)
        {
            using var closeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "Stream unavailable", closeDeadline.Token);
                await receive.WaitAsync(closeDeadline.Token);
            }
            catch (OperationCanceledException)
            {
                // The close frame was sent; bound the wait for acknowledgment.
            }
            catch (WebSocketException)
            {
                // Peer disconnected during the handshake.
            }
        }

        await stop.CancelAsync();

        try
        {
            await Task.WhenAll(send, receive);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
        }

        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                var code = receive.IsCompletedSuccessfully ? receive.Result : WebSocketCloseStatus.EndpointUnavailable;
                await socket.CloseOutputAsync(code, "Stream closed", deadline.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebSocketException)
            {
            }
        }
    }

    private static async Task SendAsync(WebSocket socket, Subscription subscription, Func<byte[], byte[]>? formatFrame,
        CancellationToken cancellationToken)
    {
        await foreach (var frame in subscription.Reader.ReadAllAsync(cancellationToken))
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));

            var payload = formatFrame is null ? frame : formatFrame(frame);
            await socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, true, deadline.Token);
        }
    }

    private static async Task<WebSocketCloseStatus> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[256];
        var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);

        return result.MessageType == WebSocketMessageType.Close ? WebSocketCloseStatus.NormalClosure : WebSocketCloseStatus.PolicyViolation;
    }
}
