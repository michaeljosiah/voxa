using Voxa.Studio.Services;

namespace Voxa.Studio.Tests;

/// <summary>D2 plumbing: WAV round-trip, stereo mix-down, resampling, and the envelope reducer.</summary>
public class WavIoTests
{
    [Fact]
    public void Write_Produces_A_Valid_Riff_Header()
    {
        var pcm = new byte[3200]; // 100 ms @ 16 kHz
        var wav = WavIo.Write(pcm, 16000);

        Assert.Equal((byte)'R', wav[0]);
        Assert.Equal((byte)'F', wav[3]);
        Assert.Equal(44 + pcm.Length, wav.Length);
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));          // sample rate
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));              // bits per sample
        Assert.Equal(pcm.Length, BitConverter.ToInt32(wav, 40));      // data size
    }

    [Fact]
    public void Read_Round_Trips_What_Write_Produced()
    {
        var pcm = new byte[6400];
        for (int i = 0; i < pcm.Length; i += 2) pcm[i] = (byte)(i % 117);
        var path = Path.Combine(TestSupport.TempDir(), "roundtrip.wav");
        File.WriteAllBytes(path, WavIo.Write(pcm, 16000));

        var read = WavIo.ReadMono(path, 16000);
        Assert.Equal(16000, read.SampleRate);
        Assert.Equal(pcm, read.Pcm);
    }

    [Fact]
    public void Read_Resamples_To_The_Target_Rate()
    {
        var pcm48k = new byte[48000 * 2]; // exactly 1 s @ 48 kHz
        var path = Path.Combine(TestSupport.TempDir(), "rate.wav");
        File.WriteAllBytes(path, WavIo.Write(pcm48k, 48000));

        var read = WavIo.ReadMono(path, 16000);
        // Still ~1 s of audio at the new rate (linear resampler edge ±1 sample).
        Assert.InRange(read.Pcm.Length / 2, 16000 - 2, 16000 + 2);
    }

    [Fact]
    public void Read_Rejects_Non_Pcm16_Loudly()
    {
        var path = Path.Combine(TestSupport.TempDir(), "not-audio.wav");
        File.WriteAllBytes(path, "RIFFxxxxNOPE"u8.ToArray());
        Assert.ThrowsAny<Exception>(() => WavIo.ReadMono(path, 16000));
    }

    [Fact]
    public void Envelope_Normalizes_To_The_Loudest_Bucket()
    {
        // Quiet first half, loud second half.
        var pcm = new byte[16000];
        for (int i = 8000; i < pcm.Length; i += 2) { pcm[i] = 0x00; pcm[i + 1] = 0x40; } // 0x4000

        var levels = PcmEnvelope.Compute(pcm, 8);
        Assert.Equal(8, levels.Count);
        Assert.Equal(0, levels[0]);     // silence
        Assert.Equal(1, levels[^1]);    // the peak bucket defines full scale
    }

    [Fact]
    public void Envelope_Of_Silence_Stays_Flat_Not_Faked()
    {
        var levels = PcmEnvelope.Compute(new byte[3200], 8);
        Assert.All(levels, l => Assert.Equal(0, l));
    }
}
