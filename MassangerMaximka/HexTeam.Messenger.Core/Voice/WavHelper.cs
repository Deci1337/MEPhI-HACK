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

    public static byte[] StripHeader(byte[] wavData)
    {
        if (wavData.Length <= HeaderSize) return wavData;
        return wavData[HeaderSize..];
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
