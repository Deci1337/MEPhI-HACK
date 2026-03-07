using System.Collections.ObjectModel;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using HexTeam.Messenger.Core.Discovery;
using HexTeam.Messenger.Core.FileTransfer;
using HexTeam.Messenger.Core.Models;
using HexTeam.Messenger.Core.Metrics;
using HexTeam.Messenger.Core.Services;
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
        private ChannelService? _channelService;
        private ObservableCollection<ChatItem>? _channelChat;
        private string? _activeChannelId;
        private List<string> _channelMembers = [];
        private string? _activeChannelName;

        private const string CallRequestPrefix = "\x1FCALL_REQUEST:";
        private const string CallAcceptPrefix = "\x1FCALL_ACCEPT:";
        private const string CallAcceptLegacy = "\x1FCALL_ACCEPT";
        private const string CallReject = "\x1FCALL_REJECT";
        private const string CallEnd = "\x1FCALL_END";
        private const string PttStartSignal = "\x1FPTT_START";
        private const string PttEndSignal = "\x1FPTT_END";
        private readonly ObservableCollection<string> _peers = [];
        private readonly List<SavedPeer> _savedPeers = [];
        private ObservableCollection<ChatItem> _chatItems = [];
        private readonly Dictionary<string, ObservableCollection<ChatItem>> _peerChats = new();
        private string? _activeChatPeer;
        private readonly Dictionary<string, ChatItem> _pendingItems = new();
        private readonly Dictionary<string, System.Net.IPEndPoint> _peerEndpointMap = new();
        private readonly Dictionary<string, int> _peerVoicePortMap = new();
        private readonly HashSet<string> _unreadPeers = new();
        private int _techLogCount;
        private volatile bool _suppressTechLog;
        private bool _isNarrowLayout;
        private bool _showingChatOnMobile;

        public MainPage()
        {
            InitializeComponent();
            PeersList.ItemsSource = _peers;
            ChatList.ItemsSource = _chatItems;
            LoadSavedPeers();
            SizeChanged += (_, _) => ApplyResponsiveLayout(Width);
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
            _chat.ImageReceived += OnImageReceived;
            _chat.DeliveryStatusChanged += OnDeliveryStatusChanged;
            _files.TransferProgressChanged += OnTransferProgressChanged;
            _files.FileReceived += OnFileReceived;
            if (_metrics != null) _metrics.MetricsUpdated += OnMetricsUpdated;
            _channelService = services.GetService<ChannelService>();
            if (_channelService != null)
            {
                _channelService.InviteReceived += OnChannelInviteReceived;
                _channelService.MembersUpdated += OnChannelMembersUpdated;
            }

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
            ApplyResponsiveLayout(Width);
        }

        protected override void OnDisappearing()
        {
            if (_channelService?.ActiveChannelId != null)
                _ = _channelService.LeaveChannel();
            if (_channelService != null)
            {
                _channelService.InviteReceived -= OnChannelInviteReceived;
                _channelService.MembersUpdated -= OnChannelMembersUpdated;
            }

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
            if (_chat != null) { _chat.MessageReceived -= OnMessageReceived; _chat.ImageReceived -= OnImageReceived; _chat.DeliveryStatusChanged -= OnDeliveryStatusChanged; }
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
                {
                    _peerEndpointMap[nodeId] = ep;
                    RememberPeer(ResolveDisplayName(nodeId), ep.Address.ToString(), ep.Port);
                }

                _autoConnectInFlight.Remove(nodeId);
                var name = ResolveDisplayName(nodeId);
                AppendChat($"[Connected] {name}", toPeer: nodeId);

                // Auto-open chat only if the user has no active conversation yet.
                // If already in a chat, just mark [NEW] so the current chat is not interrupted.
                if (_activeChatPeer == null && _activeChannelId == null)
                    SwitchChat(nodeId);
                else
                {
                    _unreadPeers.Add(nodeId);
                    RefreshPeersList();
                }

                TechLog(LogCat.Network, $"TCP connected: {name} ({nodeId})" + (ep != null ? $" remote={ep}" : ""));
                TechLog(LogCat.Protocol, $"Hello handshake complete for {nodeId}");
            });
        }

        private string ResolveDisplayName(string nodeId)
        {
            if (_discovery?.Peers.TryGetValue(nodeId, out var peer) == true
                && !string.IsNullOrWhiteSpace(peer.DisplayName))
                return peer.DisplayName;
            return nodeId.Length > 8 ? nodeId[..8] : nodeId;
        }

        private ObservableCollection<ChatItem> GetOrCreatePeerChat(string nodeId)
        {
            if (!_peerChats.TryGetValue(nodeId, out var chat))
            {
                chat = [];
                _peerChats[nodeId] = chat;
            }
            return chat;
        }

        private bool _switchingChat;
        private void SwitchChat(string nodeId)
        {
            if (_switchingChat || nodeId == _activeChatPeer) return;
            _switchingChat = true;
            try
            {
                _activeChatPeer = nodeId;
                _activeChannelId = null;
                _chatItems = GetOrCreatePeerChat(nodeId);
                ChatList.ItemsSource = _chatItems;
                ChannelBar.IsVisible = false;
                SelectedPeerLabel.Text = ResolveDisplayName(nodeId);
                if (_unreadPeers.Remove(nodeId))
                    RefreshPeersList();
                if (_chatItems.Count > 0)
                    ChatList.ScrollTo(_chatItems[^1], ScrollToPosition.End, animate: false);

                // On mobile: switch to chat view
                if (_isNarrowLayout)
                {
                    _showingChatOnMobile = true;
                    ApplyResponsiveLayout(Width);
                }
            }
            finally { _switchingChat = false; }
        }

        private void OnPeerDisconnected(string nodeId)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var name = ResolveDisplayName(nodeId);
                AppendChat($"[Disconnected] {name}", toPeer: nodeId);
                AppendChat("[!] Messages cannot be delivered until reconnected.", toPeer: nodeId);
                if (_channelMembers.Contains(nodeId))
                {
                    _ = _channelService!.HandleMemberLeave(nodeId);
                    AppendToChannelChat($"[Channel] {name} disconnected");
                }
                RefreshPeersList();
                TechLog(LogCat.Network, $"TCP disconnected: {name} ({nodeId})");
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
                    var voicePort = ParseVoicePortPayload(payload);
                    var callerIp = ResolveConnectedPeerAddress(msg.FromNodeId);
                    _peerVoicePortMap[msg.FromNodeId] = voicePort;
                    if (_activeChannelId != null && _channelMembers.Contains(msg.FromNodeId))
                    {
                        if (callerIp != null)
                        {
                            SetChannelVoiceEndPoints();
                            if (!_isInCall) _ = StartChannelCallAsync();
                        }
                    }
                    else if (!_isInCall && !_isCallingOut && callerIp != null)
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
                    if (_isCallingOut && _callPeerIp != null)
                    {
                        _isCallingOut = false;
                        if (text.StartsWith(CallAcceptPrefix, StringComparison.Ordinal))
                            _callPeerVoicePort = ParseVoicePortPayload(text[CallAcceptPrefix.Length..]);

                        _callPeerIp = ResolveConnectedPeerAddress(msg.FromNodeId) ?? _callPeerIp;
                        _peerVoicePortMap[msg.FromNodeId] = _callPeerVoicePort;
                        if (_callPeerNodeId == null) _callPeerNodeId = msg.FromNodeId;
                        TechLog(LogCat.Protocol, $"CALL_ACCEPT from {msg.FromNodeId} ip={_callPeerIp} voice_port={_callPeerVoicePort}");
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

                if (text == PttStartSignal)
                {
                    var peerName = ResolveDisplayName(msg.FromNodeId);
                    if (_activeChannelId != null)
                    {
                        ChannelPttLabel.Text = $"{peerName} is talking...";
                        ChannelPttLabel.IsVisible = true;
                    }
                    else
                    {
                        VoiceStatusLabel.Text = $"{peerName} is talking...";
                    }
                    TechLog(LogCat.Protocol, $"PTT_START from {msg.FromNodeId}");
                    return;
                }

                if (text == PttEndSignal)
                {
                    if (_activeChannelId != null)
                    {
                        ChannelPttLabel.IsVisible = false;
                        ChannelPttLabel.Text = "";
                    }
                    else
                    {
                        VoiceStatusLabel.Text = _isInCall ? "Hold Talk to speak" : "Voice: idle";
                    }
                    TechLog(LogCat.Protocol, $"PTT_END from {msg.FromNodeId}");
                    return;
                }

                var senderName = ResolveDisplayName(msg.FromNodeId);
                if (_activeChannelId != null && _channelMembers.Contains(msg.FromNodeId))
                {
                    AppendToChannelChat($"{senderName}: {text}");
                }
                else
                {
                    // Always store into correct per-peer chat regardless of active chat.
                    AppendChat($"{senderName}: {text}", toPeer: msg.FromNodeId);
                    if (_activeChatPeer != msg.FromNodeId)
                    {
                        _unreadPeers.Add(msg.FromNodeId);
                        RefreshPeersList();
                    }
                }
                TechLog(LogCat.Protocol, $"RECV ChatPacket id={msg.MessageId} from={msg.FromNodeId}");
                TechLog(LogCat.Encryption, $"Payload deserialized: {text.Length} chars, ts={msg.TimestampUtc}");
            });
        }

        private void OnImageReceived(TransportImageMessage msg)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var name = ResolveDisplayName(msg.FromNodeId);
                if (_activeChannelId != null && _channelMembers.Contains(msg.FromNodeId))
                    AppendToChannelChat($"{name}: [Photo] {msg.FileName}");
                else
                {
                    AppendChat($"{name}: [Photo] {msg.FileName}", toPeer: msg.FromNodeId, imageBytes: msg.Data);
                    if (_activeChatPeer != msg.FromNodeId && _activeChannelId == null)
                    {
                        _unreadPeers.Add(msg.FromNodeId);
                        RefreshPeersList();
                    }
                }
                TechLog(LogCat.Transport, $"RECV image from={msg.FromNodeId} file={msg.FileName} bytes={msg.Data.Length}");
            });
        }

        private void OnDeliveryStatusChanged(string messageId, DeliveryStatus status)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_pendingItems.TryGetValue(messageId, out var item))
                    item.Status = status.ToString();
                TechLog(LogCat.Protocol, $"Delivery {messageId}: {status}");
            });
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
                var integrityOk = _files?.ActiveTransfers.TryGetValue(transferId, out var tf) == true
                    && tf?.State == FileTransferState.Completed;
                var integrityTag = integrityOk ? " SHA256 OK" : " SHA256 FAIL";
                FileTransferLabel.Text = $"File received: {fileName}{integrityTag}";
                TechLog(LogCat.Protocol, $"FILE RECV complete id={transferId} integrity={integrityOk} path={savedPath}");

                if (fileName.StartsWith("voice_", StringComparison.OrdinalIgnoreCase) &&
                    fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    _lastReceivedVoicePath = savedPath;
                    AppendVoiceMessage(savedPath, fromPeer: true);
                }
                else
                {
                    AppendChat($"[File] {fileName}{integrityTag}");
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
            {
                MetricRttLabel.Text = $"{m.RttMs:F0}ms";
                MetricLossLabel.Text = $"{m.PacketLossPercent:F1}%";
                MetricRetriesLabel.Text = m.RetryCount.ToString();
                var quality = _metrics?.GetQuality(m.PeerNodeId) ?? ConnectionQuality.Unknown;
                MetricQualityLabel.Text = quality.ToString();
                MetricQualityLabel.TextColor = quality switch
                {
                    ConnectionQuality.Excellent => Color.FromArgb("#4CAF50"),
                    ConnectionQuality.Good      => Color.FromArgb("#8BC34A"),
                    ConnectionQuality.Fair      => Color.FromArgb("#FFC107"),
                    ConnectionQuality.Poor      => Color.FromArgb("#F44336"),
                    _                           => Color.FromArgb("#7A7570")
                };
                TechLog(LogCat.Metrics, $"RTT={m.RttMs:F1}ms loss={m.PacketLossPercent:F1}% throughput={m.ThroughputBytesPerSec / 1024:F1}KB/s retries={m.RetryCount} peer={m.PeerNodeId}");
            });
        }

        // --- Channel ---

        private async void OnCreateChannelClicked(object? sender, EventArgs e)
        {
            if (_channelService == null) return;
            var name = await DisplayPromptAsync("New Channel", "Channel name:", initialValue: "Group");
            if (string.IsNullOrWhiteSpace(name)) return;
            var packet = _channelService.CreateChannel(name);
            _activeChannelName = packet.ChannelName;
            _channelMembers = packet.MemberNodeIds;
            SwitchToChannel();
            TechLog(LogCat.Protocol, $"Channel created: {_channelService.ActiveChannelId} '{name}'");
            await InvitePeersToChannelAsync();
        }

        private async Task InvitePeersToChannelAsync()
        {
            if (_channelService == null) return;
            var connected = _connections?.Connections.Keys
                .Where(id => !_channelMembers.Contains(id))
                .ToList() ?? [];
            if (connected.Count == 0)
            {
                AppendToChannelChat("[Channel] No new peers to invite.");
                return;
            }

            bool inviting = true;
            while (inviting && connected.Count > 0)
            {
                var names = connected.Select(id => $"{ResolveDisplayName(id)} ({id})").ToArray();
                var picked = await DisplayActionSheet("Invite to channel", "Done", null, names);
                if (string.IsNullOrEmpty(picked) || picked == "Done") break;

                var parenIdx = picked.LastIndexOf('(');
                var nodeId = parenIdx > 0 ? picked[(parenIdx + 1)..].TrimEnd(')') : null;
                if (string.IsNullOrEmpty(nodeId)) continue;

                await _channelService.SendInvite(nodeId);
                AppendToChannelChat($"[Channel] Invite sent to {ResolveDisplayName(nodeId)}");
                TechLog(LogCat.Protocol, $"Channel invite sent to {nodeId}");
                connected.Remove(nodeId);
            }
        }

        private void OnChannelInviteReceived(ChannelPacket invite)
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                var fromName = ResolveDisplayName(invite.FromNodeId);
                var accept = await DisplayAlert(
                    "Channel Invite",
                    $"{fromName} invites you to channel '{invite.ChannelName}'",
                    "Join", "Decline");
                if (accept)
                {
                    await _channelService!.JoinChannel(invite.ChannelId, invite.FromNodeId);
                    _channelMembers = invite.MemberNodeIds;
                    _activeChannelName = invite.ChannelName;
                    SwitchToChannel();
                    AppendToChannelChat($"[Channel] Joined '{invite.ChannelName}' created by {fromName}");
                    TechLog(LogCat.Protocol, $"Joined channel {invite.ChannelId}");
                    SetChannelVoiceEndPoints();
                }
                else
                {
                    TechLog(LogCat.Protocol, $"Declined channel invite from {invite.FromNodeId}");
                }
            });
        }

        private void OnChannelMembersUpdated(ChannelPacket packet)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _channelMembers = packet.MemberNodeIds;
                if (!string.IsNullOrEmpty(packet.ChannelName)) _activeChannelName = packet.ChannelName;
                ChannelInfoLabel.Text = $"Channel: {packet.ChannelId} | Members: {packet.MemberNodeIds.Count}";
                SetChannelVoiceEndPoints();
                RefreshPeersList();
                TechLog(LogCat.Protocol, $"Channel members updated: {packet.MemberNodeIds.Count}");
            });
        }

        private void SwitchToChannel()
        {
            if (_channelChat == null) _channelChat = [];
            _activeChannelId = _channelService?.ActiveChannelId;
            _activeChatPeer = null;
            _chatItems = _channelChat;
            ChatList.ItemsSource = _channelChat;
            SelectedPeerLabel.Text = $"#{_activeChannelName ?? "channel"}";
            ChannelBar.IsVisible = true;
            ChannelInfoLabel.Text = $"Channel: {_channelService?.ActiveChannelId} | Members: {_channelMembers.Count}";
            RefreshPeersList();

            // On mobile: switch to chat view
            if (_isNarrowLayout)
            {
                _showingChatOnMobile = true;
                ApplyResponsiveLayout(Width);
            }
        }

        private void AppendToChannelChat(string text)
        {
            if (_channelChat == null) _channelChat = [];
            var item = new ChatItem { Text = text };
            _channelChat.Add(item);
            if (_activeChannelId != null)
                ChatList.ScrollTo(item, ScrollToPosition.End, animate: false);
        }

        private async void OnInviteToChannelClicked(object? sender, EventArgs e)
        {
            if (_channelService == null || _activeChannelId == null) return;
            await InvitePeersToChannelAsync();
        }

        private async void OnLeaveChannelClicked(object? sender, EventArgs e)
        {
            if (_channelService == null) return;
            if (_isInCall) EndCall();
            await _channelService.LeaveChannel();
            _activeChannelId = null;
            _channelChat = null;
            _channelMembers = [];
            _activeChannelName = null;
            _activeChatPeer = null;
            ChannelBar.IsVisible = false;
            ChannelPttLabel.IsVisible = false;
            _chatItems = [];
            ChatList.ItemsSource = _chatItems;
            SelectedPeerLabel.Text = "Select a peer";
            _voice?.ClearExtraEndPoints();
            RefreshPeersList();
            TechLog(LogCat.Protocol, "Left channel");
        }

        private void SetChannelVoiceEndPoints()
        {
            if (_voice == null) return;
            var extras = new List<System.Net.IPEndPoint>();
            foreach (var m in _channelMembers.Where(m => m != _localNodeId))
            {
                if (_peerEndpointMap.TryGetValue(m, out var tcpEp))
                {
                    var voicePort = _peerVoicePortMap.TryGetValue(m, out var vp) ? vp : _voice.ListenPort;
                    extras.Add(new System.Net.IPEndPoint(tcpEp.Address, voicePort));
                }
            }
            _voice.SetExtraEndPoints(extras);
        }

        // --- Peers ---

        private void RefreshPeersList()
        {
            _peers.Clear();
            if (_activeChannelId != null)
                _peers.Add($"#CH {_activeChannelName ?? "channel"} | {_channelMembers.Count} members");
            var seen = new HashSet<string>();
            var seenEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_connections != null)
                foreach (var nodeId in _connections.Connections.Keys)
                {
                    if (seen.Contains(nodeId)) continue;
                    seen.Add(nodeId);
                    var connEp = _connections.GetPeerEndPoint(nodeId);
                    var connEpStr = connEp != null ? $"{connEp.Address}:{connEp.Port}" : "";
                    if (!string.IsNullOrEmpty(connEpStr) && !seenEndpoints.Add(connEpStr)) continue;
                    var name = ResolveDisplayName(nodeId);
                    var isSaved = !string.IsNullOrWhiteSpace(connEpStr)
                        && _savedPeers.Any(p => string.Equals(p.EndPoint, connEpStr, StringComparison.OrdinalIgnoreCase));
                    _peers.Add(FormatPeerDisplay(name, nodeId, connEpStr, isSaved, false, true));
                }
            if (_discovery != null)
                foreach (var kv in _discovery.Peers)
                {
                    if (!seenEndpoints.Add(kv.Value.EndPoint)) continue;
                    seen.Add(kv.Key);
                    var isSaved = _savedPeers.Any(p => string.Equals(p.EndPoint, kv.Value.EndPoint, StringComparison.OrdinalIgnoreCase));
                    var isConnected = _connections?.IsConnected(kv.Key) == true;
                    _peers.Add(FormatPeerDisplay(kv.Value.DisplayName, kv.Key, kv.Value.EndPoint, isSaved, true, isConnected));
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

            if (s.StartsWith("#CH ", StringComparison.Ordinal))
            {
                SwitchToChannel();
                return;
            }

            _selectedPeerNodeId = ExtractNodeId(s);
            var savedEndPoint = ExtractEndPoint(s);
            if (!string.IsNullOrWhiteSpace(savedEndPoint))
                ManualIpEntry.Text = savedEndPoint;

            _selectedPeerNodeId = NormalizeNodeId(_selectedPeerNodeId, savedEndPoint);
            if (_selectedPeerNodeId != null)
                SwitchChat(_selectedPeerNodeId);
        }

        private async void OnPeerDoubleTapped(object? sender, TappedEventArgs e)
        {
            string? displayText = null;
            if (sender is Label label)
                displayText = label.Text;
            else if (sender is Grid grid && grid.Children.OfType<Label>().FirstOrDefault() is Label childLabel)
                displayText = childLabel.Text;

            if (string.IsNullOrWhiteSpace(displayText))
                return;

            var endPointText = ExtractEndPoint(displayText);
            if (string.IsNullOrWhiteSpace(endPointText))
                return;

            ManualIpEntry.Text = endPointText;
            if (!ParseEndpoint(endPointText, out var ip, out var port))
                return;

            if (_connections == null)
                return;

            var nodeId = NormalizeNodeId(ExtractNodeId(displayText), endPointText) ?? $"endpoint:{ip}:{port}";
            RememberPeer($"Manual {ip}", ip.ToString(), port);
            TechLog(LogCat.Network, $"Double-tap connect -> {ip}:{port}");
            var endPoint = new IPEndPoint(ip, port);
            var ok = await _connections.ConnectToPeerAsync(nodeId, endPoint);
            var connectedNodeId = ok ? PromoteConnectedPeer(endPoint, nodeId) : nodeId;
            AppendChat(ok ? $"[Connected] {connectedNodeId}" : $"[Failed] {nodeId}");
            if (ok) SwitchChat(connectedNodeId);
            RefreshPeersList();
        }

        // --- Actions ---

        private async void OnConnectClicked(object? sender, EventArgs e)
        {
            var input = ManualIpEntry.Text?.Trim();
            if (string.IsNullOrEmpty(input) || _connections == null) return;
            if (!ParseEndpoint(input, out var ip, out var port))
            {
                AppendChat("[Error] Invalid IP:port");
                return;
            }
            RememberPeer($"Manual {ip}", ip.ToString(), port);
            var nodeId = $"endpoint:{ip}:{port}";
            TechLog(LogCat.Network, $"Connecting to {ip}:{port}...");
            var endPoint = new IPEndPoint(ip, port);
            var ok = await _connections.ConnectToPeerAsync(nodeId, endPoint);
            var connectedNodeId = ok ? PromoteConnectedPeer(endPoint, nodeId) : nodeId;
            AppendChat(ok ? $"[Connected] {connectedNodeId}" : $"[Failed] {nodeId}");
            if (ok) { TechLog(LogCat.Encryption, $"TCP session established with {connectedNodeId}"); SwitchChat(connectedNodeId); }
            RefreshPeersList();
        }

        private async void OnSendClicked(object? sender, EventArgs e)
        {
            var activeEntry = MessageBarNarrow.IsVisible ? MessageEntryNarrow : MessageEntry;
            var text = activeEntry.Text?.Trim();
            if (string.IsNullOrEmpty(text) || _chat == null) return;
            MessageEntry.Text = "";
            MessageEntryNarrow.Text = "";

            if (_activeChannelId != null && _channelMembers.Count > 0)
            {
                foreach (var m in _channelMembers.Where(m => m != _localNodeId))
                {
                    TechLog(LogCat.Transport, $"CHANNEL SEND to={m} len={text.Length}");
                    var msg = await _chat.SendMessageAsync(m, text);
                    TechLog(LogCat.Protocol, $"CHANNEL SENT id={msg.MessageId}");
                }
                AppendToChannelChat($"Me: {text}");
                return;
            }

            var toNodeId = GetSelectedOrFirstPeerNodeId();
            if (string.IsNullOrEmpty(toNodeId))
            {
                AppendChat("[Error] Select a peer first");
                return;
            }
            if (_connections?.IsConnected(toNodeId) != true)
            {
                AppendChat("[Error] Peer is not connected. Double-tap the peer or press Connect first.");
                TechLog(LogCat.Network, $"SEND blocked: {toNodeId} not connected");
                return;
            }
            TechLog(LogCat.Transport, $"SEND ChatPacket to={toNodeId} len={text.Length}");
            var sentMsg = await _chat.SendMessageAsync(toNodeId, text);
            var item = AppendChat($"Me: {text}", status: "Sent");
            _pendingItems[sentMsg.MessageId] = item;
            TechLog(LogCat.Protocol, $"SENT id={sentMsg.MessageId} status={sentMsg.Status}");
        }

        private void OnClearChatClicked(object? sender, EventArgs e) => _chatItems.Clear();

        private void OnBackToPeersClicked(object? sender, EventArgs e)
        {
            if (_isNarrowLayout)
            {
                _showingChatOnMobile = false;
                ApplyResponsiveLayout(Width);
            }
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

        private async void OnSendPhotoClicked(object? sender, EventArgs e)
        {
            if (_chat == null)
            {
                AppendChat("[Error] Chat transport unavailable");
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
                var result = await FilePicker.Default.PickAsync(new PickOptions
                {
                    PickerTitle = "Select photo",
                    FileTypes = FilePickerFileType.Images
                });

                if (result == null)
                    return;

                await using var stream = await result.OpenReadAsync();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                var bytes = ms.ToArray();
                if (bytes.Length > 20 * 1024 * 1024)
                {
                    AppendChat("[Error] Photo is larger than 20 MB");
                    TechLog(LogCat.System, $"Photo rejected: {bytes.Length} bytes exceeds 20MB");
                    return;
                }

                if (_activeChannelId != null && _channelMembers.Count > 0)
                {
                    foreach (var m in _channelMembers.Where(m => m != _localNodeId))
                        await _chat.SendImageAsync(m, result.FileName, bytes);
                    AppendToChannelChat($"Me: [Photo] {result.FileName}");
                }
                else
                {
                    await _chat.SendImageAsync(toNodeId, result.FileName, bytes);
                    AppendChat($"Me: [Photo] {result.FileName}", status: "Sent", toPeer: toNodeId, imageBytes: bytes);
                }
                TechLog(LogCat.Transport, $"SEND image to={toNodeId} file={result.FileName} bytes={bytes.Length}");
            }
            catch (Exception ex)
            {
                AppendChat($"[Error] Photo send failed: {ex.Message}");
                TechLog(LogCat.System, $"Photo send failed: {ex.Message}");
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
                StopSendBtn.Text = "SEND";
                StopSendBtn.BackgroundColor = Color.FromArgb("#1976D2");
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
                StopSendBtn.Text = "STOP";
                StopSendBtn.BackgroundColor = Color.FromArgb("#3A3530");

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
                StopSendBtn.Text = "STOP";
                StopSendBtn.BackgroundColor = Color.FromArgb("#3A3530");
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
                if (_activeChannelId != null)
                {
                    foreach (var m in _channelMembers.Where(m => m != _localNodeId))
                        _ = SendCallSignalAsync(m, CallEnd);
                }
                else
                {
                    _ = SendCallSignalAsync(_callPeerNodeId, CallEnd);
                }
                if (_isInCall) EndCall();
                else ResetCallingState();
                return;
            }

            if (_activeChannelId != null)
            {
                await StartChannelCallAsync();
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
            _callPeerVoicePort = _peerVoicePortMap.TryGetValue(nodeId, out var knownVp) ? knownVp : (_voice?.ListenPort ?? 45679);
            _isCallingOut = true;

            var voicePort = _voice?.ListenPort ?? 45679;
            await SendCallSignalAsync(nodeId, $"{CallRequestPrefix}{voicePort}");

            CallBtn.Text = "Cancel";
            CallBtn.BackgroundColor = Color.FromArgb("#E65100");
            VoiceStatusLabel.Text = $"Calling {nodeId}...";
            AppendChat($"[Call] Calling {nodeId}...");
            TechLog(LogCat.Network, $"CALL_REQUEST sent to {nodeId} voice_port={voicePort}");
        }

        private async Task StartChannelCallAsync()
        {
            if (_voice == null || _audioManager == null)
            {
                AppendToChannelChat("[Error] Voice transport not available");
                return;
            }
            if (!await EnsureMicPermissionAsync())
            {
                AppendToChannelChat("[Error] Mic permission denied");
                return;
            }

            SetChannelVoiceEndPoints();
            _isInCall = true;
            try
            {
                _voiceCallManager?.Dispose();
                _voiceCallManager = new VoiceCallManager(_voice, _audioManager);
                _voiceCallManager.Log += msg =>
                    MainThread.BeginInvokeOnMainThread(() => TechLog(LogCat.System, msg));
                _voice.StartListening();
                _voiceCallManager.StartChannelMode();
            }
            catch (Exception ex)
            {
                AppendToChannelChat($"[Error] Channel call failed: {ex.Message}");
                _isInCall = false;
                ResetCallingState();
                return;
            }

            var voicePort = _voice.ListenPort;
            if (_localNodeId != null) _peerVoicePortMap[_localNodeId] = voicePort;
            foreach (var m in _channelMembers.Where(m => m != _localNodeId))
                _ = SendCallSignalAsync(m, $"{CallRequestPrefix}{voicePort}");

            RecordBtn.IsVisible = false;
            StopSendBtn.IsVisible = false;
            PttBtn.IsVisible = true;
            CallBtn.Text = "Hang Up";
            CallBtn.BackgroundColor = Color.FromArgb("#B71C1C");
            VoiceStatusLabel.Text = "Channel call - Hold Talk to speak";
            AppendToChannelChat($"[Call] Channel call started");
            TechLog(LogCat.Network, $"Channel call started, voice_port={voicePort}, members={_channelMembers.Count}");
        }

        private async Task StartCallAsync(string nodeId, IPAddress ip, int voicePort)
        {
            if (_voice == null || _audioManager == null)
            {
                AppendChat("[Error] Voice transport or audio not available");
                ResetCallingState();
                return;
            }

            if (!await EnsureMicPermissionAsync())
            {
                AppendChat("[Error] Microphone permission denied");
                ResetCallingState();
                return;
            }

            _isInCall = true;
            _callPeerNodeId = nodeId;
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
                ResetCallingState();
                return;
            }

            RecordBtn.IsVisible = false;
            StopSendBtn.IsVisible = false;
            PttBtn.IsVisible = true;
            CallBtn.Text = "Hang Up";
            CallBtn.BackgroundColor = Color.FromArgb("#B71C1C");
            VoiceStatusLabel.Text = "Hold Talk to speak";
            AppendChat($"[Call] Walkie-talkie with {ResolveDisplayName(nodeId)}");
            TechLog(LogCat.Network, $"Voice call started -> {ip}:{voicePort}");
        }

        private void EndCall()
        {
            _voiceCallManager?.Dispose();
            _voiceCallManager = null;
            _voice?.Stop();
            _voice?.ClearExtraEndPoints();

            _isInCall = false;
            _callPeerNodeId = null;
            _callPeerIp = null;
            RecordBtn.IsVisible = true;
            StopSendBtn.IsVisible = true;
            PttBtn.IsVisible = false;
            CallBtn.Text = "Call";
            CallBtn.BackgroundColor = Color.FromArgb("#2E7D32");
            VoiceStatusLabel.Text = "Voice: idle";
            if (_activeChannelId != null)
                AppendToChannelChat("[Call] Channel call ended");
            else
                AppendChat("[Call] Call ended");
            TechLog(LogCat.Network, "Voice call ended");
        }

        private async void OnAcceptCallClicked(object? sender, EventArgs e)
        {
            var fromNodeId = _incomingCallFromNodeId;
            var callerVoicePort = _incomingCallerVoicePort;
            HideIncomingCallBanner();

            if (string.IsNullOrEmpty(fromNodeId)) return;

            var callerIp = ResolveConnectedPeerAddress(fromNodeId) ?? _incomingCallerIp;
            if (callerIp == null) return;

            var localVoicePort = _voice?.ListenPort ?? 45679;
            await SendCallSignalAsync(fromNodeId, $"{CallAcceptPrefix}{localVoicePort}");
            _callPeerNodeId = fromNodeId;
            _callPeerIp = callerIp;
            _callPeerVoicePort = callerVoicePort;
            _peerVoicePortMap[fromNodeId] = callerVoicePort;
            TechLog(LogCat.Protocol, $"CALL_ACCEPT sent to {fromNodeId} local_voice_port={localVoicePort} peer_tcp_ip={callerIp} peer_voice={callerVoicePort}");
            await StartCallAsync(fromNodeId, callerIp, callerVoicePort);
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
            RecordBtn.IsVisible = true;
            StopSendBtn.IsVisible = true;
            PttBtn.IsVisible = false;
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

        private async void OnPttPressed(object? sender, EventArgs e)
        {
            if (_voiceCallManager == null || !_isInCall) return;
            PttBtn.BackgroundColor = Color.FromArgb("#F44336");
            PttBtn.Text = "TALKING";
            VoiceStatusLabel.Text = "TALKING...";
            BroadcastPttSignal(PttStartSignal);
            await _voiceCallManager.StartTalkingAsync();
        }

        private async void OnPttReleased(object? sender, EventArgs e)
        {
            if (_voiceCallManager == null) return;
            PttBtn.BackgroundColor = Color.FromArgb("#1976D2");
            PttBtn.Text = "Talk";
            VoiceStatusLabel.Text = "Hold Talk to speak";
            await _voiceCallManager.StopTalkingAsync();
            BroadcastPttSignal(PttEndSignal);
        }

        private void BroadcastPttSignal(string signal)
        {
            if (_activeChannelId != null && _channelMembers.Count > 0)
            {
                foreach (var m in _channelMembers.Where(m => m != _localNodeId))
                    _ = SendCallSignalAsync(m, signal);
            }
            else
            {
                _ = SendCallSignalAsync(_callPeerNodeId, signal);
            }
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

        private ChatItem AppendChat(string line, string? status = null, string? toPeer = null, byte[]? imageBytes = null)
        {
            var collection = !string.IsNullOrEmpty(toPeer) ? GetOrCreatePeerChat(toPeer) : _chatItems;
            var item = new ChatItem
            {
                Text = $"{DateTime.Now:HH:mm:ss} {line}",
                Status = status,
                ImageBytes = imageBytes
            };
            collection.Add(item);
            if (collection.Count > 200) collection.RemoveAt(0);
            if (ReferenceEquals(collection, _chatItems) && collection.Count > 0)
                ChatList.ScrollTo(collection[^1], ScrollToPosition.End, animate: false);
            return item;
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

        private string FormatPeerDisplay(
            string displayName,
            string? nodeId,
            string endPoint,
            bool isSaved,
            bool isOnline,
            bool isConnected)
        {
            string state;
            if (isConnected)
                state = "[OK]";
            else if (isOnline)
                state = "[?]";
            else
                state = "[saved]";

            var newBadge = nodeId != null && _unreadPeers.Contains(nodeId) ? " [NEW]" : "";
            var node = string.IsNullOrWhiteSpace(nodeId) ? "" : $" ({nodeId})";
            var ep = string.IsNullOrWhiteSpace(endPoint) ? "" : $" [{endPoint}]";
            return $"{state}{newBadge} {displayName}{node}{ep}";
        }

        private IPAddress? ResolvePeerIpAddress(string nodeId)
        {
            nodeId = NormalizeNodeId(nodeId) ?? nodeId;
            if (_peerEndpointMap.TryGetValue(nodeId, out var cachedEp))
                return cachedEp.Address;

            var connectedEp = ResolveConnectedPeerEndPoint(nodeId);
            if (connectedEp != null)
                return connectedEp.Address;

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
                return NormalizeNodeId(_selectedPeerNodeId, ManualIpEntry.Text?.Trim()) ?? _selectedPeerNodeId;

            if (_peers.Count == 0)
                return null;

            var first = _peers[0];
            return NormalizeNodeId(ExtractNodeId(first), ExtractEndPoint(first));
        }

        protected override void OnSizeAllocated(double width, double height)
        {
            base.OnSizeAllocated(width, height);
            ApplyResponsiveLayout(width);
        }

        private void ApplyResponsiveLayout(double width)
        {
            if (width <= 0)
                return;

            var narrow = width < 700;
            _isNarrowLayout = narrow;
            MessageBarWide.IsVisible = !narrow;
            MessageBarNarrow.IsVisible = narrow;
            SidebarDivider.IsVisible = !narrow;

            if (narrow)
            {
                // Mobile master-detail: show EITHER peers list OR chat, not both
                RootLayout.ColumnDefinitions[0].Width = GridLength.Star;
                RootLayout.ColumnDefinitions[1].Width = new GridLength(0);
                Grid.SetRow(SidebarPanel, 0);
                Grid.SetColumn(SidebarPanel, 0);
                Grid.SetRowSpan(SidebarPanel, 2);
                Grid.SetRow(ChatPanel, 0);
                Grid.SetColumn(ChatPanel, 0);
                Grid.SetRowSpan(ChatPanel, 2);
                SidebarPanel.MaximumHeightRequest = double.PositiveInfinity;
                PeersList.HeightRequest = -1;

                // Show/hide based on navigation state
                SidebarPanel.IsVisible = !_showingChatOnMobile;
                ChatPanel.IsVisible = _showingChatOnMobile;
                BackToPeersBtn.IsVisible = _showingChatOnMobile;
            }
            else
            {
                // Desktop: show both panels side-by-side
                RootLayout.ColumnDefinitions[0].Width = new GridLength(DeviceInfo.Idiom == DeviceIdiom.Phone ? 160 : 220);
                RootLayout.ColumnDefinitions[1].Width = GridLength.Star;
                Grid.SetRow(SidebarPanel, 0);
                Grid.SetColumn(SidebarPanel, 0);
                Grid.SetRowSpan(SidebarPanel, 2);
                Grid.SetRow(ChatPanel, 0);
                Grid.SetColumn(ChatPanel, 1);
                Grid.SetRowSpan(ChatPanel, 2);
                SidebarPanel.MaximumHeightRequest = double.PositiveInfinity;
                PeersList.HeightRequest = -1;
                SidebarPanel.IsVisible = true;
                ChatPanel.IsVisible = true;
                BackToPeersBtn.IsVisible = false;
            }

            DiagnosticsPanel.IsVisible = !narrow;
            ChatPanel.RowDefinitions[6].Height = narrow
                ? new GridLength(0)
                : new GridLength(2, GridUnitType.Star);
        }

        private string? NormalizeNodeId(string? nodeId, string? endPointText = null)
        {
            if (!string.IsNullOrWhiteSpace(endPointText) && ParseEndpoint(endPointText, out var ip, out var port))
            {
                var connectedNodeId = _connections?.FindPeerNodeId(new IPEndPoint(ip, port));
                if (!string.IsNullOrWhiteSpace(connectedNodeId))
                {
                    if (!string.IsNullOrWhiteSpace(nodeId) && !string.Equals(nodeId, connectedNodeId, StringComparison.Ordinal))
                        MergePeerIdentity(nodeId, connectedNodeId);
                    return connectedNodeId;
                }
            }

            return nodeId;
        }

        private string PromoteConnectedPeer(IPEndPoint endPoint, string fallbackNodeId)
        {
            var actualNodeId = _connections?.FindPeerNodeId(endPoint) ?? fallbackNodeId;
            if (!string.Equals(actualNodeId, fallbackNodeId, StringComparison.Ordinal))
                MergePeerIdentity(fallbackNodeId, actualNodeId);
            _selectedPeerNodeId = actualNodeId;
            return actualNodeId;
        }

        private void MergePeerIdentity(string sourceNodeId, string targetNodeId)
        {
            if (string.IsNullOrWhiteSpace(sourceNodeId) ||
                string.IsNullOrWhiteSpace(targetNodeId) ||
                string.Equals(sourceNodeId, targetNodeId, StringComparison.Ordinal))
                return;

            if (_peerChats.Remove(sourceNodeId, out var sourceChat))
            {
                var targetChat = GetOrCreatePeerChat(targetNodeId);
                foreach (var item in sourceChat)
                    targetChat.Add(item);
            }

            if (_unreadPeers.Remove(sourceNodeId))
                _unreadPeers.Add(targetNodeId);

            if (_peerEndpointMap.Remove(sourceNodeId, out var endpoint))
                _peerEndpointMap[targetNodeId] = endpoint;

            if (_peerVoicePortMap.Remove(sourceNodeId, out var voicePort))
                _peerVoicePortMap[targetNodeId] = voicePort;

            if (_selectedPeerNodeId == sourceNodeId)
                _selectedPeerNodeId = targetNodeId;

            if (_activeChatPeer == sourceNodeId)
            {
                _activeChatPeer = null;
                SwitchChat(targetNodeId);
            }
        }

        private IPEndPoint? ResolveConnectedPeerEndPoint(string nodeId)
        {
            if (string.IsNullOrWhiteSpace(nodeId))
                return null;

            if (_peerEndpointMap.TryGetValue(nodeId, out var cached))
                return cached;

            var endpoint = _connections?.GetPeerEndPoint(nodeId);
            if (endpoint != null)
                _peerEndpointMap[nodeId] = endpoint;
            return endpoint;
        }

        private IPAddress? ResolveConnectedPeerAddress(string nodeId) =>
            ResolveConnectedPeerEndPoint(nodeId)?.Address;

        private static int ParseVoicePortPayload(string payload)
        {
            if (int.TryParse(payload, out var port) && port > 0)
                return port;

            var colonIdx = payload.LastIndexOf(':');
            return colonIdx > 0 && int.TryParse(payload[(colonIdx + 1)..], out port) && port > 0
                ? port
                : 45679;
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
