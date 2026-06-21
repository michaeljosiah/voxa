namespace Voxa.Transports.Telephony.Tests;

/// <summary>
/// G.711 μ-law (PCMU) correctness (VTL-001 T1.5). The decode→encode round trip is a faithful PCM identity
/// for every code, and the codec matches known golden values.
/// </summary>
public class MuLawTests
{
    [Fact]
    public void DecodeEncode_RoundTrip_Is_Pcm_Identity_For_All_256_Codes()
    {
        // For every μ-law code: decode → encode → decode must reproduce the SAME PCM value. (μ-law has two
        // zero codes, 0x7F and 0xFF; both decode to 0, so the PCM value — not always the code — round-trips.)
        for (int code = 0; code < 256; code++)
        {
            short pcm = MuLaw.DecodeSample((byte)code);
            byte reencoded = MuLaw.EncodeSample(pcm);
            short pcm2 = MuLaw.DecodeSample(reencoded);
            Assert.Equal(pcm, pcm2);
        }
    }

    [Fact]
    public void Code_RoundTrip_Holds_Except_Negative_Zero_Alias()
    {
        // Every code re-encodes to itself except 0x7F (μ-law −0), which canonicalizes to 0xFF (+0).
        for (int code = 0; code < 256; code++)
        {
            byte reencoded = MuLaw.EncodeSample(MuLaw.DecodeSample((byte)code));
            if (code == 0x7F)
                Assert.Equal(0xFF, reencoded);
            else
                Assert.Equal((byte)code, reencoded);
        }
    }

    [Theory]
    [InlineData(0xFF, 0)]        // digital silence
    [InlineData(0x7F, 0)]        // the other zero (−0)
    [InlineData(0x80, 32124)]    // max positive magnitude
    [InlineData(0x00, -32124)]   // max negative magnitude
    public void DecodeSample_Matches_Golden_Values(int code, int expectedPcm)
        => Assert.Equal((short)expectedPcm, MuLaw.DecodeSample((byte)code));

    [Fact]
    public void Encode_Then_Decode_Span_Preserves_Loud_And_Quiet_Tones()
    {
        // A μ-law-origin tone survives encode→decode within μ-law's own quantization step (the only loss).
        var origin = new byte[256];
        for (int i = 0; i < origin.Length; i++) origin[i] = (byte)i;

        var pcm = new short[origin.Length];
        MuLaw.Decode(origin, pcm);
        var reencoded = new byte[origin.Length];
        MuLaw.Encode(pcm, reencoded);

        // Re-decoding the re-encoded bytes yields identical PCM (the meaningful identity).
        var pcm2 = new short[origin.Length];
        MuLaw.Decode(reencoded, pcm2);
        Assert.Equal(pcm, pcm2);
    }

    [Fact]
    public void Decode_Throws_When_Destination_Too_Small()
        => Assert.Throws<ArgumentException>(() => MuLaw.Decode(new byte[4], new short[2]));

    [Fact]
    public void Encode_Throws_When_Destination_Too_Small()
        => Assert.Throws<ArgumentException>(() => MuLaw.Encode(new short[4], new byte[2]));
}
