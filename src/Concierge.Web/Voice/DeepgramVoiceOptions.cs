using System.Text.Json;
using Concierge.Contracts;

namespace Concierge.Web.Voice;

/// <summary>Web's Deepgram Voice Agent configuration (section <c>Deepgram</c>).</summary>
public sealed class DeepgramVoiceOptions
{
    public const string SectionName = "Deepgram";

    /// <summary>Server-held key; never sent to the browser or logged.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Speech-to-text model.</summary>
    public string ListenModel { get; set; } = "flux-general-en";

    /// <summary>Text-to-speech voice model.</summary>
    public string SpeakModel { get; set; } = "flux-sienna-en";

    /// <summary>Minutes without speech before ending an otherwise idle chat.</summary>
    public int IdleTimeoutMinutes { get; set; } = 2;

    /// <summary>TTS speech rate (0.5 to 1.5, default 1.0).</summary>
    public double Speed { get; set; } = 1.2;
}

/// <summary>Builds the Voice Agent <c>Settings</c> message for one relay connection.</summary>
internal static class VoiceAgentSettings
{
    public const int InputSampleRate = 16_000;
    public const int OutputSampleRate = 24_000;

    internal const string FirstTimeGreeting =
        "Hello, and welcome to Hotel ZGY. I'm your concierge. How can I help you today?";

    internal const string WelcomeBackGreeting =
        "Welcome back to Hotel ZGY. How can I help you now?";

    /// <summary>
    /// Deepgram listens and speaks; Think goes to our endpoint with the connection's bearer. The prompt
    /// is required by the provider schema but unused, because the stay workflow owns instructions.
    /// MIP opt-out is top-level, as confirmed against the live Voice Agent.
    /// </summary>
    public static byte[] Build(DeepgramVoiceOptions options, Uri thinkUrl, string bearer, bool hasTurns) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "Settings",
            mip_opt_out = true,
            audio = new
            {
                input = new { encoding = "linear16", sample_rate = InputSampleRate },
                output = new { encoding = "linear16", sample_rate = OutputSampleRate, container = "none" },
            },
            agent = new
            {
                language = "en",
                greeting = hasTurns ? WelcomeBackGreeting : FirstTimeGreeting,
                listen = new { provider = new { type = "deepgram", model = options.ListenModel } },
                think = new
                {
                    provider = new { type = "open_ai", model = "concierge" },
                    prompt = "Hotel ZGY concierge. Answers come from the custom endpoint.",
                    functions = new[]
                    {
                        new
                        {
                            name = ConciergeEndChat.FunctionName,
                            description = "End the chat only when the guest clearly and explicitly asks to end the conversation.",
                            parameters = new
                            {
                                type = "object",
                                properties = new { },
                                required = Array.Empty<string>(),
                            },
                            defer_until_eot = true,
                        },
                    },
                    endpoint = new
                    {
                        url = thinkUrl.AbsoluteUri,
                        headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + bearer },
                    },
                },
                speak = new
                {
                    provider = new
                    {
                        type = "deepgram",
                        model = options.SpeakModel,
                        speed = options.Speed,
                    },
                },
            },
        });

    /// <summary>
    /// Resolves the public Think URL from <c>THINK_PUBLIC_URL</c>; Deepgram can call only HTTPS.
    /// </summary>
    public static bool TryGetThinkUrl(string? publicBaseUrl, out Uri thinkUrl)
    {
        thinkUrl = null!;
        if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        thinkUrl = new Uri(baseUri, "/v1/chat/completions");
        return true;
    }
}
