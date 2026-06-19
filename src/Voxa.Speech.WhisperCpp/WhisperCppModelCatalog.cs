namespace Voxa.Speech.WhisperCpp;

/// <summary>
/// Pinned whisper.cpp GGML model catalog (VLS-001 §5). URLs and SHA-256 hashes are compiled-in
/// constants — sourced from the Hugging Face LFS metadata of <c>ggerganov/whisper.cpp</c> — so an
/// upstream re-upload fails the hash check loudly. Bumping a model is a code change with a test,
/// never a floating reference.
/// </summary>
public static class WhisperCppModelCatalog
{
    private const string BaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    private static VoxaModelArtifact Entry(string file, string sha256, long sizeBytes)
        => new($"whisper/{file}", new Uri(BaseUrl + file), sha256, sizeBytes);

    private static readonly Dictionary<string, VoxaModelArtifact> Models = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tiny"]            = Entry("ggml-tiny.bin",            "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21", 77_691_713),
        ["tiny.en"]         = Entry("ggml-tiny.en.bin",         "921e4cf8686fdd993dcd081a5da5b6c365bfde1162e72b08d75ac75289920b1f", 77_704_715),
        ["base"]            = Entry("ggml-base.bin",            "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe", 147_951_465),
        ["base.en"]         = Entry("ggml-base.en.bin",         "a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002", 147_964_211),
        ["small"]           = Entry("ggml-small.bin",           "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b", 487_601_967),
        ["small.en"]        = Entry("ggml-small.en.bin",        "c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d", 487_614_201),
        ["tiny-q5_1"]       = Entry("ggml-tiny-q5_1.bin",       "818710568da3ca15689e31a743197b520007872ff9576237bda97bd1b469c3d7", 32_152_673),
        ["tiny.en-q5_1"]    = Entry("ggml-tiny.en-q5_1.bin",    "c77c5766f1cef09b6b7d47f21b546cbddd4157886b3b5d6d4f709e91e66c7c2b", 32_166_155),
        ["base-q5_1"]       = Entry("ggml-base-q5_1.bin",       "422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898", 59_707_625),
        ["base.en-q5_1"]    = Entry("ggml-base.en-q5_1.bin",    "4baf70dd0d7c4247ba2b81fafd9c01005ac77c2f9ef064e00dcf195d0e2fdd2f", 59_721_011),
        ["small-q5_1"]      = Entry("ggml-small-q5_1.bin",      "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb", 190_085_487),
        ["small.en-q5_1"]   = Entry("ggml-small.en-q5_1.bin",   "bfdff4894dcb76bbf647d56263ea2a96645423f1669176f4844a1bf8e478ad30", 190_098_681),

        // VLS-002 — medium & large-v3 families (multilingual + English; full + q5_0 quantized).
        // Far slower than real time on CPU; pair with Voxa:WhisperCpp:Device for GPU. SHA-256 and
        // sizes are the ggerganov/whisper.cpp Hugging Face LFS pointer oids.
        ["medium"]              = Entry("ggml-medium.bin",              "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208", 1_533_763_059),
        ["medium.en"]           = Entry("ggml-medium.en.bin",           "cc37e93478338ec7700281a7ac30a10128929eb8f427dda2e865faa8f6da4356", 1_533_774_781),
        ["medium-q5_0"]         = Entry("ggml-medium-q5_0.bin",         "19fea4b380c3a618ec4723c3eef2eb785ffba0d0538cf43f8f235e7b3b34220f", 539_212_467),
        ["medium.en-q5_0"]      = Entry("ggml-medium.en-q5_0.bin",      "76733e26ad8fe1c7a5bf7531a9d41917b2adc0f20f2e4f5531688a8c6cd88eb0", 539_225_533),
        ["large-v3"]            = Entry("ggml-large-v3.bin",            "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2", 3_095_033_483),
        ["large-v3-q5_0"]       = Entry("ggml-large-v3-q5_0.bin",       "d75795ecff3f83b5faa89d1900604ad8c780abd5739fae406de19f23ecd98ad1", 1_081_140_203),
        ["large-v3-turbo"]      = Entry("ggml-large-v3-turbo.bin",      "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69", 1_624_555_275),
        ["large-v3-turbo-q5_0"] = Entry("ggml-large-v3-turbo-q5_0.bin", "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2", 574_041_195),
    };

    public static bool TryGet(string model, out VoxaModelArtifact artifact)
        => Models.TryGetValue(model, out artifact!);

    /// <summary>Known model names, for "valid values" error messages.</summary>
    public static IReadOnlyCollection<string> KnownModels { get; } =
        Models.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
}
