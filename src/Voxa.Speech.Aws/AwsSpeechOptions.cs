namespace Voxa.Speech.Aws;

/// <summary>Configuration for the AWS Transcribe streaming STT engine.</summary>
public sealed record AwsSpeechOptions
{
    /// <summary>AWS region system name (e.g. <c>us-east-1</c>).</summary>
    public string Region { get; init; } = "us-east-1";

    /// <summary>Access key id. Omit (with the secret) to use the default AWS credential chain (env / profile / IAM role).</summary>
    public string? AccessKeyId { get; init; }

    /// <summary>Secret access key. Pairs with <see cref="AccessKeyId"/>.</summary>
    public string? SecretAccessKey { get; init; }

    /// <summary>BCP-47 language code (AWS Transcribe form, e.g. <c>en-US</c>).</summary>
    public string Language { get; init; } = "en-US";

    /// <summary>PCM sample rate of audio fed to STT (PCM, mono).</summary>
    public int InputSampleRate { get; init; } = 16000;
}
