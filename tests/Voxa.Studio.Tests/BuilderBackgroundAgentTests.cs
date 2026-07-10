using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Voxa.Processors;
using Voxa.Studio.Services;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Tests;

/// <summary>
/// VDX-008 Builder surfacing: the Background agent node wires after the Agent (agent-text in/out),
/// compiles to a real <see cref="BackgroundAgentProcessor"/> with the talker's delegation armed,
/// exports as C# composition code (never appsettings — the shape is beyond UseDefaults()), and the
/// card is honest about sitting idle behind an Echo talker.
/// </summary>
public class BuilderBackgroundAgentTests
{
    // ── graph model ─────────────────────────────────────────────────────────

    [Fact]
    public void Background_Node_Wires_After_The_Agent_And_Nowhere_Earlier()
    {
        Assert.True(BuilderGraph.CanConnect(BuilderNodeKind.Agent, BuilderNodeKind.BackgroundAgent, out _));
        Assert.True(BuilderGraph.CanConnect(BuilderNodeKind.BackgroundAgent, BuilderNodeKind.Aggregator, out _));
        Assert.True(BuilderGraph.CanConnect(BuilderNodeKind.BackgroundAgent, BuilderNodeKind.Tts, out _));

        Assert.False(BuilderGraph.CanConnect(BuilderNodeKind.Stt, BuilderNodeKind.BackgroundAgent, out var reason));
        Assert.Contains("transcription", reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(BuilderGraph.CanConnect(BuilderNodeKind.BackgroundAgent, BuilderNodeKind.Sink, out _));
    }

    [Fact]
    public void A_Chain_With_A_Background_Node_Orders_But_Is_Not_Default_Shape()
    {
        var graph = new BuilderGraph();
        var kinds = new[]
        {
            BuilderNodeKind.Source, BuilderNodeKind.Vad, BuilderNodeKind.Stt, BuilderNodeKind.Filter,
            BuilderNodeKind.Agent, BuilderNodeKind.BackgroundAgent, BuilderNodeKind.Aggregator,
            BuilderNodeKind.Tts, BuilderNodeKind.Sink,
        };
        for (var i = 0; i < kinds.Length; i++)
            graph.Nodes.Add(new BuilderNode { Id = $"n{i}", Kind = kinds[i] });
        for (var i = 0; i < kinds.Length - 1; i++)
            graph.Edges.Add(new BuilderEdge($"n{i}", $"n{i + 1}"));

        Assert.True(graph.TryOrder(out var chain, out var errors));
        Assert.Empty(errors);
        Assert.False(BuilderGraph.IsDefaultShape(chain)); // exports as C# only; profile save stays blocked
    }

    // ── compile ─────────────────────────────────────────────────────────────

    private static IReadOnlyList<BuilderNode> Chain(bool background, string backgroundProvider = "Echo")
    {
        var nodes = new List<BuilderNode>();
        BuilderNode Add(BuilderNodeKind kind, string? provider)
        {
            var node = new BuilderNode { Id = $"{kind}-{nodes.Count}", Kind = kind, Provider = provider };
            nodes.Add(node);
            return node;
        }
        Add(BuilderNodeKind.Source, null);
        Add(BuilderNodeKind.Vad, "Silero");
        Add(BuilderNodeKind.Stt, "WhisperCpp").Options["Model"] = "tiny.en";
        Add(BuilderNodeKind.Filter, null);
        Add(BuilderNodeKind.Agent, "Echo");
        if (background) Add(BuilderNodeKind.BackgroundAgent, backgroundProvider);
        Add(BuilderNodeKind.Aggregator, null);
        Add(BuilderNodeKind.Tts, "Piper").Options["Voice"] = "en_US-amy-low";
        Add(BuilderNodeKind.Sink, null);
        return nodes;
    }

    private static BuilderGraph GraphOf(IReadOnlyList<BuilderNode> chain)
    {
        var graph = new BuilderGraph();
        graph.Nodes.AddRange(chain);
        return graph;
    }

    private static ServiceProvider Provider()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Voxa:Stt"] = "WhisperCpp",
            ["Voxa:Tts"] = "Piper",
            ["Voxa:Agent:Provider"] = "Echo",
            ["Voxa:Vad:Engine"] = "Silero",
            ["Voxa:Models:CachePath"] = TestSupport.TempDir(),
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddVoxa(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Compile_Materialises_A_BackgroundAgentProcessor_With_The_Keyless_Demo_Thinker()
    {
        using var provider = Provider();
        var chain = Chain(background: true);
        var compiled = BuilderChainCompiler.Compile(
            provider, provider.GetRequiredService<IConfiguration>(), chain);

        using var scope = provider.CreateScope();
        var built = compiled.Parts.Select(p => p.Factory(scope.ServiceProvider)).ToList();
        try
        {
            Assert.Single(built.OfType<BackgroundAgentProcessor>()); // no keys needed — the demo thinker is keyless
            Assert.Single(built.OfType<AgentLoopProcessor>());       // the talker stage is intact
        }
        finally
        {
            foreach (var p in built) await p.DisposeAsync();
        }
    }

    [Fact]
    public void Compile_Without_The_Node_Adds_No_Background_Part()
    {
        using var provider = Provider();
        var config = provider.GetRequiredService<IConfiguration>();
        var with = BuilderChainCompiler.Compile(provider, config, Chain(background: true));
        var without = BuilderChainCompiler.Compile(provider, config, Chain(background: false));

        Assert.Equal(without.Parts.Count + 1, with.Parts.Count); // exactly the background stage
    }

    // ── C# export ───────────────────────────────────────────────────────────

    [Fact]
    public void GenerateCSharp_Emits_The_Background_Stage_And_Arms_The_Talker()
    {
        var chain = Chain(background: true);
        var code = BuilderChainCompiler.GenerateCSharp(GraphOf(chain), chain);

        Assert.Contains("BackgroundAgentProcessor", code);
        Assert.Contains("opts.EnableBackgroundDelegation = true;", code);
        Assert.Contains("AddVoxaBackgroundAgent", code);                    // tells the server host what to register
        Assert.Contains("VoxaBackgroundAgentOptions.ServiceKey", code);
        Assert.Contains("CreateBackgroundResultMessage", code);             // background-result turns reach the model
        Assert.Contains("using Voxa.Frames;", code);                        // TurnTrigger in the generated lambda

        var plain = Chain(background: false);
        var plainCode = BuilderChainCompiler.GenerateCSharp(GraphOf(plain), plain);
        Assert.DoesNotContain("BackgroundAgentProcessor", plainCode);
        Assert.DoesNotContain("EnableBackgroundDelegation", plainCode);
    }

    // ── view model ──────────────────────────────────────────────────────────

    private static BuilderViewModel Vm() => new(TestSupport.Services());

    private static BuilderPaletteEntry Entry(BuilderViewModel vm, BuilderNodeKind kind, string provider) =>
        vm.Palette.SelectMany(g => g.Entries)
            .Single(e => e.Kind == kind && string.Equals(e.Provider, provider, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void The_Palette_Offers_Both_Thinkers_Under_Agent()
    {
        var vm = Vm();
        var agentGroup = vm.Palette.Single(g => g.Header == "Agent");
        Assert.Contains(agentGroup.Entries, e => e.Kind == BuilderNodeKind.BackgroundAgent && e.Provider == "Echo");
        Assert.Contains(agentGroup.Entries, e => e.Kind == BuilderNodeKind.BackgroundAgent && e.Provider == "OpenAI");
    }

    [Fact]
    public void The_Card_Admits_It_Sits_Idle_Behind_An_Echo_Talker()
    {
        var vm = Vm();
        // The seeded default canvas has an Echo agent; add the demo thinker next to it.
        vm.AddNodeCommand.Execute(Entry(vm, BuilderNodeKind.BackgroundAgent, "Echo"));

        var card = vm.Nodes.Single(n => n.Kind == BuilderNodeKind.BackgroundAgent);
        Assert.Contains("demo · 4s", card.Meta);
        Assert.Contains("idle", card.Meta); // Echo talker never delegates — the card says so

        // Swap the talker to OpenAI: the hint must clear on revalidation.
        var talker = vm.Nodes.Single(n => n.Kind == BuilderNodeKind.Agent);
        talker.Model.Provider = "OpenAI";
        vm.Revalidate();
        Assert.DoesNotContain("idle", card.Meta);
    }

    [Fact]
    public void The_Inspector_Offers_The_Right_Knobs_Per_Thinker()
    {
        var vm = Vm();

        vm.AddNodeCommand.Execute(Entry(vm, BuilderNodeKind.BackgroundAgent, "Echo"));
        Assert.Contains(vm.InspectorOptions, o => o.Label.Contains("Thinker delay"));
        Assert.Contains(vm.InspectorOptions, o => o.Label.Contains("Task timeout"));

        vm.AddNodeCommand.Execute(Entry(vm, BuilderNodeKind.BackgroundAgent, "OpenAI"));
        Assert.Contains(vm.InspectorOptions, o => o.Label == "Model");
        Assert.Contains(vm.InspectorOptions, o => o.Label.Contains("API key"));
        Assert.Contains(vm.InspectorOptions, o => o.Label.Contains("Task timeout"));
    }
}
