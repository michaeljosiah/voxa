using CommunityToolkit.Mvvm.ComponentModel;
using Voxa.Studio.Services;

namespace Voxa.Studio.ViewModels;

/// <summary>
/// The Playgrounds section (VST-002 §5 IA): the STT lab and the TTS lab behind one segmented
/// switch — v1's Voices grew into the TTS Playground and moved here next to the new STT lab.
/// </summary>
public sealed partial class PlaygroundsViewModel : ObservableObject
{
    public PlaygroundsViewModel(StudioServices services)
    {
        Stt = new SttPlaygroundViewModel(services);
        Tts = new TtsPlaygroundViewModel(services);
    }

    public SttPlaygroundViewModel Stt { get; }
    public TtsPlaygroundViewModel Tts { get; }

    /// <summary>0 = STT lab, 1 = TTS lab.</summary>
    [ObservableProperty] private int _selectedLab;

    public void RefreshCacheState()
    {
        Stt.RefreshCacheState();
        Tts.RefreshCacheState();
    }
}
