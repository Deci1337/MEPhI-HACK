using System.Collections.ObjectModel;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using HexTeam.Messenger.Core.Discovery;
using HexTeam.Messenger.Core.FileTransfer;
using HexTeam.Messenger.Core.Models;
using HexTeam.Messenger.Core.Metrics;
using HexTeam.Messenger.Core.Transport;
using HexTeam.Messenger.Core.Voice;

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
        private CancellationTokenSource? _voiceSendCts;
        private readonly HashSet<string> _autoConnectInFlight = [];
        private string? _localNodeId;
        private string? _selectedPeerNodeId;
        private string? _lastTransferId;
        private readonly ObservableCollection<string> _peers = [];
        private readonly List<string> _chatLog = [];
        private int _techLogCount;
        private volatile bool _suppressTechLog;

        public MainPage()
        {
            InitializeComponent();
            PeersList.ItemsSource = _peers;
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
            if (_voice != null)
            {
                _voice.StartListening();
                _voice.FrameReceived += OnVoiceFrameReceived;
            }
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
            if (_voice != null) VoiceStatusLabel.Text = $"Voice: listening on {_voice.LocalPort}";

            RefreshPeersList();
        }

        protected override void OnDisappearing()
        {
            if (_discovery != null) { _discovery.PeerDiscovered -= OnPeerDiscovered; _discovery.PeerLost -= OnPeerLost; }
            if (_connections != null) { _connections.PeerConnected -= OnPeerConnected; _connections.PeerDisconnected -= OnPeerDisconnected; _connections.EnvelopeReceived -= OnEnvelopeReceivedLog; }
            if (_chat != null) { _chat.MessageReceived -= OnMessageReceived; _chat.DeliveryStatusChanged -= OnDeliveryStatusChanged; }
            if (_files != null) { _files.TransferProgressChanged -= OnTransferProgressChanged; _files.FileReceived -= OnFileReceived; }
            if (_voice != null) { _voice.FrameReceived -= OnVoiceFrameReceived; _voice.Stop(); }
            _voiceSendCts?.Cancel();
            if (_metrics != null) _metrics.MetricsUpdated -= OnMetricsUpdated;
            base.OnDisappearing();
        }

        // --- Events ---

        private void OnPeerDiscovered(PeerInfo peer)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
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
                RefreshPeersList();
                _autoConnectInFlight.Remove(nodeId);
                AppendChat($"[Connected] {nodeId}");
                TechLog(LogCat.Network, $"TCP connected: {nodeId}");
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
                AppendChat($"{msg.FromNodeId}: {msg.Text}");
                TechLog(LogCat.Protocol, $"RECV ChatPacket id={msg.MessageId} from={msg.FromNodeId}");
                TechLog(LogCat.Encryption, $"Payload deserialized: {msg.Text.Length} chars, ts={msg.TimestampUtc}");
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
                FileTransferLabel.Text = $"File received: {Path.GetFileName(savedPath)}";
                AppendChat($"[File Received] {Path.GetFileName(savedPath)}");
                TechLog(LogCat.Protocol, $"FILE RECV complete id={transferId} path={savedPath}");
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
            if (_discovery != null)
                foreach (var kv in _discovery.Peers)
                {
                    seen.Add(kv.Key);
                    var conn = _connections?.IsConnected(kv.Key) == true ? " [OK]" : "";
                    _peers.Add($"{kv.Value.DisplayName} ({kv.Key}){conn}");
                }
            if (_connections != null)
                foreach (var nodeId in _connections.Connections.Keys)
                {
                    if (seen.Contains(nodeId)) continue;
                    seen.Add(nodeId);
                    _peers.Add($"Peer ({nodeId}) [OK]");
                }
            PeersLabel.Text = $"Peers: {_peers.Count}";
        }

        private void OnPeerSelected(object? sender, SelectionChangedEventArgs e)
        {
            if (e.CurrentSelection.FirstOrDefault() is not string s) return;
            var start = s.IndexOf('(') + 1;
            var end = s.IndexOf(')');
            _selectedPeerNodeId = start > 0 && end > start ? s[start..end] : null;
            SelectedPeerLabel.Text = $"Selected: {_selectedPeerNodeId ?? "(none)"}";
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

        private void OnClearChatClicked(object? sender, EventArgs e)
        {
            _chatLog.Clear();
            ChatLogLabel.Text = "";
        }

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

        private void OnVoiceStartClicked(object? sender, EventArgs e)
        {
            if (_voice == null || _voiceSendCts != null)
            {
                AppendChat("[Error] Voice unavailable or already sending");
                return;
            }

            var toNodeId = GetSelectedOrFirstPeerNodeId();
            if (string.IsNullOrEmpty(toNodeId))
            {
                AppendChat("[Error] Select a peer first for voice");
                return;
            }

            if (!TryGetVoiceEndpoint(out var remoteEp))
            {
                AppendChat("[Error] Enter peer IP:port first for voice");
                return;
            }
            try
            {
                _voice.Start(remoteEp);
                VoiceStatusLabel.Text = $"Voice: sending -> {remoteEp}";
                TechLog(LogCat.Network, $"Voice started: local={_voice.LocalPort} remote={remoteEp}");

                _voiceSendCts = new CancellationTokenSource();
                _ = SendSyntheticVoiceAsync(_voiceSendCts.Token);
            }
            catch (Exception ex)
            {
                AppendChat($"[Error] Voice start failed: {ex.Message}");
                TechLog(LogCat.System, $"Voice start failed: {ex.Message}");
            }
        }

        private void OnVoiceStopClicked(object? sender, EventArgs e)
        {
            if (_voice == null || _voiceSendCts == null) return;

            _voiceSendCts?.Cancel();
            _voiceSendCts = null;

            var m = _voice.Metrics;
            VoiceStatusLabel.Text = $"Voice: listening on {_voice.LocalPort}";
            AppendChat($"[Voice] Stopped. Sent={m.FramesSent} Recv={m.FramesReceived} Loss={m.PacketLossPercent:F1}%");
            TechLog(LogCat.Metrics, $"Voice final: sent={m.FramesSent} recv={m.FramesReceived} avgLat={m.AvgLatencyMs:F1}ms jitter={m.JitterMs:F1}ms loss={m.PacketLossPercent:F1}%");
        }

        private void OnVoiceFrameReceived(byte[] data)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var m = _voice!.Metrics;
                VoiceStatusLabel.Text = $"Voice: recv={m.FramesReceived} lat={m.AvgLatencyMs:F0}ms jit={m.JitterMs:F0}ms";
            });
        }

        private async Task SendSyntheticVoiceAsync(CancellationToken ct)
        {
            var buffer = new byte[960];
            while (!ct.IsCancellationRequested && _voice != null && _voice.IsActive)
            {
                Random.Shared.NextBytes(buffer);
                await _voice.SendFrameAsync(buffer);
                await Task.Delay(20, ct);
            }
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
            _chatLog.Add($"{DateTime.Now:HH:mm:ss} {line}");
            while (_chatLog.Count > 50) _chatLog.RemoveAt(0);
            ChatLogLabel.Text = string.Join("\n", _chatLog);
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

        private static string? ExtractNodeId(string display)
        {
            var s = display.IndexOf('(') + 1;
            var e = display.IndexOf(')');
            return s > 0 && e > s ? display[s..e] : null;
        }

        private string? GetSelectedOrFirstPeerNodeId()
        {
            if (!string.IsNullOrEmpty(_selectedPeerNodeId))
                return _selectedPeerNodeId;

            return _peers.Count > 0 ? ExtractNodeId(_peers[0]) : null;
        }

        private bool TryGetVoiceEndpoint(out IPEndPoint endPoint)
        {
            if (ParseEndpoint(ManualIpEntry.Text?.Trim() ?? "", out var ip, out var tcpPort))
            {
                endPoint = new IPEndPoint(ip, tcpPort + 100);
                return true;
            }

            if (_connections != null)
            {
                var otherTcpPort = _connections.ListenPort == 45680 ? 45681 : 45680;
                endPoint = new IPEndPoint(IPAddress.Loopback, otherTcpPort + 100);
                return true;
            }

            endPoint = new IPEndPoint(IPAddress.Loopback, 45780);
            return false;
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

        private enum LogCat { System, Network, Transport, Protocol, Discovery, Encryption, Metrics, Relay }
    }
}
