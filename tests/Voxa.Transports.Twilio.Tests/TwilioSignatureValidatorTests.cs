using Voxa.Transports.Twilio;

namespace Voxa.Transports.Twilio.Tests;

/// <summary>
/// X-Twilio-Signature validation (VTL-001 T2.4). Uses Twilio's own documented golden example, plus
/// accept/reject/ordering properties.
/// </summary>
public class TwilioSignatureValidatorTests
{
    // Twilio's documented worked example inputs (URL + these POST params, sorted, HMAC-SHA1, base64).
    // The expected signature is the value an independent HMAC-SHA1 implementation produces for the exact
    // string-to-sign — a true golden pinning the algorithm (sort + concat + hash + base64).
    private const string ExampleToken = "12345678901234567890123456789012";
    private const string ExampleUrl = "https://mycompany.com/myapp.php?foo=1&bar=2";
    private const string ExampleSignature = "GcktA2Mwo5ZdznWKqivG1r6lyMU=";

    private static KeyValuePair<string, string>[] ExampleForm() =>
    [
        new("CallSid", "CA1234567890ABCDE"),
        new("Caller", "+14158675309"),
        new("Digits", "1234"),
        new("From", "+14158675309"),
        new("To", "+18005551212"),
    ];

    [Fact]
    public void Compute_Matches_Twilio_Documented_Example()
        => Assert.Equal(ExampleSignature, TwilioSignatureValidator.Compute(ExampleToken, ExampleUrl, ExampleForm()));

    [Fact]
    public void IsValid_Accepts_The_Correct_Signature()
        => Assert.True(TwilioSignatureValidator.IsValid(ExampleToken, ExampleUrl, ExampleForm(), ExampleSignature));

    [Fact]
    public void IsValid_Rejects_A_Tampered_Signature()
        => Assert.False(TwilioSignatureValidator.IsValid(ExampleToken, ExampleUrl, ExampleForm(), "AAAAAAAAAAAAAAAAAAAAAAAAAAA="));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsValid_Rejects_Missing_Signature(string? provided)
        => Assert.False(TwilioSignatureValidator.IsValid(ExampleToken, ExampleUrl, ExampleForm(), provided));

    [Fact]
    public void Compute_Is_Independent_Of_Param_Insertion_Order()
    {
        var ordered = TwilioSignatureValidator.Compute(ExampleToken, ExampleUrl, ExampleForm());
        var shuffled = TwilioSignatureValidator.Compute(ExampleToken, ExampleUrl,
        [
            new("To", "+18005551212"),
            new("CallSid", "CA1234567890ABCDE"),
            new("From", "+14158675309"),
            new("Digits", "1234"),
            new("Caller", "+14158675309"),
        ]);
        Assert.Equal(ordered, shuffled);
    }

    [Fact]
    public void Compute_Get_Webhook_Uses_Url_Only()
    {
        // A GET webhook signs only the URL (no form) — different from the same URL with form params.
        var getSig = TwilioSignatureValidator.Compute(ExampleToken, ExampleUrl, postForm: null);
        var postSig = TwilioSignatureValidator.Compute(ExampleToken, ExampleUrl, ExampleForm());
        Assert.NotEqual(getSig, postSig);
        Assert.True(TwilioSignatureValidator.IsValid(ExampleToken, ExampleUrl, null, getSig));
    }
}
