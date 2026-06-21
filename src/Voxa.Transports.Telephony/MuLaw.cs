namespace Voxa.Transports.Telephony;

/// <summary>
/// G.711 μ-law (PCMU) companding — the PSTN's standard 8-bit-per-sample audio coding, used on the wire by
/// Twilio Media Streams. Decode is a direct 256-entry table (<c>byte → PCM16</c>); encode is the standard
/// segment/quantize algorithm with a 256-entry exponent table keyed on the top bits of the sample. Both are
/// pure and allocation-free.
///
/// <para>
/// The round-trip is lossy only to μ-law's own quantization — exactly what the PSTN already imposes — so
/// re-encoding μ-law-origin audio yields the identical PCM value for every code. (μ-law has two encodings of
/// zero, <c>0x7F</c> and <c>0xFF</c>; both decode to 0 and re-encode to the canonical <c>0xFF</c>, so the
/// PCM value round-trips exactly while that one alias maps to its twin.)
/// </para>
/// </summary>
public static class MuLaw
{
    private const int Bias = 0x84;    // 132 — added before quantization, removed on decode
    private const int Clip = 32635;   // 0x7F7B — the largest magnitude μ-law can represent

    private static readonly short[] DecodeTable = BuildDecodeTable();
    private static readonly byte[] ExponentTable = BuildExponentTable();

    /// <summary>Decode one μ-law byte to a PCM16 sample.</summary>
    public static short DecodeSample(byte muLaw) => DecodeTable[muLaw];

    /// <summary>Encode one PCM16 sample to a μ-law byte.</summary>
    public static byte EncodeSample(short pcm)
    {
        int sign = (pcm >> 8) & 0x80;                 // 0x80 for negatives, 0 otherwise
        int magnitude = sign != 0 ? -pcm : pcm;       // -pcm promotes short to int — no overflow at short.MinValue
        if (magnitude > Clip) magnitude = Clip;
        magnitude += Bias;
        int exponent = ExponentTable[(magnitude >> 7) & 0xFF];
        int mantissa = (magnitude >> (exponent + 3)) & 0x0F;
        return (byte)~(sign | (exponent << 4) | mantissa);
    }

    /// <summary>Decode <paramref name="muLaw"/> into <paramref name="pcm"/> (one PCM16 sample per μ-law byte).</summary>
    public static void Decode(ReadOnlySpan<byte> muLaw, Span<short> pcm)
    {
        if (pcm.Length < muLaw.Length)
            throw new ArgumentException("Destination span is smaller than the source.", nameof(pcm));
        for (int i = 0; i < muLaw.Length; i++) pcm[i] = DecodeTable[muLaw[i]];
    }

    /// <summary>Encode <paramref name="pcm"/> into <paramref name="muLaw"/> (one μ-law byte per PCM16 sample).</summary>
    public static void Encode(ReadOnlySpan<short> pcm, Span<byte> muLaw)
    {
        if (muLaw.Length < pcm.Length)
            throw new ArgumentException("Destination span is smaller than the source.", nameof(muLaw));
        for (int i = 0; i < pcm.Length; i++) muLaw[i] = EncodeSample(pcm[i]);
    }

    private static short[] BuildDecodeTable()
    {
        var table = new short[256];
        for (int i = 0; i < 256; i++)
        {
            int u = ~i & 0xFF;                         // μ-law stores the complement
            int t = ((u & 0x0F) << 3) + Bias;          // mantissa → magnitude
            t <<= (u & 0x70) >> 4;                      // apply the exponent segment
            t -= Bias;
            table[i] = (short)((u & 0x80) != 0 ? -t : t);
        }
        return table;
    }

    private static byte[] BuildExponentTable()
    {
        // exponent = floor(log2(i)) for i >= 1 (segment of the biased magnitude's high byte); 0 for i < 2.
        var table = new byte[256];
        for (int i = 2; i < 256; i++)
        {
            byte e = 0;
            for (int v = i; v > 1; v >>= 1) e++;
            table[i] = e;
        }
        return table;
    }
}
