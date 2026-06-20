using Voxa.Speech;

namespace Voxa.Audio.Onnx;

/// <summary>
/// Self-description of an ONNX model as a bundle of pinned artifacts (VLS-006 WS3) — the ONNX analogue of
/// <c>VoxaSttDescriptor</c> etc. An ONNX model is frequently more than one file (the graph plus sidecars: a
/// tokenizer, a vocab, a config, sometimes a second encoder/decoder graph), so it self-describes as a graph
/// artifact + sidecar artifacts, each <see cref="VoxaModelArtifact.Sha256"/>-pinned and resolved through the
/// unchanged <see cref="VoxaModelCache"/>. VLS-006 provides the <i>shape</i>; the per-family catalogs live in
/// the consuming package (a <c>ParakeetCatalog</c>, a <c>SidonCatalog</c>), following the KokoroCatalog /
/// WhisperCppModelCatalog pattern.
/// </summary>
/// <param name="Id">Stable model id, e.g. <c>"parakeet-tdt-v3-0.6b"</c>.</param>
/// <param name="Graph">The <c>.onnx</c> graph (or the first of several).</param>
/// <param name="Sidecars">Tokenizer / vocab / config / extra graphs — each SHA-256-pinned. May be empty.</param>
/// <param name="SupportedDevices">
/// The execution providers this model supports, e.g. <c>[Cpu, Cuda]</c> — drives validation and the
/// <see cref="OnnxDevice.Auto"/> fallback.
/// </param>
public sealed record OnnxModelDescriptor(
    string Id,
    VoxaModelArtifact Graph,
    IReadOnlyList<VoxaModelArtifact> Sidecars,
    IReadOnlyList<OnnxDevice> SupportedDevices);

/// <summary>
/// A resolved <see cref="OnnxModelDescriptor"/>: the local graph path plus each sidecar's local path keyed by
/// <see cref="VoxaModelArtifact.Id"/>.
/// </summary>
/// <param name="GraphPath">Local filesystem path to the resolved graph.</param>
/// <param name="Sidecars">Sidecar local paths keyed by artifact id.</param>
public sealed record ResolvedOnnxModel(string GraphPath, IReadOnlyDictionary<string, string> Sidecars);

/// <summary>Resolve helpers for <see cref="OnnxModelDescriptor"/>.</summary>
public static class OnnxModelDescriptorExtensions
{
    /// <summary>
    /// Resolve every artifact (graph + sidecars) through <paramref name="cache"/>, file by file — no new
    /// download machinery, just <see cref="VoxaModelCache.ResolveAsync"/> (verified download, atomic rename,
    /// cross-process lock; already-cached files return instantly).
    /// </summary>
    /// <exception cref="VoxaModelUnavailableException">
    /// Any artifact can't be produced (offline cache miss, SHA-256 mismatch, or download failure).
    /// </exception>
    public static async Task<ResolvedOnnxModel> ResolveAsync(
        this OnnxModelDescriptor model, VoxaModelCache cache, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(cache);

        var graphPath = await cache.ResolveAsync(model.Graph, ct).ConfigureAwait(false);
        var sidecars = new Dictionary<string, string>(model.Sidecars.Count);
        foreach (var s in model.Sidecars)
            sidecars[s.Id] = await cache.ResolveAsync(s, ct).ConfigureAwait(false);
        return new ResolvedOnnxModel(graphPath, sidecars);
    }
}
