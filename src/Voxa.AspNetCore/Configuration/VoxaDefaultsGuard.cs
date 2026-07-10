using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Voxa.AspNetCore;

/// <summary>
/// Validates that the <see cref="VoxaOptions"/> contain everything <c>UseDefaults()</c> requires
/// (non-null Stt, Tts, and a resolvable agent) at host startup — but ONLY when armed by a
/// <c>UseDefaults()</c> call. À-la-carte hosts that compose pipelines manually and never call
/// <c>UseDefaults()</c> are not affected.
/// </summary>
public sealed class VoxaDefaultsGuard : IHostedService
{
    // Volatile: Arm() runs on the route-mapping thread; StartAsync may run on a different
    // thread during host startup. The host-start barrier makes this safe in practice, but
    // volatile removes any dependence on that implementation detail.
    private volatile bool _armed;
    private readonly IOptions<VoxaOptions> _options;
    private readonly VoxaProviderRegistry _registry;
    private readonly IServiceProvider _sp;

    public VoxaDefaultsGuard(
        IOptions<VoxaOptions> options,
        VoxaProviderRegistry registry,
        IServiceProvider sp)
    {
        _options = options;
        _registry = registry;
        _sp = sp;
    }

    /// <summary>Called by <c>UseDefaults()</c> to activate the startup check.</summary>
    public void Arm() => _armed = true;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_armed) return;

        var o = _options.Value;
        var errors = new List<string>();

        if (string.IsNullOrEmpty(o.Stt))
            errors.Add($"Voxa:Stt is required when using UseDefaults(). " +
                       $"Registered providers: {(_registry.SttNames.Count == 0 ? "(none — did you reference the Voxa meta-package?)" : string.Join(", ", _registry.SttNames))}.");

        if (string.IsNullOrEmpty(o.Tts))
            errors.Add($"Voxa:Tts is required when using UseDefaults(). " +
                       $"Registered providers: {(_registry.TtsNames.Count == 0 ? "(none — did you reference the Voxa meta-package?)" : string.Join(", ", _registry.TtsNames))}.");

        // Check agent resolvability. Resolve inside a scope — the composer resolves these from
        // the request scope, so a scoped IChatClient/AIAgent registration must count (resolving
        // scoped services from the root provider throws under scope validation).
        using var scope = _sp.CreateScope();
        var scoped = scope.ServiceProvider;

        // VDX-007: a host-registered IAgentTurnDriver replaces the whole Microsoft-Agents stage, so
        // the composer never resolves an AIAgent/IChatClient/factory for it. Mirror that resolution
        // order HERE — otherwise the guard eagerly resolves the (unused) default AIAgent at startup,
        // which for a host whose agent factory does real work (e.g. waiting on a sandbox warm-up)
        // reintroduces the very first-launch stall the driver seam exists to avoid.
        bool hasHostDriver = scoped.GetService<Voxa.Processors.IAgentTurnDriver>() is not null;
        bool hasDiAgent = !hasHostDriver
                       && (scoped.GetService<Microsoft.Agents.AI.AIAgent>() is not null
                        || scoped.GetService<Microsoft.Extensions.AI.IChatClient>() is not null);

        if (!hasHostDriver && !hasDiAgent)
        {
            // The factory is the agent source the composer will fall back to, so its mere
            // presence is not enough — the meta-package always registers one. Ask it whether
            // the configured provider/credentials would actually let Create() succeed; otherwise
            // the failure surfaces only when the first WebSocket request arrives.
            var factory = scoped.GetService<IVoiceAgentFactory>();
            if (factory is null)
            {
                errors.Add("UseDefaults() needs an agent. Either register an AIAgent or IChatClient in DI, " +
                           "or set Voxa:Agent:Provider to 'OpenAI' or 'Echo' (requires the Voxa meta-package " +
                           "which registers the default agent factory). See docs/getting-started.md.");
            }
            else
            {
                errors.AddRange(factory.Validate(o.Agent));
            }
        }

        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"Voxa UseDefaults() startup validation failed:{Environment.NewLine}" +
                string.Join(Environment.NewLine, errors.Select(e => "  - " + e)));

        // Eager warm-up (VLS-001 WS5.2): local providers resolve their models NOW — first-run
        // download with progress logging, model weights pre-loaded — so the first WebSocket
        // caller never pays a 100+ MB download or a model load. A warm-up failure is a host
        // startup failure with the model cache's remediation message, consistent with the
        // fail-fast contract above. Cloud providers have no WarmUpAsync and skip this entirely.
        var configuration = scoped.GetService<IConfiguration>();
        if (configuration is not null)
        {
            var root = configuration.GetSection(VoxaOptions.SectionName);
            if (root.GetValue("Models:EagerWarmup", true))
            {
                if (o.Stt is not null && _registry.TryGetStt(o.Stt, out var stt) && stt.WarmUpAsync is not null)
                    await stt.WarmUpAsync(scoped, root, cancellationToken).ConfigureAwait(false);
                if (o.Tts is not null && _registry.TryGetTts(o.Tts, out var tts) && tts.WarmUpAsync is not null)
                    await tts.WarmUpAsync(scoped, root, cancellationToken).ConfigureAwait(false);
                // VLS-004: a local enhancer (e.g. DeepFilterNet3 ONNX) resolves + preloads its model here too,
                // so the first session doesn't pay the download/load. None / cloud engines have no WarmUpAsync.
                if (!string.Equals(o.Enhance.Engine, "None", StringComparison.OrdinalIgnoreCase)
                    && _registry.TryGetEnhancer(o.Enhance.Engine, out var enh) && enh.WarmUpAsync is not null)
                    await enh.WarmUpAsync(scoped, root, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
