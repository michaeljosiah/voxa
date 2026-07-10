using System.Globalization;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Voxa.AspNetCore;
using Voxa.Audio;
using Voxa.Diagnostics;
using Voxa.Frames;
using Voxa.Processors;
using Voxa.Services.MicrosoftAgents;
using Voxa.Speech;

namespace Voxa.Studio.Services;

/// <summary>One compiled pipeline part. NodeId maps it back to a canvas node; null = an
/// instrumentation tap the builder inserted (the same taps the default composer adds).</summary>
internal sealed record CompiledPart(string? NodeId, Func<IServiceProvider, FrameProcessor> Factory);

/// <summary>A graph compiled to runnable parts plus the rates the session envelope announces.</summary>
internal sealed record CompiledChain(
    IReadOnlyList<CompiledPart> Parts, int InputSampleRate, int OutputSampleRate)
{
    public ComposedVoice ToComposedVoice() =>
        new(Parts.Select(p => p.Factory).ToList(), InputSampleRate, OutputSampleRate);
}

/// <summary>
/// Turns a validated builder chain into (a) flat config pairs, (b) runnable processor parts via
/// the live registry's descriptors, and (c) export artifacts. Mirrors
/// <see cref="DefaultVoicePipelineComposer"/> block-for-block so "run from canvas" is the same
/// pipeline a server would boot — except it follows the DRAWN chain, including shapes
/// UseDefaults() can't express (no filter, no aggregator, stacked VADs).
/// </summary>
internal static class BuilderChainCompiler
{
    // ── graph → flat config pairs (the appsettings export and the run container's overlay) ──

    internal static Dictionary<string, string?> Pairs(
        BuilderGraph graph, IReadOnlyList<BuilderNode> chain, bool includeSecrets = false)
    {
        var pairs = new Dictionary<string, string?>();
        if (!string.Equals(graph.Profile, "Default", StringComparison.OrdinalIgnoreCase))
            pairs["Voxa:Profile"] = graph.Profile;

        // Pre-VAD input cleanup rides the Source (mic) node — round-trips with the Config view's AEC/denoise.
        var source = chain.FirstOrDefault(n => n.Kind == BuilderNodeKind.Source);
        if (source is not null)
        {
            if (source.Options.TryGetValue("AecEngine", out var aec) && !string.IsNullOrWhiteSpace(aec))
                pairs["Voxa:Aec:Engine"] = aec;
            if (source.Options.TryGetValue("EnhanceEngine", out var enhance) && !string.IsNullOrWhiteSpace(enhance))
                pairs["Voxa:Enhance:Engine"] = enhance;
        }

        var vad = chain.FirstOrDefault(n => n.Kind == BuilderNodeKind.Vad);
        pairs["Voxa:Vad:Engine"] = vad?.Provider ?? "None";
        if (vad is not null)
        {
            if (vad.Options.TryGetValue("ConfidenceThreshold", out var threshold))
                pairs["Voxa:Vad:ConfidenceThreshold"] = threshold;
            if (vad.Options.TryGetValue("StopDurationMs", out var stop))
                pairs["Voxa:Vad:StopDurationMs"] = stop;
            // Smart turn rides the VAD node — emit the classifier block so the export/profile carries it.
            if (vad.Options.TryGetValue("SmartTurnProvider", out var stProvider) && !string.IsNullOrWhiteSpace(stProvider))
            {
                pairs["Voxa:SmartTurn:Provider"] = stProvider;
                if (vad.Options.TryGetValue("SmartTurnEndpoint", out var ep)) pairs["Voxa:SmartTurn:Endpoint"] = ep;
                if (vad.Options.TryGetValue("SmartTurnPythonExe", out var px)) pairs["Voxa:SmartTurn:PythonExe"] = px;
                if (vad.Options.TryGetValue("SmartTurnPythonScript", out var ps)) pairs["Voxa:SmartTurn:PythonScript"] = ps;
            }
        }

        foreach (var node in chain)
        {
            switch (node.Kind)
            {
                case BuilderNodeKind.Stt:
                    pairs["Voxa:Stt"] = node.Provider;
                    if (Is(node, "WhisperCpp"))
                    {
                        if (node.Options.TryGetValue("Model", out var model))
                            pairs["Voxa:WhisperCpp:Model"] = model;
                        if (node.Options.TryGetValue("Device", out var sttDevice))
                            pairs["Voxa:WhisperCpp:Device"] = sttDevice;
                    }
                    break;

                case BuilderNodeKind.Agent:
                    pairs["Voxa:Agent:Provider"] = node.Provider;
                    if (Is(node, "OpenAI"))
                    {
                        if (node.Options.TryGetValue("Model", out var agentModel))
                            pairs["Voxa:Agent:Model"] = agentModel;
                        // The key follows the Config view's rule: live container only, never disk.
                        if (includeSecrets && node.Options.TryGetValue("ApiKey", out var key)
                            && !string.IsNullOrWhiteSpace(key))
                            pairs["Voxa:Agent:ApiKey"] = key.Trim();
                    }
                    break;

                case BuilderNodeKind.Tts:
                    pairs["Voxa:Tts"] = node.Provider;
                    if (Is(node, "Piper") && node.Options.TryGetValue("Voice", out var piperVoice))
                        pairs["Voxa:Piper:Voice"] = piperVoice;
                    if (Is(node, "Kokoro"))
                    {
                        if (node.Options.TryGetValue("Voice", out var kokoroVoice))
                            pairs["Voxa:Kokoro:Voice"] = kokoroVoice;
                        if (node.Options.TryGetValue("Precision", out var precision))
                            pairs["Voxa:Kokoro:Precision"] = precision;
                        if (node.Options.TryGetValue("Device", out var ttsDevice))
                            pairs["Voxa:Kokoro:Device"] = ttsDevice;
                    }
                    break;
            }
        }
        return pairs;

        static bool Is(BuilderNode node, string provider) =>
            string.Equals(node.Provider, provider, StringComparison.OrdinalIgnoreCase);
    }

    // ── graph → runnable parts ───────────────────────────────────────────────

    /// <summary>
    /// Compile against an (ephemeral) container built from the graph's pairs. Resolving
    /// <c>IOptions&lt;VoxaOptions&gt;</c> here triggers the same fail-fast validator a server runs.
    /// </summary>
    internal static CompiledChain Compile(
        IServiceProvider provider, IConfiguration configuration, IReadOnlyList<BuilderNode> chain)
    {
        var registry = provider.GetRequiredService<VoxaProviderRegistry>();
        var options = provider.GetRequiredService<IOptions<VoxaOptions>>().Value;
        var tuning = provider.GetRequiredService<VoxaTuningResolver>().Resolve(options);
        var root = configuration.GetSection(VoxaOptions.SectionName);

        var sttNode = chain.First(n => n.Kind == BuilderNodeKind.Stt);
        var ttsNode = chain.First(n => n.Kind == BuilderNodeKind.Tts);
        if (!registry.TryGetStt(sttNode.Provider ?? "", out var stt))
            throw new InvalidOperationException($"STT provider '{sttNode.Provider}' is not registered.");
        if (!registry.TryGetTts(ttsNode.Provider ?? "", out var tts))
            throw new InvalidOperationException($"TTS provider '{ttsNode.Provider}' is not registered.");

        var inputSampleRate = stt.GetEffectiveInputSampleRate(root);
        var outputSampleRate = tts.GetEffectiveOutputSampleRate(root);

        var parts = new List<CompiledPart>();

        // Input cleanup (VRT-003 AEC / VLS-004 denoise), mirroring DefaultVoicePipelineComposer §0/§0.6 but
        // sourced from the DRAWN graph (the Source node), NOT the layered options — so a graph with no cleanup
        // (Reset to Default, an older saved graph) never inherits a base-config engine, and the run always
        // follows the canvas. A resolved engine runs before the VAD; None ⇒ no parts, byte-identical to before.
        var sourceNode = chain.FirstOrDefault(n => n.Kind == BuilderNodeKind.Source);
        var aecEngine = sourceNode?.Options.GetValueOrDefault("AecEngine", "None") ?? "None";
        var enhanceEngine = sourceNode?.Options.GetValueOrDefault("EnhanceEngine", "None") ?? "None";
        var aecEnabled = false;
        if (!string.Equals(aecEngine, "None", StringComparison.OrdinalIgnoreCase)
            && registry.TryGetAec(aecEngine, out var aecDesc))
        {
            var aecSettings = new VoxaAecSettings(inputSampleRate, outputSampleRate);
            parts.Add(new(null, sp => aecDesc.CreateProcessor(sp, aecSettings))); // near-end, before the VAD
            aecEnabled = true;
        }
        if (!string.Equals(enhanceEngine, "None", StringComparison.OrdinalIgnoreCase)
            && registry.TryGetEnhancer(enhanceEngine, out var enhDesc))
        {
            var enhSettings = new VoxaEnhancerSettings(inputSampleRate);
            parts.Add(new(null, sp => enhDesc.CreateProcessor(sp, enhSettings, root)));
        }
        // The diagnostics taps index off the chain NODES; offset their inserts past these pre-VAD parts.
        var leadingCleanup = parts.Count;

        foreach (var node in chain)
        {
            switch (node.Kind)
            {
                case BuilderNodeKind.Source:
                case BuilderNodeKind.Sink:
                    break; // the session owns PipelineSource/PipelineSink

                case BuilderNodeKind.Vad:
                    var settings = new VoxaVadSettings(
                        SampleRate:          inputSampleRate,
                        ConfidenceThreshold: tuning.VadConfidenceThreshold,
                        MinRms:              tuning.VadMinRms,
                        StartDuration:       tuning.VadStartDuration,
                        StopDuration:        tuning.VadStopDuration,
                        PrerollDuration:     tuning.VadPrerollDuration)
                    {
                        // CQ-007: match DefaultVoicePipelineComposer — eager-STT delay + force-split cap (null in
                        // Default ⇒ unchanged; applied under LowLatency/Quality so "run from canvas" == a server's run).
                        EagerSttDelay        = tuning.VadEagerSttDelay,
                        MaxUtteranceDuration = tuning.VadMaxUtteranceDuration,
                    };
                    if (string.Equals(node.Provider, "SilenceGate", StringComparison.OrdinalIgnoreCase))
                        parts.Add(new(node.Id, sp => CreateSilenceGate(sp, settings)));
                    else if (registry.TryGetVad(node.Provider ?? "", out var vadDesc))
                        parts.Add(new(node.Id, sp => vadDesc.CreateProcessor(sp, WithSmartTurn(sp, WithObserver(sp, settings)))));
                    else
                        throw new InvalidOperationException($"VAD engine '{node.Provider}' is not registered.");
                    break;

                case BuilderNodeKind.Stt:
                    // CQ-007: match the composer's VRT-004 interim-coalescing (default ~150 ms) so a chatty
                    // streaming engine can't flood the bounded channel under "run from canvas" either.
                    var interimMinInterval = TimeSpan.FromMilliseconds(Math.Max(0, options.InterimMinIntervalMs ?? 150));
                    parts.Add(new(node.Id, sp =>
                    {
                        var sttProcessor = stt.CreateProcessor(sp, root);
                        sttProcessor.InterimMinInterval = interimMinInterval;
                        return sttProcessor;
                    }));
                    break;

                case BuilderNodeKind.Filter:
                    parts.Add(new(node.Id, _ => new TranscriptionFilter()));
                    break;

                case BuilderNodeKind.Agent:
                    var history = options.Agent.ConversationMemory
                        ? new InMemoryChatHistory(options.Agent.MaxHistoryMessages)
                        : null;
                    // VDX-008: a BackgroundAgent node anywhere downstream means the talker needs the
                    // delegate_task tool and the loop needs its arbitration knobs (composer parity).
                    var backgroundEnabled = chain.Any(n => n.Kind == BuilderNodeKind.BackgroundAgent);
                    parts.Add(new(node.Id, sp => CreateAgentProcessor(
                        sp, options, history, tuning.MaxResponseDuration, backgroundEnabled)));
                    break;

                case BuilderNodeKind.BackgroundAgent:
                    parts.Add(new(node.Id, sp => new BackgroundAgentProcessor(
                        CreateBackgroundDriver(sp, node, options),
                        maxConcurrentTasks: IntOption(node, "MaxConcurrentTasks", options.BackgroundAgent.MaxConcurrentTasks),
                        maxQueuedRequests:  IntOption(node, "MaxQueuedRequests", options.BackgroundAgent.MaxQueuedRequests),
                        taskTimeout:        TimeSpan.FromSeconds(IntOption(node, "TaskTimeoutSeconds", options.BackgroundAgent.TaskTimeoutSeconds)),
                        diagnosticsHub:     sp.GetService<VoxaDiagnosticsHub>())));
                    break;

                case BuilderNodeKind.Aggregator:
                    parts.Add(new(node.Id, _ => new SentenceAggregator
                    {
                        EagerFirstChunkMinChars = tuning.EagerFirstChunkMinChars,
                        MaxBufferChars          = tuning.MaxBufferChars,
                    }));
                    break;

                case BuilderNodeKind.Tts:
                    parts.Add(new(node.Id, sp => tts.CreateProcessor(sp, root)));
                    break;
            }
        }

        InsertTaps(parts, chain, leadingCleanup);

        // Far-end (bot-audio) reference tap — added only when an AEC engine resolved, matching the
        // composer's §7. It feeds each outbound bot frame into the session's shared IEchoCanceller and
        // forwards every frame unchanged; with no AEC the chain has no tap (default = byte-identical).
        if (aecEnabled)
            parts.Add(new(null, sp => new EchoReferenceTapProcessor(sp.GetRequiredService<IEchoCanceller>())));

        return new CompiledChain(parts, inputSampleRate, outputSampleRate);
    }

    /// <summary>
    /// Instrumentation taps at the composer's positions — after the audio stage, after the last
    /// transcription stage, after the agent, after TTS. Live mode is the builder's whole point,
    /// so taps are unconditional here (the run container forces diagnostics on).
    /// </summary>
    private static void InsertTaps(List<CompiledPart> parts, IReadOnlyList<BuilderNode> chain, int leadingOffset = 0)
    {
        var middle = chain.Where(n => n.Kind is not (BuilderNodeKind.Source or BuilderNodeKind.Sink)).ToList();

        int After(Func<BuilderNode, bool> match)
        {
            for (var i = middle.Count - 1; i >= 0; i--)
                if (match(middle[i])) return i + 1;
            return 0;
        }

        // Insert highest index first so earlier insertions don't shift later ones.
        var taps = new (int Index, DiagnosticsTapScope Scope)[]
        {
            (After(n => n.Kind == BuilderNodeKind.Tts), DiagnosticsTapScope.Tts),
            (After(n => n.Kind == BuilderNodeKind.Agent), DiagnosticsTapScope.Agent),
            (After(n => BuilderGraph.Flow(n.Kind).Out == BuilderPortType.Transcription), DiagnosticsTapScope.Stt),
            (After(n => BuilderGraph.Flow(n.Kind).Out == BuilderPortType.Audio), DiagnosticsTapScope.Vad),
        };
        foreach (var (index, scope) in taps.OrderByDescending(t => t.Index))
            parts.Insert(index + leadingOffset, new(null, sp => new DiagnosticsTapProcessor(
                sp.GetRequiredService<VoxaDiagnosticsHub>(), scope)));
    }

    // Mirror DefaultVoicePipelineComposer.WithSmartTurn for the canvas-run path (which builds the VAD
    // directly rather than via the composer): wire a registered classifier into the silence timeout so a
    // graph run honors the same Smart turn toggle as a Talk session. No classifier registered → unchanged.
    internal static VoxaVadSettings WithSmartTurn(IServiceProvider sp, VoxaVadSettings settings)
    {
        var classifier = sp.GetService<ISmartTurnClassifier>();
        if (classifier is null) return settings;
        return settings with
        {
            ConfirmTurnEnd = (pcm, ct) => classifier.IsTurnCompleteAsync(pcm, settings.SampleRate, ct),
        };
    }

    // SilenceGate has no turn-confirmation seam, so a registered classifier cannot apply — warn rather than
    // silently ignore it (mirrors DefaultVoicePipelineComposer.CreateSilenceGate).
    private static SilenceGateProcessor CreateSilenceGate(IServiceProvider sp, VoxaVadSettings settings)
    {
        if (sp.GetService<ISmartTurnClassifier>() is not null)
            sp.GetService<ILoggerFactory>()?.CreateLogger("Voxa.Studio.BuilderChainCompiler")
                .LogWarning("A smart-turn classifier is registered but the SilenceGate VAD has no turn-confirmation " +
                            "seam — turns still end on StopDuration. Use the Silero VAD for smart turn.");
        return new SilenceGateProcessor(settings.MinRms, settings.StopDuration);
    }

    private static VoxaVadSettings WithObserver(IServiceProvider sp, VoxaVadSettings settings)
    {
        var hub = sp.GetService<VoxaDiagnosticsHub>();
        if (hub is null) return settings;
        return settings with
        {
            ProbabilityObserver = (probability, rms, voiced, gateOpen) =>
            {
                if (hub.HasListeners)
                    hub.Publish(new VadWindowEvent(probability, rms, voiced, gateOpen));
            },
        };
    }

    // The composer's agent block (resolution order + conversation memory), reproduced so a
    // non-default chain still gets exactly the agent a server would. Kept in lockstep with
    // DefaultVoicePipelineComposer.CreateAgentProcessor.
    private static FrameProcessor CreateAgentProcessor(
        IServiceProvider sp, VoxaOptions options, InMemoryChatHistory? history,
        TimeSpan? maxResponseDuration, bool backgroundEnabled)
    {
        var agentOpts = options.Agent;
        AIAgent? agent = sp.GetService<AIAgent>()
            ?? WrapChatClient(sp.GetService<IChatClient>(), agentOpts)
            ?? sp.GetService<IVoiceAgentFactory>()?.Create(sp, agentOpts);

        if (agent is null)
            throw new InvalidOperationException("The Agent node needs Voxa:Agent:Provider set (Echo or OpenAI).");

        return MicrosoftAgentVoice.CreateProcessor(agent, opts =>
        {
            opts.MaxResponseDuration = maxResponseDuration; // CQ-007: match the composer's VRT-002 WS2 §6.5 response cap

            if (backgroundEnabled)
            {
                // VDX-008 composer parity: the talker gets delegate_task; the loop gets the
                // Voxa:BackgroundAgent arbitration knobs and the hub for drop events.
                opts.EnableBackgroundDelegation = true;
                opts.BackgroundResults = new BackgroundResultOptions
                {
                    HoldWhileUserSpeaking    = options.BackgroundAgent.HoldWhileUserSpeaking,
                    MaxPendingResults        = options.BackgroundAgent.MaxPendingResults,
                    HeldResultReleaseTimeout = TimeSpan.FromMilliseconds(options.BackgroundAgent.HeldResultReleaseTimeoutMs),
                };
                opts.DiagnosticsHub = sp.GetService<VoxaDiagnosticsHub>();
            }

            if (history is null) return;
            opts.BuildMessages = (turnCtx, ct) =>
            {
                // VDX-008: background-result turns carry no user text — mirror the composer's
                // relevance-gated result message so the model never runs an empty turn.
                var turnMessage = turnCtx.Trigger == TurnTrigger.BackgroundResult && turnCtx.BackgroundResult is { } result
                    ? MicrosoftAgentVoice.CreateBackgroundResultMessage(result)
                    : new ChatMessage(ChatRole.User, turnCtx.UserText);
                var messages = new List<ChatMessage>(history.Snapshot()) { turnMessage };
                return ValueTask.FromResult<IReadOnlyList<ChatMessage>>(messages);
            };
            opts.OnTurnCompleted = (turnCtx, summary, ct) =>
            {
                if (turnCtx.Trigger == TurnTrigger.BackgroundResult)
                {
                    // A result gated to silence leaves no history trace (composer parity).
                    if (!string.IsNullOrWhiteSpace(summary.AssistantText))
                        history.AddAssistant(summary.AssistantText);
                    return ValueTask.CompletedTask;
                }
                history.AddUser(turnCtx.UserText);
                if (!string.IsNullOrWhiteSpace(summary.AssistantText))
                    history.AddAssistant(summary.AssistantText);
                return ValueTask.CompletedTask;
            };
        });
    }

    /// <summary>
    /// The node's "thinker": Echo → the keyless demo driver (delay from the node); OpenAI → a second
    /// agent on its own (heavier) model via the same factory the talker uses, so key fallback rules match.
    /// </summary>
    private static IAgentTurnDriver CreateBackgroundDriver(
        IServiceProvider sp, BuilderNode node, VoxaOptions options)
    {
        if (string.Equals(node.Provider, "Echo", StringComparison.OrdinalIgnoreCase))
            return new DemoBackgroundDriver(TimeSpan.FromSeconds(IntOption(node, "DelaySeconds", 4)));

        var thinkerOpts = new VoxaAgentOptions
        {
            Provider = "OpenAI",
            Model = node.Options.GetValueOrDefault("Model", "gpt-4o"),
            ApiKey = node.Options.GetValueOrDefault("ApiKey") is { Length: > 0 } key ? key : options.Agent.ApiKey,
            Instructions = "You are a background research assistant. Answer in 2-3 compact sentences — " +
                           "your answer is read aloud by another assistant, so no lists or markdown.",
        };
        var agent = sp.GetService<IVoiceAgentFactory>()?.Create(sp, thinkerOpts)
            ?? throw new InvalidOperationException(
                "The Background agent (OpenAI) node needs the OpenAI voice-agent factory (Voxa meta-package).");
        return MicrosoftAgentVoice.CreateTurnDriver(agent);
    }

    private static int IntOption(BuilderNode node, string key, int fallback) =>
        int.TryParse(node.Options.GetValueOrDefault(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v : fallback;

    private static AIAgent? WrapChatClient(IChatClient? chatClient, VoxaAgentOptions opts)
    {
        if (chatClient is null) return null;
        return new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name        = "VoxaVoiceAgent",
            ChatOptions = new ChatOptions { Instructions = opts.Instructions },
        });
    }

    // ── the C# exporter (for chains appsettings can't express) ───────────────

    /// <summary>
    /// Generated composition code: the same Pipeline.Build() chain the canvas runs, resolving
    /// providers/tuning at runtime (no frozen numbers — the host's config stays authoritative).
    /// </summary>
    internal static string GenerateCSharp(BuilderGraph graph, IReadOnlyList<BuilderNode> chain)
    {
        // Pre-VAD input cleanup is a property of the Source (mic) node — read it from the DRAWN graph (the
        // same place Compile() reads), so a custom-shape C# export carries exactly the cleanup the canvas shows.
        var cleanupSource = chain.FirstOrDefault(n => n.Kind == BuilderNodeKind.Source);
        var aecEngine = cleanupSource?.Options.GetValueOrDefault("AecEngine", "None") ?? "None";
        var enhanceEngine = cleanupSource?.Options.GetValueOrDefault("EnhanceEngine", "None") ?? "None";
        var aecEnabled = !string.Equals(aecEngine, "None", StringComparison.OrdinalIgnoreCase);
        var enhanceEnabled = !string.Equals(enhanceEngine, "None", StringComparison.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.AppendLine("// Generated by Voxa Studio — Builder canvas export.");
        sb.AppendLine("// This chain deviates from UseDefaults(), so it composes explicitly. Call once per");
        sb.AppendLine("// session scope (e.g. per WebSocket connection). Requires the Voxa meta-package.");
        sb.AppendLine("using Microsoft.Extensions.AI;");
        sb.AppendLine("using Microsoft.Extensions.Configuration;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using Microsoft.Extensions.Options;");
        sb.AppendLine("using Voxa.AspNetCore;");
        if (aecEnabled) sb.AppendLine("using Voxa.Audio;"); // EchoReferenceTapProcessor + IEchoCanceller (far-end tap)
        if (chain.Any(n => n.Kind == BuilderNodeKind.BackgroundAgent))
            sb.AppendLine("using Voxa.Frames;"); // TurnTrigger on background-result turns (VDX-008)
        sb.AppendLine("using Voxa.Pipelines;");
        sb.AppendLine("using Voxa.Processors;");
        sb.AppendLine("using Voxa.Services.MicrosoftAgents;");
        sb.AppendLine("using Voxa.Speech;");
        sb.AppendLine();
        sb.AppendLine("static Pipeline BuildVoicePipeline(IServiceProvider session, IConfiguration configuration)");
        sb.AppendLine("{");
        sb.AppendLine("    var registry = session.GetRequiredService<VoxaProviderRegistry>();");
        sb.AppendLine("    var options  = session.GetRequiredService<IOptions<VoxaOptions>>().Value;");
        sb.AppendLine("    var tuning   = session.GetRequiredService<VoxaTuningResolver>().Resolve(options);");
        sb.AppendLine("    var root     = configuration.GetSection(\"Voxa\");");
        sb.AppendLine();

        // The STT descriptor is resolved up front: a VAD earlier in the chain advertises the
        // SAME input rate the STT processor binds (the composer's invariant — runtime value, not
        // a frozen literal). Every valid chain has exactly one STT (the type system guarantees it).
        var sttNode = chain.First(n => n.Kind == BuilderNodeKind.Stt);
        sb.AppendLine($"    if (!registry.TryGetStt(\"{sttNode.Provider}\", out var stt))");
        sb.AppendLine($"        throw new InvalidOperationException(\"STT '{sttNode.Provider}' is not registered.\");");
        sb.AppendLine("    var inputSampleRate = stt.GetEffectiveInputSampleRate(root);");
        sb.AppendLine();
        sb.AppendLine("    var builder = Pipeline.Build().Source(new PipelineSource(\"source\"));");

        // Input cleanup (AEC/denoise) from the Source node — before the VAD, mirroring the composer's §0/§0.6.
        if (aecEnabled)
        {
            var ttsNode = chain.First(n => n.Kind == BuilderNodeKind.Tts);
            sb.AppendLine();
            sb.AppendLine($"    // {aecEngine} echo cancellation — near-end, before the VAD (far-end tap added after TTS)");
            sb.AppendLine($"    if (!registry.TryGetTts(\"{ttsNode.Provider}\", out var ttsForAec))");
            sb.AppendLine($"        throw new InvalidOperationException(\"TTS '{ttsNode.Provider}' is not registered.\");");
            sb.AppendLine($"    if (!registry.TryGetAec(\"{aecEngine}\", out var aec))");
            sb.AppendLine($"        throw new InvalidOperationException(\"AEC '{aecEngine}' is not registered.\");");
            sb.AppendLine("    builder.Then(aec.CreateProcessor(session, new VoxaAecSettings(");
            sb.AppendLine("        SampleRate: inputSampleRate, FarEndSampleRate: ttsForAec.GetEffectiveOutputSampleRate(root))));");
        }
        if (enhanceEnabled)
        {
            sb.AppendLine();
            sb.AppendLine($"    // {enhanceEngine} speech enhancement / denoise — before the VAD");
            sb.AppendLine($"    if (!registry.TryGetEnhancer(\"{enhanceEngine}\", out var enhancer))");
            sb.AppendLine($"        throw new InvalidOperationException(\"Enhancer '{enhanceEngine}' is not registered.\");");
            sb.AppendLine("    builder.Then(enhancer.CreateProcessor(session, new VoxaEnhancerSettings(inputSampleRate), root));");
        }

        foreach (var node in chain)
        {
            switch (node.Kind)
            {
                case BuilderNodeKind.Vad when string.Equals(node.Provider, "SilenceGate", StringComparison.OrdinalIgnoreCase):
                    sb.AppendLine();
                    sb.AppendLine("    // SilenceGate VAD (energy-only)");
                    sb.AppendLine("    builder.Then(new SilenceGateProcessor(tuning.VadMinRms, tuning.VadStopDuration));");
                    break;

                case BuilderNodeKind.Vad:
                    sb.AppendLine();
                    sb.AppendLine($"    // {node.Provider} VAD (tuning honours Voxa:Profile and Voxa:Vad overrides)");
                    sb.AppendLine($"    if (!registry.TryGetVad(\"{node.Provider}\", out var vad))");
                    sb.AppendLine($"        throw new InvalidOperationException(\"VAD '{node.Provider}' is not registered.\");");
                    sb.AppendLine("    builder.Then(vad.CreateProcessor(session, new VoxaVadSettings(");
                    sb.AppendLine("        SampleRate:          inputSampleRate, // the STT provider's effective input rate");
                    sb.AppendLine("        ConfidenceThreshold: tuning.VadConfidenceThreshold,");
                    sb.AppendLine("        MinRms:              tuning.VadMinRms,");
                    sb.AppendLine("        StartDuration:       tuning.VadStartDuration,");
                    sb.AppendLine("        StopDuration:        tuning.VadStopDuration,");
                    sb.AppendLine("        PrerollDuration:     tuning.VadPrerollDuration)");
                    sb.AppendLine("    {");
                    sb.AppendLine("        // Eager-STT delay + force-split cap — match the composer (null under Default ⇒ unchanged).");
                    sb.AppendLine("        EagerSttDelay        = tuning.VadEagerSttDelay,");
                    sb.AppendLine("        MaxUtteranceDuration = tuning.VadMaxUtteranceDuration,");
                    sb.AppendLine("    }));");
                    break;

                case BuilderNodeKind.Stt:
                    sb.AppendLine();
                    sb.AppendLine($"    // {node.Provider} STT (resolved above; options from the Voxa:{node.Provider} config section)");
                    sb.AppendLine("    var sttProcessor = stt.CreateProcessor(session, root);");
                    sb.AppendLine("    // VRT-004 interim coalescing (default ~150 ms) — match the composer so a chatty engine can't flood the channel.");
                    sb.AppendLine("    sttProcessor.InterimMinInterval = TimeSpan.FromMilliseconds(Math.Max(0, options.InterimMinIntervalMs ?? 150));");
                    sb.AppendLine("    builder.Then(sttProcessor);");
                    break;

                case BuilderNodeKind.Filter:
                    sb.AppendLine();
                    sb.AppendLine("    builder.Then(new TranscriptionFilter());");
                    break;

                case BuilderNodeKind.Agent:
                    var hasBackground = chain.Any(n => n.Kind == BuilderNodeKind.BackgroundAgent);
                    sb.AppendLine();
                    sb.AppendLine($"    // Agent — {node.Provider} (set Voxa:Agent:Provider/Model/ApiKey in config)");
                    sb.AppendLine("    var agent = session.GetService<AIAgent>()");
                    sb.AppendLine("        ?? session.GetRequiredService<IVoiceAgentFactory>().Create(session, options.Agent)");
                    sb.AppendLine("        ?? throw new InvalidOperationException(\"Voxa:Agent:Provider is not set.\");");
                    sb.AppendLine("    // Conversation memory mirrors UseDefaults(): on by default (Voxa:Agent:ConversationMemory).");
                    sb.AppendLine("    var history = options.Agent.ConversationMemory");
                    sb.AppendLine("        ? new InMemoryChatHistory(options.Agent.MaxHistoryMessages) : null;");
                    sb.AppendLine("    builder.Then(MicrosoftAgentVoice.CreateProcessor(agent, opts =>");
                    sb.AppendLine("    {");
                    sb.AppendLine("        opts.MaxResponseDuration = tuning.MaxResponseDuration; // VRT-002 WS2 §6.5 response cap — match the composer");
                    if (hasBackground)
                    {
                        sb.AppendLine("        // VDX-008: the BackgroundAgent stage downstream needs the talker to delegate.");
                        sb.AppendLine("        opts.EnableBackgroundDelegation = true;");
                        sb.AppendLine("        opts.BackgroundResults = new BackgroundResultOptions");
                        sb.AppendLine("        {");
                        sb.AppendLine("            HoldWhileUserSpeaking    = options.BackgroundAgent.HoldWhileUserSpeaking,");
                        sb.AppendLine("            MaxPendingResults        = options.BackgroundAgent.MaxPendingResults,");
                        sb.AppendLine("            HeldResultReleaseTimeout = TimeSpan.FromMilliseconds(options.BackgroundAgent.HeldResultReleaseTimeoutMs),");
                        sb.AppendLine("        };");
                    }
                    sb.AppendLine("        if (history is null) return;");
                    if (hasBackground)
                    {
                        sb.AppendLine("        // Background-result turns carry no user text — feed the relevance-gated result instead.");
                        sb.AppendLine("        opts.BuildMessages = (turn, ct) => ValueTask.FromResult<IReadOnlyList<ChatMessage>>(");
                        sb.AppendLine("            new List<ChatMessage>(history.Snapshot())");
                        sb.AppendLine("            {");
                        sb.AppendLine("                turn.Trigger == TurnTrigger.BackgroundResult && turn.BackgroundResult is { } result");
                        sb.AppendLine("                    ? MicrosoftAgentVoice.CreateBackgroundResultMessage(result)");
                        sb.AppendLine("                    : new ChatMessage(ChatRole.User, turn.UserText),");
                        sb.AppendLine("            });");
                        sb.AppendLine("        opts.OnTurnCompleted = (turn, summary, ct) =>");
                        sb.AppendLine("        {");
                        sb.AppendLine("            if (turn.Trigger != TurnTrigger.BackgroundResult) history.AddUser(turn.UserText);");
                        sb.AppendLine("            if (!string.IsNullOrWhiteSpace(summary.AssistantText)) history.AddAssistant(summary.AssistantText);");
                        sb.AppendLine("            return ValueTask.CompletedTask;");
                        sb.AppendLine("        };");
                    }
                    else
                    {
                        sb.AppendLine("        opts.BuildMessages = (turn, ct) => ValueTask.FromResult<IReadOnlyList<ChatMessage>>(");
                        sb.AppendLine("            new List<ChatMessage>(history.Snapshot()) { new(ChatRole.User, turn.UserText) });");
                        sb.AppendLine("        opts.OnTurnCompleted = (turn, summary, ct) =>");
                        sb.AppendLine("        {");
                        sb.AppendLine("            history.AddUser(turn.UserText);");
                        sb.AppendLine("            if (!string.IsNullOrWhiteSpace(summary.AssistantText)) history.AddAssistant(summary.AssistantText);");
                        sb.AppendLine("            return ValueTask.CompletedTask;");
                        sb.AppendLine("        };");
                    }
                    sb.AppendLine("    }));");
                    break;

                case BuilderNodeKind.BackgroundAgent:
                    sb.AppendLine();
                    sb.AppendLine("    // Background agent — the VDX-008 talker/thinker split (docs/background-agent.md).");
                    sb.AppendLine("    // Studio's canvas runs a built-in thinker; a server registers its own driver once:");
                    sb.AppendLine("    //   services.AddVoxaBackgroundAgent(sp => MicrosoftAgentVoice.CreateTurnDriver(researcherAgent));");
                    sb.AppendLine("    var backgroundDriver = session.GetKeyedService<IAgentTurnDriver>(VoxaBackgroundAgentOptions.ServiceKey)");
                    sb.AppendLine("        ?? throw new InvalidOperationException(\"No background driver registered — call services.AddVoxaBackgroundAgent(...).\");");
                    sb.AppendLine("    builder.Then(new BackgroundAgentProcessor(backgroundDriver,");
                    sb.AppendLine("        maxConcurrentTasks: options.BackgroundAgent.MaxConcurrentTasks,");
                    sb.AppendLine("        maxQueuedRequests:  options.BackgroundAgent.MaxQueuedRequests,");
                    sb.AppendLine("        taskTimeout:        TimeSpan.FromSeconds(options.BackgroundAgent.TaskTimeoutSeconds)));");
                    break;

                case BuilderNodeKind.Aggregator:
                    sb.AppendLine();
                    sb.AppendLine("    builder.Then(new SentenceAggregator");
                    sb.AppendLine("    {");
                    sb.AppendLine("        EagerFirstChunkMinChars = tuning.EagerFirstChunkMinChars,");
                    sb.AppendLine("        MaxBufferChars          = tuning.MaxBufferChars,");
                    sb.AppendLine("    });");
                    break;

                case BuilderNodeKind.Tts:
                    sb.AppendLine();
                    sb.AppendLine($"    // {node.Provider} TTS (options from the Voxa:{node.Provider} config section)");
                    sb.AppendLine($"    if (!registry.TryGetTts(\"{node.Provider}\", out var tts))");
                    sb.AppendLine($"        throw new InvalidOperationException(\"TTS '{node.Provider}' is not registered.\");");
                    sb.AppendLine("    builder.Then(tts.CreateProcessor(session, root));");
                    break;
            }
        }

        // Far-end (bot-audio) reference tap — only with an AEC engine, matching the composer's §7.
        if (aecEnabled)
        {
            sb.AppendLine();
            sb.AppendLine("    // Far-end reference tap — feeds outbound bot audio into the session's IEchoCanceller.");
            sb.AppendLine("    builder.Then(new EchoReferenceTapProcessor(session.GetRequiredService<IEchoCanceller>()));");
        }

        sb.AppendLine();
        sb.AppendLine("    return builder.Sink(new PipelineSink(\"sink\"));");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
