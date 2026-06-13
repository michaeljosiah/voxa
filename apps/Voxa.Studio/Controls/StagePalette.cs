using Avalonia.Media;

namespace Voxa.Studio.Controls;

/// <summary>
/// The five stage colors (VST-002 §3.3) for the workbench charts — identical hues to the Talk
/// waterfall and the Builder canvas, because a color means the same stage everywhere.
/// </summary>
internal static class StagePalette
{
    public static readonly Dictionary<string, IBrush> Brushes = new()
    {
        ["vad_close"] = new SolidColorBrush(Color.Parse("#76849B")),
        ["stt_final"] = new SolidColorBrush(Color.Parse("#4FC3F7")),
        ["agent_first_token"] = new SolidColorBrush(Color.Parse("#CE93D8")),
        ["tts_first_byte"] = new SolidColorBrush(Color.Parse("#FFB74D")),
        ["audio_out"] = new SolidColorBrush(Color.Parse("#66BB6A")),
    };

    public static readonly IBrush Fallback = new SolidColorBrush(Color.Parse("#4FC3F7"));
    public static readonly IBrush Label = new SolidColorBrush(Color.Parse("#76849B"));
    public static readonly Typeface Mono = new("Cascadia Code, Consolas, monospace");

    public static IBrush For(string stage) => Brushes.GetValueOrDefault(stage, Fallback);

    public static string LabelFor(string stage) => stage switch
    {
        "vad_close" => "VAD",
        "stt_final" => "STT",
        "agent_first_token" => "AGENT",
        "tts_first_byte" => "TTS",
        "audio_out" => "OUT",
        _ => stage.ToUpperInvariant(),
    };
}
