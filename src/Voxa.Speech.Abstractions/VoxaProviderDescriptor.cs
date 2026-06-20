using Microsoft.Extensions.Configuration;
using Voxa.Processors;
using Voxa.Speech.Voices;

namespace Voxa.Speech;

/// <summary>
/// Abstraction so descriptor factories can obtain an <see cref="HttpClient"/> without
/// referencing Voxa.AspNetCore. Implemented by <c>VoxaHttpResolver</c> and registered
/// by <c>AddVoxa()</c>. Returns null in manual-composition hosts that never called AddVoxa,
/// in which case engines fall back to <see cref="VoxaHttp.Shared"/>.
/// </summary>
public interface IVoxaHttpClientProvider
{
    HttpClient? Resolve();
}

/// <summary>
/// Self-description of an STT provider for config-driven composition ("Voxa:Stt": "OpenAI").
/// Lives in abstractions so provider packages can describe themselves without referencing
/// DI or ASP.NET. The factory runs once per connection — engines are per-session stateful.
/// </summary>
public sealed record VoxaSttDescriptor(
    string Name,
    string ConfigSection,
    int PreferredInputSampleRate,
    Func<IConfigurationSection, IReadOnlyList<string>> Validate,
    Func<IServiceProvider, IConfigurationSection, SpeechToTextProcessor> CreateProcessor)
{
    /// <summary>
    /// Override hook for providers whose config does not follow the
    /// <c>&lt;ConfigSection&gt;:InputSampleRate</c> key convention. Receives the root
    /// "Voxa" section; returns the sample rate the processor will actually run at.
    /// </summary>
    public Func<IConfigurationSection, int>? ResolveInputSampleRate { get; init; }

    /// <summary>
    /// Optional startup warm-up (VLS-001 WS5.2), invoked by <c>VoxaDefaultsGuard</c> after
    /// validation when this provider is active and <c>Voxa:Models:EagerWarmup</c> is true
    /// (default). Local providers resolve their models here (first-run download with progress
    /// logging) and pre-load shared state, so the first WebSocket caller never pays a download
    /// or a model load. Cloud providers leave this null.
    /// </summary>
    public Func<IServiceProvider, IConfigurationSection, CancellationToken, Task>? WarmUpAsync { get; init; }

    /// <summary>
    /// The sample rate the STT processor will actually be configured with for the given config —
    /// the host's <c>&lt;ConfigSection&gt;:InputSampleRate</c> override when present, otherwise
    /// <see cref="PreferredInputSampleRate"/>. The session envelope and VAD must use this value,
    /// not the descriptor default, or they diverge from the processor when a host overrides the rate.
    /// </summary>
    public int GetEffectiveInputSampleRate(IConfigurationSection root)
        => ResolveInputSampleRate?.Invoke(root)
           ?? root.GetSection(ConfigSection).GetValue("InputSampleRate", PreferredInputSampleRate);
}

/// <summary>TTS twin of <see cref="VoxaSttDescriptor"/>.</summary>
public sealed record VoxaTtsDescriptor(
    string Name,
    string ConfigSection,
    int OutputSampleRate,
    Func<IConfigurationSection, IReadOnlyList<string>> Validate,
    Func<IServiceProvider, IConfigurationSection, TextToSpeechProcessor> CreateProcessor)
{
    /// <summary>
    /// Override hook for providers whose config does not follow the
    /// <c>&lt;ConfigSection&gt;:OutputSampleRate</c> key convention.
    /// </summary>
    public Func<IConfigurationSection, int>? ResolveOutputSampleRate { get; init; }

    /// <summary>
    /// Optional startup warm-up — see <see cref="VoxaSttDescriptor.WarmUpAsync"/>.
    /// </summary>
    public Func<IServiceProvider, IConfigurationSection, CancellationToken, Task>? WarmUpAsync { get; init; }

    /// <summary>
    /// The sample rate the TTS processor will actually be configured with for the given config —
    /// the host's <c>&lt;ConfigSection&gt;:OutputSampleRate</c> override when present, otherwise
    /// <see cref="OutputSampleRate"/>.
    /// </summary>
    public int GetEffectiveOutputSampleRate(IConfigurationSection root)
        => ResolveOutputSampleRate?.Invoke(root)
           ?? root.GetSection(ConfigSection).GetValue("OutputSampleRate", OutputSampleRate);

    /// <summary>
    /// Optional capability (VVL-001 WS0): list the voices this provider can use right now. Null ⇒
    /// the provider has no live catalog (its voices are a compiled-in list, e.g. Piper/Kokoro).
    /// Receives the same captured "Voxa" root section the factories do — never service-locate config.
    /// </summary>
    public Func<IServiceProvider, IConfigurationSection, IVoiceCatalogProvider>? ResolveCatalog { get; init; }

    /// <summary>
    /// Optional capability (VVL-001 WS0): create/delete voices from samples. Null ⇒ this provider
    /// cannot clone. Resolved with the captured "Voxa" root section; the host owns the consent gate.
    /// </summary>
    public Func<IServiceProvider, IConfigurationSection, IVoiceCloneProvider>? ResolveCloner { get; init; }
}

/// <summary>
/// Settings derived from the active profile/config passed to a VAD descriptor factory.
/// Defined here (in abstractions) so VAD packages can describe themselves without referencing
/// the AspNetCore config model.
/// </summary>
public sealed record VoxaVadSettings(
    int SampleRate,
    float ConfidenceThreshold,
    double MinRms,
    TimeSpan StartDuration,
    TimeSpan StopDuration,
    TimeSpan PrerollDuration)
{
    /// <summary>
    /// Optional per-window observer (VST-001 WS0): invoked synchronously on the VAD's
    /// processing thread after each inference window with
    /// <c>(probability, rms, voiced, gateOpen)</c>. Wired by the composer to the pipeline
    /// diagnostics hub when <c>Voxa:Diagnostics:Enabled</c> is true; null otherwise.
    /// The callback runs ~31×/s in the audio hot path — it must not block or allocate beyond
    /// what it publishes.
    /// </summary>
    public Action<float, double, bool, bool>? ProbabilityObserver { get; init; }

    /// <summary>
    /// Optional smart-turn confirmation (P0): when set, the VAD's silence timeout asks this callback
    /// whether the turn is really over (<c>true</c>) or just a mid-sentence pause (<c>false</c>), so
    /// <see cref="StopDuration"/> can be aggressive (~200 ms) without clipping a speaker who pauses to
    /// think. The buffer is the current turn's speech PCM (16-bit mono at <see cref="SampleRate"/>, up to ~8 s).
    /// Wired by the composer to a registered <see cref="ISmartTurnClassifier"/>; null otherwise
    /// (classic silence-only behavior — byte-for-byte unchanged).
    /// </summary>
    public Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<bool>>? ConfirmTurnEnd { get; init; }

    /// <summary>
    /// Speculative ("eager") STT delay (VRT-002 WS1). Threaded to <c>SileroVadOptions.EagerSttDelay</c>: when
    /// set (and &lt; <see cref="StopDuration"/>) the VAD emits a marked speculative end-of-utterance at this
    /// silence delay so STT starts before the full hangover elapses. Null ⇒ no eager dispatch (unchanged).
    /// </summary>
    public TimeSpan? EagerSttDelay { get; init; }

    /// <summary>
    /// Force-split cap on a single open-gate utterance (VRT-002 WS2). Threaded to
    /// <c>SileroVadOptions.MaxUtteranceDuration</c>: a non-pausing speaker gets a forced intermediate
    /// flush + fresh utterance at this cap. Null ⇒ no cap (unchanged).
    /// </summary>
    public TimeSpan? MaxUtteranceDuration { get; init; }
}

/// <summary>
/// Self-description of a VAD provider. VAD knobs are profile-mediated rather than per-provider
/// credentials, so the factory receives already-resolved <see cref="VoxaVadSettings"/> instead
/// of an <see cref="IConfigurationSection"/>.
/// </summary>
public sealed record VoxaVadDescriptor(
    string Name,
    Func<IServiceProvider, VoxaVadSettings, FrameProcessor> CreateProcessor);

/// <summary>
/// Settings the composer resolves and hands the AEC factory (VRT-003; peer of <see cref="VoxaVadSettings"/>).
/// <paramref name="SampleRate"/> is the near-end (mic / STT-input) rate the canceller runs at;
/// <paramref name="FarEndSampleRate"/> is the far-end (bot / TTS-output) rate the reference tap feeds. They
/// differ in mixed-rate pipelines (e.g. 16 kHz mic, 24 kHz TTS), so a real canceller needs both to resample and
/// time-align the far-end against the near-end. PCM is 16-bit mono throughout the Voxa pipeline.
/// </summary>
public sealed record VoxaAecSettings(int SampleRate, int FarEndSampleRate);

/// <summary>
/// Self-description of an acoustic-echo-canceller provider (VRT-003), peer of <see cref="VoxaVadDescriptor"/>.
/// AEC knobs are rate/profile-mediated, so the factory receives resolved <see cref="VoxaAecSettings"/> rather
/// than an <see cref="IConfigurationSection"/>. The factory builds the before-VAD echo-canceller processor.
/// </summary>
public sealed record VoxaAecDescriptor(
    string Name,
    Func<IServiceProvider, VoxaAecSettings, FrameProcessor> CreateProcessor);

/// <summary>
/// Self-description of a speech-enhancement (denoise) provider (VLS-004), modelled on the STT/TTS descriptors:
/// a config-section <see cref="Validate"/> and a <c>CreateProcessor(sp, root)</c> factory that builds the
/// before-VAD <c>AudioEnhancerProcessor</c>. The factory receives the captured <c>"Voxa"</c> root section (never
/// service-locate <see cref="IConfiguration"/>). <see cref="WarmUpAsync"/> is the optional startup hook a real
/// ONNX engine uses to resolve + load its model so the first caller pays no download (cf. the local STT/TTS
/// descriptors).
/// </summary>
public sealed record VoxaEnhancerDescriptor(
    string Name,
    Func<IConfigurationSection, IReadOnlyList<string>> Validate,
    Func<IServiceProvider, IConfigurationSection, FrameProcessor> CreateProcessor)
{
    /// <summary>Optional startup warm-up — see <see cref="VoxaSttDescriptor.WarmUpAsync"/>.</summary>
    public Func<IServiceProvider, IConfigurationSection, CancellationToken, Task>? WarmUpAsync { get; init; }
}
