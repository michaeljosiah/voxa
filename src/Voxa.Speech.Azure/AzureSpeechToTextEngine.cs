using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

namespace Voxa.Speech.Azure;

/// <summary>
/// <see cref="ISpeechToTextEngine"/> implementation backed by the Microsoft Cognitive Services
/// Speech SDK. Uses a <see cref="PushAudioInputStream"/> for incremental audio input and
/// continuous-recognition events for interim + final transcripts.
/// </summary>
public sealed class AzureSpeechToTextEngine : ISpeechToTextEngine
{
    private readonly AzureSpeechOptions _options;
    private readonly Channel<TranscriptionResult> _transcripts =
        Channel.CreateUnbounded<TranscriptionResult>();

    private SpeechConfig? _speechConfig;
    private PushAudioInputStream? _pushStream;
    private AudioConfig? _audioConfig;
    private SpeechRecognizer? _recognizer;

    public AzureSpeechToTextEngine(AzureSpeechOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _speechConfig = SpeechConfig.FromSubscription(_options.SubscriptionKey, _options.Region);
        _speechConfig.SpeechRecognitionLanguage = _options.RecognitionLanguage;

        var format = AudioStreamFormat.GetWaveFormatPCM(
            (uint)_options.InputSampleRate,
            bitsPerSample: 16,
            (byte)_options.InputChannels);

        _pushStream = AudioInputStream.CreatePushStream(format);
        _audioConfig = AudioConfig.FromStreamInput(_pushStream);
        _recognizer = new SpeechRecognizer(_speechConfig, _audioConfig);

        _recognizer.Recognizing += OnRecognizing;
        _recognizer.Recognized += OnRecognized;
        _recognizer.Canceled += OnCanceled;

        await _recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);
    }

    public ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        if (_pushStream is null || pcm.IsEmpty) return ValueTask.CompletedTask;

        // PushAudioInputStream.Write copies the data into its own buffer synchronously, so we can
        // hand it a borrowed array and reuse/return it immediately.
        if (MemoryMarshal.TryGetArray(pcm, out ArraySegment<byte> seg) &&
            seg.Offset == 0 && seg.Count == seg.Array!.Length)
        {
            // Frame-owned, exactly-sized array (the common case from WebSocketAudioSource): no copy.
            _pushStream.Write(seg.Array, seg.Count);
        }
        else
        {
            var rented = ArrayPool<byte>.Shared.Rent(pcm.Length);
            try
            {
                pcm.Span.CopyTo(rented);
                _pushStream.Write(rented, pcm.Length);
            }
            finally { ArrayPool<byte>.Shared.Return(rented); }
        }
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<TranscriptionResult> ReadTranscriptsAsync(CancellationToken ct)
        => _transcripts.Reader.ReadAllAsync(ct);

    public async Task StopAsync()
    {
        if (_recognizer is not null)
        {
            try { await _recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false); }
            catch { /* best-effort */ }
        }

        _pushStream?.Close();
        _transcripts.Writer.TryComplete();
    }

    public ValueTask DisposeAsync()
    {
        if (_recognizer is not null)
        {
            _recognizer.Recognizing -= OnRecognizing;
            _recognizer.Recognized -= OnRecognized;
            _recognizer.Canceled -= OnCanceled;
            _recognizer.Dispose();
        }
        _audioConfig?.Dispose();
        _pushStream?.Dispose();
        return ValueTask.CompletedTask;
    }

    private void OnRecognizing(object? sender, SpeechRecognitionEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Result.Text))
        {
            _transcripts.Writer.TryWrite(new TranscriptionResult(e.Result.Text, IsFinal: false, _options.RecognitionLanguage));
        }
    }

    private void OnRecognized(object? sender, SpeechRecognitionEventArgs e)
    {
        if (e.Result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrEmpty(e.Result.Text))
        {
            _transcripts.Writer.TryWrite(new TranscriptionResult(e.Result.Text, IsFinal: true, _options.RecognitionLanguage));
        }
    }

    private void OnCanceled(object? sender, SpeechRecognitionCanceledEventArgs e)
    {
        if (e.Reason == CancellationReason.Error)
        {
            _transcripts.Writer.TryComplete(new InvalidOperationException(
                $"Azure STT cancelled: {e.ErrorCode} {e.ErrorDetails}"));
        }
        else
        {
            _transcripts.Writer.TryComplete();
        }
    }
}
