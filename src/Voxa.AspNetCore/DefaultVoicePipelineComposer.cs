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

    /// <summary>HTTP-host entry point — forwards to the transport-agnostic overload.</summary>
    public ComposedVoice Compose(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        return Compose(httpContext.RequestServices);
    }

    /// <summary>
    /// Transport-agnostic composition (VST-001 WS0): any host with a per-session service scope —
    /// a WebSocket connection, Voxa Studio's in-process audio transport — gets the same pipeline.
    /// </summary>
    public ComposedVoice Compose(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
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

        var parts = new List<Func<IServiceProvider, FrameProcessor>>();

        // Diagnostics taps (VST-001 WS0): inserted only when enabled, so a production pipeline
        // with diagnostics off composes byte-identically to one built before taps existed.
        var diagnostics = o.Diagnostics.Enabled;
        void Tap(Voxa.Diagnostics.DiagnosticsTapScope scope)
        {
            if (diagnostics)
                parts.Add(sp => new Voxa.Diagnostics.DiagnosticsTapProcessor(
                    sp.GetRequiredService<Voxa.Diagnostics.VoxaDiagnosticsHub>(), scope));
        }

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
                parts.Add(sp => vadDesc.CreateProcessor(sp, WithSmartTurn(sp, WithVadObserver(sp, vadSettings, diagnostics))));
            }
            else
            {
                _logger.LogWarning(
                    "Voxa:Vad:Engine '{Engine}' not found in registry; falling back to SilenceGate.",
                    o.Vad.Engine);
                parts.Add(_ => new SilenceGateProcessor(vadSettings.MinRms, vadSettings.StopDuration));
            }
        }
        Tap(Voxa.Diagnostics.DiagnosticsTapScope.Vad);

        // 2. STT
        parts.Add(sp => stt.CreateProcessor(sp, root));

        // 3. Transcription filter. The Stt tap sits after it so diagnostics report the
        // transcripts that actually drive the agent, not ones the filter rejected.
        parts.Add(_ => new TranscriptionFilter());
        Tap(Voxa.Diagnostics.DiagnosticsTapScope.Stt);

        // 4. Agent with built-in conversation memory
        var history = o.Agent.ConversationMemory
            ? new InMemoryChatHistory(o.Agent.MaxHistoryMessages)
            : null;
        parts.Add(sp => CreateAgentProcessor(sp, o.Agent, history));
        Tap(Voxa.Diagnostics.DiagnosticsTapScope.Agent);

        // 5. Sentence aggregator (profile-tuned)
        parts.Add(_ => new SentenceAggregator
        {
            EagerFirstChunkMinChars = tuning.EagerFirstChunkMinChars,
            MaxBufferChars          = tuning.MaxBufferChars,
        });

        // 6. TTS
        parts.Add(sp => tts.CreateProcessor(sp, root));
        Tap(Voxa.Diagnostics.DiagnosticsTapScope.Tts);

        return new ComposedVoice(
            Parts:            parts,
            InputSampleRate:  inputSampleRate,
            OutputSampleRate: outputSampleRate);
    }

    /// <summary>
    /// Wire a registered <see cref="ISmartTurnClassifier"/> into the VAD's turn-end confirmation (P0).
    /// Zero-cost when none is registered — <c>GetService</c> returns null and the VAD keeps its classic
    /// silence-only behavior. The VAD's sample rate is captured so the classifier can read the PCM.
    /// </summary>
    private static VoxaVadSettings WithSmartTurn(IServiceProvider sp, VoxaVadSettings settings)
    {
        var classifier = sp.GetService<ISmartTurnClassifier>();
        if (classifier is null) return settings;
        var sampleRate = settings.SampleRate;
        return settings with
        {
            ConfirmTurnEnd = (pcm, ct) => classifier.IsTurnCompleteAsync(pcm, sampleRate, ct),
        };
    }

    /// <summary>
    /// Attach the diagnostics hub's per-window observer to the VAD settings (VST-001 WS0).
    /// The delegate is a guarded TryWrite — safe on the audio hot path.
    /// </summary>
    private static VoxaVadSettings WithVadObserver(
        IServiceProvider sp, VoxaVadSettings settings, bool diagnostics)
    {
        if (!diagnostics) return settings;
        var hub = sp.GetService<Voxa.Diagnostics.VoxaDiagnosticsHub>();
        if (hub is null) return settings;
        return settings with
        {
            ProbabilityObserver = (probability, rms, voiced, gateOpen) =>
            {
                if (hub.HasListeners)
                    hub.Publish(new Voxa.Diagnostics.VadWindowEvent(probability, rms, voiced, gateOpen));
            },
        };
    }

    private FrameProcessor CreateAgentProcessor(
        IServiceProvider sp,
        VoxaAgentOptions agentOpts,
        InMemoryChatHistory? history)
    {
        // Resolution order: AIAgent (DI) → IChatClient (DI) → IVoiceAgentFactory (provider-backed)
        AIAgent? agent = sp.GetService<AIAgent>()
            ?? WrapChatClient(sp.GetService<IChatClient>(), agentOpts)
            ?? sp.GetService<IVoiceAgentFactory>()?.Create(sp, agentOpts);

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

/// <summary>
/// Per-connection composition result: processor factories + announced sample rates. Factories
/// take the session's <see cref="IServiceProvider"/> (transport-agnostic since VST-001 WS0) —
/// HTTP hosts pass the connection's <c>RequestServices</c>.
/// </summary>
public sealed record ComposedVoice(
    IReadOnlyList<Func<IServiceProvider, FrameProcessor>> Parts,
    int InputSampleRate,
    int OutputSampleRate);
