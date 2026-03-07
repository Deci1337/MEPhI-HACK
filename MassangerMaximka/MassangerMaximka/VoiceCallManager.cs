using System.Collections.Concurrent;
using HexTeam.Messenger.Core.Voice;
using Plugin.Maui.Audio;

namespace MassangerMaximka;

/// <summary>
/// Manages real-time voice call lifecycle: overlapped recording in chunks,
/// WAV header stripping for transmission, and batched sequential playback.
/// </summary>
public sealed class VoiceCallManager : IDisposable
{
    private const int ChunkMs = 200;
    private const int PlaybackBatchMs = 600;
    private const int PlaybackBatchMaxFrames = 5;

    private readonly UdpVoiceTransport _transport;
    private readonly IAudioManager _audioManager;
    private readonly string _tempDir;

    private CancellationTokenSource? _cts;
    private readonly ConcurrentQueue<byte[]> _playbackQueue = new();
    private volatile bool _active;

    public bool IsActive => _active;

    public event Action<string>? Log;

    public VoiceCallManager(UdpVoiceTransport transport, IAudioManager audioManager)
    {
        _transport = transport;
        _audioManager = audioManager;
        _tempDir = Path.Combine(FileSystem.Current.AppDataDirectory, "CallChunks");
        Directory.CreateDirectory(_tempDir);
    }

    public void Start(System.Net.IPEndPoint remoteEndPoint)
    {
        if (_active) return;
        _active = true;
        _cts = new CancellationTokenSource();

        _transport.Start(remoteEndPoint);
        _transport.FrameReceived += OnFrameReceived;

        var ct = _cts.Token;
        _ = Task.Run(() => RecordLoopAsync(ct), ct);
        _ = Task.Run(() => PlaybackLoopAsync(ct), ct);
    }

    public void Stop()
    {
        if (!_active) return;
        _active = false;

        _transport.FrameReceived -= OnFrameReceived;
        _transport.Stop();

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        while (_playbackQueue.TryDequeue(out _)) { }
        CleanTempFiles();
    }

    private async Task RecordLoopAsync(CancellationToken ct)
    {
        int sentCount = 0, silentCount = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var recorder = _audioManager.CreateRecorder();
                var options = new AudioRecorderOptions
                {
                    SampleRate = 16000,
                    Channels = ChannelType.Mono,
                    BitDepth = BitDepth.Pcm16bit,
                    Encoding = Plugin.Maui.Audio.Encoding.Wav,
                    ThrowIfNotSupported = false
                };
                await recorder.StartAsync(options);
                await Task.Delay(ChunkMs, ct);
                var source = await recorder.StopAsync();

                string? filePath = null;
                if (source is FileAudioSource fsa)
                    filePath = fsa.GetFilePath();
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) continue;

                var wavBytes = await File.ReadAllBytesAsync(filePath, ct);
                TryDelete(filePath);

                if (wavBytes.Length <= WavHelper.HeaderSize) continue;

                var pcm = WavHelper.StripHeader(wavBytes);

                var isSilent = pcm.Length > 0 && pcm.All(b => b == 0);
                if (isSilent) silentCount++;
                sentCount++;
                if (sentCount % 25 == 1)
                    Log?.Invoke($"Voice TX: sent={sentCount} silent={silentCount} pcm={pcm.Length}B");

                await _transport.SendFrameAsync(pcm);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log?.Invoke($"Record error: {ex.Message}");
                try { await Task.Delay(50, ct); } catch { break; }
            }
        }
    }

    private void OnFrameReceived(byte[] pcmData)
    {
        if (!_active || pcmData.Length == 0) return;
        _playbackQueue.Enqueue(pcmData);
        var count = Interlocked.Increment(ref _receivedCount);
        if (count % 25 == 1)
            Log?.Invoke($"Voice RX: received={count} queue={_playbackQueue.Count} pcm={pcmData.Length}B");
    }

    private int _receivedCount;

    /// <summary>
    /// Batched playback: accumulate several PCM frames into one WAV before playing.
    /// Reduces per-player creation overhead and eliminates inter-chunk clicks.
    /// </summary>
    private async Task PlaybackLoopAsync(CancellationToken ct)
    {
        int playedCount = 0;
        while (!ct.IsCancellationRequested)
        {
            if (_playbackQueue.IsEmpty)
            {
                await Task.Delay(15, ct);
                continue;
            }

            var batch = new List<byte[]>();
            int totalBytes = 0;
            while (batch.Count < PlaybackBatchMaxFrames && _playbackQueue.TryDequeue(out var pcm))
            {
                batch.Add(pcm);
                totalBytes += pcm.Length;
            }

            if (batch.Count == 0) continue;

            if (batch.Count < 2 && _playbackQueue.IsEmpty)
            {
                await Task.Delay(PlaybackBatchMs / 3, ct);
                while (batch.Count < PlaybackBatchMaxFrames && _playbackQueue.TryDequeue(out var extra))
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
                var stream = new MemoryStream(wav);
                player = _audioManager.CreatePlayer(stream);
                player.Play();

                var durationMs = WavHelper.EstimateDurationMs(merged.Length);
                var waitMs = Math.Max(20, durationMs - 10);
                await Task.Delay(waitMs, ct);

                playedCount += batch.Count;
                if (playedCount % 25 < batch.Count)
                    Log?.Invoke($"Voice PLAY: played={playedCount} frames={batch.Count} pcm={merged.Length}B dur={durationMs}ms");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log?.Invoke($"Playback error: {ex.Message}");
            }
            finally
            {
                try { player?.Dispose(); } catch { }
            }
        }
    }

    private void CleanTempFiles()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_tempDir, "c_*.wav"))
                TryDelete(f);
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public void Dispose() => Stop();
}
