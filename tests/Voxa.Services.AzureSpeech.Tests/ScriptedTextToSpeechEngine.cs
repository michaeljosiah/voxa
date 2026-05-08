using System.Runtime.CompilerServices;
using System.Text;
using Voxa.Services.AzureSpeech.Engines;

namespace Voxa.Services.AzureSpeech.Tests;

/// <summary>In-memory <see cref="ITextToSpeechEngine"/> that yields scripted PCM chunks per text request.</summary>
internal sealed class ScriptedTextToSpeechEngine : ITextToSpeechEngine
{
    private readonly Func<string, byte[][]> _generate;
    private readonly List<string> _calls = new();
    private readonly object _lock = new();

    public bool Disposed { get; private set; }

    public IReadOnlyList<string> SynthesizeCalls
    {
        get { lock (_lock) return _calls.ToList(); }
    }

    public ScriptedTextToSpeechEngine(Func<string, byte[][]>? generate = null)
    {
        _generate = generate ?? (text => new[] { Encoding.UTF8.GetBytes($"PCM:{text}") });
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public async IAsyncEnumerable<byte[]> SynthesizeAsync(
        string text,
        [EnumeratorCancellation] CancellationToken ct)
    {
        lock (_lock) _calls.Add(text);
        foreach (var chunk in _generate(text))
        {
            ct.ThrowIfCancellationRequested();
            yield return chunk;
            await Task.Yield();
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
