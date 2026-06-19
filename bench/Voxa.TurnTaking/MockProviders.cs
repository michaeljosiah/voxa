using Voxa.AspNetCore;
using Voxa.Speech;

namespace Voxa.TurnTaking;

/// <summary>
/// The "Mock" STT/TTS providers — deterministic and offline. Registered via the 3-arg <c>AddVoxa</c> so the
/// composer resolves them by name (<c>Voxa:Stt=Mock</c> / <c>Voxa:Tts=Mock</c>); the agent uses the keyless
/// <c>Echo</c> provider as the deterministic mock LLM. This is what keeps the default smoke lane offline.
/// </summary>
internal static class MockProviders
{
    public const string Name = "Mock";

    public static VoxaSttDescriptor Stt { get; } = new(
        Name: Name,
        ConfigSection: Name,
        PreferredInputSampleRate: 16000,
        Validate: _ => Array.Empty<string>(),
        CreateProcessor: (_, _) => new SpeechToTextProcessor(() => new MockSpeechToTextEngine()));

    public static VoxaTtsDescriptor Tts { get; } = new(
        Name: Name,
        ConfigSection: Name,
        OutputSampleRate: MockTextToSpeechEngine.SampleRate,
        Validate: _ => Array.Empty<string>(),
        CreateProcessor: (_, _) => new TextToSpeechProcessor(
            () => new MockTextToSpeechEngine(), MockTextToSpeechEngine.SampleRate));

    public static void Register(VoxaBuilder voxa)
    {
        voxa.AddProvider(Stt);
        voxa.AddProvider(Tts);
    }
}
