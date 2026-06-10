using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Voxa.Frames;
using Voxa.Processors;
using Voxa.Services.MicrosoftAgents;
using Voxa.Speech;

namespace Voxa.AspNetCore;

/// <summary>
/// Composes the default per-connection pipeline (VAD → STT → TranscriptionFilter →
/// agent + memory → SentenceAggregator → TTS) from the registered provider descriptors and
/// the active latency profile. Consumed by the UseDefaults() route handler.
/// </summary>
public sealed class DefaultVoicePipelineComposer
{
    private readonly IOptions<VoxaOptions> _options;
    private readonly VoxaProviderRegistry _registry;
    private readonly VoxaTuningResolver _resolver;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DefaultVoicePipelineComposer> _logger;

    public DefaultVoicePipelineComposer(
        IOptions<VoxaOptions> options,
        VoxaProviderRegistry registry,
        VoxaTuningResolver resolver,
        IConfiguration configuration,
        ILogger<DefaultVoicePipelineComposer> logger)
    {
        _options = options;
        _registry = registry;
        _resolver = resolver;
        _configuration = configuration;
        _logger = logger;
    }

    public ComposedVoice Compose(HttpContext httpContext)
    {
        var o = _options.Value;
        var tuning = _resolver.Resolve(o);
        var root = _configuration.GetSection(VoxaOptions.SectionName);

        // Normally guaranteed by VoxaDefaultsGuard at startup; explicit checks here cover hosts
        // that resolve the composer directly without arming the guard.
        if (string.IsNullOrEmpty(o.Stt))
            throw new InvalidOperationException(
                "Voxa:Stt is not set. The default pipeline requires an STT provider name. " +
                $"Registered: {(_registry.SttNames.Count == 0 ? "(none)" : string.Join(", ", _registry.SttNames))}.");
        if (string.IsNullOrEmpty(o.Tts))
            throw new InvalidOperationException(
                "Voxa:Tts is not set. The default pipeline requires a TTS provider name. " +
                $"Registered: {(_registry.TtsNames.Count == 0 ? "(none)" : string.Join(", ", _registry.TtsNames))}.");

        if (!_registry.TryGetStt(o.Stt, out var stt))
            throw new InvalidOperationException($"STT provider '{o.Stt}' not found in registry. This should have been caught by VoxaDefaultsGuard.");
        if (!_registry.TryGetTts(o.Tts, out var tts))
            throw new InvalidOperationException($"TTS provider '{o.Tts}' not found in registry. This should have been caught by VoxaDefaultsGuard.");

        var parts = new List<Func<HttpContext, FrameProcessor>>();

        // Effective rates honour per-provider config overrides (e.g. Voxa:OpenAI:InputSampleRate).
        // The descriptor constants are only defaults — the processors bind the override, so the
        // VAD and the session envelope must advertise the same rate the processors actually use.
        var inputSampleRate  = stt.GetEffectiveInputSampleRate(root);
        var outputSampleRate = tts.GetEffectiveOutputSampleRate(root);

        // 1. VAD (engine names are case-insensitive, matching Profile and provider lookups)
        if (!string.Equals(o.Vad.Engine, "None", StringComparison.OrdinalIgnoreCase))
        {
            var vadSettings = new VoxaVadSettings(
                SampleRate:           inputSampleRate,
                ConfidenceThreshold:  tuning.VadConfidenceThreshold,
                MinRms:               tuning.VadMinRms,
                StartDuration:        tuning.VadStartDuration,
                StopDuration:         tuning.VadStopDuration,
                PrerollDuration:      tuning.VadPrerollDuration);

            if (string.Equals(o.Vad.Engine, "SilenceGate", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(_ => new SilenceGateProcessor(vadSettings.MinRms, vadSettings.StopDuration));
            }
            else if (_registry.TryGetVad(o.Vad.Engine, out var vadDesc))
            {
                parts.Add(ctx => vadDesc.CreateProcessor(ctx.RequestServices, vadSettings));
            }
            else
            {
                _logger.LogWarning(
                    "Voxa:Vad:Engine '{Engine}' not found in registry; falling back to SilenceGate.",
                    o.Vad.Engine);
                parts.Add(_ => new SilenceGateProcessor(vadSettings.MinRms, vadSettings.StopDuration));
            }
        }

        // 2. STT
        parts.Add(ctx => stt.CreateProcessor(ctx.RequestServices, root));

        // 3. Transcription filter
        parts.Add(_ => new TranscriptionFilter());

        // 4. Agent with built-in conversation memory
        var history = o.Agent.ConversationMemory
            ? new InMemoryChatHistory(o.Agent.MaxHistoryMessages)
            : null;
        parts.Add(ctx => CreateAgentProcessor(ctx, o.Agent, history));

        // 5. Sentence aggregator (profile-tuned)
        parts.Add(_ => new SentenceAggregator
        {
            EagerFirstChunkMinChars = tuning.EagerFirstChunkMinChars,
            MaxBufferChars          = tuning.MaxBufferChars,
        });

        // 6. TTS
        parts.Add(ctx => tts.CreateProcessor(ctx.RequestServices, root));

        return new ComposedVoice(
            Parts:            parts,
            InputSampleRate:  inputSampleRate,
            OutputSampleRate: outputSampleRate);
    }

    private FrameProcessor CreateAgentProcessor(
        HttpContext ctx,
        VoxaAgentOptions agentOpts,
        InMemoryChatHistory? history)
    {
        var sp = ctx.RequestServices;

        // Resolution order: AIAgent (DI) → IChatClient (DI) → IVoiceAgentFactory (provider-backed)
        AIAgent? agent = sp.GetService<AIAgent>()
            ?? WrapChatClient(sp.GetService<IChatClient>(), agentOpts)
            ?? sp.GetService<IVoiceAgentFactory>()?.Create(ctx, agentOpts);

        if (agent is null)
            throw new InvalidOperationException(
                "UseDefaults() needs an agent. Either register an AIAgent or IChatClient in DI, " +
                "or set Voxa:Agent:Provider (requires the Voxa meta-package). See docs/getting-started.md.");

        return MicrosoftAgentVoice.CreateProcessor(agent, opts =>
        {
            if (history is not null)
            {
                opts.BuildMessages = (turnCtx, ct) =>
                {
                    var messages = new List<Microsoft.Extensions.AI.ChatMessage>(history.Snapshot())
                    {
                        new(ChatRole.User, turnCtx.UserText),
                    };
                    return ValueTask.FromResult<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>>(messages);
                };
                opts.OnTurnCompleted = (turnCtx, summary, ct) =>
                {
                    history.AddUser(turnCtx.UserText);
                    if (!string.IsNullOrWhiteSpace(summary.AssistantText))
                        history.AddAssistant(summary.AssistantText);
                    return ValueTask.CompletedTask;
                };
            }
        });
    }

    private static AIAgent? WrapChatClient(IChatClient? chatClient, VoxaAgentOptions opts)
    {
        if (chatClient is null) return null;
        return new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name        = "VoxaVoiceAgent",
            ChatOptions = new ChatOptions { Instructions = opts.Instructions },
        });
    }
}

/// <summary>Per-connection composition result: processor factories + announced sample rates.</summary>
public sealed record ComposedVoice(
    IReadOnlyList<Func<HttpContext, FrameProcessor>> Parts,
    int InputSampleRate,
    int OutputSampleRate);
