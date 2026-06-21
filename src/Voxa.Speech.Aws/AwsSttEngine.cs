using System.Threading.Channels;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.EventStreams;
using Amazon.TranscribeStreaming;
using Amazon.TranscribeStreaming.Model;

namespace Voxa.Speech.Aws;

/// <summary>
/// AWS Transcribe streaming <see cref="ISpeechToTextEngine"/> over the official <c>AWSSDK.TranscribeStreaming</c>
/// (v4) client. Audio is fed through an <see cref="IEventStreamPublisher"/> the SDK pulls from; transcript events
/// are pumped into a <see cref="StreamingTranscriptAccumulator"/> — interims for live display, one VAD-gated final
/// per utterance.
/// </summary>
public sealed class AwsSttEngine : ISpeechToTextEngine
{
    private readonly AwsSpeechOptions _options;
    private readonly StreamingTranscriptAccumulator _acc = new();
    private readonly Channel<AudioEvent> _audioQueue = Channel.CreateUnbounded<AudioEvent>();
    private AmazonTranscribeStreamingClient? _client;
    private Task? _processing;
    private CancellationTokenSource? _cts;

    public AwsSttEngine(AwsSpeechOptions options)
        => _options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task StartAsync(CancellationToken ct)
    {
        if (_client is not null) return;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var region = RegionEndpoint.GetBySystemName(_options.Region);
        _client = string.IsNullOrEmpty(_options.AccessKeyId)
            ? new AmazonTranscribeStreamingClient(region)
            : new AmazonTranscribeStreamingClient(
                new BasicAWSCredentials(_options.AccessKeyId, _options.SecretAccessKey), region);

        var request = new StartStreamTranscriptionRequest
        {
            LanguageCode = LanguageCode.FindValue(_options.Language),
            MediaEncoding = MediaEncoding.Pcm,
            MediaSampleRateHertz = _options.InputSampleRate,
            AudioStreamPublisher = NextAudioEventAsync,
        };

        var response = await _client.StartStreamTranscriptionAsync(request, _cts.Token).ConfigureAwait(false);
        var stream = response.TranscriptResultStream;
        stream.TranscriptEventReceived += OnTranscript;
        stream.ExceptionReceived += (_, _) => _acc.Complete();
        _processing = stream.StartProcessingAsync();
    }

    private void OnTranscript(object? sender, EventStreamEventReceivedArgs<TranscriptEvent> e)
    {
        var results = e.EventStreamEvent?.Transcript?.Results;
        if (results is null) return;
        foreach (var r in results)
        {
            var text = r.Alternatives is { Count: > 0 } alts ? alts[0].Transcript : null;
            if (!string.IsNullOrEmpty(text))
                _acc.OnFragment(text, isSegmentFinal: r.IsPartial == false, _options.Language);
        }
    }

    public async ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        try
        {
            await _audioQueue.Writer.WriteAsync(new AudioEvent { AudioChunk = new MemoryStream(pcm.ToArray()) }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ChannelClosedException or OperationCanceledException)
        {
            // Stream closing — drop this chunk rather than fault the pipeline.
        }
    }

    public IAsyncEnumerable<TranscriptionResult> ReadTranscriptsAsync(CancellationToken ct) => _acc.ReadAllAsync(ct);

    public Task FlushAsync()
    {
        _acc.Flush(_options.Language);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _audioQueue.Writer.TryComplete(); // ends the publisher → AWS closes the stream
        if (_processing is not null)
        {
            try { await _processing.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        _cts?.Cancel();
        _acc.Complete();
    }

    // The SDK pulls audio by calling this delegate repeatedly: return the next queued AudioEvent, or null when
    // the queue closes to signal end of stream.
    private async Task<IAudioStreamEvent> NextAudioEventAsync()
    {
        if (await _audioQueue.Reader.WaitToReadAsync().ConfigureAwait(false) && _audioQueue.Reader.TryRead(out var ev))
            return ev;
        return null!; // no more audio
    }

    public async ValueTask DisposeAsync()
    {
        _audioQueue.Writer.TryComplete();
        _cts?.Cancel();
        if (_processing is not null)
        {
            try { await _processing.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        _client?.Dispose();
        _cts?.Dispose();
        _acc.Complete();
        GC.SuppressFinalize(this);
    }
}
