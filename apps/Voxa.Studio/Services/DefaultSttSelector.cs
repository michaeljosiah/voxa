namespace Voxa.Studio.Services;

/// <summary>
/// Picks Studio's effective STT when no config layer chose one (VLS-009 §6). The user's explicit choice always
/// wins; otherwise Voxtral is auto-selected only when the machine can actually run it <b>and</b> a local vLLM
/// server is configured; otherwise the keyless whisper.cpp default — so a fresh, GPU-less install behaves exactly
/// as before, and Voxtral becomes the default the moment a capable machine is pointed at a server. Pure and
/// Avalonia-free so it is headless-testable.
/// </summary>
public static class DefaultSttSelector
{
    public const string Voxtral = "Voxtral";
    public const string WhisperCpp = "WhisperCpp";

    /// <param name="explicitStt">The STT name a config layer already chose, or null/blank if none did.</param>
    /// <param name="voxtralConfigured">True when a Voxtral hosting mode (ServerUrl or launch target) is set.</param>
    /// <param name="gpuCapable">True when probed GPU VRAM meets Voxtral's floor.</param>
    public static string Select(string? explicitStt, bool voxtralConfigured, bool gpuCapable)
        => !string.IsNullOrWhiteSpace(explicitStt) ? explicitStt
         : voxtralConfigured && gpuCapable         ? Voxtral
         :                                           WhisperCpp;
}
