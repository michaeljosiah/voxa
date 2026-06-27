using System.Diagnostics;

namespace Voxa.Speech.Voxtral;

/// <summary>Reports installed GPU VRAM so a host (Voxa Studio) can gate the Voxtral default on real capability.
/// Behind an interface so tests inject fake hardware instead of shelling out to <c>nvidia-smi</c>.</summary>
public interface IGpuInfoProbe
{
    /// <summary>VRAM of the largest single GPU, rounded to whole GiB; <c>0</c> when none is detected or the probe
    /// fails. Voxtral needs one GPU big enough to hold the 4B model, so the maximum matters — not the sum.</summary>
    int LargestGpuMemoryGb();
}

/// <summary>Default <see cref="IGpuInfoProbe"/>: runs <c>nvidia-smi --query-gpu=memory.total</c> and takes the
/// largest per-GPU total. Any failure (no NVIDIA driver, command not found, timeout) reports <c>0</c> — "not
/// capable" — so the caller falls back to whisper.cpp rather than erroring.</summary>
public sealed class NvidiaSmiGpuInfoProbe : IGpuInfoProbe
{
    // GPU VRAM is constant for the process lifetime and nvidia-smi spawns a process, so probe once and cache —
    // repeated capability checks (and every Studio test that builds the services) then don't re-shell out.
    private static readonly Lazy<int> Cached = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    public int LargestGpuMemoryGb() => Cached.Value;

    private static int Probe()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--query-gpu=memory.total");
            psi.ArgumentList.Add("--format=csv,noheader,nounits");

            using var p = Process.Start(psi);
            if (p is null) return 0;

            // Output is a handful of bytes (one integer line per GPU), well under the OS pipe buffer, so waiting
            // for exit before reading can't deadlock. Bound the wait so a wedged driver can't hang startup.
            if (!p.WaitForExit(3000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return 0;
            }

            var output = p.StandardOutput.ReadToEnd();
            var maxMib = 0;
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (int.TryParse(line, out var mib) && mib > maxMib) maxMib = mib;

            // MiB → GiB, rounded: a "16 GB" card reports ~16376 MiB, which floors to 15 and would fail its own gate.
            return (int)Math.Round(maxMib / 1024.0);
        }
        catch
        {
            return 0; // no nvidia-smi / no NVIDIA GPU / any failure → treat as not capable
        }
    }
}

/// <summary>Decides whether this machine can actually run Voxtral under vLLM: a single GPU at or above the
/// configured VRAM floor (default 16 GiB). Pure over an injected <see cref="IGpuInfoProbe"/> so it's testable
/// without hardware. Consumed by Studio's default-STT selector — never by the engine, which assumes a ready server.</summary>
public sealed class VoxtralCapabilityProbe
{
    private readonly IGpuInfoProbe _gpu;

    public VoxtralCapabilityProbe(IGpuInfoProbe? gpu = null) => _gpu = gpu ?? new NvidiaSmiGpuInfoProbe();

    /// <summary>True when the largest GPU has at least <paramref name="minGpuMemoryGb"/> GiB VRAM.</summary>
    public bool IsCapable(int minGpuMemoryGb) => _gpu.LargestGpuMemoryGb() >= minGpuMemoryGb;
}
