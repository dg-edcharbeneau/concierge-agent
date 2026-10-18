using System.Text.Json;
using Concierge.Contracts;

namespace Concierge.Web.Voice;

/// <summary>Validation and response framing for the one client-side end-chat function.</summary>
internal static class DeepgramFunctionProtocol
{
    public static bool TryCreateEndChatResponse(
        ReadOnlySpan<byte> message,
        VoiceConnection connection,
        out byte[] response)
    {
        response = [];
        try
        {
            using var document = JsonDocument.Parse(message.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || !type.ValueEquals("FunctionCallRequest")
                || !root.TryGetProperty("functions", out var functions)
                || functions.ValueKind != JsonValueKind.Array
                || functions.GetArrayLength() != 1)
            {
                return false;
            }

            var function = functions[0];
            if (function.ValueKind != JsonValueKind.Object
                || !function.TryGetProperty("client_side", out var clientSide)
                || clientSide.ValueKind != JsonValueKind.True
                || !function.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(id.GetString())
                || id.GetString()!.Length > 128
                || !function.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String
                || !StringComparer.Ordinal.Equals(name.GetString(), ConciergeEndChat.FunctionName)
                || !TryReadEmptyArguments(function, out _)
                || !connection.ConfirmEndChatCall(id.GetString()!))
            {
                return false;
            }

            response = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "FunctionCallResponse",
                id = id.GetString(),
                name = name.GetString(),
                content = "End chat confirmed.",
            });
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool HandleCancellation(ReadOnlySpan<byte> message, VoiceConnection connection)
    {
        try
        {
            using var document = JsonDocument.Parse(message.ToArray());
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.ValueEquals("FunctionCallCancelled")
                && root.TryGetProperty("functions", out var functions)
                && functions.ValueKind == JsonValueKind.Array)
            {
                var cancelled = false;
                foreach (var function in functions.EnumerateArray())
                {
                    if (function.ValueKind == JsonValueKind.Object
                        && function.TryGetProperty("id", out var id)
                        && id.ValueKind == JsonValueKind.String
                        && function.TryGetProperty("name", out var name)
                        && name.ValueKind == JsonValueKind.String
                        && StringComparer.Ordinal.Equals(name.GetString(), ConciergeEndChat.FunctionName))
                    {
                        cancelled |= connection.CancelEndChatCall(id.GetString()!);
                    }
                }

                return cancelled;
            }

            return false;
        }
        catch (JsonException)
        {
            // A malformed cancellation must never be treated as confirmation.
            return false;
        }
    }

    private static bool TryReadEmptyArguments(JsonElement root, out JsonElement arguments)
    {
        arguments = default;
        if (!root.TryGetProperty("arguments", out arguments))
        {
            return false;
        }

        if (arguments.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var parsed = JsonDocument.Parse(arguments.GetString()!);
                arguments = parsed.RootElement.Clone();
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return arguments.ValueKind == JsonValueKind.Object
            && !arguments.EnumerateObject().Any();
    }
}
