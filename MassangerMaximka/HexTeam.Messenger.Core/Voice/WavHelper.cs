namespace HexTeam.Messenger.Core.Voice;

/// <summary>
/// Minimal WAV header utilities for real-time voice streaming.
/// PCM 16-bit mono 16 kHz -- standard telephony quality.
/// </summary>
public static class WavHelper
{
    public const int HeaderSize = 44;
    public const int SampleRate = 16000;
    public const int BitsPerSample = 16;
    public const int Channels = 1;
    public const int BytesPerSample = BitsPerSample / 8;
    public const int ByteRate = SampleRate * Channels * BytesPerSample;
    public const int BlockAlign = Channels * BytesPerSample;

    public readonly record struct WavInfo(int SampleRate, int ChannelCount, int BitsPerSample, byte[] PcmData);

    /// <summary>
    /// Parse a WAV byte array: extract the actual sample rate, channel count,
    /// bits per sample and raw PCM data from the RIFF structure.
    /// </summary>
    public static WavInfo ParseWav(byte[] wavData)
    {
        int rate = SampleRate;
        int ch = Channels;
        int bits = BitsPerSample;
        byte[] pcm = [];

        if (wavData.Length > 12
            && wavData[0] == 'R' && wavData[1] == 'I'
            && wavData[2] == 'F' && wavData[3] == 'F')
        {
            int pos = 12;
            while (pos + 8 <= wavData.Length)
            {
                var chunkId = System.Text.Encoding.ASCII.GetString(wavData, pos, 4);
                int chunkSize = BitConverter.ToInt32(wavData, pos + 4);
                if (chunkId == "fmt " && chunkSize >= 16)
                {
                    ch = BitConverter.ToInt16(wavData, pos + 8 + 2);
                    rate = BitConverter.ToInt32(wavData, pos + 8 + 4);
                    bits = BitConverter.ToInt16(wavData, pos + 8 + 14);
                }
                else if (chunkId == "data")
                {
                    pcm = wavData[(pos + 8)..Math.Min(pos + 8 + chunkSize, wavData.Length)];
                }
                pos += 8 + chunkSize;
                if (chunkSize % 2 != 0) pos++;
            }
        }
        else if (wavData.Length > HeaderSize)
        {
            pcm = wavData[HeaderSize..];
        }
        return new WavInfo(rate, ch, bits, pcm);
    }

    public static byte[] StripHeader(byte[] wavData) => ParseWav(wavData).PcmData;

    /// <summary>
    /// Linear-interpolation resample for 16-bit mono PCM.
    /// Converts from any sample rate to the target rate.
    /// </summary>
    public static byte[] Resample16BitMono(byte[] pcm, int fromRate, int toRate)
    {
        if (fromRate == toRate || pcm.Length < 2) return pcm;

        int srcSamples = pcm.Length / 2;
        double ratio = (double)fromRate / toRate;
        int dstSamples = (int)(srcSamples / ratio);
        if (dstSamples < 1) return pcm;

        var result = new byte[dstSamples * 2];
        for (int i = 0; i < dstSamples; i++)
        {
            double srcPos = i * ratio;
            int idx0 = (int)srcPos;
            int idx1 = Math.Min(idx0 + 1, srcSamples - 1);
            double frac = srcPos - idx0;

            short s0 = BitConverter.ToInt16(pcm, idx0 * 2);
            short s1 = BitConverter.ToInt16(pcm, idx1 * 2);
            short val = (short)(s0 + (s1 - s0) * frac);
            BitConverter.TryWriteBytes(result.AsSpan(i * 2), val);
        }
        return result;
    }

    /// <summary>
    /// Down-mix stereo 16-bit PCM to mono by averaging L and R samples.
    /// </summary>
    public static byte[] StereoToMono(byte[] pcm)
    {
        int framePairs = pcm.Length / 4;
        var mono = new byte[framePairs * 2];
        for (int i = 0; i < framePairs; i++)
        {
            short left = BitConverter.ToInt16(pcm, i * 4);
            short right = BitConverter.ToInt16(pcm, i * 4 + 2);
            short avg = (short)((left + right) / 2);
            BitConverter.TryWriteBytes(mono.AsSpan(i * 2), avg);
        }
        return mono;
    }

    public static byte[] WrapWithHeader(byte[] pcmData)
    {
        var totalSize = HeaderSize + pcmData.Length;
        var wav = new byte[totalSize];

        // RIFF header
        wav[0] = (byte)'R'; wav[1] = (byte)'I'; wav[2] = (byte)'F'; wav[3] = (byte)'F';
        WriteInt32(wav, 4, totalSize - 8);
        wav[8] = (byte)'W'; wav[9] = (byte)'A'; wav[10] = (byte)'V'; wav[11] = (byte)'E';

        // fmt sub-chunk
        wav[12] = (byte)'f'; wav[13] = (byte)'m'; wav[14] = (byte)'t'; wav[15] = (byte)' ';
        WriteInt32(wav, 16, 16);            // sub-chunk size
        WriteInt16(wav, 20, 1);             // PCM format
        WriteInt16(wav, 22, Channels);
        WriteInt32(wav, 24, SampleRate);
        WriteInt32(wav, 28, ByteRate);
        WriteInt16(wav, 32, BlockAlign);
        WriteInt16(wav, 34, BitsPerSample);

        // data sub-chunk
        wav[36] = (byte)'d'; wav[37] = (byte)'a'; wav[38] = (byte)'t'; wav[39] = (byte)'a';
        WriteInt32(wav, 40, pcmData.Length);
        Buffer.BlockCopy(pcmData, 0, wav, HeaderSize, pcmData.Length);

        return wav;
    }

    /// <summary>
    /// Estimated playback duration of raw PCM data in milliseconds.
    /// </summary>
    public static int EstimateDurationMs(int pcmByteCount) =>
        pcmByteCount * 1000 / ByteRate;

    private static void WriteInt32(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
        buf[offset + 2] = (byte)(value >> 16);
        buf[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteInt16(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
    }
}
