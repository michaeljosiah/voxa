using Voxa.Speech.Azure;

namespace Voxa.Speech.Azure.Tests;

public class AzureSpeechSmokeTests
{
    private static AzureSpeechOptions MakeOptions() => new()
    {
        SubscriptionKey = "fake",
        Region = "eastus",
    };

    [Fact]
    public void Stt_Engine_Constructs_With_Options()
    {
        var engine = new AzureSpeechToTextEngine(MakeOptions());
        Assert.NotNull(engine);
    }

    [Fact]
    public void Tts_Engine_Constructs_With_Options()
    {
        var engine = new AzureTextToSpeechEngine(MakeOptions());
        Assert.NotNull(engine);
    }

    [Fact]
    public void Helper_Returns_Configured_Processors()
    {
        var stt = AzureSpeech.StreamingTranscription(MakeOptions());
        var tts = AzureSpeech.Synthesis(MakeOptions());
        Assert.Equal("SpeechToText", stt.Name);
        Assert.Equal("TextToSpeech", tts.Name);
    }
}
