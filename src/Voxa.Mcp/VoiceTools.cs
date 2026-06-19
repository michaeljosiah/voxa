using System.ComponentModel;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Server;

namespace Voxa.Mcp;

/// <summary>
/// The MCP tool surface (VDX-002): give an agent a voice (<c>voxa_speak</c>) and ears
/// (<c>voxa_transcribe</c>) backed by Voxa's keyless local tier, plus a voice listing. The attributed
/// methods are thin wrappers; the actual work lives in <see cref="VoiceToolCore"/> so it stays testable
/// without the MCP transport. Tool names use underscores for broad client compatibility.
/// </summary>
[McpServerToolType]
public static class VoiceTools
{
    [McpServerTool(Name = "voxa_speak")]
    [Description("Synthesize speech from text with a local Voxa TTS engine (Piper or Kokoro) and return " +
                 "the path to the generated WAV file. Keyless and fully offline.")]
    public static Task<string> Speak(
        IConfiguration configuration, // injected from the MCP host's DI — not a tool argument
        [Description("The text to speak.")] string text,
        [Description("TTS engine: 'piper' (fast, default) or 'kokoro' (higher quality).")] string tts = "piper",
        [Description("Voice name; defaults to the engine's default voice.")] string? voice = null,
        [Description("Optional output WAV path; a temp file is used when omitted.")] string? outputPath = null,
        CancellationToken cancellationToken = default)
        => VoiceToolCore.SpeakAsync(configuration, text, tts, voice, outputPath, cancellationToken);

    [McpServerTool(Name = "voxa_transcribe")]
    [Description("Transcribe a 16 kHz mono 16-bit PCM WAV file to text with local whisper.cpp. " +
                 "Keyless and fully offline.")]
    public static Task<string> Transcribe(
        IConfiguration configuration, // injected from the MCP host's DI — not a tool argument
        [Description("Path to a 16 kHz mono 16-bit PCM WAV file.")] string wavPath,
        [Description("Whisper model name (default base.en; e.g. tiny.en, small.en).")] string? model = null,
        [Description("Language code (default en; 'auto' to detect).")] string? language = null,
        CancellationToken cancellationToken = default)
        => VoiceToolCore.TranscribeAsync(configuration, wavPath, model, language, cancellationToken);

    [McpServerTool(Name = "voxa_list_voices")]
    [Description("List the local TTS voices available to voxa_speak (Piper and Kokoro), one per line.")]
    public static string ListVoices() => string.Join("\n", VoiceToolCore.ListVoices());
}
