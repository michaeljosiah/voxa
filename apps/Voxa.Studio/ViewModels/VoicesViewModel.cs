using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Voxa.Speech;
using Voxa.Speech.Kokoro;
using Voxa.Speech.Piper;
using Voxa.Studio.Services;

namespace Voxa.Studio.ViewModels;

/// <summary>One auditionable voice from the pinned local catalogs.</summary>
public sealed partial class VoiceRow : ObservableObject
{
    public required string Engine { get; init; }
    public required string Name { get; init; }
    public required int SampleRate { get; init; }
    public required long SizeBytes { get; init; }

    [ObservableProperty] private bool _isCached;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double? _ttfbMs;
    [ObservableProperty] private double? _rtf;
    [ObservableProperty] private string? _pin; // "A", "B", or null

    /// <summary>Last synthesized audio for instant replay / A-B / export. (Text it was made from.)</summary>
    public byte[]? LastPcm { get; set; }
    public string? LastText { get; set; }

    public string SizeText => $"{SizeBytes / (1024.0 * 1024):F0} MB";
    public string RateText => $"{SampleRate / 1000.0:0.#} kHz";
    public string TtfbText => TtfbMs is { } t ? $"{t:F0} ms" : "—";
    public string RtfText => Rtf is { } r ? $"{r:F2}×" : "—";
}

/// <summary>
/// The Voice Lab (VST-001 WS3): browse the Piper + Kokoro catalogs, audition any voice against
/// arbitrary text through the REAL engines (same warm pool / ONNX session as production), see
/// TTFB and RTF measured on this machine, A/B two pinned voices, export WAV.
/// </summary>
public sealed partial class VoicesViewModel : ObservableObject
{
    private readonly StudioServices _services;

    public VoicesViewModel(StudioServices services)
    {
        _services = services;

        foreach (var name in PiperVoiceCatalog.KnownVoices)
        {
            if (!PiperVoiceCatalog.TryGet(name, out var voice)) continue;
            Voices.Add(new VoiceRow
            {
                Engine = "Piper",
                Name = name,
                SampleRate = voice.SampleRate,
                SizeBytes = voice.Onnx.SizeBytes + voice.Json.SizeBytes,
            });
        }

        KokoroCatalog.TryGetModel("int8", out var kokoroModel);
        foreach (var name in KokoroCatalog.KnownVoices)
        {
            if (!KokoroCatalog.TryGetVoice(name, out var style)) continue;
            Voices.Add(new VoiceRow
            {
                Engine = "Kokoro",
                Name = name,
                SampleRate = KokoroCatalog.OutputSampleRate,
                SizeBytes = kokoroModel.SizeBytes + style.SizeBytes,
            });
        }

        RefreshCacheState();
    }

    public ObservableCollection<VoiceRow> Voices { get; } = new();

    [ObservableProperty] private string _auditionText =
        "The quick brown fox jumps over the lazy dog — and Voxa says it out loud.";
    [ObservableProperty] private string _statusText = "Pick a voice and press play. First use downloads that voice.";
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private VoiceRow? _pinnedA;
    [ObservableProperty] private VoiceRow? _pinnedB;

    /// <summary>Set true while a Talk session owns the output device — playback buttons disable.</summary>
    [ObservableProperty] private bool _playbackBlocked;

    public void RefreshCacheState()
    {
        foreach (var row in Voices)
            row.IsCached = IsRowCached(row);
    }

    private bool IsRowCached(VoiceRow row) => row.Engine switch
    {
        "Piper" => PiperVoiceCatalog.TryGet(row.Name, out var v)
                   && _services.ModelCache.IsCached(v.Onnx) && _services.ModelCache.IsCached(v.Json),
        "Kokoro" => KokoroCatalog.TryGetModel("int8", out var m) && _services.ModelCache.IsCached(m)
                    && KokoroCatalog.TryGetVoice(row.Name, out var s) && _services.ModelCache.IsCached(s),
        _ => false,
    };

    // ── audition ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task PlayAsync(VoiceRow row)
    {
        if (row.IsBusy || PlaybackBlocked) return;
        ErrorText = null;
        row.IsBusy = true;
        try
        {
            // Re-synthesize only when the text changed; replay is instant.
            if (row.LastPcm is null || row.LastText != AuditionText)
            {
                StatusText = row.IsCached
                    ? $"Synthesizing with {row.Name}…"
                    : $"Downloading {row.Name} ({row.SizeText}) then synthesizing…";
                await SynthesizeAsync(row);
                row.IsCached = IsRowCached(row);
            }

            await PlayPcmAsync(row);
            StatusText = $"{row.Name}: TTFB {row.TtfbText}, RTF {row.RtfText} on this machine.";
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            StatusText = $"Failed to audition {row.Name}.";
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    private async Task SynthesizeAsync(VoiceRow row)
    {
        await using var engine = CreateEngine(row);
        var clock = Stopwatch.StartNew();
        await engine.StartAsync(CancellationToken.None);

        using var pcm = new MemoryStream();
        double? ttfb = null;
        clock.Restart();
        await foreach (var chunk in engine.SynthesizeAsync(AuditionText, CancellationToken.None))
        {
            ttfb ??= clock.Elapsed.TotalMilliseconds;
            pcm.Write(chunk.Span); // engines may pool buffers — copy before the next chunk
        }
        var wallMs = clock.Elapsed.TotalMilliseconds;

        row.LastPcm = pcm.ToArray();
        row.LastText = AuditionText;
        row.TtfbMs = ttfb;
        var audioSeconds = row.LastPcm.Length / 2.0 / row.SampleRate;
        row.Rtf = audioSeconds > 0 ? wallMs / 1000.0 / audioSeconds : null;
    }

    private ITextToSpeechEngine CreateEngine(VoiceRow row) => row.Engine switch
    {
        "Piper" => new PiperTtsEngine(
            new PiperOptions { Voice = row.Name, OutputSampleRate = row.SampleRate, MaxProcesses = 1 },
            _services.ModelCache),
        "Kokoro" => new KokoroTtsEngine(
            new KokoroOptions { Voice = row.Name, Precision = "int8" },
            _services.ModelCache),
        _ => throw new InvalidOperationException($"Unknown engine '{row.Engine}'."),
    };

    private async Task PlayPcmAsync(VoiceRow row)
    {
        if (row.LastPcm is null or { Length: 0 }) return;
        var device = _services.AudioDevice;
        await device.StartRenderAsync(
            device.RenderEndpoints().FirstOrDefault() ?? new Audio.AudioEndpoint("default", "Default", true),
            row.SampleRate, CancellationToken.None);
        await device.RenderAsync(row.LastPcm, CancellationToken.None);
    }

    // ── A/B compare ──────────────────────────────────────────────────────────

    [RelayCommand]
    private void Pin(VoiceRow row)
    {
        if (row.Pin == "A") { row.Pin = null; PinnedA = null; return; }
        if (row.Pin == "B") { row.Pin = null; PinnedB = null; return; }
        if (PinnedA is null) { ClearPin(PinnedA); PinnedA = row; row.Pin = "A"; }
        else if (PinnedB is null) { PinnedB = row; row.Pin = "B"; }
        else { PinnedB.Pin = null; PinnedB = row; row.Pin = "B"; }
    }

    private static void ClearPin(VoiceRow? row)
    {
        if (row is not null) row.Pin = null;
    }

    [RelayCommand] private Task PlayAAsync() => PinnedA is { } a ? PlayAsync(a) : Task.CompletedTask;
    [RelayCommand] private Task PlayBAsync() => PinnedB is { } b ? PlayAsync(b) : Task.CompletedTask;

    // ── export ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ExportWavAsync(VoiceRow row)
    {
        try
        {
            if (row.LastPcm is null || row.LastText != AuditionText)
            {
                row.IsBusy = true;
                try { await SynthesizeAsync(row); }
                finally { row.IsBusy = false; }
            }

            var dir = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (string.IsNullOrEmpty(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var path = Path.Combine(dir, $"voxa-{row.Name}-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
            await File.WriteAllBytesAsync(path, ToWav(row.LastPcm!, row.SampleRate));
            StatusText = $"Exported {path}";
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    /// <summary>Minimal RIFF/WAVE writer: PCM16 mono.</summary>
    internal static byte[] ToWav(byte[] pcm, int sampleRate)
    {
        using var ms = new MemoryStream(44 + pcm.Length);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
        return ms.ToArray();
    }
}
