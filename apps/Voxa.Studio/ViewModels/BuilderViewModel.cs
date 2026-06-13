using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Voxa.AspNetCore;
using Voxa.Diagnostics;
using Voxa.Speech;
using Voxa.Speech.Kokoro;
using Voxa.Speech.Piper;
using Voxa.Speech.WhisperCpp;
using Voxa.Studio.Audio;
using Voxa.Studio.Services;

namespace Voxa.Studio.ViewModels;

/// <summary>A palette row: one placeable node kind/provider with its port color.</summary>
public sealed record BuilderPaletteEntry(
    BuilderNodeKind Kind, string? Provider, string Label, BuilderPortType DotType);

/// <summary>A palette section ("VAD", "STT", …).</summary>
public sealed record BuilderPaletteGroup(string Header, IReadOnlyList<BuilderPaletteEntry> Entries);

// ── inspector option rows (three editor shapes, template-matched by type) ──

public abstract partial class BuilderOptionVm : ObservableObject
{
    private readonly Action<string> _write;
    protected BuilderOptionVm(string label, Action<string> write) { Label = label; _write = write; }
    public string Label { get; }
    protected void Write(string value) => _write(value);
}

public sealed partial class ChoiceOptionVm : BuilderOptionVm
{
    public ChoiceOptionVm(string label, IReadOnlyList<string> choices, string value, Action<string> write)
        : base(label, write) { Choices = choices; _value = value; }
    public IReadOnlyList<string> Choices { get; }
    [ObservableProperty] private string _value;
    partial void OnValueChanged(string value) => Write(value);
}

public sealed partial class TextOptionVm : BuilderOptionVm
{
    public TextOptionVm(string label, string value, Action<string> write, bool isSecret = false)
        : base(label, write) { _value = value; IsSecret = isSecret; }
    public bool IsSecret { get; }
    [ObservableProperty] private string _value;
    partial void OnValueChanged(string value) => Write(value);
}

public sealed partial class RangeOptionVm : BuilderOptionVm
{
    private readonly string _format;
    public RangeOptionVm(string label, double min, double max, double step, double value,
        string format, string unit, Action<string> write)
        : base(label, write) { Min = min; Max = max; Step = step; _value = value; _format = format; Unit = unit; }
    public double Min { get; }
    public double Max { get; }
    public double Step { get; }
    public string Unit { get; }
    [ObservableProperty] private double _value;
    public string ValueText => Value.ToString(_format, CultureInfo.InvariantCulture) + Unit;
    partial void OnValueChanged(double value)
    {
        OnPropertyChanged(nameof(ValueText));
        Write(value.ToString(_format, CultureInfo.InvariantCulture));
    }
}

/// <summary>One node on the canvas — position, selection, and (while live) real instrument state.</summary>
public sealed partial class BuilderNodeVm : ObservableObject
{
    public const double NodeWidth = 132;
    public const double NodeHeight = 60;

    public BuilderNodeVm(BuilderNode model)
    {
        Model = model;
        _x = model.X;
        _y = model.Y;
        (InType, OutType) = BuilderGraph.Flow(model.Kind);
    }

    public BuilderNode Model { get; }
    public string Id => Model.Id;
    public BuilderNodeKind Kind => Model.Kind;
    public BuilderPortType? InType { get; }
    public BuilderPortType? OutType { get; }
    public bool HasIn => InType is not null;
    public bool HasOut => OutType is not null;

    /// <summary>Stage palette key — the §3.3 stage colors mean the same stages everywhere.</summary>
    public string StageKey => Kind switch
    {
        BuilderNodeKind.Source or BuilderNodeKind.Vad => "vad",
        BuilderNodeKind.Stt or BuilderNodeKind.Filter => "stt",
        BuilderNodeKind.Agent or BuilderNodeKind.Aggregator => "agent",
        BuilderNodeKind.Tts => "tts",
        _ => "out",
    };

    public string KindLabel => Kind switch
    {
        BuilderNodeKind.Source => "source",
        BuilderNodeKind.Vad => "vad",
        BuilderNodeKind.Stt => "stt",
        BuilderNodeKind.Filter => "filter",
        BuilderNodeKind.Agent => "agent",
        BuilderNodeKind.Aggregator => "aggregator",
        BuilderNodeKind.Tts => "tts",
        _ => "sink",
    };

    public string Name => Kind switch
    {
        BuilderNodeKind.Source => "Mic",
        BuilderNodeKind.Sink => "Speaker",
        BuilderNodeKind.Filter => "TranscriptionFilter",
        BuilderNodeKind.Aggregator => "SentenceAggregator",
        BuilderNodeKind.Vad when Model.Provider is not null => $"{Model.Provider} VAD",
        _ => Model.Provider ?? Kind.ToString(),
    };

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _meta = "";
    [ObservableProperty] private bool _isCached;

    /// <summary>The '+' affordance: visible while the out port dangles (set by the VM).</summary>
    [ObservableProperty] private bool _showPlus;

    /// <summary>Pulsed true→false by the view for the refused-wire shake.</summary>
    [ObservableProperty] private bool _isRefused;

    // live instrument state (real hub data only — blank while idle, per P4)
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string? _latencyText;
    [ObservableProperty] private string? _queueText;

    internal long ActiveSinceTick;

    partial void OnXChanged(double value) => Model.X = value;
    partial void OnYChanged(double value) => Model.Y = value;
}

/// <summary>A wire on the canvas. Live state drives the edge renderer's particles.</summary>
public sealed partial class BuilderEdgeVm : ObservableObject
{
    public BuilderEdgeVm(BuilderNodeVm from, BuilderNodeVm to, BuilderPortType type)
    {
        From = from;
        To = to;
        PortType = type;
    }

    public BuilderNodeVm From { get; }
    public BuilderNodeVm To { get; }
    public BuilderPortType PortType { get; }

    /// <summary>Audio edges shimmer while the VAD gate is open.</summary>
    [ObservableProperty] private bool _isFlowing;

    /// <summary>Tick (Environment.TickCount64) of the last discrete frame pulse on this wire.</summary>
    public long LastPulseTick { get; internal set; } = long.MinValue;
}

/// <summary>
/// The Pipeline Builder (VST-002 D3, §8): a chain-only node canvas over the live provider
/// registry. Avalonia-free — the view's ≤30 fps timer calls <see cref="DrainPending"/> and the
/// edge renderer reads the node/edge VMs, the TalkViewModel pattern.
/// </summary>
public sealed partial class BuilderViewModel : ObservableObject
{
    private const double GridSnap = 20;
    private const double TidyGapX = 176;
    private const double TidyY = 120;
    private const int UndoCapacity = 50;
    private static readonly TimeSpan NodeGlow = TimeSpan.FromMilliseconds(700);

    private readonly StudioServices _services;
    private readonly ConcurrentQueue<DiagnosticEvent> _pending = new();
    private readonly List<string> _undo = new();
    private readonly List<string> _redo = new();

    private BuilderGraph _graph = new();
    private TalkSession? _session;
    private ServiceProvider? _runProvider;
    private CancellationTokenSource? _subscription;
    private IReadOnlyList<CompiledPart> _runParts = [];
    private Dictionary<string, double> _stages = new();
    private int _turnNumber;
    private int _nextId;

    public BuilderViewModel(StudioServices services)
    {
        _services = services;
        Palette = BuildPalette();
        SeedFromPairs(LivePairs());
    }

    // ── bindable state ───────────────────────────────────────────────────────

    public IReadOnlyList<BuilderPaletteGroup> Palette { get; }
    public ObservableCollection<BuilderNodeVm> Nodes { get; } = new();
    public ObservableCollection<BuilderEdgeVm> Edges { get; } = new();
    public ObservableCollection<BuilderOptionVm> InspectorOptions { get; } = new();
    public IReadOnlyList<string> Profiles { get; } = ["Default", "LowLatency", "Quality", "Cheap"];

    [ObservableProperty] private string _selectedProfile = "Default";
    [ObservableProperty] private BuilderNodeVm? _selectedNode;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(StopCommand))]
    private bool _isRunning;
    /// <summary>True while a Talk session owns the audio device — Run disables.</summary>
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private bool _runBlocked;
    [ObservableProperty] private string _statusText = "Wire the chain — ports only accept matching frame types.";
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private bool _isChainValid;
    [ObservableProperty] private string _chainText = "";
    [ObservableProperty] private bool _isDefaultShape;

    // export pane (progressive disclosure: hidden until an export button is pressed)
    [ObservableProperty] private bool _isExportOpen;
    [ObservableProperty] private string _exportTitle = "";
    [ObservableProperty] private string _exportText = "";

    // live turn ticker (the canvas-bottom strip; real waterfalls only)
    [ObservableProperty] private TurnWaterfall? _lastTurn;

    /// <summary>Compatible follow-ups for the selected node's dangling out port ('+' affordance).</summary>
    public ObservableCollection<BuilderPaletteEntry> PlusChoices { get; } = new();

    // ── palette ──────────────────────────────────────────────────────────────

    private IReadOnlyList<BuilderPaletteGroup> BuildPalette()
    {
        var vads = new[] { "Silero", "SilenceGate" }
            .Union(_services.Registry.VadNames, StringComparer.OrdinalIgnoreCase)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var stts = _services.Registry.SttNames.OrderBy(n => n, StringComparer.Ordinal).ToList();
        var ttss = _services.Registry.TtsNames.OrderBy(n => n, StringComparer.Ordinal).ToList();

        return
        [
            new("Input", [new(BuilderNodeKind.Source, null, "Mic", BuilderPortType.Audio)]),
            new("VAD", vads.Select(v => new BuilderPaletteEntry(BuilderNodeKind.Vad, v, v, BuilderPortType.Audio)).ToList()),
            new("STT", stts.Select(s => new BuilderPaletteEntry(BuilderNodeKind.Stt, s, s, BuilderPortType.Transcription)).ToList()),
            new("Text", [
                new(BuilderNodeKind.Filter, null, "TranscriptionFilter", BuilderPortType.Transcription),
                new(BuilderNodeKind.Aggregator, null, "SentenceAggregator", BuilderPortType.AgentText)]),
            new("Agent", [
                new(BuilderNodeKind.Agent, "Echo", "Echo agent", BuilderPortType.AgentText),
                new(BuilderNodeKind.Agent, "OpenAI", "OpenAI agent", BuilderPortType.AgentText)]),
            new("TTS", ttss.Select(t => new BuilderPaletteEntry(BuilderNodeKind.Tts, t, t, BuilderPortType.SynthAudio)).ToList()),
            new("Output", [new(BuilderNodeKind.Sink, null, "Speaker", BuilderPortType.SynthAudio)]),
        ];
    }

    // ── graph mutations ──────────────────────────────────────────────────────

    [RelayCommand]
    private void AddNode(BuilderPaletteEntry entry)
    {
        PushUndo();
        var vm = Materialise(NewNode(entry));
        var rightmost = Nodes.Where(n => n != vm).Select(n => n.X + BuilderNodeVm.NodeWidth).DefaultIfEmpty(24 - 44).Max();
        vm.X = Snap(rightmost + 44);
        vm.Y = TidyY;
        Select(vm);
        Revalidate();
    }

    /// <summary>
    /// Place a palette node at a dropped canvas point. Dragging from the palette is the only way to
    /// add a node (the canvas drop hands the position here), so the node lands where the user let go.
    /// </summary>
    public void AddNodeAt(BuilderPaletteEntry entry, double x, double y)
    {
        PushUndo();
        var vm = Materialise(NewNode(entry));
        vm.X = Snap(Math.Max(0, x - BuilderNodeVm.NodeWidth / 2));
        vm.Y = Snap(Math.Max(0, y - BuilderNodeVm.NodeHeight / 2));
        Select(vm);
        Revalidate();
    }

    /// <summary>The '+' affordance: append a compatible node already wired to the dangling port.</summary>
    [RelayCommand]
    private void AddAndConnect(BuilderPaletteEntry entry)
    {
        var from = SelectedNode;
        if (from is null || from.OutType is null) return;
        PushUndo();
        var vm = Materialise(NewNode(entry));
        vm.X = Snap(from.X + TidyGapX);
        vm.Y = from.Y;
        Edges.Add(new BuilderEdgeVm(from, vm, from.OutType.Value));
        _graph.Edges.Add(new BuilderEdge(from.Id, vm.Id));
        Select(vm);
        Revalidate();
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        var vm = SelectedNode;
        if (vm is null) return;
        PushUndo();
        foreach (var edge in Edges.Where(e => e.From == vm || e.To == vm).ToList())
            Edges.Remove(edge);
        _graph.Edges.RemoveAll(e => e.FromId == vm.Id || e.ToId == vm.Id);
        Nodes.Remove(vm);
        _graph.Nodes.Remove(vm.Model);
        Select(null);
        Revalidate();
    }

    /// <summary>Wire two nodes. Refusals return the one-line reason the snap-back shows.</summary>
    public bool TryConnect(BuilderNodeVm from, BuilderNodeVm to, out string? reason)
    {
        if (from == to) { reason = "A node can't feed itself."; return false; }
        if (!BuilderGraph.CanConnect(from.Kind, to.Kind, out reason)) return false;
        if (Edges.Any(e => e.From == from)) { reason = $"{from.Name} already has an outgoing wire (chains are single-out)."; return false; }
        if (Edges.Any(e => e.To == to)) { reason = $"{to.Name} already has an incoming wire (chains are single-in)."; return false; }

        PushUndo();
        Edges.Add(new BuilderEdgeVm(from, to, from.OutType!.Value));
        _graph.Edges.Add(new BuilderEdge(from.Id, to.Id));
        Revalidate();
        return true;
    }

    [RelayCommand]
    private void DisconnectInput()
    {
        if (SelectedNode is { } node && Edges.FirstOrDefault(e => e.To == node) is { } edge)
            Disconnect(edge);
    }

    [RelayCommand]
    private void DisconnectOutput()
    {
        if (SelectedNode is { } node && Edges.FirstOrDefault(e => e.From == node) is { } edge)
            Disconnect(edge);
    }

    public void Disconnect(BuilderEdgeVm edge)
    {
        PushUndo();
        Edges.Remove(edge);
        _graph.Edges.RemoveAll(e => e.FromId == edge.From.Id && e.ToId == edge.To.Id);
        Revalidate();
    }

    public void Select(BuilderNodeVm? vm)
    {
        foreach (var node in Nodes) node.IsSelected = node == vm;
        SelectedNode = vm;
    }

    partial void OnSelectedNodeChanged(BuilderNodeVm? value)
    {
        RebuildInspector();
        RefreshPlusChoices();
    }

    partial void OnSelectedProfileChanged(string value)
    {
        _graph.Profile = value;
        // VAD cards and sliders show profile-resolved defaults — refresh them when the profile moves.
        foreach (var node in Nodes) RefreshNodeBadges(node);
        if (SelectedNode?.Kind == BuilderNodeKind.Vad) RebuildInspector();
        Revalidate();
    }

    /// <summary>
    /// The selected profile's resolved tuning (no explicit overrides) — the same public path
    /// the composer takes. Used so untouched VAD knobs display what actually runs.
    /// </summary>
    private VoxaEffectiveTuning ProfileTuning() =>
        _services.Provider.GetRequiredService<VoxaTuningResolver>()
            .Resolve(new VoxaOptions { Profile = SelectedProfile });

    /// <summary>Snap a dragged node to the grid (view calls this when the drag ends).</summary>
    public void SnapNode(BuilderNodeVm vm)
    {
        vm.X = Snap(vm.X);
        vm.Y = Snap(vm.Y);
    }

    private static double Snap(double v) => Math.Max(0, Math.Round(v / GridSnap) * GridSnap);

    [RelayCommand]
    private void Tidy()
    {
        PushUndo();
        var ordered = _graph.TryOrder(out var chain, out _)
            ? chain.Select(n => Nodes.First(vm => vm.Id == n.Id)).ToList()
            : Nodes.ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].X = 24 + i * TidyGapX;
            ordered[i].Y = TidyY;
        }
    }

    private BuilderNode NewNode(BuilderPaletteEntry entry)
    {
        var node = new BuilderNode
        {
            Id = $"{entry.Kind.ToString().ToLowerInvariant()}-{++_nextId}",
            Kind = entry.Kind,
            Provider = entry.Provider,
        };
        switch (entry.Kind)
        {
            // VAD tuning is intentionally NOT seeded: an empty Options means "follow the profile"
            // (the resolver merges explicit config OVER the profile, so a seeded 0.5/800 would
            // silently beat a Quality selection). The inspector sliders display the profile's
            // resolved values; only a user edit writes an explicit override.
            case BuilderNodeKind.Stt when Is(entry.Provider, "WhisperCpp"):
                node.Options["Model"] = "tiny.en";
                break;
            case BuilderNodeKind.Agent when Is(entry.Provider, "OpenAI"):
                node.Options["Model"] = "gpt-4o-mini";
                break;
            case BuilderNodeKind.Tts when Is(entry.Provider, "Piper"):
                node.Options["Voice"] = "en_US-amy-low";
                break;
            case BuilderNodeKind.Tts when Is(entry.Provider, "Kokoro"):
                node.Options["Voice"] = "af_heart";
                node.Options["Precision"] = "int8";
                break;
        }
        return node;
    }

    private BuilderNodeVm Materialise(BuilderNode node)
    {
        _graph.Nodes.Add(node);
        var vm = new BuilderNodeVm(node);
        RefreshNodeBadges(vm);
        Nodes.Add(vm);
        return vm;
    }

    private static bool Is(string? provider, string name) =>
        string.Equals(provider, name, StringComparison.OrdinalIgnoreCase);

    // ── validation + status ──────────────────────────────────────────────────

    internal void Revalidate()
    {
        if (_graph.TryOrder(out var chain, out var errors))
        {
            IsChainValid = true;
            IsDefaultShape = BuilderGraph.IsDefaultShape(chain);
            ChainText = string.Join(" → ", chain.Select(BuilderGraph.NodeLabel));
            StatusText = IsDefaultShape
                ? "Valid chain — matches UseDefaults(); exports as appsettings."
                : "Valid chain — custom shape; exports as C# composition code.";
        }
        else
        {
            IsChainValid = false;
            IsDefaultShape = false;
            ChainText = "";
            StatusText = errors[0];
        }
        foreach (var node in Nodes) node.ShowPlus = node.HasOut && Edges.All(e => e.From != node);
        RefreshPlusChoices();
        RunCommand.NotifyCanExecuteChanged();
        ExportAppSettingsCommand.NotifyCanExecuteChanged();
        ExportCSharpCommand.NotifyCanExecuteChanged();
    }

    private void RefreshPlusChoices()
    {
        PlusChoices.Clear();
        var from = SelectedNode;
        if (from?.OutType is not { } outType || Edges.Any(e => e.From == from)) return;
        foreach (var entry in Palette.SelectMany(g => g.Entries))
            if (BuilderGraph.Flow(entry.Kind).In == outType)
                PlusChoices.Add(entry);
    }

    // ── inspector ────────────────────────────────────────────────────────────

    private void RebuildInspector()
    {
        InspectorOptions.Clear();
        var vm = SelectedNode;
        if (vm is null) return;
        var node = vm.Model;

        void Touch(string key, string value)
        {
            node.Options[key] = value;
            RefreshNodeBadges(vm);
            Revalidate();
        }

        switch (node.Kind)
        {
            case BuilderNodeKind.Source:
                InspectorOptions.Add(new ChoiceOptionVm("Microphone",
                    _services.AudioDevice.CaptureEndpoints().Select(e => e.DisplayName).ToList(),
                    node.Options.GetValueOrDefault("Device", ""), v => Touch("Device", v)));
                break;
            case BuilderNodeKind.Sink:
                InspectorOptions.Add(new ChoiceOptionVm("Speaker",
                    _services.AudioDevice.RenderEndpoints().Select(e => e.DisplayName).ToList(),
                    node.Options.GetValueOrDefault("Device", ""), v => Touch("Device", v)));
                break;
            case BuilderNodeKind.Vad:
                // Sliders default to the SELECTED PROFILE's resolved values, so an untouched VAD
                // node honestly shows what runs; dragging writes an explicit override.
                var tuning = ProfileTuning();
                InspectorOptions.Add(new RangeOptionVm("ConfidenceThreshold", 0, 1, 0.05,
                    ParseDouble(node.Options.GetValueOrDefault("ConfidenceThreshold"), tuning.VadConfidenceThreshold),
                    "0.00", "", v => Touch("ConfidenceThreshold", v)));
                InspectorOptions.Add(new RangeOptionVm("StopDuration", 200, 1500, 50,
                    ParseDouble(node.Options.GetValueOrDefault("StopDurationMs"), tuning.VadStopDuration.TotalMilliseconds),
                    "0", " ms", v => Touch("StopDurationMs", v)));
                break;
            case BuilderNodeKind.Stt when Is(node.Provider, "WhisperCpp"):
                InspectorOptions.Add(new ChoiceOptionVm("Model", WhisperCppModelCatalog.KnownModels.ToList(),
                    node.Options.GetValueOrDefault("Model", "tiny.en"), v => Touch("Model", v)));
                break;
            case BuilderNodeKind.Agent when Is(node.Provider, "OpenAI"):
                InspectorOptions.Add(new TextOptionVm("Model",
                    node.Options.GetValueOrDefault("Model", "gpt-4o-mini"), v => Touch("Model", v)));
                InspectorOptions.Add(new TextOptionVm("API key (run only, never exported)",
                    node.Options.GetValueOrDefault("ApiKey", ""), v => Touch("ApiKey", v), isSecret: true));
                break;
            case BuilderNodeKind.Tts when Is(node.Provider, "Piper"):
                InspectorOptions.Add(new ChoiceOptionVm("Voice", PiperVoiceCatalog.KnownVoices.ToList(),
                    node.Options.GetValueOrDefault("Voice", "en_US-amy-low"), v => Touch("Voice", v)));
                break;
            case BuilderNodeKind.Tts when Is(node.Provider, "Kokoro"):
                InspectorOptions.Add(new ChoiceOptionVm("Voice", KokoroCatalog.KnownVoices.ToList(),
                    node.Options.GetValueOrDefault("Voice", "af_heart"), v => Touch("Voice", v)));
                InspectorOptions.Add(new ChoiceOptionVm("Precision", KokoroCatalog.KnownPrecisions.ToList(),
                    node.Options.GetValueOrDefault("Precision", "int8"), v => Touch("Precision", v)));
                break;
        }
    }

    private static double ParseDouble(string? s, double fallback) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>The node card's mono meta line + cached badge — real state, not decoration.</summary>
    private void RefreshNodeBadges(BuilderNodeVm vm)
    {
        var node = vm.Model;
        switch (node.Kind)
        {
            case BuilderNodeKind.Source:
            case BuilderNodeKind.Sink:
                vm.Meta = node.Options.GetValueOrDefault("Device", "") is { Length: > 0 } device
                    ? device : "default device";
                break;
            case BuilderNodeKind.Vad:
                // Show the profile's resolved values when no explicit override — the card never
                // claims a number the run won't use.
                var vad = ProfileTuning();
                var stop = node.Options.GetValueOrDefault("StopDurationMs",
                    ((int)vad.VadStopDuration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
                var thr = node.Options.GetValueOrDefault("ConfidenceThreshold",
                    vad.VadConfidenceThreshold.ToString("0.##", CultureInfo.InvariantCulture));
                vm.Meta = $"stop {stop} ms · thr {thr}";
                break;
            case BuilderNodeKind.Stt when Is(node.Provider, "WhisperCpp"):
                var model = node.Options.GetValueOrDefault("Model", "tiny.en");
                vm.IsCached = WhisperCppModelCatalog.TryGet(model, out var artifact)
                    && _services.ModelCache.IsCached(artifact);
                vm.Meta = model;
                break;
            case BuilderNodeKind.Filter:
                vm.Meta = "drops hallucinations";
                break;
            case BuilderNodeKind.Agent:
                vm.Meta = Is(node.Provider, "OpenAI")
                    ? node.Options.GetValueOrDefault("Model", "gpt-4o-mini") : "replies verbatim";
                break;
            case BuilderNodeKind.Aggregator:
                vm.Meta = "sentence chunks";
                break;
            case BuilderNodeKind.Tts when Is(node.Provider, "Piper"):
                var piperVoice = node.Options.GetValueOrDefault("Voice", "en_US-amy-low");
                vm.IsCached = PiperVoiceCatalog.TryGet(piperVoice, out var piperEntry)
                    && _services.ModelCache.IsCached(piperEntry.Onnx)
                    && _services.ModelCache.IsCached(piperEntry.Json);
                vm.Meta = piperVoice;
                break;
            case BuilderNodeKind.Tts when Is(node.Provider, "Kokoro"):
                vm.Meta = node.Options.GetValueOrDefault("Voice", "af_heart");
                break;
            default:
                vm.Meta = node.Provider ?? "";
                break;
        }
    }

    /// <summary>Cache state can change behind us (Models view downloads) — refresh on entry.</summary>
    public void RefreshCacheState()
    {
        foreach (var vm in Nodes) RefreshNodeBadges(vm);
    }

    // ── undo / redo ──────────────────────────────────────────────────────────

    internal void PushUndo()
    {
        _undo.Add(_graph.ToJson());
        if (_undo.Count > UndoCapacity) _undo.RemoveAt(0);
        _redo.Clear();
    }

    [RelayCommand]
    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(_graph.ToJson());
        var json = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Restore(BuilderGraph.FromJson(json));
    }

    [RelayCommand]
    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(_graph.ToJson());
        var json = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Restore(BuilderGraph.FromJson(json));
    }

    // ── seed / load / save ───────────────────────────────────────────────────

    private Dictionary<string, string?> LivePairs()
    {
        var voxa = _services.Configuration.GetSection("Voxa");
        return new Dictionary<string, string?>
        {
            ["Voxa:Profile"] = voxa["Profile"],
            ["Voxa:Vad:Engine"] = voxa["Vad:Engine"],
            ["Voxa:Stt"] = voxa["Stt"],
            ["Voxa:WhisperCpp:Model"] = voxa["WhisperCpp:Model"],
            ["Voxa:Agent:Provider"] = voxa["Agent:Provider"],
            ["Voxa:Agent:Model"] = voxa["Agent:Model"],
            ["Voxa:Tts"] = voxa["Tts"],
            ["Voxa:Piper:Voice"] = voxa["Piper:Voice"],
            ["Voxa:Kokoro:Voice"] = voxa["Kokoro:Voice"],
            ["Voxa:Kokoro:Precision"] = voxa["Kokoro:Precision"],
        };
    }

    /// <summary>
    /// Build the default-shape graph from flat config pairs — the initial canvas (the active
    /// config as a graph) and the Config view's "Open in Builder" both land here.
    /// </summary>
    public void SeedFromPairs(IReadOnlyDictionary<string, string?> pairs)
    {
        string Get(string key, string fallback) =>
            pairs.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : fallback;

        var graph = new BuilderGraph { Profile = Get("Voxa:Profile", "Default") };
        var chain = new List<BuilderNode>();
        _nextId = 0;

        BuilderNode Add(BuilderNodeKind kind, string? provider)
        {
            var node = new BuilderNode
            {
                Id = $"{kind.ToString().ToLowerInvariant()}-{++_nextId}",
                Kind = kind,
                Provider = provider,
            };
            chain.Add(node);
            graph.Nodes.Add(node);
            return node;
        }

        Add(BuilderNodeKind.Source, null);
        var engine = Get("Voxa:Vad:Engine", "Silero");
        if (!string.Equals(engine, "None", StringComparison.OrdinalIgnoreCase))
        {
            var vad = Add(BuilderNodeKind.Vad, engine);
            // Only carry VAD knobs the source ACTUALLY set — an absent key means "follow the
            // profile", so seeding a default here would quietly override the profile selector.
            if (pairs.TryGetValue("Voxa:Vad:ConfidenceThreshold", out var threshold) && !string.IsNullOrEmpty(threshold))
                vad.Options["ConfidenceThreshold"] = threshold;
            if (pairs.TryGetValue("Voxa:Vad:StopDurationMs", out var stopMs) && !string.IsNullOrEmpty(stopMs))
                vad.Options["StopDurationMs"] = stopMs;
        }
        var stt = Add(BuilderNodeKind.Stt, Get("Voxa:Stt", "WhisperCpp"));
        if (Is(stt.Provider, "WhisperCpp")) stt.Options["Model"] = Get("Voxa:WhisperCpp:Model", "tiny.en");
        Add(BuilderNodeKind.Filter, null);
        var agent = Add(BuilderNodeKind.Agent, Get("Voxa:Agent:Provider", "Echo"));
        if (Is(agent.Provider, "OpenAI")) agent.Options["Model"] = Get("Voxa:Agent:Model", "gpt-4o-mini");
        Add(BuilderNodeKind.Aggregator, null);
        var tts = Add(BuilderNodeKind.Tts, Get("Voxa:Tts", "Piper"));
        if (Is(tts.Provider, "Piper")) tts.Options["Voice"] = Get("Voxa:Piper:Voice", "en_US-amy-low");
        if (Is(tts.Provider, "Kokoro"))
        {
            tts.Options["Voice"] = Get("Voxa:Kokoro:Voice", "af_heart");
            tts.Options["Precision"] = Get("Voxa:Kokoro:Precision", "int8");
        }
        Add(BuilderNodeKind.Sink, null);

        for (var i = 0; i < chain.Count - 1; i++)
        {
            graph.Edges.Add(new BuilderEdge(chain[i].Id, chain[i + 1].Id));
            chain[i].X = 24 + i * TidyGapX;
            chain[i].Y = TidyY;
        }
        chain[^1].X = 24 + (chain.Count - 1) * TidyGapX;
        chain[^1].Y = TidyY;

        Restore(graph);
        _undo.Clear();
        _redo.Clear();
    }

    /// <summary>Replace the document and rebuild every VM from it (undo, load, seed).</summary>
    private void Restore(BuilderGraph graph)
    {
        _graph = graph;
        Nodes.Clear();
        Edges.Clear();
        Select(null);
        foreach (var node in graph.Nodes)
        {
            var vm = new BuilderNodeVm(node);
            RefreshNodeBadges(vm);
            Nodes.Add(vm);
        }
        foreach (var edge in graph.Edges)
        {
            var from = Nodes.FirstOrDefault(n => n.Id == edge.FromId);
            var to = Nodes.FirstOrDefault(n => n.Id == edge.ToId);
            if (from?.OutType is { } type && to is not null)
                Edges.Add(new BuilderEdgeVm(from, to, type));
        }
        _nextId = Math.Max(_nextId, graph.Nodes.Count == 0 ? 0
            : graph.Nodes.Max(n => int.TryParse(n.Id.Split('-').LastOrDefault(), out var i) ? i : 0));
        SelectedProfile = Profiles.FirstOrDefault(p =>
            string.Equals(p, graph.Profile, StringComparison.OrdinalIgnoreCase)) ?? "Default";
        Revalidate();
    }

    /// <summary>Test seam: redirects the save/load file out of the real user profile.</summary>
    internal string? GraphPathOverride;

    private string GraphPath => GraphPathOverride
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "voxa-graph.json");

    [RelayCommand]
    private async Task SaveGraphAsync()
    {
        try
        {
            // excludeSecrets: an API key typed into the inspector runs the live graph but never
            // reaches disk — the Config view's rule, enforced at the serializer.
            await File.WriteAllTextAsync(GraphPath, _graph.ToJson(excludeSecrets: true));
            StatusText = $"Saved {GraphPath}";
        }
        catch (Exception ex) { ErrorText = ex.Message; }
    }

    [RelayCommand]
    private async Task LoadGraphAsync()
    {
        try
        {
            if (!File.Exists(GraphPath))
            {
                StatusText = $"No saved graph at {GraphPath} yet — Save writes one.";
                return;
            }
            PushUndo();
            Restore(BuilderGraph.FromJson(await File.ReadAllTextAsync(GraphPath)));
            StatusText = $"Loaded {GraphPath}";
        }
        catch (Exception ex) { ErrorText = ex.Message; }
    }

    // ── exporters ────────────────────────────────────────────────────────────

    private bool CanExportAppSettings() => IsChainValid && IsDefaultShape;

    [RelayCommand(CanExecute = nameof(CanExportAppSettings))]
    private void ExportAppSettings()
    {
        if (!_graph.TryOrder(out var chain, out _)) return;
        ExportTitle = "appsettings.json — this chain matches UseDefaults()";
        ExportText = ConfigViewModel.ToNestedJson(BuilderChainCompiler.Pairs(_graph, chain));
        IsExportOpen = true;
    }

    private bool CanExportCSharp() => IsChainValid;

    [RelayCommand(CanExecute = nameof(CanExportCSharp))]
    private void ExportCSharp()
    {
        if (!_graph.TryOrder(out var chain, out _)) return;
        ExportTitle = "C# composition — the same chain, composed explicitly";
        ExportText = BuilderChainCompiler.GenerateCSharp(_graph, chain);
        IsExportOpen = true;
    }

    [RelayCommand]
    private void CloseExport() => IsExportOpen = false;

    // ── run from canvas ──────────────────────────────────────────────────────

    /// <summary>Test seam: replaces the ephemeral-container session creation.</summary>
    internal Func<ComposedVoice, TalkSession>? SessionFactoryOverride;

    private bool CanRun() => IsChainValid && !IsRunning && !RunBlocked;
    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (!_graph.TryOrder(out var chain, out var orderErrors))
        {
            ErrorText = string.Join(" ", orderErrors);
            return;
        }

        ErrorText = null;
        try
        {
            // An EPHEMERAL container: the graph's pairs layered over the live config, diagnostics
            // forced on (the canvas-as-instrument needs the hub). The app's container is untouched.
            var pairs = BuilderChainCompiler.Pairs(_graph, chain, includeSecrets: true);
            pairs["Voxa:Diagnostics:Enabled"] = "true";
            var config = new ConfigurationBuilder()
                .AddConfiguration(_services.Configuration)
                .AddInMemoryCollection(pairs)
                .Build();
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Information));
            services.AddSingleton<IConfiguration>(config);
            services.AddVoxa(config);
            _runProvider = services.BuildServiceProvider();

            // Validation + compile BEFORE any download or audio start — fail fast, in words.
            var compiled = BuilderChainCompiler.Compile(_runProvider, config, chain);
            _runParts = compiled.Parts;

            var cache = new VoxaModelCache(VoxaModelCacheOptions.FromConfiguration(config.GetSection("Voxa")));
            var missing = ActiveConfigArtifacts.Missing(config, cache);
            if (missing.Count > 0)
            {
                var totalMb = missing.Sum(a => a.SizeBytes) / (1024 * 1024);
                StatusText = $"Downloading {missing.Count} model file(s), ~{totalMb} MB…";
                var progress = new Progress<VoxaPrefetchProgress>(p =>
                    StatusText = $"Downloading {p.ArtifactId}  ({p.CompletedCount + (p.Completed ? 0 : 1)}/{p.TotalCount})…");
                await cache.PrefetchAsync(missing, progress);
                RefreshCacheState();
            }

            StatusText = "Starting graph…";
            _session = SessionFactoryOverride is not null
                ? SessionFactoryOverride(compiled.ToComposedVoice())
                : TalkSession.Create(_runProvider, _services.AudioDevice, _ => compiled.ToComposedVoice());

            _subscription = new CancellationTokenSource();
            _ = Task.Run(() => SubscribeAsync(_session, _subscription.Token));
            _ = WatchSessionAsync(_session);

            await _session.StartAsync(
                PickEndpoint(_services.AudioDevice.CaptureEndpoints(), chain, BuilderNodeKind.Source),
                PickEndpoint(_services.AudioDevice.RenderEndpoints(), chain, BuilderNodeKind.Sink));
            IsRunning = true;
            StatusText = "Live — the canvas is the instrument now. Say something.";
        }
        catch (Exception ex)
        {
            ErrorText = ex switch
            {
                OptionsValidationException ove => string.Join(" ", ove.Failures),
                _ => ex.Message,
            };
            StatusText = "Failed to start.";
            await TearDownAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        await TearDownAsync();
        StatusText = "Stopped.";
    }

    private async Task TearDownAsync()
    {
        _subscription?.Cancel();
        _subscription = null;
        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }
        _runProvider?.Dispose();
        _runProvider = null;
        _runParts = [];
        IsRunning = false;
        LastTurn = null;
        _stages = new Dictionary<string, double>();
        foreach (var node in Nodes)
        {
            node.IsActive = false;
            node.LatencyText = null;
            node.QueueText = null;
        }
        foreach (var edge in Edges) edge.IsFlowing = false;
    }

    private AudioEndpoint PickEndpoint(
        IReadOnlyList<AudioEndpoint> endpoints, IReadOnlyList<BuilderNode> chain, BuilderNodeKind kind)
    {
        if (endpoints.Count == 0)
            throw new InvalidOperationException("No audio device available. Connect one and retry.");
        // Devices are stored by display name (readable in the saved JSON); fall back to default.
        var wanted = chain.FirstOrDefault(n => n.Kind == kind)?.Options.GetValueOrDefault("Device");
        return endpoints.FirstOrDefault(e => e.DisplayName == wanted) ?? endpoints[0];
    }

    private async Task WatchSessionAsync(TalkSession session)
    {
        try { await session.WaitAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorText = ex.Message;
            StatusText = "Pipeline failed.";
        }
    }

    private async Task SubscribeAsync(TalkSession session, CancellationToken ct)
    {
        try
        {
            await foreach (var e in session.Hub.SubscribeAsync(ct))
                _pending.Enqueue(e);
        }
        catch (OperationCanceledException) { /* session stop */ }
    }

    /// <summary>Test seam: inject hub events exactly as the subscription loop would.</summary>
    internal void EnqueueForTest(DiagnosticEvent e) => _pending.Enqueue(e);

    // ── live mode (the canvas as instrument — every signal here is a real hub event) ──

    private static readonly Dictionary<string, BuilderNodeKind> StageNode = new()
    {
        ["vad_close"] = BuilderNodeKind.Vad,
        ["stt_final"] = BuilderNodeKind.Stt,
        ["agent_first_token"] = BuilderNodeKind.Agent,
        ["tts_first_byte"] = BuilderNodeKind.Tts,
        ["audio_out"] = BuilderNodeKind.Sink,
    };

    private static readonly string[] StageOrder =
        ["vad_close", "stt_final", "agent_first_token", "tts_first_byte", "audio_out"];

    /// <summary>Apply buffered hub events to the canvas. Called by the view's ≤30 fps timer.</summary>
    public void DrainPending()
    {
        var now = Environment.TickCount64;
        while (_pending.TryDequeue(out var e))
        {
            switch (e)
            {
                case VadWindowEvent vad:
                    foreach (var edge in Edges)
                        if (edge.PortType == BuilderPortType.Audio)
                            edge.IsFlowing = vad.GateOpen;
                    break;

                case TranscriptEvent { IsFinal: true }:
                    Pulse(BuilderPortType.Transcription, now);
                    break;

                case AgentDeltaEvent:
                    Pulse(BuilderPortType.AgentText, now);
                    break;

                case TtsChunkEvent:
                    Pulse(BuilderPortType.SynthAudio, now);
                    break;

                case StageLatencyEvent stage:
                    ApplyStage(stage, now);
                    break;

                case PipelineErrorEvent err:
                    ErrorText = err.Message;
                    break;
            }
        }

        // Glow decay + live queue depths (FrameProcessor.QueuedFrameCount, polled per frame).
        foreach (var node in Nodes)
            if (node.IsActive && now - node.ActiveSinceTick > NodeGlow.TotalMilliseconds)
                node.IsActive = false;
        if (_session is { } session && _runParts.Count > 0)
        {
            // Processors is [source, parts…, sink]; part i is processor i+1.
            for (var i = 0; i < _runParts.Count && i + 1 < session.Processors.Count; i++)
            {
                if (_runParts[i].NodeId is not { } nodeId) continue;
                var node = Nodes.FirstOrDefault(n => n.Id == nodeId);
                if (node is null) continue;
                var depth = session.Processors[i + 1].QueuedFrameCount;
                node.QueueText = depth > 0 ? $"q{depth}" : null;
            }
        }
    }

    private void Pulse(BuilderPortType type, long now)
    {
        foreach (var edge in Edges)
            if (edge.PortType == type)
                edge.LastPulseTick = now;
    }

    private void ApplyStage(StageLatencyEvent stage, long now)
    {
        if (StageNode.TryGetValue(stage.Stage, out var kind))
        {
            // Stage events land on the FIRST node of that kind — chains hold at most one of each
            // measurable stage (the type system forbids a second STT/Agent/TTS).
            var node = Nodes.FirstOrDefault(n => n.Kind == kind);
            if (node is not null)
            {
                node.LatencyText = stage.Ms >= 1 ? $"{stage.Ms:F0} ms" : $"{stage.Ms:F2} ms";
                node.IsActive = true;
                node.ActiveSinceTick = now;
            }
        }

        _stages[stage.Stage] = stage.Ms;
        if (stage.Stage != "audio_out") return;

        // audio_out closes a turn — publish the ticker's waterfall (TalkViewModel's rule).
        double cursor = 0;
        var segments = new List<WaterfallSegment>();
        foreach (var key in StageOrder)
        {
            if (!_stages.TryGetValue(key, out var ms)) continue;
            segments.Add(new WaterfallSegment(key, ms, cursor));
            cursor += ms;
        }
        if (segments.Count > 0)
            LastTurn = new TurnWaterfall(++_turnNumber, segments);
        _stages = new Dictionary<string, double>();
    }
}
