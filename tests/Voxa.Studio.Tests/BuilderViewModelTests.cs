using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Voxa.AspNetCore;
using Voxa.Diagnostics;
using Voxa.Studio.Services;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Tests;

/// <summary>
/// VST-002 D3 §8: the Builder canvas VM — seeding, palette, the '+' affordance, type-checked
/// wiring, undo, persistence, exporters, and the live-mode event mapping. The chain compiler is
/// exercised against a real AddVoxa container (factories are never invoked — no model loads).
/// </summary>
public class BuilderViewModelTests
{
    private static BuilderViewModel Vm() => new(TestSupport.Services());

    private static BuilderNodeVm NodeOf(BuilderViewModel vm, BuilderNodeKind kind) =>
        vm.Nodes.First(n => n.Kind == kind);

    // ── seeding ──────────────────────────────────────────────────────────────

    [Fact]
    public void The_Canvas_Opens_With_The_Active_Config_As_A_Graph()
    {
        var vm = Vm();

        // Studio's shipped local config: Source→Silero→WhisperCpp→Filter→Echo→Aggregator→Piper→Sink.
        Assert.Equal(8, vm.Nodes.Count);
        Assert.True(vm.IsChainValid);
        Assert.True(vm.IsDefaultShape);
        Assert.Equal("Source → Silero → WhisperCpp → Filter → Echo → Aggregator → Piper → Sink",
            vm.ChainText);
        Assert.Equal("tiny.en", NodeOf(vm, BuilderNodeKind.Stt).Meta);
    }

    [Fact]
    public void Open_In_Builder_Seeds_From_Config_Pairs()
    {
        var vm = Vm();
        vm.SeedFromPairs(new Dictionary<string, string?>
        {
            ["Voxa:Vad:Engine"] = "None",
            ["Voxa:Stt"] = "WhisperCpp",
            ["Voxa:Tts"] = "Kokoro",
            ["Voxa:Kokoro:Voice"] = "af_bella",
            ["Voxa:Agent:Provider"] = "OpenAI",
            ["Voxa:Agent:Model"] = "gpt-4o",
            ["Voxa:Profile"] = "LowLatency",
        });

        Assert.Equal(7, vm.Nodes.Count); // no VAD node
        Assert.DoesNotContain(vm.Nodes, n => n.Kind == BuilderNodeKind.Vad);
        Assert.Equal("Kokoro", NodeOf(vm, BuilderNodeKind.Tts).Model.Provider);
        Assert.Equal("af_bella", NodeOf(vm, BuilderNodeKind.Tts).Model.Options["Voice"]);
        Assert.Equal("gpt-4o", NodeOf(vm, BuilderNodeKind.Agent).Model.Options["Model"]);
        Assert.Equal("LowLatency", vm.SelectedProfile);
        Assert.True(vm.IsDefaultShape);
    }

    // ── palette + wiring ─────────────────────────────────────────────────────

    [Fact]
    public void The_Palette_Is_Generated_From_The_Live_Registry()
    {
        var vm = Vm();
        var entries = vm.Palette.SelectMany(g => g.Entries).ToList();

        Assert.Contains(entries, e => e.Kind == BuilderNodeKind.Stt && e.Provider == "WhisperCpp");
        Assert.Contains(entries, e => e.Kind == BuilderNodeKind.Tts && e.Provider == "Piper");
        Assert.Contains(entries, e => e.Kind == BuilderNodeKind.Vad && e.Provider == "Silero");
        Assert.Contains(entries, e => e.Kind == BuilderNodeKind.Agent && e.Provider == "Echo");
        Assert.Contains(entries, e => e.Kind == BuilderNodeKind.Filter);
        Assert.Contains(entries, e => e.Kind == BuilderNodeKind.Aggregator);
    }

    [Fact]
    public void Incompatible_Wires_Are_Refused_With_The_Reason()
    {
        var vm = Vm();
        var agent = NodeOf(vm, BuilderNodeKind.Agent);
        var stt = NodeOf(vm, BuilderNodeKind.Stt);

        Assert.False(vm.TryConnect(agent, stt, out var reason));
        Assert.Contains("agent-text", reason);

        // Occupied ports refuse too — the chain rule, said in words.
        var source = NodeOf(vm, BuilderNodeKind.Source);
        var vad = NodeOf(vm, BuilderNodeKind.Vad);
        Assert.False(vm.TryConnect(source, vad, out var occupied));
        Assert.Contains("single-out", occupied);
    }

    [Fact]
    public void The_Plus_Affordance_Offers_Only_Type_Compatible_Nodes()
    {
        var vm = Vm();
        vm.Select(NodeOf(vm, BuilderNodeKind.Stt));
        vm.DisconnectOutputCommand.Execute(null);

        // STT emits transcription — only transcription consumers appear.
        Assert.True(NodeOf(vm, BuilderNodeKind.Stt).ShowPlus);
        Assert.NotEmpty(vm.PlusChoices);
        Assert.All(vm.PlusChoices, e =>
            Assert.Equal(BuilderPortType.Transcription, BuilderGraph.Flow(e.Kind).In));
        Assert.Contains(vm.PlusChoices, e => e.Kind == BuilderNodeKind.Filter);
        Assert.Contains(vm.PlusChoices, e => e.Kind == BuilderNodeKind.Agent);
    }

    [Fact]
    public void Add_And_Connect_Appends_Wired_To_The_Dangling_Port()
    {
        var vm = Vm();
        var stt = NodeOf(vm, BuilderNodeKind.Stt);
        vm.Select(stt);
        vm.DisconnectOutputCommand.Execute(null);
        var filter = NodeOf(vm, BuilderNodeKind.Filter);
        vm.Select(filter);
        vm.RemoveSelectedCommand.Execute(null);

        vm.Select(stt);
        vm.AddAndConnectCommand.Execute(
            vm.PlusChoices.First(e => e.Kind == BuilderNodeKind.Filter));

        var newFilter = NodeOf(vm, BuilderNodeKind.Filter);
        Assert.Contains(vm.Edges, e => e.From == stt && e.To == newFilter);
        Assert.Same(newFilter, vm.SelectedNode);

        // Re-wire the new filter into the agent and the chain is whole again.
        Assert.True(vm.TryConnect(newFilter, NodeOf(vm, BuilderNodeKind.Agent), out _));
        Assert.True(vm.IsChainValid);
        Assert.True(vm.IsDefaultShape);
    }

    [Fact]
    public void Undo_Restores_The_Document_Redo_Replays_It()
    {
        var vm = Vm();
        vm.Select(NodeOf(vm, BuilderNodeKind.Filter));
        vm.RemoveSelectedCommand.Execute(null);
        Assert.Equal(7, vm.Nodes.Count);
        Assert.False(vm.IsChainValid); // the filter's wires went with it

        vm.Undo();
        Assert.Equal(8, vm.Nodes.Count);
        Assert.True(vm.IsChainValid);

        vm.Redo();
        Assert.Equal(7, vm.Nodes.Count);
    }

    [Fact]
    public async Task Save_And_Load_Roundtrip_The_Graph_File()
    {
        var path = Path.Combine(TestSupport.TempDir(), "graph.json");
        var vm = Vm();
        vm.GraphPathOverride = path;
        NodeOf(vm, BuilderNodeKind.Stt).Model.Options["Model"] = "base.en";
        await vm.SaveGraphCommand.ExecuteAsync(null);
        Assert.True(File.Exists(path));

        var fresh = Vm();
        fresh.GraphPathOverride = path;
        await fresh.LoadGraphCommand.ExecuteAsync(null);
        Assert.Equal("base.en", NodeOf(fresh, BuilderNodeKind.Stt).Model.Options["Model"]);
        Assert.True(fresh.IsChainValid);
    }

    // ── reset + validation (VST builder polish) ──────────────────────────────

    [Fact]
    public void Reset_To_Default_Restores_The_Default_Pipeline_And_Is_Undoable()
    {
        var vm = Vm();
        vm.SeedFromPairs(new Dictionary<string, string?>
        {
            ["Voxa:Vad:Engine"] = "None",
            ["Voxa:Tts"] = "Kokoro",
        });
        Assert.Equal(7, vm.Nodes.Count); // no VAD

        vm.ResetToDefaultCommand.Execute(null);

        Assert.Equal(8, vm.Nodes.Count);
        Assert.True(vm.IsChainValid);
        Assert.True(vm.IsDefaultShape);
        Assert.Equal("Silero", NodeOf(vm, BuilderNodeKind.Vad).Model.Provider);
        Assert.Equal("Piper", NodeOf(vm, BuilderNodeKind.Tts).Model.Provider);
        Assert.Equal("tiny.en", NodeOf(vm, BuilderNodeKind.Stt).Model.Options["Model"]);

        vm.Undo(); // a fat-fingered reset is recoverable
        Assert.Equal(7, vm.Nodes.Count);
    }

    [Fact]
    public void A_Valid_Chain_Allows_Save_And_Rings_No_Node()
    {
        var vm = Vm();
        Assert.True(vm.IsChainValid);
        Assert.True(vm.SaveGraphCommand.CanExecute(null));
        Assert.Empty(vm.ValidationErrors);
        Assert.All(vm.Nodes, n => Assert.False(n.HasError));
    }

    [Fact]
    public void An_Invalid_Chain_Blocks_Save_Lists_Every_Error_And_Rings_The_Bad_Nodes()
    {
        var vm = Vm();
        Assert.True(vm.SaveGraphCommand.CanExecute(null));

        // Removing the filter strands STT's output and the Agent's input.
        vm.Select(NodeOf(vm, BuilderNodeKind.Filter));
        vm.RemoveSelectedCommand.Execute(null);

        Assert.False(vm.IsChainValid);
        Assert.False(vm.SaveGraphCommand.CanExecute(null)); // #5: invalid can't be saved
        Assert.NotEmpty(vm.ValidationErrors);

        Assert.True(NodeOf(vm, BuilderNodeKind.Stt).HasError);    // dangling output
        Assert.True(NodeOf(vm, BuilderNodeKind.Agent).HasError);  // dangling input
        Assert.False(NodeOf(vm, BuilderNodeKind.Source).HasError); // still fully wired

        // Repairing the chain clears the block and the rings.
        Assert.True(vm.TryConnect(NodeOf(vm, BuilderNodeKind.Stt), NodeOf(vm, BuilderNodeKind.Agent), out _));
        Assert.True(vm.IsChainValid);
        Assert.True(vm.SaveGraphCommand.CanExecute(null));
        Assert.Empty(vm.ValidationErrors);
        Assert.All(vm.Nodes, n => Assert.False(n.HasError));
    }

    // ── exporters ────────────────────────────────────────────────────────────

    [Fact]
    public void A_Default_Shape_Exports_As_AppSettings()
    {
        var vm = Vm();
        Assert.True(vm.ExportAppSettingsCommand.CanExecute(null));
        vm.ExportAppSettingsCommand.Execute(null);

        Assert.True(vm.IsExportOpen);
        Assert.Contains("\"Stt\": \"WhisperCpp\"", vm.ExportText);
        Assert.Contains("\"Voice\": \"en_US-amy-low\"", vm.ExportText);
        Assert.Contains("\"Engine\": \"Silero\"", vm.ExportText);
        Assert.DoesNotContain("ApiKey", vm.ExportText); // never in an export, ever
    }

    [Fact]
    public void An_Untouched_Vad_Node_Defers_To_The_Profile_Not_Hardcoded_Knobs()
    {
        // Regression for the deep-review finding: seeding 0.5/800 silently beat the profile.
        // A fresh VAD node must export NO explicit VAD tuning, leaving the profile in charge.
        var vm = Vm();
        vm.SelectedProfile = "Quality";
        vm.ExportAppSettingsCommand.Execute(null);

        Assert.Contains("\"Profile\": \"Quality\"", vm.ExportText);
        Assert.DoesNotContain("ConfidenceThreshold", vm.ExportText);
        Assert.DoesNotContain("StopDurationMs", vm.ExportText);

        // The inspector shows the profile's resolved values (Quality → 0.6 / 1000 ms), not 0.5/800.
        vm.Select(NodeOf(vm, BuilderNodeKind.Vad));
        var threshold = vm.InspectorOptions.OfType<RangeOptionVm>().First(o => o.Label == "ConfidenceThreshold");
        var stop = vm.InspectorOptions.OfType<RangeOptionVm>().First(o => o.Label == "StopDuration");
        Assert.Equal(0.6, threshold.Value, 3);
        Assert.Equal(1000, stop.Value, 3);

        // But once the user moves a slider, the explicit override IS exported.
        threshold.Value = 0.42;
        vm.ExportAppSettingsCommand.Execute(null);
        Assert.Contains("\"ConfidenceThreshold\": \"0.42\"", vm.ExportText);
    }

    [Fact]
    public async Task A_Saved_Graph_Never_Carries_The_Api_Key_To_Disk()
    {
        // Regression for the Critical finding: the inspector field promises "never exported",
        // but ToJson() serialized node.Options including the key. Save must strip it.
        var path = Path.Combine(TestSupport.TempDir(), "secret-graph.json");
        var vm = Vm();
        vm.GraphPathOverride = path;
        NodeOf(vm, BuilderNodeKind.Agent).Model.Options["ApiKey"] = "sk-super-secret";
        NodeOf(vm, BuilderNodeKind.Stt).Model.Options["Model"] = "base.en"; // a NON-secret option

        await vm.SaveGraphCommand.ExecuteAsync(null);
        var onDisk = await File.ReadAllTextAsync(path);

        Assert.DoesNotContain("sk-super-secret", onDisk);
        Assert.DoesNotContain("ApiKey", onDisk);
        Assert.Contains("base.en", onDisk); // non-secret options still persist
    }

    [Fact]
    public void A_Custom_Shape_Refuses_AppSettings_And_Exports_Code()
    {
        var vm = Vm();
        // Drop the filter and wire STT straight into the agent: runnable, not config-expressible.
        var stt = NodeOf(vm, BuilderNodeKind.Stt);
        vm.Select(NodeOf(vm, BuilderNodeKind.Filter));
        vm.RemoveSelectedCommand.Execute(null);
        Assert.True(vm.TryConnect(stt, NodeOf(vm, BuilderNodeKind.Agent), out _));

        Assert.True(vm.IsChainValid);
        Assert.False(vm.IsDefaultShape);
        Assert.False(vm.ExportAppSettingsCommand.CanExecute(null));

        vm.ExportCSharpCommand.Execute(null);
        Assert.Contains("Pipeline.Build().Source", vm.ExportText);
        Assert.Contains("TryGetStt(\"WhisperCpp\"", vm.ExportText);
        Assert.Contains("TryGetTts(\"Piper\"", vm.ExportText);
        Assert.DoesNotContain("TranscriptionFilter", vm.ExportText);
        Assert.Contains("SentenceAggregator", vm.ExportText);
    }

    [Fact]
    public void Generated_CSharp_Composes_The_Same_Pipeline_The_Canvas_Runs()
    {
        // Regression for the deep-review finding: the codegen froze the VAD sample rate at 16000
        // and dropped conversation memory — both divergences from what Run actually composes.
        var vm = Vm();
        vm.ExportCSharpCommand.Execute(null);
        var code = vm.ExportText;

        // VAD takes the STT's effective input rate at runtime, not a frozen literal.
        Assert.Contains("var inputSampleRate = stt.GetEffectiveInputSampleRate(root);", code);
        Assert.Contains("SampleRate:          inputSampleRate", code);
        Assert.DoesNotContain("SampleRate:          16000", code);

        // Conversation memory is wired exactly as the composer does (on by default).
        Assert.Contains("options.Agent.ConversationMemory", code);
        Assert.Contains("new InMemoryChatHistory(options.Agent.MaxHistoryMessages)", code);
        Assert.Contains("opts.OnTurnCompleted", code);
    }

    // ── the chain compiler (real container, factories never invoked) ────────

    [Fact]
    public void The_Compiler_Mirrors_The_Composers_Tap_Positions()
    {
        var config = TestSupport.LocalConfig();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddVoxa(config);
        using var provider = services.BuildServiceProvider();

        var vm = Vm();
        Assert.True(GraphOf(vm).TryOrder(out var chain, out _));
        var compiled = BuilderChainCompiler.Compile(provider, config, chain);

        // 6 middles + 4 instrumentation taps, taps after the audio/transcription/agent/tts stages.
        Assert.Equal(16000, compiled.InputSampleRate);
        Assert.Equal(
            new[] { "vad", "tap", "stt", "filter", "tap", "agent", "tap", "aggregator", "tts", "tap" },
            compiled.Parts.Select(p => p.NodeId is null ? "tap" : p.NodeId.Split('-')[0]).ToArray());
    }

    [Fact]
    public void The_Compiler_Follows_A_Custom_Chain_Exactly()
    {
        var config = TestSupport.LocalConfig();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddVoxa(config);
        using var provider = services.BuildServiceProvider();

        // Source→STT→Agent→TTS→Sink: no VAD, no filter, no aggregator.
        var graph = new BuilderGraph();
        graph.Nodes.Add(new BuilderNode { Id = "src", Kind = BuilderNodeKind.Source });
        graph.Nodes.Add(new BuilderNode { Id = "stt", Kind = BuilderNodeKind.Stt, Provider = "WhisperCpp" });
        graph.Nodes.Add(new BuilderNode { Id = "agt", Kind = BuilderNodeKind.Agent, Provider = "Echo" });
        graph.Nodes.Add(new BuilderNode { Id = "tts", Kind = BuilderNodeKind.Tts, Provider = "Piper" });
        graph.Nodes.Add(new BuilderNode { Id = "snk", Kind = BuilderNodeKind.Sink });
        for (var i = 0; i < graph.Nodes.Count - 1; i++)
            graph.Edges.Add(new BuilderEdge(graph.Nodes[i].Id, graph.Nodes[i + 1].Id));
        Assert.True(graph.TryOrder(out var chain, out _));

        var compiled = BuilderChainCompiler.Compile(provider, config, chain);

        Assert.Equal(
            new string?[] { null, "stt", null, "agt", null, "tts", null },
            compiled.Parts.Select(p => p.NodeId).ToArray());
    }

    [Fact]
    public void Pairs_Carry_Secrets_Only_When_Asked()
    {
        var vm = Vm();
        vm.SeedFromPairs(new Dictionary<string, string?>
        {
            ["Voxa:Agent:Provider"] = "OpenAI",
        });
        NodeOf(vm, BuilderNodeKind.Agent).Model.Options["ApiKey"] = "sk-test";
        Assert.True(GraphOf(vm).TryOrder(out var chain, out _));

        var graph = GraphOf(vm);
        Assert.False(BuilderChainCompiler.Pairs(graph, chain).ContainsKey("Voxa:Agent:ApiKey"));
        Assert.Equal("sk-test",
            BuilderChainCompiler.Pairs(graph, chain, includeSecrets: true)["Voxa:Agent:ApiKey"]);
        Assert.Equal("None",
            BuilderChainCompiler.Pairs(new BuilderGraph(), chain.Where(n => n.Kind != BuilderNodeKind.Vad).ToList())
                ["Voxa:Vad:Engine"]);
    }

    private static BuilderGraph GraphOf(BuilderViewModel vm)
    {
        // The VM's document is private; its nodes share the same BuilderNode instances.
        var graph = new BuilderGraph { Profile = vm.SelectedProfile };
        graph.Nodes.AddRange(vm.Nodes.Select(n => n.Model));
        graph.Edges.AddRange(vm.Edges.Select(e => new BuilderEdge(e.From.Id, e.To.Id)));
        return graph;
    }

    // ── live mode (the canvas as instrument — fed exactly like the hub would) ──

    [Fact]
    public void Stage_Latency_Lands_On_The_Stage_Node()
    {
        var vm = Vm();
        vm.EnqueueForTest(new StageLatencyEvent("stt_final", 42.3));
        vm.DrainPending();

        var stt = NodeOf(vm, BuilderNodeKind.Stt);
        Assert.Equal("42 ms", stt.LatencyText);
        Assert.True(stt.IsActive);
    }

    [Fact]
    public void The_Gate_Opening_Makes_Audio_Edges_Flow()
    {
        var vm = Vm();
        vm.EnqueueForTest(new VadWindowEvent(0.9f, 0.1, Voiced: true, GateOpen: true));
        vm.DrainPending();

        Assert.All(vm.Edges.Where(e => e.PortType == BuilderPortType.Audio),
            e => Assert.True(e.IsFlowing));
        Assert.All(vm.Edges.Where(e => e.PortType != BuilderPortType.Audio),
            e => Assert.False(e.IsFlowing));

        vm.EnqueueForTest(new VadWindowEvent(0.1f, 0.01, Voiced: false, GateOpen: false));
        vm.DrainPending();
        Assert.All(vm.Edges, e => Assert.False(e.IsFlowing));
    }

    [Fact]
    public void A_Final_Transcript_Pulses_The_Transcription_Wires()
    {
        var vm = Vm();
        vm.EnqueueForTest(new TranscriptEvent("hello world", IsFinal: true));
        vm.DrainPending();

        Assert.All(vm.Edges.Where(e => e.PortType == BuilderPortType.Transcription),
            e => Assert.True(e.LastPulseTick > 0));
        Assert.All(vm.Edges.Where(e => e.PortType == BuilderPortType.Audio),
            e => Assert.True(e.LastPulseTick < 0));
    }

    [Fact]
    public void Audio_Out_Closes_The_Turn_And_Publishes_The_Ticker()
    {
        var vm = Vm();
        vm.EnqueueForTest(new StageLatencyEvent("vad_close", 110));
        vm.EnqueueForTest(new StageLatencyEvent("stt_final", 60));
        vm.EnqueueForTest(new StageLatencyEvent("agent_first_token", 20));
        vm.EnqueueForTest(new StageLatencyEvent("tts_first_byte", 35));
        vm.EnqueueForTest(new StageLatencyEvent("audio_out", 5));
        vm.DrainPending();

        Assert.NotNull(vm.LastTurn);
        Assert.Equal(5, vm.LastTurn!.Segments.Count);
        Assert.Equal(230, vm.LastTurn.TotalMs);
    }

    [Fact]
    public void Pipeline_Errors_Surface_In_Words()
    {
        var vm = Vm();
        vm.EnqueueForTest(new PipelineErrorEvent("stt", "model file is corrupt"));
        vm.DrainPending();
        Assert.Equal("model file is corrupt", vm.ErrorText);
    }

    [Fact]
    public void A_Live_Talk_Session_Blocks_Run()
    {
        var vm = Vm();
        Assert.True(vm.RunCommand.CanExecute(null));
        vm.RunBlocked = true;
        Assert.False(vm.RunCommand.CanExecute(null));
    }
}
