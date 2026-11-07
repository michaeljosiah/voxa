using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Voxa.ArkeSidecar.Tests;

public class SidecarApplicationTests
{
    private sealed class FakeRuntime : ISidecarSpeechRuntime
    {
        public HealthResponse Health { get; set; } = new(
            true, "1.2.3", 1, "x64", ["kokoro", "whisper"],
            new Dictionary<string, EngineHealth> { ["kokoro"] = new(true, null), ["whisper"] = new(true, null) }, null);
        public TtsRequest? Tts { get; private set; }
        public byte[] SttAudio { get; private set; } = [];
        public string? SttPrompt { get; private set; }
        public Exception? WordTimingFailure { get; set; }
        public Func<CancellationToken, Task>? OnWords { get; set; }

        public Task<byte[]> SynthesizeAsync(TtsRequest request, CancellationToken ct)
        {
            Tts = request;
            return Task.FromResult("RIFFxxxxWAVE"u8.ToArray());
        }

        public Task<string> TranscribeAsync(ReadOnlyMemory<byte> audio, string? contentType, CancellationToken ct, string? prompt = null)
        {
            SttAudio = audio.ToArray();
            SttPrompt = prompt;
            return Task.FromResult("hello harbour");
        }

        public async Task<WordTimingResponse> TranscribeWordsAsync(ReadOnlyMemory<byte> audio, string? contentType, CancellationToken ct)
        {
            SttAudio = audio.ToArray();
            if (OnWords is not null) await OnWords(ct);
            if (WordTimingFailure is not null) throw WordTimingFailure;
            return new("hello harbour", 1.8, new("whisper.cpp/dtw-word-boundaries-v1", "1.9.1", "sha256:fixture"),
                [new("hello", 0.2, 0.51, 0.97), new("harbour", 0.72, 1.45, 0.89)]);
        }
    }

    [Fact]
    public async Task Name_hint_is_Unicode_and_private_to_one_transcription()
    {
        var (app, client, fake) = await StartAsync();
        await using (app)
        using (client)
        {
            const string prompt = "Tunde, Rónkẹ, Ikoyi";
            using var request = new HttpRequestMessage(HttpMethod.Post, "/stt") { Content = new ByteArrayContent([1, 0, 2, 0]) };
            request.Headers.Add("X-Voxa-Prompt", Convert.ToBase64String(Encoding.UTF8.GetBytes(prompt)));
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(prompt, fake.SttPrompt);
            Assert.Equal([1, 0, 2, 0], fake.SttAudio);
            using var plain = await client.PostAsync("/stt", new ByteArrayContent([3, 0]));
            Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
            Assert.Null(fake.SttPrompt);
        }
    }

    [Theory]
    [InlineData("not base64")]
    [InlineData("/w==")]
    [InlineData("AA==")]
    public async Task Rejects_malformed_name_hints_before_transcribing(string header)
    {
        var (app, client, fake) = await StartAsync();
        await using (app)
        using (client)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/stt") { Content = new ByteArrayContent([1, 0]) };
            request.Headers.Add("X-Voxa-Prompt", header);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Empty(fake.SttAudio);
        }
    }

    [Theory]
    [InlineData(100, HttpStatusCode.OK)]
    [InlineData(101, HttpStatusCode.BadRequest)]
    public async Task Bounds_the_name_hint_by_UTF8_bytes(int characters, HttpStatusCode expected)
    {
        var (app, client, _) = await StartAsync();
        await using (app)
        using (client)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/stt") { Content = new ByteArrayContent([1, 0]) };
            request.Headers.Add("X-Voxa-Prompt", Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('é', characters))));
            using var response = await client.SendAsync(request);
            Assert.Equal(expected, response.StatusCode);
        }
    }

    [Fact]
    public async Task Exposes_health_and_exact_arke_voice_shape()
    {
        var (app, client, _) = await StartAsync();
        await using (app)
        {
            using var health = JsonDocument.Parse(await client.GetStringAsync("/health"));
            Assert.True(health.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(1, health.RootElement.GetProperty("protocolVersion").GetInt32());
            Assert.Equal("x64", health.RootElement.GetProperty("architecture").GetString());
            Assert.True(health.RootElement.GetProperty("engineStatus").GetProperty("kokoro").GetProperty("ready").GetBoolean());

            using var voices = JsonDocument.Parse(await client.GetStringAsync("/voices"));
            Assert.Equal(6, voices.RootElement.GetArrayLength());
            Assert.Equal("af_bella", voices.RootElement[0].GetProperty("id").GetString());
            Assert.Equal("kokoro", voices.RootElement[0].GetProperty("engine").GetString());
        }
    }

    [Fact]
    public async Task Rejects_non_json_tts_requests_with_415()
    {
        var (app, client, _) = await StartAsync();
        await using (app)
        {
            using var content = new StringContent("hello");
            var response = await client.PostAsync("/tts", content);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        }
    }

    [Fact]
    public async Task Rejects_unbounded_tts_speed_cardinality()
    {
        var options = SidecarOptions.Parse(["--host", "127.0.0.1", "--port", "5901"]);
        var runtime = new SidecarSpeechRuntime(options);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            runtime.SynthesizeAsync(new TtsRequest { Voice = "af_bella", Text = "hello", Speed = 1.01 }, default));
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Adapts_tts_and_stt_requests_without_logging_content()
    {
        var (app, client, fake) = await StartAsync();
        await using (app)
        {
            var tts = await client.PostAsJsonAsync("/tts", new { voice = "af_bella", text = "secret speech", speed = 1.2 });
            Assert.Equal("audio/wav", tts.Content.Headers.ContentType?.MediaType);
            Assert.Equal("secret speech", fake.Tts?.Text);

            using var audio = new ByteArrayContent([1, 0, 2, 0]);
            audio.Headers.ContentType = new("audio/pcm");
            var stt = await client.PostAsync("/stt", audio);
            Assert.Equal("hello harbour", (await stt.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("text").GetString());
            Assert.Equal([1, 0, 2, 0], fake.SttAudio);
        }
    }

    [Fact]
    public void Rejects_non_loopback_clients()
    {
        Assert.False(SidecarApplication.IsLoopback(IPAddress.Parse("10.0.0.2")));
        Assert.True(SidecarApplication.IsLoopback(IPAddress.Loopback));
    }

    [Fact]
    public async Task Word_timing_is_additive_and_preserves_engine_bounds_and_identity()
    {
        var (app, client, fake) = await StartAsync();
        await using (app)
        using (client)
        {
            using var legacyHealth = JsonDocument.Parse(await client.GetStringAsync("/health"));
            Assert.False(legacyHealth.RootElement.TryGetProperty("wordTiming", out _));
            fake.Health = fake.Health with { WordTiming = new(true, null) };
            using var health = JsonDocument.Parse(await client.GetStringAsync("/health"));
            Assert.True(health.RootElement.GetProperty("wordTiming").GetProperty("ready").GetBoolean());
            using var audio = new ByteArrayContent([1, 0, 2, 0]);
            audio.Headers.ContentType = new("audio/wav");
            using var response = await client.PostAsync("/stt/words", audio);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("hello harbour", result.GetProperty("text").GetString());
            Assert.Equal(1.8, result.GetProperty("seconds").GetDouble());
            Assert.Equal("sha256:fixture", result.GetProperty("engine").GetProperty("model").GetString());
            Assert.Equal(0.72, result.GetProperty("words")[1].GetProperty("startSec").GetDouble());
            Assert.Equal(0.89, result.GetProperty("words")[1].GetProperty("probability").GetDouble());
            Assert.Equal([1, 0, 2, 0], fake.SttAudio);
        }
    }

    [Theory]
    [InlineData(415)]
    [InlineData(422)]
    [InlineData(503)]
    public async Task Word_timing_failures_do_not_fall_back_to_plain_transcription(int status)
    {
        var (app, client, fake) = await StartAsync();
        await using (app)
        using (client)
        {
            fake.WordTimingFailure = status switch
            {
                415 => new InvalidDataException("Invalid audio."),
                422 => new WordTimingRejectedException("Unusable timestamps."),
                _ => new SidecarUnavailableException("Word timing is unavailable.")
            };
            using var response = await client.PostAsync("/stt/words", new ByteArrayContent([0, 0]));
            Assert.Equal(status, (int)response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(result.TryGetProperty("words", out _));
            Assert.False(result.TryGetProperty("text", out _));
        }
    }

    [Fact]
    public async Task Disconnect_cancels_word_timing_work()
    {
        var (app, client, fake) = await StartAsync();
        await using (app)
        using (client)
        using (var cancel = new CancellationTokenSource())
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fake.OnWords = async ct =>
            {
                entered.SetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { cancelled.SetResult(); throw; }
            };
            var request = client.PostAsync("/stt/words", new ByteArrayContent([0, 0]), cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Missing_assets_start_and_report_safe_unavailable_reason()
    {
        var runtime = new SidecarSpeechRuntime(SidecarOptions.Parse(["--host", "127.0.0.1", "--port", "5901"]));
        var health = runtime.Health;
        Assert.True(health.Ok);
        Assert.Equal("Kokoro is unavailable. Whisper is unavailable.", health.UnavailableReason);
        Assert.False(health.WordTiming!.Ready);
        Assert.DoesNotContain(Path.GetTempPath(), health.UnavailableReason, StringComparison.OrdinalIgnoreCase);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Kokoro_and_whisper_availability_are_independent()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"voxa-arke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var whisper = Path.Combine(dir, "whisper.bin");
        await File.WriteAllBytesAsync(whisper, [1]);
        var runtime = new SidecarSpeechRuntime(SidecarOptions.Parse([
            "--host", "127.0.0.1", "--port", "5901", "--whisper-model", whisper]));
        try
        {
            Assert.True(runtime.Health.Ok);
            Assert.Equal("Kokoro is unavailable.", runtime.Health.UnavailableReason);
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(dir, true);
        }
    }

    private static async Task<(WebApplication App, HttpClient Client, FakeRuntime Runtime)> StartAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var fake = new FakeRuntime();
        var app = SidecarApplication.Build(["--host", "127.0.0.1", "--port", port.ToString()], fake);
        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") }, fake);
    }
}
