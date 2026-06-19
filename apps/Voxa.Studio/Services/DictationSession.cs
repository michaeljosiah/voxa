using System.Text;
using Voxa.Speech;
using Voxa.Studio.Audio;

namespace Voxa.Studio.Services;

/// <summary>
/// Push-to-talk dictation core (VST-004): capture the microphone, then transcribe the buffered
/// utterance with a local STT engine. This is the headless, Avalonia-free heart of Studio's dictation
/// mode — the view, the floating pill, and the global hotkey wrap it. It depends only on
/// <see cref="IStudioAudioDevice"/> and a STT-engine factory, so it is unit-testable with fakes (no real
/// device, no model download), following the Voice-Lab pattern.
/// </summary>
public sealed class DictationSession : IAsyncDisposable
{
    /// <summary>The lifecycle the floating pill renders: idle → recording → transcribing → done/failed.</summary>
    public enum DictationState { Idle, Recording, Transcribing, Completed, Failed }

    /// <summary>STT input rate — whisper.cpp is 16 kHz mono.</summary>
    public const int SampleRate = 16000;

    private readonly IStudioAudioDevice _audio;
    private readonly Func<ISpeechToTextEngine> _engineFactory;
    private readonly MemoryStream _buffer = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _capture;
    private Task _captureTask = Task.CompletedTask;

    public DictationSession(IStudioAudioDevice audio, Func<ISpeechToTextEngine> engineFactory)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _engineFactory = engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));
    }

    public DictationState State { get; private set; } = DictationState.Idle;
    public string Transcript { get; private set; } = string.Empty;
    public string? ErrorMessage { get; private set; }

    /// <summary>Raised on every state transition (the pill subscribes to walk its stages).</summary>
    public event Action<DictationState>? StateChanged;

    /// <summary>Begin capturing the mic into the utterance buffer. No-op if already recording.</summary>
    public void Start(AudioEndpoint microphone)
    {
        ArgumentNullException.ThrowIfNull(microphone);
        if (State == DictationState.Recording) return;

        lock (_gate) _buffer.SetLength(0);
        ErrorMessage = null;
        _capture = new CancellationTokenSource();
        var ct = _capture.Token;
        SetState(DictationState.Recording);

        _captureTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in _audio.CaptureAsync(microphone, SampleRate, ct))
                    lock (_gate) _buffer.Write(frame.Span);
            }
            catch (OperationCanceledException) { /* stop requested */ }
        });
    }

    /// <summary>Stop capturing and transcribe the buffered audio; returns the transcript ("" on failure).</summary>
    public async Task<string> StopAndTranscribeAsync(CancellationToken ct = default)
    {
        if (State != DictationState.Recording) return Transcript;

        _capture?.Cancel();
        try
        {
            await _captureTask;
        }
        catch (OperationCanceledException) { /* expected on stop */ }
        catch (Exception ex)
        {
            // A real capture failure (unplugged device, unsupported format) must surface as Failed —
            // not be swallowed and then reported as a successful empty transcript.
            ErrorMessage = ex.Message;
            SetState(DictationState.Failed);
            return string.Empty;
        }

        SetState(DictationState.Transcribing);
        byte[] pcm;
        lock (_gate) pcm = _buffer.ToArray();

        try
        {
            Transcript = await TranscribeAsync(pcm, ct);
            SetState(DictationState.Completed);
            return Transcript;
        }
        catch (Exception ex)
        {
            // A failed utterance shows in the pill rather than crashing the app (parity with Talk).
            ErrorMessage = ex.Message;
            SetState(DictationState.Failed);
            return string.Empty;
        }
    }

    /// <summary>Run buffered PCM through a fresh engine and join the final transcripts.</summary>
    internal async Task<string> TranscribeAsync(byte[] pcm, CancellationToken ct)
    {
        var engine = _engineFactory();
        try
        {
            await engine.StartAsync(ct);
            await engine.WriteAudioAsync(pcm, ct);
            await engine.FlushAsync();
            await engine.StopAsync();

            var sb = new StringBuilder();
            await foreach (var result in engine.ReadTranscriptsAsync(ct))
                if (result.IsFinal) sb.Append(result.Text).Append(' ');
            return sb.ToString().Trim();
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }

    private void SetState(DictationState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        _capture?.Cancel();
        try { await _captureTask; } catch { /* best-effort */ }
        _capture?.Dispose();
        _buffer.Dispose();
    }
}
