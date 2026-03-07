using System.Collections.ObjectModel;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using HexTeam.Messenger.Core.Discovery;
using HexTeam.Messenger.Core.FileTransfer;
using HexTeam.Messenger.Core.Models;
using HexTeam.Messenger.Core.Metrics;
using HexTeam.Messenger.Core.Transport;
using HexTeam.Messenger.Core.Voice;
using Plugin.Maui.Audio;

namespace MassangerMaximka
{
    public partial class MainPage : ContentPage
    {
        private UdpDiscoveryService? _discovery;
        private PeerConnectionService? _connections;
        private TcpChatTransport? _chat;
        private FileTransferService? _files;
        private UdpVoiceTransport? _voice;
        private MetricsService? _metrics;
        private IAudioManager? _audioManager;
        private IAudioRecorder? _audioRecorder;
        private IAudioPlayer? _voicePlayer;
        private MemoryStream? _voicePlaybackStream;
        private readonly HashSet<string> _autoConnectInFlight = [];
        private string? _localNodeId;
        private string? _selectedPeerNodeId;
        private string? _lastTransferId;
        private string? _lastReceivedVoicePath;
        private bool _isRecordingVoice;
        private string? _voiceRecordPath;
        private bool _isInCall;
        private bool _isCallingOut;
        private string? _callPeerNodeId;
        private IPAddress? _callPeerIp;
        private int _callPeerVoicePort;
        private string? _incomingCallFromNodeId;
        private IPAddress? _incomingCallerIp;
        private int _incomingCallerVoicePort;
        private VoiceCallManager? _voiceCallManager;

        private const string CallRequestPrefix = "\x1FCALL_REQUEST:";
        private const string CallAcceptPrefix = "\x1FCALL_ACCEPT:";
        private const string CallAcceptLegacy = "\x1FCALL_ACCEPT";
        private const string CallReject = "\x1FCALL_REJECT";
        private const string CallEnd = "\x1FCALL_END";
        private readonly ObservableCollection<string> _peers = [];
        private readonly List<SavedPeer> _savedPeers = [];
        private readonly ObservableCollection<ChatItem> _chatItems = [];
        private readonly Dictionary<string, System.Net.IPEndPoint> _peerEndpointMap = new();
        private int _techLogCount;
        private volatile bool _suppressTechLog;

        public MainPage()
        {
            InitializeComponent();
            PeersList.ItemsSource = _peers;
            ChatList.ItemsSource = _chatItems;
            LoadSavedPeers();
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            var services = MauiProgram.AppInstance?.Services;
            if (services == null) return;

            _discovery = services.GetService<UdpDiscoveryService>();
            _connections = services.GetService<PeerConnectionService>();
            _chat = services.GetService<TcpChatTransport>();
            _files = services.GetService<FileTransferService>();
            _voice = services.GetService<UdpVoiceTransport>();
            _metrics = services.GetService<MetricsService>();
            _audioManager = services.GetService<IAudioManager>();
            var config = services.GetService<NodeConfiguration>();
            _localNodeId = config?.NodeId;

            if (_discovery == null || _connections == null || _chat == null || _files == null)
            {
                StatusLabel.Text = "Services not found";
                return;
            }

            _discovery.Start();
            _connections.StartListening();
            _metrics?.Start();

            _discovery.PeerDiscovered += OnPeerDiscovered;
            _discovery.PeerLost += OnPeerLost;
            _connections.PeerConnected += OnPeerConnected;
            _connections.PeerDisconnected += OnPeerDisconnected;
            _connections.EnvelopeReceived += OnEnvelopeReceivedLog;
            _chat.MessageReceived += OnMessageReceived;
            _chat.DeliveryStatusChanged += OnDeliveryStatusChanged;
            _files.TransferProgressChanged += OnTransferProgressChanged;
            _files.FileReceived += OnFileReceived;
            if (_metrics != null) _metrics.MetricsUpdated += OnMetricsUpdated;

            StatusLabel.Text = $"TCP: {_connections.ListenPort} | NodeId: {config?.NodeId ?? "?"}";
            var otherPort = _connections.ListenPort == 45680 ? 45681 : 45680;
            PortHintLabel.Text = $"Auto-discovery enabled. Fallback connect: 127.0.0.1:{otherPort}";

            TechLog(LogCat.System, $"Node started: {config?.NodeId}");
            TechLog(LogCat.Network, $"TCP listening on port {_connections.ListenPort}");
            TechLog(LogCat.Discovery, $"UDP discovery on port {config?.DiscoveryPort}");
            TechLog(LogCat.Encryption, $"Envelope serialization: length-prefixed JSON over TCP");
            TechLog(LogCat.Encryption, $"File integrity: SHA256 chunk + full-file hash");
            TechLog(LogCat.Protocol, $"MaxHops={ProtocolConstants.MaxHops}, AckTimeout={ProtocolConstants.AckTimeout.TotalSeconds}s, Retry={ProtocolConstants.MaxRetryCount}, Hash={ProtocolConstants.HashAlgorithm}");
            VoiceStatusLabel.Text = "Voice: idle";

            RefreshPeersList();
        }

        protected override void OnDisappearing()
        {
            if (_isInCall || _isCallingOut)
            {
                _ = SendCallSignalAsync(_callPeerNodeId, CallEnd);
                if (_isInCall) EndCall();
                else ResetCallingState();
            }
            _voiceCallManager?.Dispose();
            _voiceCallManager = null;
            if (_discovery != null) { _discovery.PeerDiscovered -= OnPeerDiscovered; _discovery.PeerLost -= OnPeerLost; }
            if (_connections != null) { _connections.PeerConnected -= OnPeerConnected; _connections.PeerDisconnected -= OnPeerDisconnected; _connections.EnvelopeReceived -= OnEnvelopeReceivedLog; }
            if (_chat != null) { _chat.MessageReceived -= OnMessageReceived; _chat.DeliveryStatusChanged -= OnDeliveryStatusChanged; }
            if (_files != null) { _files.TransferProgressChanged -= OnTransferProgressChanged; _files.FileReceived -= OnFileReceived; }
            if (_metrics != null) _metrics.MetricsUpdated -= OnMetricsUpdated;
            DisposeVoicePlayback();
            base.OnDisappearing();
        }

        // --- Events ---

        private void OnPeerDiscovered(PeerInfo peer)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                RememberPeer(peer.DisplayName, peer.IpAddress, peer.Port);
                RefreshPeersList();
                StatusLabel.Text = $"TCP: {_connections?.ListenPort ?? 0} | Peers: {_peers.Count}";
                TechLog(LogCat.Discovery, $"Peer discovered: {peer.DisplayName} ({peer.NodeId}) at {peer.EndPoint}");
                _ = TryAutoConnectPeerAsync(peer);
            });
        }

        private void OnPeerLost(string nodeId)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                RefreshPeersList();
                _autoConnectInFlight.Remove(nodeId);
                TechLog(LogCat.Discovery, $"Peer lost: {nodeId}");
            });
        }

        private void OnPeerConnected(string nodeId)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var ep = _connections?.GetPeerEndPoint(nodeId);
                if (ep != null)
                    _peerEndpointMap[nodeId] = ep;

                RefreshPeersList();
                _autoConnectInFlight.Remove(nodeId);
                AppendChat($"[Connected] {nodeId}");
                TechLog(LogCat.Network, $"TCP connected: {nodeId}" + (ep != null ? $" remote={ep}" : ""));
                TechLog(LogCat.Protocol, $"Hello packet sent/received for {nodeId}");
            });
        }

        private void OnPeerDisconnected(string nodeId)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                RefreshPeersList();
                AppendChat($"[Disconnected] {nodeId}");
                TechLog(LogCat.Network, $"TCP disconnected: {nodeId}");
            });
        }

        private void OnMessageReceived(TransportChatMessage msg)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var text = msg.Text;

                if (text.StartsWith(CallRequestPrefix, StringComparison.Ordinal))
                {
                    var payload = text[CallRequestPrefix.Length..];
                    var colonIdx = payload.LastIndexOf(':');
                    var ipStr = colonIdx > 0 ? payload[..colonIdx] : payload;
                    var voicePort = colonIdx > 0 && int.TryParse(payload[(colonIdx + 1)..], out var vp) ? vp : 45679;
                    if (!_isInCall && !_isCallingOut && IPAddress.TryParse(ipStr, out var callerIp))
                    {
                        _incomingCallFromNodeId = msg.FromNodeId;
                        _incomingCallerIp = callerIp;
                        _incomingCallerVoicePort = voicePort;
                        ShowIncomingCallBanner(msg.FromNodeId);
                    }
                    else
                    {
                        _ = SendCallSignalAsync(msg.FromNodeId, CallReject);
                    }
                    TechLog(LogCat.Protocol, $"CALL_REQUEST from {msg.FromNodeId} voice_port={voicePort}");
                    return;
                }

                if (text.StartsWith(CallAcceptPrefix, StringComparison.Ordinal) || text == CallAcceptLegacy)
                {
                    if (_isCallingOut && _callPeerNodeId == msg.FromNodeId && _callPeerIp != null)
                    {
                        _isCallingOut = false;
                        if (text.StartsWith(CallAcceptPrefix, StringComparison.Ordinal))
                        {
                            var payload = text[CallAcceptPrefix.Length..];
                            var colonIdx = payload.LastIndexOf(':');
                            if (colonIdx > 0 && int.TryParse(payload[(colonIdx + 1)..], out var peerVp))
                                _callPeerVoicePort = peerVp;
                        }
                        TechLog(LogCat.Protocol, $"CALL_ACCEPT from {msg.FromNodeId} voice_port={_callPeerVoicePort}");
                        _ = StartCallAsync(_callPeerNodeId, _callPeerIp, _callPeerVoicePort);
                    }
                    return;
                }

                if (text == CallReject)
                {
                    if (_isCallingOut)
                    {
                        AppendChat($"[Call] {msg.FromNodeId} declined the call");
                        ResetCallingState();
                        TechLog(LogCat.Protocol, $"CALL_REJECT from {msg.FromNodeId}");
                    }
                    return;
                }

                if (text == CallEnd)
                {
                    HideIncomingCallBanner();
                    if (_isInCall) EndCall();
                    else if (_isCallingOut) ResetCallingState();
                    TechLog(LogCat.Protocol, $"CALL_END from {msg.FromNodeId}");
                    return;
                }

                AppendChat($"{msg.FromNodeId}: {text}");
                TechLog(LogCat.Protocol, $"RECV ChatPacket id={msg.MessageId} from={msg.FromNodeId}");
                TechLog(LogCat.Encryption, $"Payload deserialized: {text.Length} chars, ts={msg.TimestampUtc}");
            });
        }

        private void OnDeliveryStatusChanged(string messageId, DeliveryStatus status)
        {
            MainThread.BeginInvokeOnMainThread(() =>
                TechLog(LogCat.Protocol, $"Delivery {messageId}: {status}"));
        }

        private void OnTransferProgressChanged(TransportFileTransferInfo transfer)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var percent = transfer.Progress * 100;
                FileTransferLabel.Text = $"File: {transfer.FileName} {transfer.State} {percent:F0}% ({transfer.ConfirmedChunks}/{transfer.TotalChunks})";
                TechLog(LogCat.Transport, $"FILE {transfer.TransferId} {transfer.State} {transfer.ConfirmedChunks}/{transfer.TotalChunks}");
            });
        }

        private void OnFileReceived(string transferId, string savedPath)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (savedPath.StartsWith("ERROR:", StringComparison.Ordinal))
                {
                    AppendChat($"[File Error] {savedPath}");
                    FileTransferLabel.Text = $"File: save failed";
                    TechLog(LogCat.System, $"FILE SAVE FAILED id={transferId} {savedPath}");
                    return;
                }

                var fileName = Path.GetFileName(savedPath);
                FileTransferLabel.Text = $"File received: {fileName}";
                TechLog(LogCat.Protocol, $"FILE RECV complete id={transferId} path={savedPath}");

                if (fileName.StartsWith("voice_", StringComparison.OrdinalIgnoreCase) &&
                    fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    _lastReceivedVoicePath = savedPath;
                    AppendVoiceMessage(savedPath, fromPeer: true);
                }
                else
                {
                    AppendChat($"[File] {fileName}");
                }
            });
        }

        private Task OnEnvelopeReceivedLog(string fromPeer, TransportEnvelope env)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                TechLog(LogCat.Transport, $"RECV {env.Type} id={env.PacketId} from={fromPeer} hop={env.HopCount}/{env.MaxHops} payload={env.Payload.Length}B");
                if (env.Type == TransportPacketType.Ack)
                    TechLog(LogCat.Protocol, $"ACK received for packet from {fromPeer}");
            });
            return Task.CompletedTask;
        }

        private void OnMetricsUpdated(ConnectionMetrics m)
        {
            MainThread.BeginInvokeOnMainThread(() =>
                TechLog(LogCat.Metrics, $"RTT={m.RttMs:F1}ms loss={m.PacketLossPercent:F1}% throughput={m.ThroughputBytesPerSec / 1024:F1}KB/s retries={m.RetryCount} peer={m.PeerNodeId}"));
        }

        // --- Peers ---

        private void RefreshPeersList()
        {
            _peers.Clear();
            var seen = new HashSet<string>();
            var seenEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_discovery != null)
                foreach (var kv in _discovery.Peers)
                {
                    if (!seenEndpoints.Add(kv.Value.EndPoint)) continue;
                    seen.Add(kv.Key);
                    var isSaved = _savedPeers.Any(p => string.Equals(p.EndPoint, kv.Value.EndPoint, StringComparison.OrdinalIgnoreCase));
                    var isConnected = _connections?.IsConnected(kv.Key) == true;
                    _peers.Add(FormatPeerDisplay(kv.Value.DisplayName, kv.Key, kv.Value.EndPoint, isSaved, true, isConnected));
                }
            if (_connections != null)
                foreach (var nodeId in _connections.Connections.Keys)
                {
                    if (seen.Contains(nodeId)) continue;
                    seen.Add(nodeId);
                    _peers.Add(FormatPeerDisplay("Peer", nodeId, "", false, false, true));
                }
            foreach (var peer in _savedPeers.OrderBy(p => p.DisplayName).ThenBy(p => p.EndPoint))
            {
                if (seenEndpoints.Contains(peer.EndPoint)) continue;
                _peers.Add(FormatPeerDisplay(peer.DisplayName, null, peer.EndPoint, true, false, false));
            }
            PeersLabel.Text = $"Peers: {_peers.Count}";
        }

        private void OnPeerSelected(object? sender, SelectionChangedEventArgs e)
        {
            if (e.CurrentSelection.FirstOrDefault() is not string s) return;
            _selectedPeerNodeId = ExtractNodeId(s);
            var savedEndPoint = ExtractEndPoint(s);
            if (!string.IsNullOrWhiteSpace(savedEndPoint))
                ManualIpEntry.Text = savedEndPoint;
            SelectedPeerLabel.Text = $"Selected: {_selectedPeerNodeId ?? "(none)"}";
        }

        private async void OnPeerDoubleTapped(object? sender, TappedEventArgs e)
        {
            if (sender is not Label label)
                return;

            var endPointText = ExtractEndPoint(label.Text);
            if (string.IsNullOrWhiteSpace(endPointText))
                return;

            ManualIpEntry.Text = endPointText;
            if (!ParseEndpoint(endPointText, out var ip, out var port))
                return;

            if (_connections == null || _discovery == null)
                return;

            var nodeId = ExtractNodeId(label.Text) ?? $"manual-{ip}:{port}";
            RememberPeer($"Manual {ip}", ip.ToString(), port);
            _discovery.AddManualPeer(nodeId, $"Manual {ip}", new IPEndPoint(ip, port));
            TechLog(LogCat.Network, $"Double-tap connect -> {ip}:{port}");
            var ok = await _connections.ConnectToPeerAsync(nodeId, new IPEndPoint(ip, port));
            AppendChat(ok ? $"[Connected] {nodeId}" : $"[Failed] {nodeId}");
            RefreshPeersList();
        }

        // --- Actions ---

        private async void OnConnectClicked(object? sender, EventArgs e)
        {
            var input = ManualIpEntry.Text?.Trim();
            if (string.IsNullOrEmpty(input) || _connections == null || _discovery == null) return;
            if (!ParseEndpoint(input, out var ip, out var port))
            {
                AppendChat("[Error] Invalid IP:port");
                return;
            }
            RememberPeer($"Manual {ip}", ip.ToString(), port);
            var nodeId = $"manual-{ip}:{port}";
            _discovery.AddManualPeer(nodeId, $"Manual {ip}", new IPEndPoint(ip, port));
            TechLog(LogCat.Network, $"Connecting to {ip}:{port}...");
            var ok = await _connections.ConnectToPeerAsync(nodeId, new IPEndPoint(ip, port));
            AppendChat(ok ? $"[Connected] {nodeId}" : $"[Failed] {nodeId}");
            if (ok) TechLog(LogCat.Encryption, $"TCP session established with {nodeId}");
            RefreshPeersList();
        }

        private async void OnSendClicked(object? sender, EventArgs e)
        {
            var text = MessageEntry.Text?.Trim();
            if (string.IsNullOrEmpty(text) || _chat == null) return;
            var toNodeId = _selectedPeerNodeId;
            if (string.IsNullOrEmpty(toNodeId))
            {
                toNodeId = _peers.Count > 0 ? ExtractNodeId(_peers[0]) : null;
            }
            if (string.IsNullOrEmpty(toNodeId))
            {
                AppendChat("[Error] Select a peer first");
                return;
            }
            MessageEntry.Text = "";
            TechLog(LogCat.Transport, $"SEND ChatPacket to={toNodeId} len={text.Length}");
            TechLog(LogCat.Encryption, $"Serializing envelope: JSON + length-prefix (4B header)");
            var msg = await _chat.SendMessageAsync(toNodeId, text);
            AppendChat($"Me -> {toNodeId}: {text}");
            TechLog(LogCat.Protocol, $"SENT id={msg.MessageId} status={msg.Status}");
        }

        private void OnClearChatClicked(object? sender, EventArgs e) => _chatItems.Clear();

        private async void OnSendTestFileClicked(object? sender, EventArgs e)
        {
            if (_files == null)
            {
                AppendChat("[Error] File service unavailable");
                return;
            }

            var toNodeId = GetSelectedOrFirstPeerNodeId();
            if (string.IsNullOrEmpty(toNodeId))
            {
                AppendChat("[Error] Select a peer first");
                return;
            }

            try
            {
                var filePath = await PickOrCreateTestFileAsync();
                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length == 0)
                {
                    AppendChat("[Error] Empty files are not supported for demo send");
                    FileTransferLabel.Text = $"File: {fileInfo.Name} is empty";
                    return;
                }

                FileTransferLabel.Text = "File: sending...";
                TechLog(LogCat.Transport, $"FILE SEND start to={toNodeId} path={filePath}");
                var transfer = await _files.SendFileAsync(toNodeId, filePath);
                _lastTransferId = transfer.TransferId;
                AppendChat($"[File] Sent {Path.GetFileName(filePath)} to {toNodeId}");
                TechLog(LogCat.Protocol, $"FILE SENT id={transfer.TransferId} status={transfer.State}");
            }
            catch (Exception ex)
            {
                AppendChat($"[Error] File send failed: {ex.Message}");
                TechLog(LogCat.System, $"File send failed: {ex.Message}");
            }
        }

        private async void OnResumeFileClicked(object? sender, EventArgs e)
        {
            if (_files == null || string.IsNullOrEmpty(_lastTransferId))
            {
                AppendChat("[Error] No paused transfer to resume");
                return;
            }

            try
            {
                FileTransferLabel.Text = "File: resuming...";
                var transfer = await _files.ResumeTransferAsync(_lastTransferId);
                if (transfer == null)
                {
                    AppendChat("[Error] Resume failed");
                    return;
                }

                AppendChat($"[File] Resume requested for {_lastTransferId}");
                TechLog(LogCat.Protocol, $"FILE RESUME id={_lastTransferId} confirmed={transfer.ConfirmedChunks}");
            }
            catch (Exception ex)
            {
                AppendChat($"[Error] Resume failed: {ex.Message}");
                TechLog(LogCat.System, $"File resume failed: {ex.Message}");
            }
        }

        private async void OnRecordVoiceClicked(object? sender, EventArgs e)
        {
            if (_isRecordingVoice || _audioManager == null)
            {
                AppendChat(_audioManager == null ? "[Error] Audio not available" : "[Error] Already recording");
                return;
            }
            if (!await EnsureMicPermissionAsync())
            {
                AppendChat("[Error] Microphone permission denied");
                return;
            }
            try
            {
                var dir = Path.Combine(FileSystem.Current.AppDataDirectory, "VoiceMessages");
                Directory.CreateDirectory(dir);
                _voiceRecordPath = Path.Combine(dir, $"voice_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
                if (!await EnsureMicrophonePermissionAsync())
                {
                    AppendChat("[Error] Microphone permission denied");
                    VoiceStatusLabel.Text = "Voice: mic permission denied";
                    return;
                }

                _audioRecorder = _audioManager.CreateRecorder();
                if (!_audioRecorder.CanRecordAudio)
                {
                    AppendChat("[Error] Device cannot record audio");
                    VoiceStatusLabel.Text = "Voice: recorder unavailable";
                    return;
                }

                await _audioRecorder.StartAsync(CreateVoiceRecorderOptions());
                _isRecordingVoice = true;
                RecordBtn.BackgroundColor = Color.FromArgb("#F44336");
                VoiceStatusLabel.Text = "Voice: RECORDING...";
                TechLog(LogCat.System, $"Voice recording started -> temp file, final path {_voiceRecordPath}");
            }
            catch (Exception ex)
            {
                AppendChat($"[Error] Record failed: {ex.Message}");
                TechLog(LogCat.System, $"Voice record error: {ex.Message}");
            }
        }

        private async void OnStopSendVoiceClicked(object? sender, EventArgs e)
        {
            if (!_isRecordingVoice || _files == null || _audioRecorder == null)
            {
                AppendChat("[Error] Not recording or file service unavailable");
                return;
            }
            try
            {
                var source = await _audioRecorder.StopAsync();
                _isRecordingVoice = false;
                _audioRecorder = null;
                RecordBtn.BackgroundColor = Color.FromArgb("#C62828");

                var path = await PersistVoiceRecordingAsync(source);
                if (string.IsNullOrEmpty(path) || !File.Exists(path) || new FileInfo(path).Length <= 44)
                {
                    AppendChat("[Error] Recording produced no audio data");
                    VoiceStatusLabel.Text = "Voice: idle";
                    TechLog(LogCat.System, "Voice recording stop returned empty or invalid file");
                    return;
                }
                _voiceRecordPath = path;

                var fi = new FileInfo(path);
                var bytes = File.ReadAllBytes(path);
                var dataBytes = bytes.Skip(44).Take(20).ToArray();
                var isAllZero = dataBytes.Length > 0 && dataBytes.All(b => b == 0);
                TechLog(LogCat.System, $"Voice file: {bytes.Length}B, silence={isAllZero}");
                if (isAllZero)
                    TechLog(LogCat.System, "WARNING: recording is silent -- check emulator mic config (AVD -> Advanced -> Microphone)");

                VoiceStatusLabel.Text = $"Voice: sending {fi.Length / 1024}KB...";
                TechLog(LogCat.Transport, $"Voice message saved: {fi.Length} bytes");

                var toNodeId = GetSelectedOrFirstPeerNodeId();
                if (string.IsNullOrEmpty(toNodeId))
                {
                    AppendChat("[Error] Select a peer to send voice message");
                    VoiceStatusLabel.Text = "Voice: idle";
                    return;
                }

                var transfer = await _files.SendFileAsync(toNodeId, path);
                _lastTransferId = transfer.TransferId;
                AppendVoiceMessage(path, fromPeer: false);
                VoiceStatusLabel.Text = "Voice: sent";
                TechLog(LogCat.Protocol, $"VOICE SENT id={transfer.TransferId}");
            }
            catch (Exception ex)
            {
                _isRecordingVoice = false;
                _audioRecorder = null;
                RecordBtn.BackgroundColor = Color.FromArgb("#C62828");
                AppendChat($"[Error] Voice send failed: {ex.Message}");
                TechLog(LogCat.System, $"Voice send error: {ex.Message}");
                VoiceStatusLabel.Text = "Voice: idle";
            }
        }

        private void OnPlayVoiceClicked(object? sender, EventArgs e)
        {
            var path = _lastReceivedVoicePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                path = _voiceRecordPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                AppendChat("[Error] No voice message to play (record or receive one first)");
                return;
            }
            PlayVoiceFile(path);
        }

        private async void OnCallButtonClicked(object? sender, EventArgs e)
        {
            if (_isInCall || _isCallingOut)
            {
                _ = SendCallSignalAsync(_callPeerNodeId, CallEnd);
                if (_isInCall) EndCall();
                else ResetCallingState();
                return;
            }

            var nodeId = GetSelectedOrFirstPeerNodeId();
            if (string.IsNullOrEmpty(nodeId))
            {
                AppendChat("[Error] Select a peer to call");
                return;
            }

            var ip = ResolvePeerIpAddress(nodeId);
            if (ip == null)
            {
                AppendChat("[Error] Cannot resolve peer IP address");
                return;
            }

            _callPeerNodeId = nodeId;
            _callPeerIp = ip;
            var config = MauiProgram.AppInstance?.Services.GetService<NodeConfiguration>();
            _callPeerVoicePort = config?.VoicePort ?? 45679;
            _isCallingOut = true;

            var localIp = GetLocalIpAddress();
            var voicePort = _voice?.ListenPort ?? 45679;
            await SendCallSignalAsync(nodeId, $"{CallRequestPrefix}{localIp}:{voicePort}");

            CallBtn.Text = "Cancel";
            CallBtn.BackgroundColor = Color.FromArgb("#E65100");
            VoiceStatusLabel.Text = $"Calling {nodeId}...";
            AppendChat($"[Call] Calling {nodeId}...");
            TechLog(LogCat.Network, $"CALL_REQUEST sent to {nodeId} ip={localIp} voice_port={voicePort}");
        }

        private async Task StartCallAsync(string nodeId, IPAddress ip, int voicePort)
        {
            if (_voice == null || _audioManager == null)
            {
                AppendChat("[Error] Voice transport or audio not available");
                return;
            }

            if (!await EnsureMicPermissionAsync())
            {
                AppendChat("[Error] Microphone permission denied");
                return;
            }

            _isInCall = true;
            var remoteVoiceEndPoint = new System.Net.IPEndPoint(ip, voicePort);

            try
            {
                _voiceCallManager?.Dispose();
                _voiceCallManager = new VoiceCallManager(_voice, _audioManager);
                _voiceCallManager.Log += msg =>
                    MainThread.BeginInvokeOnMainThread(() => TechLog(LogCat.System, msg));
                _voiceCallManager.Start(remoteVoiceEndPoint);
            }
            catch (Exception ex)
            {
                AppendChat($"[Error] Voice transport failed to start: {ex.Message}");
                TechLog(LogCat.System, $"Voice call start error: {ex.Message}");
                _isInCall = false;
                return;
            }

            CallBtn.Text = "Hang Up";
            CallBtn.BackgroundColor = Color.FromArgb("#B71C1C");
            VoiceStatusLabel.Text = $"Call: {nodeId}";
            AppendChat($"[Call] Started call with {nodeId}");
            TechLog(LogCat.Network, $"Voice call started -> {ip}:{voicePort}");
        }

        private void EndCall()
        {
            _voiceCallManager?.Dispose();
            _voiceCallManager = null;

            _isInCall = false;
            _callPeerNodeId = null;
            _callPeerIp = null;
            CallBtn.Text = "Call";
            CallBtn.BackgroundColor = Color.FromArgb("#2E7D32");
            VoiceStatusLabel.Text = "Voice: idle";
            AppendChat("[Call] Call ended");
            TechLog(LogCat.Network, "Voice call ended");
        }

        private async void OnAcceptCallClicked(object? sender, EventArgs e)
        {
            var fromNodeId = _incomingCallFromNodeId;
            var callerIp = _incomingCallerIp;
            var callerVoicePort = _incomingCallerVoicePort;
            HideIncomingCallBanner();

            if (string.IsNullOrEmpty(fromNodeId) || callerIp == null) return;

            var localIp = GetLocalIpAddress();
            var localVoicePort = _voice?.ListenPort ?? 45679;
            await SendCallSignalAsync(fromNodeId, $"{CallAcceptPrefix}{localIp}:{localVoicePort}");
            _callPeerNodeId = fromNodeId;
            _callPeerIp = callerIp;
            _callPeerVoicePort = callerVoicePort;
            await StartCallAsync(fromNodeId, callerIp, callerVoicePort);
            TechLog(LogCat.Protocol, $"CALL_ACCEPT sent to {fromNodeId} local_voice={localIp}:{localVoicePort}");
        }

        private async void OnDeclineCallClicked(object? sender, EventArgs e)
        {
            var fromNodeId = _incomingCallFromNodeId;
            HideIncomingCallBanner();

            if (string.IsNullOrEmpty(fromNodeId)) return;

            await SendCallSignalAsync(fromNodeId, CallReject);
            AppendChat($"[Call] Declined call from {fromNodeId}");
            TechLog(LogCat.Protocol, $"CALL_REJECT sent to {fromNodeId}");
        }

        private void ShowIncomingCallBanner(string fromNodeId)
        {
            IncomingCallLabel.Text = $"Incoming call from {fromNodeId}";
            IncomingCallBanner.IsVisible = true;
        }

        private void HideIncomingCallBanner()
        {
            IncomingCallBanner.IsVisible = false;
            _incomingCallFromNodeId = null;
            _incomingCallerIp = null;
            _incomingCallerVoicePort = 0;
        }

        private void ResetCallingState()
        {
            _isCallingOut = false;
            _callPeerNodeId = null;
            _callPeerIp = null;
            CallBtn.Text = "Call";
            CallBtn.BackgroundColor = Color.FromArgb("#2E7D32");
            VoiceStatusLabel.Text = "Voice: idle";
        }

        private async Task SendCallSignalAsync(string? nodeId, string signal)
        {
            if (_chat == null || string.IsNullOrEmpty(nodeId)) return;
            try { await _chat.SendMessageAsync(nodeId, signal); }
            catch { }
        }

        private static string GetLocalIpAddress()
        {
            try
            {
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                return host.AddressList
                    .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    ?.ToString() ?? "127.0.0.1";
            }
            catch { return "127.0.0.1"; }
        }

        private static async Task<bool> EnsureMicPermissionAsync()
        {
            var status = await Permissions.CheckStatusAsync<Permissions.Microphone>();
            if (status != PermissionStatus.Granted)
                status = await Permissions.RequestAsync<Permissions.Microphone>();
            return status == PermissionStatus.Granted;
        }

        private void OnForgetSavedPeersClicked(object? sender, EventArgs e)
        {
            _savedPeers.Clear();
            SaveSavedPeers();
            RefreshPeersList();
            AppendChat("[Peers] Saved peers cleared");
            TechLog(LogCat.System, "Saved peers list cleared");
        }

        private void PlayVoiceFile(string path)
        {
            if (_audioManager == null)
            {
                _ = Launcher.OpenAsync(new OpenFileRequest("Voice", new ReadOnlyFile(path)));
                return;
            }
            try
            {
                DisposeVoicePlayback();
                _voicePlaybackStream = new MemoryStream(File.ReadAllBytes(path));
                _voicePlayer = _audioManager.CreatePlayer(_voicePlaybackStream);
                _voicePlayer.PlaybackEnded += OnVoicePlaybackEnded;
                _voicePlayer.Play();
                VoiceStatusLabel.Text = $"Voice: playing {Path.GetFileName(path)}";
                TechLog(LogCat.System, $"Playing voice: {path}");
            }
            catch (Exception ex)
            {
                TechLog(LogCat.System, $"Voice play error: {ex.Message}");
                _ = Launcher.OpenAsync(new OpenFileRequest("Voice", new ReadOnlyFile(path)));
            }
        }

        private async void OnCopyLogsClicked(object? sender, EventArgs e)
        {
            var sb = new StringBuilder();
            foreach (var child in TechLogStack.Children)
            {
                if (child is Label label)
                    sb.AppendLine(label.Text);
            }
            await Clipboard.SetTextAsync(sb.ToString());
            TechLog(LogCat.System, $"Logs copied to clipboard ({_techLogCount} entries)");
        }

        private void OnClearLogsClicked(object? sender, EventArgs e)
        {
            _suppressTechLog = true;
            try
            {
                var children = TechLogStack.Children.ToList();
                foreach (var child in children)
                    TechLogStack.Children.Remove(child);
                _techLogCount = 0;
                LogCountLabel.Text = "";
            }
            catch { }
            finally { _suppressTechLog = false; }
        }

        // --- Logging ---

        private void AppendChat(string line)
        {
            _chatItems.Add(new ChatItem { Text = $"{DateTime.Now:HH:mm:ss} {line}" });
            if (_chatItems.Count > 200) _chatItems.RemoveAt(0);
            ChatList.ScrollTo(_chatItems[^1], ScrollToPosition.End, animate: false);
        }

        private void AppendVoiceMessage(string filePath, bool fromPeer)
        {
            var name = Path.GetFileNameWithoutExtension(filePath);
            var prefix = fromPeer ? "Received" : "Sent";
            _chatItems.Add(new ChatItem
            {
                Text = $"{DateTime.Now:HH:mm:ss} {prefix}: {name}",
                VoicePath = filePath
            });
            ChatList.ScrollTo(_chatItems[^1], ScrollToPosition.End, animate: false);
        }

        private void OnVoiceItemPlayClicked(object? sender, EventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is string path)
                PlayVoiceFile(path);
        }

        private void TechLog(LogCat cat, string text)
        {
            if (_suppressTechLog) return;
            var color = cat switch
            {
                LogCat.Encryption => Color.FromArgb("#00C853"),   // bright green
                LogCat.Network    => Color.FromArgb("#29B6F6"),   // light blue
                LogCat.Transport  => Color.FromArgb("#FFB300"),   // amber
                LogCat.Protocol   => Color.FromArgb("#CE93D8"),   // light purple
                LogCat.Discovery  => Color.FromArgb("#4DD0E1"),   // cyan
                LogCat.Metrics    => Color.FromArgb("#FF8A65"),   // orange
                LogCat.System     => Color.FromArgb("#BDBDBD"),   // gray
                LogCat.Relay      => Color.FromArgb("#F06292"),   // pink
                _ => Colors.White
            };

            var prefix = cat switch
            {
                LogCat.Encryption => "[ENC]",
                LogCat.Network    => "[NET]",
                LogCat.Transport  => "[TRN]",
                LogCat.Protocol   => "[PRT]",
                LogCat.Discovery  => "[DSC]",
                LogCat.Metrics    => "[MET]",
                LogCat.System     => "[SYS]",
                LogCat.Relay      => "[RLY]",
                _ => "[???]"
            };

            var label = new Label
            {
                Text = $"{DateTime.Now:HH:mm:ss.fff} {prefix} {text}",
                TextColor = color,
                FontSize = 11,
                FontFamily = "OpenSansRegular",
                LineBreakMode = LineBreakMode.TailTruncation
            };

            try
            {
                TechLogStack.Children.Add(label);
                _techLogCount++;
                while (TechLogStack.Children.Count > 200)
                    TechLogStack.Children.RemoveAt(0);
                LogCountLabel.Text = $"({_techLogCount} entries)";
                _ = TechLogScrollView.ScrollToAsync(0, TechLogStack.Height, false);
            }
            catch { }
        }

        // --- Helpers ---

        private static bool ParseEndpoint(string input, out IPAddress ip, out int port)
        {
            ip = IPAddress.Loopback; port = 45680;
            var parts = input.Split(':');
            if (parts.Length != 2) return false;
            return IPAddress.TryParse(parts[0], out ip!) && int.TryParse(parts[1], out port);
        }

        private async Task<bool> EnsureMicrophonePermissionAsync()
        {
            var status = await Permissions.CheckStatusAsync<Permissions.Microphone>();
            if (status == PermissionStatus.Granted)
                return true;

            status = await Permissions.RequestAsync<Permissions.Microphone>();
            return status == PermissionStatus.Granted;
        }

        private static AudioRecorderOptions CreateVoiceRecorderOptions() => new()
        {
            SampleRate = 44100,
            Channels = ChannelType.Mono,
            BitDepth = BitDepth.Pcm16bit,
            Encoding = Plugin.Maui.Audio.Encoding.Wav,
            ThrowIfNotSupported = true
        };

        private async Task<string?> PersistVoiceRecordingAsync(IAudioSource source)
        {
            var preferredPath = _voiceRecordPath;
            if (!string.IsNullOrWhiteSpace(preferredPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(preferredPath)!);
                if (File.Exists(preferredPath) && new FileInfo(preferredPath).Length > 44)
                    return preferredPath;
            }

            if (source is FileAudioSource fileSource)
            {
                var sourcePath = fileSource.GetFilePath();
                if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
                {
                    if (string.IsNullOrWhiteSpace(preferredPath))
                        return sourcePath;

                    await using var input = File.OpenRead(sourcePath);
                    await using var output = File.Create(preferredPath);
                    await input.CopyToAsync(output);
                    return preferredPath;
                }
            }

            if (string.IsNullOrWhiteSpace(preferredPath))
                return null;

            await using var audioStream = source.GetAudioStream();
            if (audioStream == null)
                return null;

            await using var fileStream = File.Create(preferredPath);
            await audioStream.CopyToAsync(fileStream);
            return preferredPath;
        }

        private void OnVoicePlaybackEnded(object? sender, EventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (VoiceStatusLabel.Text.StartsWith("Voice: playing", StringComparison.Ordinal))
                    VoiceStatusLabel.Text = "Voice: idle";
            });
            DisposeVoicePlayback();
        }

        private void DisposeVoicePlayback()
        {
            if (_voicePlayer != null)
            {
                _voicePlayer.PlaybackEnded -= OnVoicePlaybackEnded;
                _voicePlayer.Dispose();
                _voicePlayer = null;
            }

            _voicePlaybackStream?.Dispose();
            _voicePlaybackStream = null;
        }

        private static string? ExtractNodeId(string display)
        {
            var s = display.IndexOf('(') + 1;
            var e = display.IndexOf(')');
            return s > 0 && e > s ? display[s..e] : null;
        }

        private static string? ExtractEndPoint(string display)
        {
            var s = display.LastIndexOf('[') + 1;
            var e = display.LastIndexOf(']');
            return s > 0 && e > s ? display[s..e] : null;
        }

        private static string FormatPeerDisplay(
            string displayName,
            string? nodeId,
            string endPoint,
            bool isSaved,
            bool isOnline,
            bool isConnected)
        {
            var parts = new List<string>();
            if (isSaved) parts.Add("saved");
            if (isOnline) parts.Add("online");
            if (isConnected) parts.Add("connected");
            var status = parts.Count == 0 ? "" : $"[{string.Join(" | ", parts)}] ";
            var node = string.IsNullOrWhiteSpace(nodeId) ? "" : $" ({nodeId})";
            var ep = string.IsNullOrWhiteSpace(endPoint) ? "" : $" [{endPoint}]";
            return $"{status}{displayName}{node}{ep}";
        }

        private IPAddress? ResolvePeerIpAddress(string nodeId)
        {
            if (_peerEndpointMap.TryGetValue(nodeId, out var cachedEp))
                return cachedEp.Address;

            if (_discovery?.Peers.TryGetValue(nodeId, out var peer) == true &&
                IPAddress.TryParse(peer.IpAddress, out var discovered))
                return discovered;

            var epText = ManualIpEntry.Text?.Trim();
            if (!string.IsNullOrEmpty(epText) && ParseEndpoint(epText, out var manualIp, out _))
                return manualIp;

            if (!string.IsNullOrEmpty(_selectedPeerNodeId))
            {
                var display = _peers.FirstOrDefault(p => p.Contains(_selectedPeerNodeId));
                if (display != null)
                {
                    var ep = ExtractEndPoint(display);
                    if (!string.IsNullOrEmpty(ep) && ParseEndpoint(ep, out var displayIp, out _))
                        return displayIp;
                }
            }

            return null;
        }

        private string? GetSelectedOrFirstPeerNodeId()
        {
            if (!string.IsNullOrEmpty(_selectedPeerNodeId))
                return _selectedPeerNodeId;

            return _peers.Count > 0 ? ExtractNodeId(_peers[0]) : null;
        }

        private async Task TryAutoConnectPeerAsync(PeerInfo peer)
        {
            if (_connections == null || string.IsNullOrEmpty(peer.NodeKey))
                return;

            if (_connections.IsConnected(peer.NodeKey))
                return;

            if (string.Equals(peer.NodeKey, _localNodeId, StringComparison.Ordinal))
                return;

            // Only one side initiates connect to avoid simultaneous outgoing connections.
            if (!ShouldInitiateAutoConnect(peer.NodeKey))
                return;

            if (!_autoConnectInFlight.Add(peer.NodeKey))
                return;

            try
            {
                var endPoint = new IPEndPoint(IPAddress.Parse(peer.IpAddress), peer.Port);
                TechLog(LogCat.Discovery, $"Auto-connect -> {peer.DisplayName} ({peer.EndPoint})");
                var connected = await _connections.ConnectToPeerAsync(peer.NodeKey, endPoint);
                if (connected)
                    TechLog(LogCat.Network, $"Auto-connected to {peer.NodeKey}");
                else
                    TechLog(LogCat.Network, $"Auto-connect failed for {peer.NodeKey}");
            }
            catch (Exception ex)
            {
                TechLog(LogCat.System, $"Auto-connect error for {peer.NodeKey}: {ex.Message}");
            }
            finally
            {
                _autoConnectInFlight.Remove(peer.NodeKey);
                RefreshPeersList();
            }
        }

        private bool ShouldInitiateAutoConnect(string remoteNodeId)
        {
            if (string.IsNullOrEmpty(_localNodeId))
                return true;

            return string.Compare(_localNodeId, remoteNodeId, StringComparison.Ordinal) < 0;
        }

        private void LoadSavedPeers()
        {
            try
            {
                if (!File.Exists(SavedPeersPath))
                    return;
                var json = File.ReadAllText(SavedPeersPath);
                var peers = JsonSerializer.Deserialize<List<SavedPeer>>(json);
                if (peers == null)
                    return;
                _savedPeers.Clear();
                _savedPeers.AddRange(peers
                    .Where(p => !string.IsNullOrWhiteSpace(p.IpAddress) && p.Port > 0)
                    .DistinctBy(p => p.EndPoint));
            }
            catch
            {
            }
        }

        private void SaveSavedPeers()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SavedPeersPath)!);
                var json = JsonSerializer.Serialize(_savedPeers.OrderBy(p => p.DisplayName).ThenBy(p => p.EndPoint));
                File.WriteAllText(SavedPeersPath, json);
            }
            catch
            {
            }
        }

        private void RememberPeer(string displayName, string ipAddress, int port)
        {
            if (string.IsNullOrWhiteSpace(ipAddress) || port <= 0)
                return;
            var existing = _savedPeers.FindIndex(p => p.IpAddress.Equals(ipAddress, StringComparison.OrdinalIgnoreCase) && p.Port == port);
            var peer = new SavedPeer(string.IsNullOrWhiteSpace(displayName) ? ipAddress : displayName, ipAddress, port);
            if (existing >= 0)
                _savedPeers[existing] = peer;
            else
                _savedPeers.Add(peer);
            SaveSavedPeers();
        }

        private static async Task<string> PickOrCreateTestFileAsync()
        {
            try
            {
                var result = await FilePicker.Default.PickAsync(new PickOptions
                {
                    PickerTitle = "Select file to send"
                });

                if (result != null && !string.IsNullOrWhiteSpace(result.FullPath))
                    return result.FullPath;
            }
            catch
            {
            }

            return await EnsureTestFileAsync();
        }

        private static async Task<string> EnsureTestFileAsync()
        {
            var dir = Path.Combine(FileSystem.Current.AppDataDirectory, "TestFiles");
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, "transport-test.bin");
            if (File.Exists(path) && new FileInfo(path).Length >= 4 * 1024 * 1024)
                return path;

            var buffer = new byte[4 * 1024 * 1024];
            Random.Shared.NextBytes(buffer);
            await File.WriteAllBytesAsync(path, buffer);
            return path;
        }

        private static string SavedPeersPath =>
            Path.Combine(FileSystem.Current.AppDataDirectory, "saved-peers.json");

        private sealed record SavedPeer(string DisplayName, string IpAddress, int Port)
        {
            public string EndPoint => $"{IpAddress}:{Port}";
        }

        private enum LogCat { System, Network, Transport, Protocol, Discovery, Encryption, Metrics, Relay }
    }
}
