using Google.Cloud.Speech.V2;
using Google.Protobuf;

namespace Voxa.Speech.Google;

/// <summary>
/// Google Cloud Speech-to-Text v2 streaming <see cref="ISpeechToTextEngine"/> (gRPC bidi). Opens a
/// <c>StreamingRecognize</c> call, sends the config request, streams LINEAR16 audio, and pumps responses into a
/// <see cref="StreamingTranscriptAccumulator"/> — interims for live display, one VAD-gated final per utterance.
/// </summary>
public sealed class GoogleSttEngine : ISpeechToTextEngine
{
    private readonly GoogleSpeechOptions _options;
    private readonly StreamingTranscriptAccumulator _acc = new();
    private SpeechClient.StreamingRecognizeStream? _stream;
    private Task? _readLoop;
    private CancellationTokenSource? _cts;

    public GoogleSttEngine(GoogleSpeechOptions options)
        => _options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task StartAsync(CancellationToken ct)
    {
        if (_stream is not null) return;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var builder = new SpeechClientBuilder();
        if (!string.IsNullOrEmpty(_options.CredentialsPath)) builder.CredentialsPath = _options.CredentialsPath;
        else if (!string.IsNullOrEmpty(_options.CredentialsJson)) builder.JsonCredentials = _options.CredentialsJson;
        if (!string.Equals(_options.Location, "global", StringComparison.OrdinalIgnoreCase))
            builder.Endpoint = $"{_options.Location}-speech.googleapis.com";
        var client = await builder.BuildAsync(ct).ConfigureAwait(false);

        var stream = client.StreamingRecognize();
        await stream.WriteAsync(new StreamingRecognizeRequest
        {
            Recognizer = $"projects/{_options.ProjectId}/locations/{_options.Location}/recognizers/{_options.Recognizer}",
            StreamingConfig = new StreamingRecognitionConfig
            {
                Config = new RecognitionConfig
                {
                    ExplicitDecodingConfig = new ExplicitDecodingConfig
                    {
                        Encoding = ExplicitDecodingConfig.Types.AudioEncoding.Linear16,
                        SampleRateHertz = _options.InputSampleRate,
                        AudioChannelCount = 1,
                    },
                    LanguageCodes = { _options.Language },
                    Model = _options.Model,
                },
                StreamingFeatures = new StreamingRecognitionFeatures { InterimResults = true },
            },
        }).ConfigureAwait(false);

        _stream = stream;
        _readLoop = Task.Run(() => ReadLoopAsync(stream, _cts.Token));
    }

    public async ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        var stream = _stream;
        if (stream is null) return;
        try
        {
            await stream.WriteAsync(new StreamingRecognizeRequest { Audio = ByteString.CopyFrom(pcm.Span) }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            // Stream completed/closing — drop this chunk rather than fault the pipeline.
        }
    }

    public IAsyncEnumerable<TranscriptionResult> ReadTranscriptsAsync(CancellationToken ct) => _acc.ReadAllAsync(ct);

    public Task FlushAsync()
    {
        _acc.Flush(_options.Language);
        return Task.CompletedTask;
    }

    public Task OnUserStartedSpeakingAsync()
    {
        _acc.OnUtteranceStart();
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var stream = _stream;
        if (stream is not null)
        {
            try { await stream.WriteCompleteAsync().ConfigureAwait(false); } catch { /* already closing */ }
        }
        _cts?.Cancel();
        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        _acc.Flush(_options.Language); // drain a buffered last utterance before completing
        _acc.Complete();
    }

    private async Task ReadLoopAsync(SpeechClient.StreamingRecognizeStream stream, CancellationToken ct)
    {
        try
        {
            await foreach (var response in stream.GetResponseStream().WithCancellation(ct).ConfigureAwait(false))
            {
                foreach (var result in response.Results)
                {
                    var transcript = result.Alternatives.Count > 0 ? result.Alternatives[0].Transcript : string.Empty;
                    if (!string.IsNullOrEmpty(transcript))
                        _acc.OnFragment(transcript, result.IsFinal, _options.Language);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception) { /* stream error — read loop ends; StopAsync completes the channel */ }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        _cts?.Dispose();
        _acc.Complete();
        GC.SuppressFinalize(this);
    }
}
