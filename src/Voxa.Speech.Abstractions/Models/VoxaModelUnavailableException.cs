namespace Voxa.Speech;

/// <summary>
/// Thrown when <see cref="VoxaModelCache"/> cannot produce an artifact: offline-mode cache miss,
/// SHA-256 verification failure, or a failed download. The message always names the artifact, the
/// path(s) involved, and the exact remediation — the VLS-001 "fail with the fix in the message"
/// rule.
/// </summary>
public sealed class VoxaModelUnavailableException : Exception
{
    public VoxaModelUnavailableException(string message) : base(message) { }
    public VoxaModelUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }
}
