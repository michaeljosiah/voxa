namespace Voxa.Speech.Piper;

/// <summary>One curated piper voice: the ONNX model plus its sibling JSON config.</summary>
public sealed record PiperVoice(string Name, int SampleRate, VoxaModelArtifact Onnx, VoxaModelArtifact Json);

/// <summary>
/// Pinned piper voice catalog (VLS-001 §6.WS2.3). URLs and SHA-256 hashes are compiled-in
/// constants sourced from <c>rhasspy/piper-voices</c> on Hugging Face. Quality → rate:
/// x_low/low = 16 kHz, medium/high = 22.05 kHz. Both files cache into the same directory because
/// piper requires <c>model.onnx.json</c> next to <c>model.onnx</c>.
/// </summary>
public static class PiperVoiceCatalog
{
    private const string BaseUrl = "https://huggingface.co/rhasspy/piper-voices/resolve/main/";

    private static PiperVoice Entry(
        string name, string hfDir, int sampleRate,
        string onnxSha, long onnxSize, string jsonSha, long jsonSize)
        => new(
            name, sampleRate,
            Onnx: new VoxaModelArtifact(
                $"piper/voices/{name}.onnx", new Uri($"{BaseUrl}{hfDir}/{name}.onnx"), onnxSha, onnxSize),
            Json: new VoxaModelArtifact(
                $"piper/voices/{name}.onnx.json", new Uri($"{BaseUrl}{hfDir}/{name}.onnx.json"), jsonSha, jsonSize));

    private static readonly Dictionary<string, PiperVoice> Voices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en_US-lessac-medium"] = Entry("en_US-lessac-medium", "en/en_US/lessac/medium", 22050,
            "5efe09e69902187827af646e1a6e9d269dee769f9877d17b16b1b46eeaaf019f", 63_201_294,
            "efe19c417bed055f2d69908248c6ba650fa135bc868b0e6abb3da181dab690a0", 4_885),
        ["en_US-lessac-high"] = Entry("en_US-lessac-high", "en/en_US/lessac/high", 22050,
            "4cabf7c3a638017137f34a1516522032d4fe3f38228a843cc9b764ddcbcd9e09", 113_895_201,
            "db42b97d9859f257bc1561b8ed980e7fb2398402050a74ddd6cbec931a92412f", 4_883),
        ["en_US-amy-low"] = Entry("en_US-amy-low", "en/en_US/amy/low", 16000,
            "a5a91abb7de0f104358a25aded480ddacf1ff0762886325886ec406a2e86aab3", 63_104_526,
            "2250a9a605b8dc35a116717fadc5056695dd809e34a15d02f72a0f52d53d3ebb", 4_164),
        ["en_GB-alan-medium"] = Entry("en_GB-alan-medium", "en/en_GB/alan/medium", 22050,
            "0a309668932205e762801f1efc2736cd4b0120329622adf62be09e56339d3330", 63_201_294,
            "c0f0d124e5895c00e7c03b35dcc8287f319a6998a365b182deb5c8e752ee8c1e", 4_888),
        ["de_DE-thorsten-medium"] = Entry("de_DE-thorsten-medium", "de/de_DE/thorsten/medium", 22050,
            "7e64762d8e5118bb578f2eea6207e1a35a8e0c30595010b666f983fc87bb7819", 63_201_294,
            "974adee790533adb273a1ac88f49027d2a1b8f0f2cf4905954a4791e79264e85", 4_819),
        ["fr_FR-siwis-medium"] = Entry("fr_FR-siwis-medium", "fr/fr_FR/siwis/medium", 22050,
            "641d1ab097da2b81128c076810edb052b385decc8be3381814802a64a73baf99", 63_201_294,
            "39479916c2db192b5ac9764daddd0c744d83e023ad890c6976c0633ae4df8959", 4_875),
        ["es_ES-davefx-medium"] = Entry("es_ES-davefx-medium", "es/es_ES/davefx/medium", 22050,
            "6658b03b1a6c316ee4c265a9896abc1393353c2d9e1bca7d66c2c442e222a917", 63_201_294,
            "0e0dda87c732f6f38771ff274a6380d9252f327dca77aa2963d5fbdf9ec54842", 4_817),
    };

    public static bool TryGet(string voice, out PiperVoice entry) => Voices.TryGetValue(voice, out entry!);

    /// <summary>Known voice names, for "valid values" error messages.</summary>
    public static IReadOnlyCollection<string> KnownVoices { get; } =
        Voices.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Deterministic rate inference from the quality suffix — usable at composition time without
    /// loading anything (the VDX-001 effective-rate rule). Works for catalog and non-catalog names.
    /// </summary>
    public static int InferSampleRateFromName(string voiceName)
        => voiceName.EndsWith("-low", StringComparison.OrdinalIgnoreCase)
        || voiceName.EndsWith("-x_low", StringComparison.OrdinalIgnoreCase)
            ? 16000
            : 22050;
}
