using System.Diagnostics;
using System.Text;

namespace Voxa.Speech.Kokoro;

/// <summary>
/// Out-of-process grapheme→phoneme conversion via the espeak-ng CLI (VLS-001 WS3.1). One short
/// process per sentence (~10–30 ms, stateless, EOF-delimited) — the inverse of Piper's isolation
/// split, keeping the GPL phonemizer out of this process while the heavy synthesis stays in-proc.
/// </summary>
internal sealed class EspeakPhonemizer
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly string _exePath;
    private readonly string? _dataPath; // --path arg; null for system installs that know their own data
    private readonly string _voice;

    public EspeakPhonemizer(string exePath, string? dataPath, string voice)
    {
        _exePath = exePath;
        _dataPath = dataPath;
        _voice = voice;
    }

    public async Task<string> PhonemizeAsync(string text, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (_dataPath is not null)
        {
            psi.ArgumentList.Add($"--path={_dataPath}");

            // Archive-resolved espeak keeps its shared libraries in ../lib. The builds carry an
            // $ORIGIN rpath, but belt-and-braces the loader path for platforms where that's lost.
            if (!OperatingSystem.IsWindows())
            {
                var libDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_exePath)!, "..", "lib"));
                if (Directory.Exists(libDir))
                {
                    var variable = OperatingSystem.IsMacOS() ? "DYLD_LIBRARY_PATH" : "LD_LIBRARY_PATH";
                    var existing = Environment.GetEnvironmentVariable(variable);
                    psi.Environment[variable] = string.IsNullOrEmpty(existing) ? libDir : $"{libDir}:{existing}";
                }
            }
        }
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add(_voice);
        psi.ArgumentList.Add("--ipa");
        psi.ArgumentList.Add("-q");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start espeak-ng at '{_exePath}'.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            // Text goes via stdin — no command-line length/quoting hazards.
            await process.StandardInput.WriteAsync(text.AsMemory(), timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();

            var stdout = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"espeak-ng exited with code {process.ExitCode}. stderr: {stderr.Trim()}");
            }

            return FormatPhonemes(stdout, text);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"espeak-ng did not respond within {Timeout.TotalSeconds:F0}s.");
        }
    }

    /// <summary>
    /// espeak emits one line per clause and drops punctuation; Kokoro's vocabulary carries
    /// punctuation tokens for prosody. Re-join clauses with ", " and restore the sentence's
    /// terminal punctuation. Pure — unit-tested directly.
    /// </summary>
    internal static string FormatPhonemes(string espeakOutput, string originalText)
    {
        var lines = espeakOutput
            .Split('\n')
            .Select(l => l.Trim().Trim('​')) // espeak pads with zero-width chars on some builds
            .Where(l => l.Length > 0)
            .ToArray();
        var joined = string.Join(", ", lines);

        var trimmed = originalText.TrimEnd();
        if (trimmed.Length > 0 && trimmed[^1] is '.' or '!' or '?' or '…' or ';' or ':')
            joined += trimmed[^1];

        return joined;
    }
}
