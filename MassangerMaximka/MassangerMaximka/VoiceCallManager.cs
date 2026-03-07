using System.Collections.Concurrent;
using HexTeam.Messenger.Core.Voice;
using Plugin.Maui.Audio;

namespace MassangerMaximka;

/// <summary>
/// Walkie-talkie voice call: hold Talk to record, release to send.
/// Receives and plays back incoming frames continuously.
/// </summary>
public sealed class VoiceCallManager : IDisposable
{
    private const int PcmChunkSize = 6400; // 200ms at 16kHz mono 16-bit

    private readonly UdpVoiceTransport _transport;
    private readonly IAudioManager _audioManager;

    private CancellationTokenSource? _cts;
    private readonly ConcurrentQueue<byte[]> _playbackQueue = new();
    private volatile bool _active;
    private volatile bool _talking;
    private IAudioRecorder? _talkRecorder;
    private int _receivedCount;

    public bool IsActive => _active;
    public bool IsTalking => _talking;

    public event Action<string>? Log;

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

    /// <summary>Start playback loop only (channel mode where transport is already listening).</summary>
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
        try
        {
            _talkRecorder = _audioManager.CreateRecorder();
            try
            {
                await _talkRecorder.StartAsync(new AudioRecorderOptions
                {
                    SampleRate = 16000,
                    Channels = ChannelType.Mono,
                    BitDepth = BitDepth.Pcm16bit,
                    Encoding = Plugin.Maui.Audio.Encoding.Wav,
                    ThrowIfNotSupported = true
                });
            }
            catch
            {
                _talkRecorder = _audioManager.CreateRecorder();
                await _talkRecorder.StartAsync(new AudioRecorderOptions
                {
                    SampleRate = 44100,
                    Channels = ChannelType.Mono,
                    BitDepth = BitDepth.Pcm16bit,
                    Encoding = Plugin.Maui.Audio.Encoding.Wav,
                    ThrowIfNotSupported = false
                });
            }
            Log?.Invoke("PTT: recording...");
        }
        catch (Exception ex)
        {
            _talking = false;
            Log?.Invoke($"PTT record error: {ex.Message}");
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

            byte[]? wavBytes = null;

            if (source is FileAudioSource fsa)
            {
                var filePath = fsa.GetFilePath();
                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                {
                    wavBytes = await File.ReadAllBytesAsync(filePath);
                    try { File.Delete(filePath); } catch { }
                }
            }

            // Fallback: read via stream (works on Android when FileAudioSource path is unavailable)
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
                    }
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"PTT stream fallback error: {ex.Message}");
                }
            }

            if (wavBytes == null || wavBytes.Length <= WavHelper.HeaderSize) return;

            var info = WavHelper.ParseWav(wavBytes);
            var pcm = info.PcmData;
            if (pcm.Length == 0) return;

            Log?.Invoke($"PTT raw: rate={info.SampleRate} ch={info.ChannelCount} bits={info.BitsPerSample} pcm={pcm.Length}B");

            if (info.ChannelCount == 2 && info.BitsPerSample == 16)
                pcm = WavHelper.StereoToMono(pcm);

            if (info.SampleRate != WavHelper.SampleRate)
                pcm = WavHelper.Resample16BitMono(pcm, info.SampleRate, WavHelper.SampleRate);

            if (pcm.Length == 0) return;

            int sent = 0;
            for (int i = 0; i < pcm.Length; i += PcmChunkSize)
            {
                var len = Math.Min(PcmChunkSize, pcm.Length - i);
                var chunk = new byte[len];
                Buffer.BlockCopy(pcm, i, chunk, 0, len);
                await _transport.SendFrameAsync(chunk);
                sent++;
                if (sent % 10 == 0) await Task.Delay(1);
            }
            Log?.Invoke($"PTT: sent {sent} chunks, {pcm.Length}B, ~{WavHelper.EstimateDurationMs(pcm.Length)}ms");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"PTT send error: {ex.Message}");
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

    private void OnFrameReceived(byte[] pcmData)
    {
        if (!_active || pcmData.Length == 0) return;
        _playbackQueue.Enqueue(pcmData);
        var count = Interlocked.Increment(ref _receivedCount);
        if (count % 10 == 1)
            Log?.Invoke($"Voice RX: received={count} queue={_playbackQueue.Count} pcm={pcmData.Length}B");
    }

    private async Task PlaybackLoopAsync(CancellationToken ct)
    {
        int playedCount = 0;
        while (!ct.IsCancellationRequested)
        {
            if (_playbackQueue.IsEmpty)
            {
                await Task.Delay(20, ct);
                continue;
            }

            var batch = new List<byte[]>();
            int totalBytes = 0;
            while (batch.Count < 8 && _playbackQueue.TryDequeue(out var pcm))
            {
                batch.Add(pcm);
                totalBytes += pcm.Length;
            }
            if (batch.Count == 0) continue;

            if (batch.Count < 3)
            {
                await Task.Delay(80, ct);
                while (batch.Count < 8 && _playbackQueue.TryDequeue(out var extra))
                {
                    batch.Add(extra);
                    totalBytes += extra.Length;
                }
            }

            var merged = new byte[totalBytes];
            int offset = 0;
            foreach (var chunk in batch)
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
                await Task.Delay(Math.Max(20, durationMs - 20), ct);

                playedCount += batch.Count;
                if (playedCount % 10 < batch.Count)
                    Log?.Invoke($"Voice PLAY: played={playedCount} frames={batch.Count} pcm={merged.Length}B dur={durationMs}ms");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log?.Invoke($"Playback error: {ex.Message}"); }
            finally { try { player?.Dispose(); } catch { } }
        }
    }

    public void Dispose() => Stop();
}
