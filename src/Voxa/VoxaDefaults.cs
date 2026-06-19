using System.ClientModel;
using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAI;
using Voxa.AspNetCore;
using Voxa.Audio.SileroVad;
using Voxa.Speech.Azure;
using Voxa.Speech.ElevenLabs;
using Voxa.Speech.Kokoro;
using Voxa.Speech.Mistral;
using Voxa.Speech.OpenAI;
using Voxa.Speech.Piper;
using Voxa.Speech.WhisperCpp;

namespace Voxa;

/// <summary>
/// Entry-point extensions for the Voxa meta-package. Provides the "five lines to a voice bot"
/// experience: <c>services.AddVoxa(builder.Configuration)</c>.
/// </summary>
public static class VoxaDefaultsExtensions
{
    /// <summary>
    /// Register all built-in speech providers — cloud (OpenAI, Azure, ElevenLabs, Mistral) and
    /// local/offline (WhisperCpp STT, Piper and Kokoro TTS) — the Silero VAD engine, and the
    /// default agent factory (OpenAI chat, or the keyless Echo agent for demos/CI). Settings are
    /// bound from the <c>Voxa</c> section of <paramref name="configuration"/>.
    /// </summary>
    public static IServiceCollection AddVoxa(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddVoxa(configuration, voxa =>
        {
            voxa.AddProvider(OpenAISpeechDescriptors.Stt);
            voxa.AddProvider(OpenAISpeechDescriptors.Tts);
            voxa.AddProvider(AzureSpeechDescriptors.Stt);
            voxa.AddProvider(AzureSpeechDescriptors.Tts);
            voxa.AddProvider(ElevenLabsDescriptors.Tts);
            voxa.AddProvider(MistralDescriptors.Tts);
            voxa.AddProvider(MistralDescriptors.Stt);   // Voxtral STT (VVL-001 WS2)
            voxa.AddProvider(SileroVadDescriptors.Vad);
            // The local/offline tier (VLS-001): no API keys, no network after first-run download.
            voxa.AddProvider(WhisperCppDescriptors.Stt);
            voxa.AddProvider(PiperDescriptors.Tts);
            voxa.AddProvider(KokoroDescriptors.Tts);
        });

        // TryAdd: a host-registered IVoiceAgentFactory always wins, whether it was added
        // before this call (TryAdd skips ours) or after (later registration wins at resolve).
        // The factory captures the configuration passed to AddVoxa rather than resolving
        // IConfiguration from DI — the same rule the validator and composer follow, because
        // ASP.NET registers IConfiguration implicitly but plain-ServiceCollection hosts
        // (Voxa Studio, tests) do not, and resolving it there throws at first session start.
        services.TryAddSingleton<IVoiceAgentFactory>(_ => new DefaultAgentFactory(configuration));

        return services;
    }
}

/// <summary>
/// Default <see cref="IVoiceAgentFactory"/> registered by the Voxa meta-package, dispatching on
/// <c>Voxa:Agent:Provider</c>: null/<c>"OpenAI"</c> → a <see cref="ChatClientAgent"/> over the
/// OpenAI chat completions API (model/key from <c>Voxa:Agent:Model</c> / <c>Voxa:Agent:ApiKey</c>,
/// key falling back to <c>Voxa:OpenAI:ApiKey</c>); <c>"Ollama"</c> → the same
/// <see cref="ChatClientAgent"/> pointed at a local Ollama daemon's OpenAI-compatible endpoint
/// (<c>Voxa:Agent:BaseUrl</c>, default <c>http://localhost:11434/v1</c>; keyless, fully offline —
/// VLS-003); <c>"Echo"</c> → a keyless diagnostic agent that repeats the transcript back — it closes
/// the no-API-key loop for first-touch demos and zero-cost CI conversations (VLS-001 WS4).
/// </summary>
internal sealed class DefaultAgentFactory : IVoiceAgentFactory
{
    private readonly IConfiguration _configuration;

    public DefaultAgentFactory(IConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public AIAgent Create(IServiceProvider services, VoxaAgentOptions options)
    {
        // Same checks VoxaDefaultsGuard runs at startup via Validate() — repeated here so
        // hosts that never arm the guard still get config-key-level messages, not the opaque
        // ArgumentException ApiKeyCredential throws for an empty key.
        var errors = CheckUsable(options);
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(" ", errors));

        if (IsEcho(options))
        {
            return new ChatClientAgent(new EchoChatClient(), new ChatClientAgentOptions
            {
                Name = "VoxaEchoAgent",
            });
        }

        IChatClient chatClient = IsOllama(options)
            // Ollama speaks the OpenAI chat-completions API, so the OpenAI client is reused, pointed at
            // the local daemon. It needs no real credential — never reuse the OpenAI key here, or it
            // would be sent as a bearer token to the Ollama endpoint (VLS-003).
            ? new OpenAIClient(
                    new ApiKeyCredential(OllamaApiKey(options)),
                    new OpenAIClientOptions { Endpoint = OllamaEndpoint() })
                .GetChatClient(OllamaModel(options))
                .AsIChatClient()
            : new OpenAIClient(new ApiKeyCredential(ResolveApiKey(options)!))
                .GetChatClient(options.Model)
                .AsIChatClient();

        return new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name        = IsOllama(options) ? "VoxaOllamaAgent" : "VoxaVoiceAgent",
            ChatOptions = new ChatOptions { Instructions = options.Instructions },
        });
    }

    /// <summary>
    /// Called by VoxaDefaultsGuard at host startup. Without this, the factory's mere presence
    /// in DI (it is always registered by the meta-package) would satisfy the guard's agent
    /// probe, deferring provider/credential failures to the first WebSocket request.
    /// </summary>
    public IReadOnlyList<string> Validate(VoxaAgentOptions options) => CheckUsable(options);

    private static bool IsEcho(VoxaAgentOptions options)
        => string.Equals(options.Provider, "Echo", StringComparison.OrdinalIgnoreCase);

    private static bool IsOllama(VoxaAgentOptions options)
        => string.Equals(options.Provider, "Ollama", StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<string> CheckUsable(VoxaAgentOptions options)
    {
        // Echo is keyless by design — always usable.
        if (IsEcho(options)) return [];

        // Ollama is a local, keyless daemon (OpenAI-compatible). Validate only the endpoint shape —
        // never probe the daemon at startup, which would make boot depend on `ollama serve` running.
        if (IsOllama(options))
        {
            return Uri.TryCreate(OllamaBaseUrl(), UriKind.Absolute, out var uri)
                   && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host)
                ? []
                :
                [
                    $"Voxa:Agent:BaseUrl '{OllamaBaseUrl()}' is not a valid http(s) URL. Point it at your " +
                    "Ollama OpenAI-compatible endpoint (default http://localhost:11434/v1).",
                ];
        }

        if (options.Provider is not null &&
            !string.Equals(options.Provider, "OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                $"The default Voxa agent factory only handles Voxa:Agent:Provider 'OpenAI', 'Ollama' or 'Echo' " +
                $"(got '{options.Provider}'). " +
                "Register a custom IVoiceAgentFactory singleton for other providers.",
            ];
        }

        if (string.IsNullOrEmpty(ResolveApiKey(options)))
        {
            return
            [
                "No OpenAI API key configured for the default voice agent. " +
                "Set Voxa:Agent:ApiKey or Voxa:OpenAI:ApiKey (user-secrets or environment variable " +
                "Voxa__OpenAI__ApiKey), set Voxa:Agent:Provider to 'Echo' for a keyless demo agent or " +
                "'Ollama' for a local LLM, or register your own AIAgent / IChatClient / IVoiceAgentFactory in DI.",
            ];
        }

        return [];
    }

    private string? ResolveApiKey(VoxaAgentOptions options)
        => !string.IsNullOrEmpty(options.ApiKey) ? options.ApiKey : _configuration["Voxa:OpenAI:ApiKey"];

    // Ollama needs no real credential; never reuse the OpenAI key (it would reach the Ollama endpoint).
    // Honour an explicit Voxa:Agent:ApiKey only (a secured gateway), else a constant placeholder.
    private static string OllamaApiKey(VoxaAgentOptions options)
        => string.IsNullOrEmpty(options.ApiKey) ? "ollama" : options.ApiKey;

    // Ollama's OpenAI-compatible base URL (VLS-003): Voxa:Agent:BaseUrl wins, else Voxa:Ollama:BaseUrl,
    // else the daemon default.
    private string OllamaBaseUrl()
        => _configuration["Voxa:Agent:BaseUrl"]
           ?? _configuration["Voxa:Ollama:BaseUrl"]
           ?? "http://localhost:11434/v1";

    private Uri OllamaEndpoint() => new(OllamaBaseUrl());

    // Voxa:Agent:Model names the Ollama model (which must be pulled, e.g. `ollama pull llama3.2`). The
    // OpenAI-oriented default is meaningless to Ollama, so fall back to a small, common local default.
    private static string OllamaModel(VoxaAgentOptions options)
        => string.IsNullOrWhiteSpace(options.Model) ||
           string.Equals(options.Model, "gpt-4o-mini", StringComparison.OrdinalIgnoreCase)
            ? "llama3.2"
            : options.Model;
}

/// <summary>
/// The keyless parrot behind <c>Voxa:Agent:Provider = "Echo"</c>. Streams the reply in several
/// chunks deliberately, so sentence aggregation and incremental TTS are genuinely exercised in
/// demos and e2e tests — not collapsed into one degenerate chunk.
/// </summary>
internal sealed class EchoChatClient : IChatClient
{
    private static string ReplyFor(IEnumerable<ChatMessage> messages)
    {
        var lastUser = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var reply = $"You said: {lastUser.Trim()}";
        return reply[^1] is '.' or '!' or '?' ? reply : reply + ".";
    }

    /// <summary>Split into 2–3 chunks at word boundaries.</summary>
    internal static IReadOnlyList<string> Chunk(string reply)
    {
        var words = reply.Split(' ');
        if (words.Length <= 2) return [reply];
        var firstCut = words.Length / 3 + 1;
        var secondCut = 2 * words.Length / 3 + 1;
        return
        [
            string.Join(' ', words[..firstCut]) + " ",
            string.Join(' ', words[firstCut..secondCut]) + (secondCut < words.Length ? " " : string.Empty),
            .. secondCut < words.Length ? new[] { string.Join(' ', words[secondCut..]) } : Array.Empty<string>(),
        ];
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ReplyFor(messages))));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var chunk in Chunk(ReplyFor(messages)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            await Task.Yield(); // genuinely incremental — updates arrive across awaits
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
