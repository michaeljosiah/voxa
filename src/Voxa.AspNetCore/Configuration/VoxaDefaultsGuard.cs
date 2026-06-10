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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_armed) return Task.CompletedTask;

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
        bool hasAgent = scoped.GetService<Microsoft.Agents.AI.AIAgent>() is not null
                     || scoped.GetService<Microsoft.Extensions.AI.IChatClient>() is not null
                     || scoped.GetService<IVoiceAgentFactory>() is not null;

        if (!hasAgent)
        {
            errors.Add("UseDefaults() needs an agent. Either register an AIAgent or IChatClient in DI, " +
                       "or set Voxa:Agent:Provider (requires the Voxa meta-package which registers " +
                       "OpenAIChatAgentFactory). See docs/getting-started.md.");
        }

        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"Voxa UseDefaults() startup validation failed:{Environment.NewLine}" +
                string.Join(Environment.NewLine, errors.Select(e => "  - " + e)));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
