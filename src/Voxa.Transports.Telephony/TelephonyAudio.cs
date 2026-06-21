using System.Runtime.InteropServices;

namespace Voxa.Transports.Telephony;

/// <summary>
/// Wire ↔ PCM16 conversion shared by the telephony source and sink. μ-law decode/encode goes through the
/// <see cref="MuLaw"/> tables; PCM16 wire audio is reinterpreted in place (little-endian, the convention for
/// every PCM path in Voxa). Kept tiny so the source/sink read as the mirror images they are.
/// </summary>
internal static class TelephonyAudio
{
    /// <summary>Decode raw wire bytes to PCM16 samples (μ-law table decode, or a PCM16 reinterpret).</summary>
    public static short[] WireToPcm(ReadOnlySpan<byte> wire, TelephonyAudioEncoding encoding)
    {
        if (encoding == TelephonyAudioEncoding.MuLaw)
        {
            var pcm = new short[wire.Length];
            MuLaw.Decode(wire, pcm);
            return pcm;
        }

        // PCM16 little-endian: one sample per two bytes (a trailing odd byte, if any, is dropped).
        return MemoryMarshal.Cast<byte, short>(wire).ToArray();
    }

    /// <summary>Encode PCM16 samples to raw wire bytes (μ-law table encode, or a PCM16 reinterpret).</summary>
    public static byte[] PcmToWire(ReadOnlySpan<short> pcm, TelephonyAudioEncoding encoding)
    {
        if (encoding == TelephonyAudioEncoding.MuLaw)
        {
            var wire = new byte[pcm.Length];
            MuLaw.Encode(pcm, wire);
            return wire;
        }

        var bytes = new byte[pcm.Length * sizeof(short)];
        MemoryMarshal.AsBytes(pcm).CopyTo(bytes);
        return bytes;
    }

    /// <summary>Copy PCM16 samples into a fresh exact-size little-endian byte buffer (an AudioRawFrame payload).</summary>
    public static byte[] PcmToBytes(ReadOnlySpan<short> pcm)
    {
        var bytes = new byte[pcm.Length * sizeof(short)];
        MemoryMarshal.AsBytes(pcm).CopyTo(bytes);
        return bytes;
    }
}
