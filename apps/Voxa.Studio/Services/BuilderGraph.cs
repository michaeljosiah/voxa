using System.Text.Json;
using System.Text.Json.Serialization;

namespace Voxa.Studio.Services;

/// <summary>The frame type a builder port carries — the §3.3 stage palette gives each a color.</summary>
public enum BuilderPortType { Audio, Transcription, AgentText, SynthAudio }

/// <summary>
/// Node kinds the canvas can place. Voxa pipelines are a linear chain (no fan-out in the
/// runtime), so this set is exactly what the default composer can materialise — the §8.3
/// honesty constraint as a type.
/// </summary>
public enum BuilderNodeKind { Source, Vad, Stt, Filter, Agent, Aggregator, Tts, Sink }

/// <summary>One node on the canvas. Options are flat config-style values (e.g. "Model").</summary>
public sealed class BuilderNode
{
    public required string Id { get; init; }
    public required BuilderNodeKind Kind { get; init; }

    /// <summary>Registry/provider name (e.g. "WhisperCpp", "Silero", "OpenAI"); null for built-ins.</summary>
    public string? Provider { get; set; }

    public Dictionary<string, string> Options { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>A wire between an out port and an in port. At most one edge per port (chain rule).</summary>
public sealed record BuilderEdge(string FromId, string ToId);

/// <summary>
/// The builder's document: nodes + edges + the canvas-level profile. Pure data — Avalonia-free,
/// JSON-serializable, validated as a single Source→Sink chain.
/// </summary>
public sealed class BuilderGraph
{
    public string Profile { get; set; } = "Default";
    public List<BuilderNode> Nodes { get; init; } = new();
    public List<BuilderEdge> Edges { get; init; } = new();

    // ── the frame-flow table (the same truth the default composer encodes) ──

    public static (BuilderPortType? In, BuilderPortType? Out) Flow(BuilderNodeKind kind) => kind switch
    {
        BuilderNodeKind.Source     => (null, BuilderPortType.Audio),
        BuilderNodeKind.Vad        => (BuilderPortType.Audio, BuilderPortType.Audio),
        BuilderNodeKind.Stt        => (BuilderPortType.Audio, BuilderPortType.Transcription),
        BuilderNodeKind.Filter     => (BuilderPortType.Transcription, BuilderPortType.Transcription),
        BuilderNodeKind.Agent      => (BuilderPortType.Transcription, BuilderPortType.AgentText),
        BuilderNodeKind.Aggregator => (BuilderPortType.AgentText, BuilderPortType.AgentText),
        BuilderNodeKind.Tts        => (BuilderPortType.AgentText, BuilderPortType.SynthAudio),
        BuilderNodeKind.Sink       => (BuilderPortType.SynthAudio, null),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>One-line reason a wire is impossible, e.g. for the snap-back toast.</summary>
    public static bool CanConnect(BuilderNodeKind from, BuilderNodeKind to, out string? reason)
    {
        var (_, output) = Flow(from);
        var (input, _) = Flow(to);
        if (output is null) { reason = $"{from} has no output port."; return false; }
        if (input is null) { reason = $"{to} has no input port."; return false; }
        if (output != input)
        {
            reason = $"{from} emits {PortLabel(output.Value)} frames; {to} consumes {PortLabel(input.Value)}.";
            return false;
        }
        reason = null;
        return true;
    }

    public static string PortLabel(BuilderPortType type) => type switch
    {
        BuilderPortType.Audio => "audio",
        BuilderPortType.Transcription => "transcription",
        BuilderPortType.AgentText => "agent-text",
        BuilderPortType.SynthAudio => "synth-audio",
        _ => "?",
    };

    public BuilderNode? Find(string id) => Nodes.FirstOrDefault(n => n.Id == id);

    // ── validation + chain ordering ──────────────────────────────────────────

    /// <summary>
    /// Validate the document and produce the Source→Sink chain order. Construction keeps the
    /// invariants, but a loaded JSON file gets no such courtesy — everything re-checks here.
    /// </summary>
    public bool TryOrder(out IReadOnlyList<BuilderNode> chain, out IReadOnlyList<string> errors)
    {
        var problems = new List<string>();
        chain = [];

        var sources = Nodes.Where(n => n.Kind == BuilderNodeKind.Source).ToList();
        var sinks = Nodes.Where(n => n.Kind == BuilderNodeKind.Sink).ToList();
        if (sources.Count != 1) problems.Add($"The chain needs exactly one Source (found {sources.Count}).");
        if (sinks.Count != 1) problems.Add($"The chain needs exactly one Sink (found {sinks.Count}).");

        var byId = new Dictionary<string, BuilderNode>(StringComparer.Ordinal);
        foreach (var node in Nodes)
        {
            if (!byId.TryAdd(node.Id, node))
                problems.Add($"Duplicate node id '{node.Id}'.");
        }

        var outEdge = new Dictionary<string, BuilderEdge>(StringComparer.Ordinal);
        var inEdge = new Dictionary<string, BuilderEdge>(StringComparer.Ordinal);
        foreach (var edge in Edges)
        {
            if (!byId.TryGetValue(edge.FromId, out var from) || !byId.TryGetValue(edge.ToId, out var to))
            {
                problems.Add($"Edge {edge.FromId}→{edge.ToId} references a missing node.");
                continue;
            }
            if (!CanConnect(from.Kind, to.Kind, out var reason))
                problems.Add($"{NodeLabel(from)} → {NodeLabel(to)}: {reason}");
            if (!outEdge.TryAdd(edge.FromId, edge))
                problems.Add($"{NodeLabel(from)} has more than one outgoing wire (chains are single-out).");
            if (!inEdge.TryAdd(edge.ToId, edge))
                problems.Add($"{NodeLabel(to)} has more than one incoming wire (chains are single-in).");
        }

        if (problems.Count > 0) { errors = problems; return false; }

        // Walk the single out-edges from the source; the walk must end at the sink and visit
        // every node (an orphan or a dangling middle node is an incomplete chain, not a warning).
        var ordered = new List<BuilderNode>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var cursor = sources[0];
        while (true)
        {
            if (!visited.Add(cursor.Id)) { problems.Add("The chain loops back on itself."); break; }
            ordered.Add(cursor);
            if (cursor.Kind == BuilderNodeKind.Sink) break;
            if (!outEdge.TryGetValue(cursor.Id, out var next))
            {
                problems.Add($"{NodeLabel(cursor)} has a dangling output — the chain never reaches the Sink.");
                break;
            }
            cursor = byId[next.ToId];
        }

        if (problems.Count == 0 && ordered.Count != Nodes.Count)
        {
            var stranded = Nodes.Where(n => !visited.Contains(n.Id)).Select(NodeLabel);
            problems.Add($"Not wired into the chain: {string.Join(", ", stranded)}.");
        }

        errors = problems;
        if (problems.Count > 0) return false;
        chain = ordered;
        return true;
    }

    public static string NodeLabel(BuilderNode node) => node.Provider ?? node.Kind.ToString();

    /// <summary>
    /// True when the chain is exactly what <c>UseDefaults()</c> composes — Source, optional VAD,
    /// STT, TranscriptionFilter, Agent, SentenceAggregator, TTS, Sink — and is therefore honestly
    /// expressible as an appsettings block. Anything else exports as composition code instead.
    /// </summary>
    public static bool IsDefaultShape(IReadOnlyList<BuilderNode> chain)
    {
        var kinds = chain.Select(n => n.Kind).ToList();
        BuilderNodeKind[][] shapes =
        [
            [BuilderNodeKind.Source, BuilderNodeKind.Vad, BuilderNodeKind.Stt, BuilderNodeKind.Filter,
             BuilderNodeKind.Agent, BuilderNodeKind.Aggregator, BuilderNodeKind.Tts, BuilderNodeKind.Sink],
            [BuilderNodeKind.Source, BuilderNodeKind.Stt, BuilderNodeKind.Filter,
             BuilderNodeKind.Agent, BuilderNodeKind.Aggregator, BuilderNodeKind.Tts, BuilderNodeKind.Sink],
        ];
        return shapes.Any(s => s.SequenceEqual(kinds));
    }

    // ── persistence (the user-profile JSON of §8.2's canvas furniture) ──────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static BuilderGraph FromJson(string json) =>
        JsonSerializer.Deserialize<BuilderGraph>(json, JsonOptions)
            ?? throw new InvalidDataException("The graph file is empty.");
}
