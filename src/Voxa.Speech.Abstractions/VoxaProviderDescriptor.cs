using Microsoft.Extensions.Configuration;
using Voxa.Processors;

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
    /// The sample rate the TTS processor will actually be configured with for the given config —
    /// the host's <c>&lt;ConfigSection&gt;:OutputSampleRate</c> override when present, otherwise
    /// <see cref="OutputSampleRate"/>.
    /// </summary>
    public int GetEffectiveOutputSampleRate(IConfigurationSection root)
        => ResolveOutputSampleRate?.Invoke(root)
           ?? root.GetSection(ConfigSection).GetValue("OutputSampleRate", OutputSampleRate);
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
    TimeSpan PrerollDuration);

/// <summary>
/// Self-description of a VAD provider. VAD knobs are profile-mediated rather than per-provider
/// credentials, so the factory receives already-resolved <see cref="VoxaVadSettings"/> instead
/// of an <see cref="IConfigurationSection"/>.
/// </summary>
public sealed record VoxaVadDescriptor(
    string Name,
    Func<IServiceProvider, VoxaVadSettings, FrameProcessor> CreateProcessor);
