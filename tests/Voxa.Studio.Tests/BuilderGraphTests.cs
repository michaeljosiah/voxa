using Voxa.Studio.Services;

namespace Voxa.Studio.Tests;

/// <summary>
/// VST-002 D3 §8: the chain-only graph document — the frame-flow table, single-in/single-out
/// validation, Source→Sink ordering, the default-shape test, and JSON persistence.
/// </summary>
public class BuilderGraphTests
{
    private static BuilderNode Node(string id, BuilderNodeKind kind, string? provider = null) =>
        new() { Id = id, Kind = kind, Provider = provider };

    private static BuilderGraph DefaultGraph(bool withVad = true, bool withFilter = true, bool withAggregator = true)
    {
        var graph = new BuilderGraph();
        graph.Nodes.Add(Node("src", BuilderNodeKind.Source));
        if (withVad) graph.Nodes.Add(Node("vad", BuilderNodeKind.Vad, "Silero"));
        graph.Nodes.Add(Node("stt", BuilderNodeKind.Stt, "WhisperCpp"));
        if (withFilter) graph.Nodes.Add(Node("fil", BuilderNodeKind.Filter));
        graph.Nodes.Add(Node("agt", BuilderNodeKind.Agent, "Echo"));
        if (withAggregator) graph.Nodes.Add(Node("agg", BuilderNodeKind.Aggregator));
        graph.Nodes.Add(Node("tts", BuilderNodeKind.Tts, "Piper"));
        graph.Nodes.Add(Node("snk", BuilderNodeKind.Sink));
        for (var i = 0; i < graph.Nodes.Count - 1; i++)
            graph.Edges.Add(new BuilderEdge(graph.Nodes[i].Id, graph.Nodes[i + 1].Id));
        return graph;
    }

    [Fact]
    public void The_Flow_Table_Only_Wires_Matching_Frame_Types()
    {
        Assert.True(BuilderGraph.CanConnect(BuilderNodeKind.Stt, BuilderNodeKind.Agent, out _));
        Assert.True(BuilderGraph.CanConnect(BuilderNodeKind.Vad, BuilderNodeKind.Vad, out _));

        // The refusal carries the one-line reason the snap-back toast shows.
        Assert.False(BuilderGraph.CanConnect(BuilderNodeKind.Agent, BuilderNodeKind.Stt, out var reason));
        Assert.Contains("agent-text", reason);
        Assert.Contains("audio", reason);

        Assert.False(BuilderGraph.CanConnect(BuilderNodeKind.Sink, BuilderNodeKind.Tts, out var noOut));
        Assert.Contains("no output", noOut);
    }

    [Fact]
    public void A_Complete_Chain_Orders_Source_To_Sink()
    {
        Assert.True(DefaultGraph().TryOrder(out var chain, out var errors));
        Assert.Empty(errors);
        Assert.Equal(
            new[] { "src", "vad", "stt", "fil", "agt", "agg", "tts", "snk" },
            chain.Select(n => n.Id).ToArray());
    }

    [Fact]
    public void Default_Shape_Means_Exactly_What_UseDefaults_Composes()
    {
        Assert.True(DefaultGraph().TryOrder(out var full, out _));
        Assert.True(BuilderGraph.IsDefaultShape(full));

        // No VAD is still config-expressible (Vad:Engine=None)…
        Assert.True(DefaultGraph(withVad: false).TryOrder(out var noVad, out _));
        Assert.True(BuilderGraph.IsDefaultShape(noVad));

        // …but dropping the filter or aggregator is a shape appsettings can't express.
        Assert.True(DefaultGraph(withFilter: false).TryOrder(out var noFilter, out _));
        Assert.False(BuilderGraph.IsDefaultShape(noFilter));
        Assert.True(DefaultGraph(withAggregator: false).TryOrder(out var noAgg, out _));
        Assert.False(BuilderGraph.IsDefaultShape(noAgg));
    }

    [Fact]
    public void A_Dangling_Chain_Names_The_Node_That_Never_Reaches_The_Sink()
    {
        var graph = DefaultGraph();
        graph.Edges.RemoveAll(e => e.FromId == "agt");

        Assert.False(graph.TryOrder(out _, out var errors));
        Assert.Contains(errors, e => e.Contains("dangling") && e.Contains("Echo"));
    }

    [Fact]
    public void A_Stranded_Node_Is_An_Error_Not_A_Warning()
    {
        var graph = DefaultGraph();
        graph.Nodes.Add(Node("extra", BuilderNodeKind.Vad, "SilenceGate"));

        Assert.False(graph.TryOrder(out _, out var errors));
        Assert.Contains(errors, e => e.Contains("Not wired") && e.Contains("SilenceGate"));
    }

    [Fact]
    public void Loaded_Json_Gets_No_Courtesy_Two_Sources_Refused()
    {
        var graph = DefaultGraph();
        graph.Nodes.Add(Node("src2", BuilderNodeKind.Source));

        Assert.False(graph.TryOrder(out _, out var errors));
        Assert.Contains(errors, e => e.Contains("exactly one Source"));
    }

    [Fact]
    public void Chains_Are_Single_Out_A_Second_Wire_Is_Refused()
    {
        var graph = DefaultGraph(withVad: false);
        graph.Nodes.Add(Node("vad2", BuilderNodeKind.Vad, "Silero"));
        graph.Edges.Add(new BuilderEdge("src", "vad2")); // src already feeds stt

        Assert.False(graph.TryOrder(out _, out var errors));
        Assert.Contains(errors, e => e.Contains("single-out"));
    }

    [Fact]
    public void A_Cycle_Is_Reported_Not_Walked_Forever()
    {
        var graph = DefaultGraph(withVad: false);
        graph.Nodes.Add(Node("vad1", BuilderNodeKind.Vad, "Silero"));
        graph.Nodes.Add(Node("vad2", BuilderNodeKind.Vad, "SilenceGate"));
        graph.Edges.Add(new BuilderEdge("vad1", "vad2"));
        graph.Edges.Add(new BuilderEdge("vad2", "vad1"));

        Assert.False(graph.TryOrder(out _, out var errors));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Json_Roundtrip_Preserves_The_Document()
    {
        var graph = DefaultGraph();
        graph.Profile = "LowLatency";
        graph.Find("stt")!.Options["Model"] = "base.en";
        graph.Find("vad")!.X = 240;
        graph.Find("vad")!.Y = 80;

        var restored = BuilderGraph.FromJson(graph.ToJson());

        Assert.Equal("LowLatency", restored.Profile);
        Assert.Equal(graph.Nodes.Count, restored.Nodes.Count);
        Assert.Equal(graph.Edges.Count, restored.Edges.Count);
        Assert.Equal("base.en", restored.Find("stt")!.Options["Model"]);
        Assert.Equal(BuilderNodeKind.Vad, restored.Find("vad")!.Kind);
        Assert.Equal(240, restored.Find("vad")!.X);
        Assert.True(restored.TryOrder(out _, out _));
    }
}
