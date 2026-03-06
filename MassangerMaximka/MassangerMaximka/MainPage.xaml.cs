using System.Collections.ObjectModel;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using HexTeam.Messenger.Core.Discovery;
using HexTeam.Messenger.Core.Models;
using HexTeam.Messenger.Core.Transport;

namespace MassangerMaximka
{
    public partial class MainPage : ContentPage
    {
        private UdpDiscoveryService? _discovery;
        private PeerConnectionService? _connections;
        private TcpChatTransport? _chat;
        private string? _selectedPeerNodeId;
        private readonly ObservableCollection<string> _peers = [];
        private readonly List<string> _chatLog = [];

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

            if (_discovery == null || _connections == null || _chat == null)
            {
                StatusLabel.Text = "Services not found";
                return;
            }

            _discovery.Start();
            _connections.StartListening();
            services.GetService<HexTeam.Messenger.Core.Metrics.MetricsService>()?.Start();

            _discovery.PeerDiscovered += OnPeerDiscovered;
            _connections.PeerConnected += OnPeerConnected;
            _connections.PeerDisconnected += OnPeerDisconnected;
            _chat.MessageReceived += OnMessageReceived;

            StatusLabel.Text = $"TCP: {_connections.ListenPort} | Peers: {_discovery.Peers.Count}";
            var otherPort = _connections.ListenPort == 45680 ? 45681 : 45680;
            PortHintLabel.Text = $"Your port: {_connections.ListenPort}. Connect to other: 127.0.0.1:{otherPort}";
            RefreshPeersList();
        }

        protected override void OnDisappearing()
        {
            if (_discovery != null) _discovery.PeerDiscovered -= OnPeerDiscovered;
            if (_connections != null) _connections.PeerConnected -= OnPeerConnected;
            if (_connections != null) _connections.PeerDisconnected -= OnPeerDisconnected;
            if (_chat != null) _chat.MessageReceived -= OnMessageReceived;
            base.OnDisappearing();
        }

        private void OnPeerDiscovered(PeerInfo peer)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                RefreshPeersList();
                StatusLabel.Text = $"TCP: {_connections?.ListenPort ?? 0} | Peers: {_peers.Count}";
            });
        }

        private void OnPeerConnected(string nodeId)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                RefreshPeersList();
                AppendLog($"[Connected] {nodeId}");
            });
        }

        private void OnPeerDisconnected(string nodeId)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                RefreshPeersList();
                AppendLog($"[Disconnected] {nodeId}");
            });
        }

        private void OnMessageReceived(ChatMessage msg)
        {
            MainThread.BeginInvokeOnMainThread(() =>
                AppendLog($"{msg.FromNodeId}: {msg.Text}"));
        }

        private void RefreshPeersList()
        {
            _peers.Clear();
            var seen = new HashSet<string>();
            if (_discovery != null)
                foreach (var p in _discovery.Peers.Values)
                {
                    seen.Add(p.NodeId);
                    var conn = _connections?.IsConnected(p.NodeId) == true ? " [OK]" : "";
                    _peers.Add($"{p.DisplayName} ({p.NodeId}){conn}");
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
            if (start > 0 && end > start)
                _selectedPeerNodeId = s[start..end];
            else
                _selectedPeerNodeId = null;
            SelectedPeerLabel.Text = _selectedPeerNodeId ?? "(none)";
        }

        private async void OnConnectClicked(object? sender, EventArgs e)
        {
            var input = ManualIpEntry.Text?.Trim();
            if (string.IsNullOrEmpty(input) || _connections == null || _discovery == null) return;

            if (!ParseEndpoint(input, out var ip, out var port))
            {
                AppendLog("[Error] Invalid IP:port format");
                return;
            }

            var nodeId = $"manual-{ip}:{port}";
            var peer = _discovery.AddManualPeer(nodeId, $"Manual {ip}", new IPEndPoint(ip, port));
            var ok = await _connections.ConnectToPeerAsync(nodeId, peer.EndPoint);
            AppendLog(ok ? $"[Connected] {nodeId}" : $"[Failed] {nodeId}");
            RefreshPeersList();
        }

        private async void OnSendClicked(object? sender, EventArgs e)
        {
            var text = MessageEntry.Text?.Trim();
            if (string.IsNullOrEmpty(text) || _chat == null) return;

            var toNodeId = _selectedPeerNodeId;
            if (string.IsNullOrEmpty(toNodeId))
            {
                if (_peers.Count == 0)
                {
                    AppendLog("[Error] No peer selected and no peers available");
                    return;
                }
                toNodeId = ExtractNodeId(_peers[0]);
            }

            if (string.IsNullOrEmpty(toNodeId))
            {
                AppendLog("[Error] Select a peer first");
                return;
            }

            MessageEntry.Text = "";
            await _chat.SendMessageAsync(toNodeId, text);
            AppendLog($"Me -> {toNodeId}: {text}");
        }

        private static bool ParseEndpoint(string input, out IPAddress ip, out int port)
        {
            ip = IPAddress.Loopback;
            port = 45680;
            var parts = input.Split(':');
            if (parts.Length != 2) return false;
            if (!IPAddress.TryParse(parts[0], out ip!)) return false;
            if (!int.TryParse(parts[1], out port)) return false;
            return true;
        }

        private static string? ExtractNodeId(string display)
        {
            var start = display.IndexOf('(') + 1;
            var end = display.IndexOf(')');
            return start > 0 && end > start ? display[start..end] : null;
        }

        private void AppendLog(string line)
        {
            _chatLog.Add($"{DateTime.Now:HH:mm:ss} {line}");
            while (_chatLog.Count > 50) _chatLog.RemoveAt(0);
            ChatLogLabel.Text = string.Join("\n", _chatLog);
        }
    }
}
