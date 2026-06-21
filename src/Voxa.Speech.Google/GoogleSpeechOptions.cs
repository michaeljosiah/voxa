namespace Voxa.Speech.Google;

/// <summary>Configuration for the Google Cloud Speech-to-Text v2 streaming STT engine.</summary>
public sealed record GoogleSpeechOptions
{
    /// <summary>GCP project id (required) — part of the recognizer resource name.</summary>
    public required string ProjectId { get; init; }

    /// <summary>Recognition region. <c>global</c> (default) or a region like <c>us-central1</c> (sets the regional endpoint).</summary>
    public string Location { get; init; } = "global";

    /// <summary>Inline recognizer id, or <c>_</c> (default) for ad-hoc config in the request.</summary>
    public string Recognizer { get; init; } = "_";

    /// <summary>BCP-47 language code.</summary>
    public string Language { get; init; } = "en-US";

    /// <summary>Recognition model (e.g. <c>long</c>, <c>short</c>, <c>telephony</c>).</summary>
    public string Model { get; init; } = "long";

    /// <summary>PCM sample rate of audio fed to STT (LINEAR16, mono).</summary>
    public int InputSampleRate { get; init; } = 16000;

    /// <summary>Path to a service-account JSON key file. Falls back to <see cref="CredentialsJson"/>, then ADC.</summary>
    public string? CredentialsPath { get; init; }

    /// <summary>Inline service-account JSON. Used when <see cref="CredentialsPath"/> is unset.</summary>
    public string? CredentialsJson { get; init; }
}
