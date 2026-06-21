namespace Voxa.Transports.Twilio;

/// <summary>
/// Options for the Twilio voice endpoint, bound from <c>Voxa:Telephony:Twilio</c> at map time (the
/// config-capture rule: the endpoint captures the <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>
/// passed to <c>AddVoxa</c> rather than service-locating it). Override in code via the
/// <c>MapVoxaTwilioVoice(pattern, configure)</c> callback.
/// </summary>
public sealed class TwilioTelephonyOptions
{
    /// <summary>The config section these options bind from.</summary>
    public const string SectionName = "Voxa:Telephony:Twilio";

    /// <summary>
    /// Verify Twilio's <c>X-Twilio-Signature</c> on the webhook before returning TwiML. On by default —
    /// without it, anyone who learns the URL can drive your pipeline. Disable only for local tunnels
    /// (ngrok) where the signed URL won't match the request URL the server sees.
    /// </summary>
    public bool ValidateSignature { get; set; } = true;

    /// <summary>
    /// Twilio auth token, used to compute the request signature. Required when
    /// <see cref="ValidateSignature"/> is true (the endpoint fails closed if it is missing). Prefer
    /// environment variables / a secret store over <c>appsettings.json</c>.
    /// </summary>
    public string? AuthToken { get; set; }

    /// <summary>
    /// Override the <c>wss://</c> base URL embedded in the TwiML <c>&lt;Stream&gt;</c> when the public URL
    /// differs from the request host (reverse proxy, tunnel). Example: <c>wss://abc123.ngrok.io</c>. The
    /// media path (<c>{pattern}/media</c>) is appended automatically. When unset, the request host is used.
    /// </summary>
    public string? PublicWssBaseUrl { get; set; }
}
