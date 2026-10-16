using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Concierge.Contracts;
using Concierge.Web.Services;

namespace Concierge.Web.Voice;

/// <summary>
/// The OpenAI-compatible <c>POST /v1/chat/completions</c> handler that Deepgram's custom Think
/// provider calls. It submits only the latest user message as a voice turn to the stay workflow and
/// returns the completed answer as one SSE chunk sequence.
/// </summary>
internal static class ThinkEndpoint
{
    public const int MaxBodyBytes = 64 * 1024;

    public static async Task HandleAsync(HttpContext context)
    {
        var requestStarted = Stopwatch.GetTimestamp();
        var services = context.RequestServices;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ThinkEndpoint).FullName!);

        // Authenticate before touching the body so unauthenticated callers cannot make us buffer anything.
        if (!services.GetRequiredService<VoiceSessionRegistry>().TryAuthenticate(GetBearer(context), out var connection))
        {
            logger.LogInformation("Think request rejected: missing, unknown, revoked, or expired voice bearer.");
            await WriteErrorAsync(context, StatusCodes.Status401Unauthorized,
                "Provide the voice session's bearer credential in the Authorization header.",
                "invalid_request_error", "invalid_api_key");
            VoiceTelemetry.RecordThink(requestStarted, "unauthorized");
            return;
        }

        var body = await ReadBodyAsync(context);
        if (body is null)
        {
            logger.LogInformation("Think request rejected: body exceeds {MaxBodyBytes} bytes.", MaxBodyBytes);
            await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge,
                $"The request body must be at most {MaxBodyBytes} bytes.",
                "invalid_request_error", "request_too_large");
            VoiceTelemetry.RecordThink(requestStarted, "too_large");
            return;
        }

        if (!ThinkRequest.TryParse(body, out var request, out var parseError))
        {
            logger.LogInformation("Think request rejected: {ParseError}", parseError);
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, parseError,
                "invalid_request_error", "invalid_request");
            VoiceTelemetry.RecordThink(requestStarted, "invalid");
            return;
        }

        var session = connection.Session;
        var turnId = ThinkRequest.TurnId(session.Id, connection.Number, request.UserOrdinal, request.UserMessage);
        var concierge = services.GetRequiredService<IConciergeService>();
        var started = Stopwatch.GetTimestamp();
        // The turn ID is also the Temporal update ID, so the request span can be matched to the
        // UpdateWorkflow span when a Deepgram retry joins an earlier request's turn.
        Activity.Current?
            .SetTag("concierge.voice.turn_id", turnId)
            .SetTag("concierge.voice.connection", connection.Number)
            .SetTag("concierge.voice.user_ordinal", request.UserOrdinal);

        // The shared task outlives this request: a Deepgram abort or retry must not cancel or
        // resubmit admitted Temporal work. ConciergeService holds no disposable scoped state.
        var turn = session.JoinOrStart(turnId, () => concierge.SubmitVoiceTurnAsync(
            session.Stay, turnId, request.UserMessage, CancellationToken.None));

        ConciergeTurnResult result;
        try
        {
            result = await turn.WaitAsync(context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation(
                "Think caller disconnected after {ElapsedMs:F0} ms; the voice turn continues in Temporal.",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            VoiceTelemetry.RecordThink(requestStarted, "caller_disconnected");
            return;
        }
        catch (ConciergeApiException exception)
        {
            logger.LogWarning("Think voice turn failed with {Code} ({StatusCode}).", exception.Code, exception.StatusCode);
            await WriteErrorAsync(context, exception.StatusCode, exception.Message,
                exception.StatusCode >= 500 ? "server_error" : "invalid_request_error", exception.Code);
            VoiceTelemetry.RecordThink(requestStarted, "failed");
            return;
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Think voice turn failed. Cause types: {CauseTypes}.",
                ConciergeDiagnostics.ExceptionTypeChain(exception));
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable,
                "The concierge is temporarily unavailable. Please try again.", "server_error", "unavailable");
            VoiceTelemetry.RecordThink(requestStarted, "unavailable");
            return;
        }

        logger.LogInformation(
            "Think voice turn answered in {ElapsedMs:F0} ms (connection {ConnectionNumber}, user message {UserOrdinal}).",
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, connection.Number, request.UserOrdinal);

        var created = services.GetRequiredService<TimeProvider>().GetUtcNow().ToUnixTimeSeconds();
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.WriteAsync(
            ThinkRequest.SseFrames(turnId, request.Model, created, result.AssistantMessage),
            context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
        VoiceTelemetry.RecordThink(requestStarted, "answered");
    }

    private static string? GetBearer(HttpContext context) =>
        AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var header)
        && StringComparer.OrdinalIgnoreCase.Equals(header.Scheme, "Bearer")
            ? header.Parameter
            : null;

    /// <summary>Reads at most <see cref="MaxBodyBytes"/>; returns <see langword="null"/> when the body is larger.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpContext context)
    {
        if (context.Request.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        var buffer = new byte[MaxBodyBytes + 1];
        var count = 0;
        int read;
        while (count < buffer.Length
            && (read = await context.Request.Body.ReadAsync(buffer.AsMemory(count), context.RequestAborted)) > 0)
        {
            count += read;
        }

        return count > MaxBodyBytes ? null : buffer[..count];
    }

    private static Task WriteErrorAsync(HttpContext context, int statusCode, string message, string type, string code)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(
            new OpenAIErrorResponse(new OpenAIError(message, type, null, code)),
            context.RequestAborted);
    }

    private sealed record OpenAIErrorResponse(OpenAIError Error);

    private sealed record OpenAIError(string Message, string Type, string? Param, string Code);
}

/// <summary>The parts of an OpenAI chat completions request that a voice turn uses.</summary>
internal sealed record ThinkRequest(string UserMessage, int UserOrdinal, string Model)
{
    private const string DefaultModel = "concierge";

    /// <summary>
    /// Takes only the latest user message; the rest of Deepgram's history is ignored because the stay
    /// workflow owns conversation memory. The user-message count identifies the turn within a connection.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> body, out ThinkRequest request, out string error)
    {
        request = null!;
        error = string.Empty;
        try
        {
            var reader = new Utf8JsonReader(body);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("messages", out var messages)
                || messages.ValueKind != JsonValueKind.Array)
            {
                error = "Send a JSON object with a 'messages' array.";
                return false;
            }

            var ordinal = 0;
            JsonElement latest = default;
            foreach (var message in messages.EnumerateArray())
            {
                if (message.ValueKind == JsonValueKind.Object
                    && message.TryGetProperty("role", out var role)
                    && role.ValueKind == JsonValueKind.String
                    && role.ValueEquals("user"))
                {
                    ordinal++;
                    latest = message;
                }
            }

            if (ordinal == 0)
            {
                error = "Include at least one message with role 'user'.";
                return false;
            }

            if (!latest.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(content.GetString()))
            {
                error = "The latest 'user' message must have nonblank string 'content'.";
                return false;
            }

            var model = root.TryGetProperty("model", out var modelValue)
                && modelValue.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(modelValue.GetString())
                    ? modelValue.GetString()!
                    : DefaultModel;
            request = new ThinkRequest(content.GetString()!.Trim(), ordinal, model);
            return true;
        }
        catch (JsonException)
        {
            error = "Send a valid JSON request body.";
            return false;
        }
    }

    /// <summary>
    /// <c>v:{voiceSessionId}:{connectionNumber}:{userOrdinal}:{messageHash}</c>. The message hash separates
    /// resumed speech that changes the latest text without changing its ordinal; the connection number keeps
    /// IDs unique across manual reconnects.
    /// </summary>
    public static string TurnId(Guid voiceSessionId, int connectionNumber, int userOrdinal, string userMessage)
    {
        var messageHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userMessage)));
        return $"v:{voiceSessionId:N}:{connectionNumber}:{userOrdinal}:{messageHash}";
    }

    /// <summary>Assistant-role chunk, one content chunk, the stop chunk, then <c>[DONE]</c>.</summary>
    public static string SseFrames(string id, string model, long created, string answer)
    {
        string Frame(object delta, string? finishReason) =>
            "data: " + JsonSerializer.Serialize(new
            {
                id,
                @object = "chat.completion.chunk",
                created,
                model,
                choices = new[] { new { index = 0, delta, finish_reason = finishReason } },
            }) + "\n\n";

        return Frame(new { role = "assistant" }, null)
            + Frame(new { content = answer }, null)
            + Frame(new { }, "stop")
            + "data: [DONE]\n\n";
    }
}
