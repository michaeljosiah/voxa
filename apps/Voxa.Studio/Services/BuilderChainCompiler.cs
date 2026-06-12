using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Voxa.AspNetCore;
using Voxa.Diagnostics;
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

        var vad = chain.FirstOrDefault(n => n.Kind == BuilderNodeKind.Vad);
        pairs["Voxa:Vad:Engine"] = vad?.Provider ?? "None";
        if (vad is not null)
        {
            if (vad.Options.TryGetValue("ConfidenceThreshold", out var threshold))
                pairs["Voxa:Vad:ConfidenceThreshold"] = threshold;
            if (vad.Options.TryGetValue("StopDurationMs", out var stop))
                pairs["Voxa:Vad:StopDurationMs"] = stop;
        }

        foreach (var node in chain)
        {
            switch (node.Kind)
            {
                case BuilderNodeKind.Stt:
                    pairs["Voxa:Stt"] = node.Provider;
                    if (Is(node, "WhisperCpp") && node.Options.TryGetValue("Model", out var model))
                        pairs["Voxa:WhisperCpp:Model"] = model;
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
                        PrerollDuration:     tuning.VadPrerollDuration);
                    if (string.Equals(node.Provider, "SilenceGate", StringComparison.OrdinalIgnoreCase))
                        parts.Add(new(node.Id, _ => new SilenceGateProcessor(settings.MinRms, settings.StopDuration)));
                    else if (registry.TryGetVad(node.Provider ?? "", out var vadDesc))
                        parts.Add(new(node.Id, sp => vadDesc.CreateProcessor(sp, WithObserver(sp, settings))));
                    else
                        throw new InvalidOperationException($"VAD engine '{node.Provider}' is not registered.");
                    break;

                case BuilderNodeKind.Stt:
                    parts.Add(new(node.Id, sp => stt.CreateProcessor(sp, root)));
                    break;

                case BuilderNodeKind.Filter:
                    parts.Add(new(node.Id, _ => new TranscriptionFilter()));
                    break;

                case BuilderNodeKind.Agent:
                    var history = options.Agent.ConversationMemory
                        ? new InMemoryChatHistory(options.Agent.MaxHistoryMessages)
                        : null;
                    parts.Add(new(node.Id, sp => CreateAgentProcessor(sp, options.Agent, history)));
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

        InsertTaps(parts, chain);
        return new CompiledChain(parts, inputSampleRate, outputSampleRate);
    }

    /// <summary>
    /// Instrumentation taps at the composer's positions — after the audio stage, after the last
    /// transcription stage, after the agent, after TTS. Live mode is the builder's whole point,
    /// so taps are unconditional here (the run container forces diagnostics on).
    /// </summary>
    private static void InsertTaps(List<CompiledPart> parts, IReadOnlyList<BuilderNode> chain)
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
            parts.Insert(index, new(null, sp => new DiagnosticsTapProcessor(
                sp.GetRequiredService<VoxaDiagnosticsHub>(), scope)));
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
        IServiceProvider sp, VoxaAgentOptions agentOpts, InMemoryChatHistory? history)
    {
        AIAgent? agent = sp.GetService<AIAgent>()
            ?? WrapChatClient(sp.GetService<IChatClient>(), agentOpts)
            ?? sp.GetService<IVoiceAgentFactory>()?.Create(sp, agentOpts);

        if (agent is null)
            throw new InvalidOperationException("The Agent node needs Voxa:Agent:Provider set (Echo or OpenAI).");

        return MicrosoftAgentVoice.CreateProcessor(agent, opts =>
        {
            if (history is null) return;
            opts.BuildMessages = (turnCtx, ct) =>
            {
                var messages = new List<ChatMessage>(history.Snapshot()) { new(ChatRole.User, turnCtx.UserText) };
                return ValueTask.FromResult<IReadOnlyList<ChatMessage>>(messages);
            };
            opts.OnTurnCompleted = (turnCtx, summary, ct) =>
            {
                history.AddUser(turnCtx.UserText);
                if (!string.IsNullOrWhiteSpace(summary.AssistantText))
                    history.AddAssistant(summary.AssistantText);
                return ValueTask.CompletedTask;
            };
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

    // ── the C# exporter (for chains appsettings can't express) ───────────────

    /// <summary>
    /// Generated composition code: the same Pipeline.Build() chain the canvas runs, resolving
    /// providers/tuning at runtime (no frozen numbers — the host's config stays authoritative).
    /// </summary>
    internal static string GenerateCSharp(BuilderGraph graph, IReadOnlyList<BuilderNode> chain)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Generated by Voxa Studio — Builder canvas export.");
        sb.AppendLine("// This chain deviates from UseDefaults(), so it composes explicitly. Call once per");
        sb.AppendLine("// session scope (e.g. per WebSocket connection). Requires the Voxa meta-package.");
        sb.AppendLine("using Microsoft.Extensions.AI;");
        sb.AppendLine("using Microsoft.Extensions.Configuration;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using Microsoft.Extensions.Options;");
        sb.AppendLine("using Voxa.AspNetCore;");
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
                    sb.AppendLine("        PrerollDuration:     tuning.VadPrerollDuration)));");
                    break;

                case BuilderNodeKind.Stt:
                    sb.AppendLine();
                    sb.AppendLine($"    // {node.Provider} STT (resolved above; options from the Voxa:{node.Provider} config section)");
                    sb.AppendLine("    builder.Then(stt.CreateProcessor(session, root));");
                    break;

                case BuilderNodeKind.Filter:
                    sb.AppendLine();
                    sb.AppendLine("    builder.Then(new TranscriptionFilter());");
                    break;

                case BuilderNodeKind.Agent:
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
                    sb.AppendLine("        if (history is null) return;");
                    sb.AppendLine("        opts.BuildMessages = (turn, ct) => ValueTask.FromResult<IReadOnlyList<ChatMessage>>(");
                    sb.AppendLine("            new List<ChatMessage>(history.Snapshot()) { new(ChatRole.User, turn.UserText) });");
                    sb.AppendLine("        opts.OnTurnCompleted = (turn, summary, ct) =>");
                    sb.AppendLine("        {");
                    sb.AppendLine("            history.AddUser(turn.UserText);");
                    sb.AppendLine("            if (!string.IsNullOrWhiteSpace(summary.AssistantText)) history.AddAssistant(summary.AssistantText);");
                    sb.AppendLine("            return ValueTask.CompletedTask;");
                    sb.AppendLine("        };");
                    sb.AppendLine("    }));");
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

        sb.AppendLine();
        sb.AppendLine("    return builder.Sink(new PipelineSink(\"sink\"));");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
