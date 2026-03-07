using System.Collections.Concurrent;
using HexTeam.Messenger.Core.Voice;
using Plugin.Maui.Audio;

namespace MassangerMaximka;

/// <summary>
/// Manages real-time voice call lifecycle: overlapped recording in 200ms chunks,
/// WAV header stripping for transmission, and queued sequential playback.
/// </summary>
public sealed class VoiceCallManager : IDisposable
{
    private const int ChunkMs = 200;
    private const int PlaybackOverlapMs = 20;

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
    }

    private async Task PlaybackLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!_playbackQueue.TryDequeue(out var pcm))
            {
                await Task.Delay(10, ct);
                continue;
            }

            try
            {
                var wav = WavHelper.WrapWithHeader(pcm);
                using var stream = new MemoryStream(wav);
                var player = _audioManager.CreatePlayer(stream);
                player.Play();

                var durationMs = WavHelper.EstimateDurationMs(pcm.Length);
                var waitMs = Math.Max(10, durationMs - PlaybackOverlapMs);
                await Task.Delay(waitMs, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log?.Invoke($"Playback error: {ex.Message}");
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
