using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Voxa.Speech;

namespace Voxa.Audio.SmartTurn;

/// <summary>
/// DI registration for smart-turn detection. <b>Opt-in</b> — the Voxa meta-package does not register it
/// (it adds a call on the turn-taking path). Call <c>AddVoxaSmartTurn(configuration)</c> after
/// <c>AddVoxa(...)</c>; the default composer then wires the registered classifier into the VAD, and a
/// <c>Voxa:Vad:StopDuration</c> as low as ~200 ms becomes safe.
/// </summary>
public static class SmartTurnServiceCollectionExtensions
{
    private const string VoxaSectionName = "Voxa";

    /// <summary>
    /// Register the configured smart-turn classifier from <c>Voxa:SmartTurn</c> as the
    /// <see cref="ISmartTurnClassifier"/> the composer picks up:
    /// <list type="bullet">
    /// <item><c>Provider: "Sidecar"</c> + a <c>PythonScript</c>/<c>ExecutablePath</c> → a Voxa-managed
    /// Python process running the real model (<see cref="SidecarSmartTurnClassifier"/>).</item>
    /// <item><c>Provider: "Http"</c> + an <c>Endpoint</c> → <see cref="HttpSmartTurnClassifier"/>.</item>
    /// </list>
    /// Throws if a provider is selected without its required setting (fail fast); a missing/"None"
    /// provider registers nothing (classic silence-only behavior).
    /// </summary>
    public static IServiceCollection AddVoxaSmartTurn(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var voxa = configuration.GetSection(VoxaSectionName);
        var provider = voxa.GetSection(SmartTurnOptions.SectionName)["Provider"];

        if (string.Equals(provider, "Sidecar", StringComparison.OrdinalIgnoreCase))
        {
            var options = SmartTurnOptions.FromConfiguration(voxa);
            if (string.IsNullOrWhiteSpace(options.PythonScript) && string.IsNullOrWhiteSpace(options.ExecutablePath))
                throw new InvalidOperationException(
                    "Voxa:SmartTurn:Provider is 'Sidecar' but neither Voxa:SmartTurn:PythonScript nor " +
                    "Voxa:SmartTurn:ExecutablePath is set.");

            services.AddSingleton<ISmartTurnClassifier>(sp => new SidecarSmartTurnClassifier(
                options, sp.GetService<ILoggerFactory>()?.CreateLogger("Voxa.Audio.SmartTurn")));
        }
        else if (string.Equals(provider, "Http", StringComparison.OrdinalIgnoreCase))
        {
            var options = SmartTurnOptions.FromConfiguration(voxa);
            if (string.IsNullOrWhiteSpace(options.Endpoint))
                throw new InvalidOperationException(
                    "Voxa:SmartTurn:Provider is 'Http' but Voxa:SmartTurn:Endpoint is not set.");

            services.AddSingleton<ISmartTurnClassifier>(sp => new HttpSmartTurnClassifier(
                options,
                // Honor a host-customized Voxa HTTP client (proxy / mTLS / retries / test handler), exactly
                // as the STT/TTS providers do, falling back to the shared client when none is registered —
                // otherwise the smart-turn endpoint alone would ignore the host's outbound settings.
                (sp.GetService(typeof(IVoxaHttpClientProvider)) as IVoxaHttpClientProvider)?.Resolve() ?? VoxaHttp.Shared,
                sp.GetService<ILoggerFactory>()?.CreateLogger("Voxa.Audio.SmartTurn")));
        }

        return services;
    }
}
