using System.Collections.Concurrent;
using HexTeam.Messenger.Core.Voice;
using Plugin.Maui.Audio;

namespace MassangerMaximka;

/// <summary>
/// Walkie-talkie voice call: hold Talk to record, release to send.
/// Receives and plays back incoming frames as a single continuous WAV.
/// </summary>
public sealed class VoiceCallManager : IDisposable
{
    private const int PcmChunkSize = 1280; // 40ms at 16kHz mono 16-bit — fits in single UDP packet (no IP fragmentation)
    private const int AccumulateMs = 400;
    private static readonly int[] FallbackSampleRates = [16000, 44100, 48000];

    private readonly UdpVoiceTransport _transport;
    private readonly IAudioManager _audioManager;

    private CancellationTokenSource? _cts;
    private readonly ConcurrentQueue<byte[]> _playbackQueue = new();
    private volatile bool _active;
    private volatile bool _talking;
    private IAudioRecorder? _talkRecorder;
    private int _recordedSampleRate;
    private int _receivedCount;
    private volatile bool _firstFrameSent;
    private volatile bool _firstFrameReceived;

    public bool IsActive => _active;
    public bool IsTalking => _talking;

    public event Action<string>? Log;
    public event Action<string>? LogError;

    public VoiceCallManager(UdpVoiceTransport transport, IAudioManager audioManager)
    {
        _transport = transport;
        _audioManager = audioManager;
    }

    public void Start(System.Net.IPEndPoint remoteEndPoint)
    {
        if (_active) return;
        _active = true;
        _cts = new CancellationTokenSource();

        _transport.Start(remoteEndPoint);
        _transport.FrameReceived += OnFrameReceived;

        _ = Task.Run(() => PlaybackLoopAsync(_cts.Token), _cts.Token);
        Log?.Invoke("Walkie-talkie ready. Hold Talk to speak.");
    }

    public void StartChannelMode()
    {
        if (_active) return;
        _active = true;
        _cts = new CancellationTokenSource();
        _transport.FrameReceived += OnFrameReceived;
        _ = Task.Run(() => PlaybackLoopAsync(_cts.Token), _cts.Token);
        Log?.Invoke("Channel walkie-talkie ready. Hold Talk to speak.");
    }

    public async Task StartTalkingAsync()
    {
        if (!_active || _talking) return;
        _talking = true;
        _recordedSampleRate = 0;
        _firstFrameSent = false;
        _transport.ResetSendDiagnostics();

        try
        {
            foreach (var rate in FallbackSampleRates)
            {
                try
                {
                    _talkRecorder = _audioManager.CreateRecorder();
                    await _talkRecorder.StartAsync(new AudioRecorderOptions
                    {
                        SampleRate = rate,
                        Channels = ChannelType.Mono,
                        BitDepth = BitDepth.Pcm16bit,
                        Encoding = Plugin.Maui.Audio.Encoding.Wav,
                        ThrowIfNotSupported = rate != FallbackSampleRates[^1]
                    });
                    _recordedSampleRate = rate;
                    Log?.Invoke($"PTT: recording at {rate}Hz");
                    return;
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"PTT: {rate}Hz failed ({ex.Message}), trying next...");
                }
            }

            _talking = false;
            LogError?.Invoke("PTT: all sample rates failed");
        }
        catch (Exception ex)
        {
            _talking = false;
            LogError?.Invoke($"PTT record error: {ex.Message}");
        }
    }

    public async Task StopTalkingAsync()
    {
        if (!_talking || _talkRecorder == null) return;
        _talking = false;

        try
        {
            var source = await _talkRecorder.StopAsync();
            _talkRecorder = null;

            var wavBytes = await ReadRecordedAudioAsync(source);
            if (wavBytes == null || wavBytes.Length <= WavHelper.HeaderSize)
            {
                LogError?.Invoke("PTT: no audio data captured");
                return;
            }

            var pcm = NormalizePcm(wavBytes);
            if (pcm.Length == 0)
            {
                LogError?.Invoke("PTT: normalization produced 0 bytes");
                return;
            }

            await SendPcmChunksAsync(pcm);
        }
        catch (Exception ex)
        {
            LogError?.Invoke($"PTT send error: {ex.Message}");
        }
    }

    public void Stop()
    {
        if (!_active) return;
        _active = false;
        _talking = false;

        _transport.FrameReceived -= OnFrameReceived;
        _transport.Stop();

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        while (_playbackQueue.TryDequeue(out _)) { }
    }

    private async Task<byte[]?> ReadRecordedAudioAsync(IAudioSource source)
    {
        byte[]? wavBytes = null;

        if (source is FileAudioSource fsa)
        {
            try
            {
                var filePath = fsa.GetFilePath();
                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                {
                    wavBytes = await File.ReadAllBytesAsync(filePath);
                    Log?.Invoke($"PTT: read {wavBytes.Length}B from file");
                    try { File.Delete(filePath); } catch { }
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"PTT: file read failed: {ex.Message}");
            }
        }

        if (wavBytes == null || wavBytes.Length <= WavHelper.HeaderSize)
        {
            try
            {
                await using var stream = source.GetAudioStream();
                if (stream != null)
                {
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms);
                    wavBytes = ms.ToArray();
                    Log?.Invoke($"PTT: read {wavBytes.Length}B from stream");
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"PTT: stream read failed: {ex.Message}");
            }
        }

        return wavBytes;
    }

    private byte[] NormalizePcm(byte[] wavBytes)
    {
        var info = WavHelper.ParseWav(wavBytes);

        var rate = info.SampleRate;
        if (rate < 8000 || rate > 96000)
        {
            rate = _recordedSampleRate > 0 ? _recordedSampleRate : 16000;
            Log?.Invoke($"PTT: bad sample rate {info.SampleRate}, using {rate}");
        }

        var pcm = info.PcmData;
        if (pcm.Length == 0) return pcm;

        Log?.Invoke($"PTT: raw rate={rate} ch={info.ChannelCount} bits={info.BitsPerSample} pcm={pcm.Length}B");

        if (info.ChannelCount == 2 && info.BitsPerSample == 16)
            pcm = WavHelper.StereoToMono(pcm);

        if (rate != WavHelper.SampleRate)
            pcm = WavHelper.Resample16BitMono(pcm, rate, WavHelper.SampleRate);

        return pcm;
    }

    private async Task SendPcmChunksAsync(byte[] pcm)
    {
        int sent = 0;
        for (int i = 0; i < pcm.Length; i += PcmChunkSize)
        {
            var len = Math.Min(PcmChunkSize, pcm.Length - i);
            var chunk = new byte[len];
            Buffer.BlockCopy(pcm, i, chunk, 0, len);

            if (!_firstFrameSent)
            {
                _firstFrameSent = true;
                var remote = _transport.RemoteEndPoint?.ToString() ?? "none";
                var extraCount = _transport.ExtraEndPointCount;
                Log?.Invoke($"Voice TX: first frame remote={remote} extras={extraCount} chunk={chunk.Length}B total={pcm.Length}B");
            }

            await _transport.SendFrameAsync(chunk);
            sent++;

            if (sent % 8 == 0) await Task.Delay(3);
        }
        Log?.Invoke($"PTT: sent {sent} chunks, {pcm.Length}B, ~{WavHelper.EstimateDurationMs(pcm.Length)}ms, udp_out={_transport.Metrics.FramesSent} remote={_transport.RemoteEndPoint?.ToString() ?? "null"} extras={_transport.ExtraEndPointCount}");
    }

    private void OnFrameReceived(byte[] pcmData)
    {
        if (!_active || pcmData.Length == 0) return;

        if (!_firstFrameReceived)
        {
            _firstFrameReceived = true;
            Log?.Invoke($"Voice RX: first frame, {pcmData.Length}B");
        }

        _playbackQueue.Enqueue(pcmData);
        var count = Interlocked.Increment(ref _receivedCount);
        if (count % 10 == 1)
            Log?.Invoke($"Voice RX: total={count} queue={_playbackQueue.Count} pcm={pcmData.Length}B");
    }

    /// <summary>
    /// Accumulates all PCM chunks from a PTT burst into one WAV and plays it
    /// as a single audio clip, avoiding rapid create/dispose of IAudioPlayer.
    /// </summary>
    private async Task PlaybackLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_playbackQueue.IsEmpty)
            {
                await Task.Delay(20, ct);
                continue;
            }

            // Wait to accumulate the entire burst
            await Task.Delay(AccumulateMs, ct);

            int totalBytes = 0;
            var chunks = new List<byte[]>();
            while (_playbackQueue.TryDequeue(out var pcm))
            {
                chunks.Add(pcm);
                totalBytes += pcm.Length;
            }

            if (totalBytes == 0) continue;

            var merged = new byte[totalBytes];
            int offset = 0;
            foreach (var chunk in chunks)
            {
                Buffer.BlockCopy(chunk, 0, merged, offset, chunk.Length);
                offset += chunk.Length;
            }

            IAudioPlayer? player = null;
            try
            {
                var wav = WavHelper.WrapWithHeader(merged);
                player = _audioManager.CreatePlayer(new MemoryStream(wav));
                player.Play();

                var durationMs = WavHelper.EstimateDurationMs(merged.Length);
                Log?.Invoke($"Voice PLAY: {chunks.Count} chunks, {merged.Length}B, ~{durationMs}ms");

                // Wait for playback to finish + small buffer
                await Task.Delay(Math.Max(100, durationMs + 50), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { LogError?.Invoke($"Playback error: {ex.Message}"); }
            finally { try { player?.Dispose(); } catch { } }
        }
    }

    public void Dispose() => Stop();
}
