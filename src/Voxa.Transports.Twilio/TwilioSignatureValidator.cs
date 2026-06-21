using System.Security.Cryptography;
using System.Text;

namespace Voxa.Transports.Twilio;

/// <summary>
/// Validates Twilio's <c>X-Twilio-Signature</c> request signature (HMAC-SHA1 over the request URL plus,
/// for POST webhooks, the sorted form parameters; base64-encoded). This is the documented control that
/// proves a webhook request actually came from Twilio. Pure and offline — unit-testable without a network.
/// </summary>
public static class TwilioSignatureValidator
{
    /// <summary>
    /// Compute the expected signature: the full request <paramref name="url"/> with each POST form parameter
    /// (sorted by name) appended as <c>name + value</c>, HMAC-SHA1'd with <paramref name="authToken"/> and
    /// base64-encoded. Pass <paramref name="postForm"/> = null for a GET webhook (URL only).
    /// </summary>
    public static string Compute(string authToken, string url, IEnumerable<KeyValuePair<string, string>>? postForm)
    {
        ArgumentNullException.ThrowIfNull(authToken);
        ArgumentNullException.ThrowIfNull(url);

        var sb = new StringBuilder(url);
        if (postForm is not null)
        {
            foreach (var kv in postForm.OrderBy(static k => k.Key, StringComparer.Ordinal))
                sb.Append(kv.Key).Append(kv.Value);
        }

        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(authToken));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// True if <paramref name="providedSignature"/> (the <c>X-Twilio-Signature</c> header) matches the
    /// signature computed over <paramref name="url"/> + <paramref name="postForm"/>. The compare is
    /// constant-time. A missing/empty header is never valid.
    /// </summary>
    public static bool IsValid(
        string authToken,
        string url,
        IEnumerable<KeyValuePair<string, string>>? postForm,
        string? providedSignature)
    {
        if (string.IsNullOrEmpty(providedSignature)) return false;

        var expected = Compute(authToken, url, postForm);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(providedSignature));
    }
}
