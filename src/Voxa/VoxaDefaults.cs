using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAI;
using Voxa.AspNetCore;
using Voxa.Audio.SileroVad;
using Voxa.Speech.Azure;
using Voxa.Speech.ElevenLabs;
using Voxa.Speech.Mistral;
using Voxa.Speech.OpenAI;

namespace Voxa;

/// <summary>
/// Entry-point extensions for the Voxa meta-package. Provides the "five lines to a voice bot"
/// experience: <c>services.AddVoxa(builder.Configuration)</c>.
/// </summary>
public static class VoxaDefaultsExtensions
{
    /// <summary>
    /// Register all built-in speech providers (OpenAI, Azure, ElevenLabs, Mistral), the Silero
    /// VAD engine, and the default OpenAI chat agent factory. Settings are bound from the
    /// <c>Voxa</c> section of <paramref name="configuration"/>.
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
            voxa.AddProvider(SileroVadDescriptors.Vad);
        });

        // TryAdd: a host-registered IVoiceAgentFactory always wins, whether it was added
        // before this call (TryAdd skips ours) or after (later registration wins at resolve).
        services.TryAddSingleton<IVoiceAgentFactory>(sp =>
            new OpenAIChatAgentFactory(sp.GetRequiredService<IConfiguration>()));

        return services;
    }
}

/// <summary>
/// Default <see cref="IVoiceAgentFactory"/> registered by the Voxa meta-package. Creates a
/// <see cref="ChatClientAgent"/> backed by the OpenAI chat completions API.
/// The model and API key come from <c>Voxa:Agent:Model</c> / <c>Voxa:Agent:ApiKey</c>,
/// falling back to <c>Voxa:OpenAI:ApiKey</c> for the key.
/// </summary>
internal sealed class OpenAIChatAgentFactory : IVoiceAgentFactory
{
    private readonly IConfiguration _configuration;

    public OpenAIChatAgentFactory(IConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public AIAgent Create(HttpContext context, VoxaAgentOptions options)
    {
        // Agent:Provider must be null or "OpenAI" when using the default factory.
        if (options.Provider is not null &&
            !string.Equals(options.Provider, "OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The default Voxa agent factory only handles Voxa:Agent:Provider 'OpenAI' " +
                $"(got '{options.Provider}'). " +
                "Register a custom IVoiceAgentFactory singleton for other providers.");
        }

        var apiKey = !string.IsNullOrEmpty(options.ApiKey)
            ? options.ApiKey
            : _configuration["Voxa:OpenAI:ApiKey"];

        // Fail with a config-key-level message rather than the opaque ArgumentException
        // ApiKeyCredential throws for an empty key.
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException(
                "No OpenAI API key configured for the default voice agent. " +
                "Set Voxa:Agent:ApiKey or Voxa:OpenAI:ApiKey (user-secrets or environment variable " +
                "Voxa__OpenAI__ApiKey), or register your own AIAgent / IChatClient / IVoiceAgentFactory in DI.");

        IChatClient chatClient = new OpenAIClient(new ApiKeyCredential(apiKey))
            .GetChatClient(options.Model)
            .AsIChatClient();

        return new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name        = "VoxaVoiceAgent",
            ChatOptions = new ChatOptions { Instructions = options.Instructions },
        });
    }
}
