using System.Net.WebSockets;
using System.Text.Json;
using Concierge.Contracts;
using Concierge.Web.Services;
using Microsoft.Extensions.Options;

namespace Concierge.Web.Voice;

/// <summary>
/// Browser voice WebSocket on the normal UI listener. Browsers cannot set headers on a WebSocket, so
/// the chat credential travels as the second <c>Sec-WebSocket-Protocol</c> value after
/// <see cref="Subprotocol"/>, keeping it out of URLs and request logs.
/// </summary>
internal static class VoiceRelayEndpoint
{
    public const string Path = "/api/concierge/voice";
    public const string Subprotocol = "concierge-voice";

    public static async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new ConciergeApiError(
                "invalid_request",
                $"Open {Path} as a WebSocket with the '{Subprotocol}' subprotocol and the chat credential."));
            return;
        }

        var requested = context.WebSockets.WebSocketRequestedProtocols;
        var credential = requested.Count == 2 && requested[0] == Subprotocol ? requested[1] : null;
        Guid? voiceSessionId = Guid.TryParse(context.Request.Query["voiceSession"], out var parsed) ? parsed : null;

        var services = context.RequestServices;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(VoiceRelayEndpoint).FullName!);
        using var browser = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            SubProtocol = Subprotocol,
            KeepAliveInterval = TimeSpan.FromSeconds(15),
        });

        var options = services.GetRequiredService<IOptions<DeepgramVoiceOptions>>().Value;
        var publicUrl = services.GetRequiredService<IConfiguration>()["THINK_PUBLIC_URL"];
        if (string.IsNullOrWhiteSpace(options.ApiKey) || !VoiceAgentSettings.TryGetThinkUrl(publicUrl, out var thinkUrl))
        {
            logger.LogWarning(
                "Voice is not configured: set Deepgram__ApiKey and an HTTPS THINK_PUBLIC_URL (AppHost provides both).");
            await VoiceCloseCodes.CloseAsync(browser, VoiceCloseCodes.Unavailable,
                "Voice is not configured on this server. You can keep chatting by text.");
            return;
        }

        ConciergeVoiceAuthorization authorization;
        var concierge = services.GetRequiredService<IConciergeService>();
        try
        {
            authorization = await concierge.AuthorizeVoiceAsync(credential, context.RequestAborted);
        }
        catch (ConciergeApiException exception) when (exception.StatusCode == StatusCodes.Status401Unauthorized)
        {
            await VoiceCloseCodes.CloseAsync(browser, VoiceCloseCodes.Unauthorized, exception.Message);
            return;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                "Voice authorization failed. Cause types: {CauseTypes}.",
                ConciergeDiagnostics.ExceptionTypeChain(exception));
            await VoiceCloseCodes.CloseAsync(browser, VoiceCloseCodes.Unavailable,
                "The concierge is temporarily unavailable. Please try again.");
            return;
        }

        var registry = services.GetRequiredService<VoiceSessionRegistry>();
        var connection = registry.Open(authorization.Session, voiceSessionId);
        VoiceTelemetry.ActiveRelays.Add(1);
        try
        {
            // Lets the browser keep turn IDs unique across a manual Reconnect.
            await browser.SendAsync(
                JsonSerializer.SerializeToUtf8Bytes(new { type = "VoiceSession", voiceSessionId = connection.Session.Id }),
                WebSocketMessageType.Text, true, context.RequestAborted);

            var settings = VoiceAgentSettings.Build(options, thinkUrl, connection.Bearer, authorization.HasTurns);
            await new VoiceRelay(
                browser,
                connection,
                logger,
                services.GetRequiredService<TimeProvider>(),
                TimeSpan.FromMinutes(options.IdleTimeoutMinutes),
                (turnId, cancellationToken) =>
                    concierge.MarkVoiceTurnInterruptedAsync(connection.Session.Stay, turnId, cancellationToken))
                .RunAsync(options.ApiKey, settings, context.RequestAborted);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
        {
            // The browser left before the relay started.
        }
        finally
        {
            VoiceTelemetry.ActiveRelays.Add(-1);
            registry.Close(connection);
        }
    }
}
