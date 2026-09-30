using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Media;
using ConnectMe.Core.Network;
using ConnectMe.Core.Protocol;
using ConnectMe.Core.Topology;
using ConnectMe.Windows.Win32;

namespace ConnectMe.Windows;

public partial class MainWindow : Window
{
    private readonly SpatialTopologyEngine _topology = new();
    private readonly ConnectMeNetworkNode _network = new();
    private readonly Win32InputEngine _inputEngine;
    private readonly WindowsClipboardService _clipboardService;
    private readonly List<ShelfItemEntry> _shelfItems = new();

    private ScreenEdge _selectedEdge = ScreenEdge.Right;
    private PeerDeviceNode? _selectedPeer;
    private UdpClient? _loopbackSimUdp;
    private CancellationTokenSource? _loopbackSimCts;

    public MainWindow()
    {
        InitializeComponent();

        _inputEngine = new Win32InputEngine(_topology, _network);
        _clipboardService = new WindowsClipboardService(_network);

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _network.LogMessage += AppendLog;
        _network.PeerDiscoveredOrUpdated += OnPeerDiscoveredOrUpdated;
        _network.ShelfItemReceived += OnShelfItemReceived;

        _inputEngine.ActiveTargetChanged += OnActiveTargetChanged;
        _inputEngine.ScreenLockToggled += OnScreenLockToggled;

        _network.Start();
        _inputEngine.StartHooks();
        _clipboardService.AttachToWindow(this);

        PinCodeText.Text = _network.PairingPin;
        MonitorResolutionText.Text = $"{_topology.LocalWidth} x {_topology.LocalHeight}";

        var ips = ConnectMeNetworkNode.GetLocalIPv4Addresses();
        string ipText = string.Join(", ", ips.Select(i => i.ToString()));
        LocalNetworkInfoText.Text =
            $"Yerel IP: {ipText}  |  UDP Girdi Portu: {_network.InputUdpPort}  |  TCP Veri Portu: {_network.ControlTcpPort}  |  Keşif: {_network.DiscoveryPort}";

        // Pre-fill subnet prefix in manual IP box
        var firstLan = ips.FirstOrDefault(i => !IPAddress.IsLoopback(i));
        if (firstLan != null)
        {
            var parts = firstLan.ToString().Split('.');
            if (parts.Length == 4)
            {
                ManualIpTextBox.Text = $"{parts[0]}.{parts[1]}.{parts[2]}.";
            }
        }

        UpdateEdgeButtonsVisual();
        AppendLog($"[Sistem] Connect Me Windows Motoru hazır ({_topology.LocalWidth}x{_topology.LocalHeight}).");
        AppendLog("[Bilgi] Android cihazınız aynı Wi-Fi ağındaysa otomatik görünür veya IP adresini girip bağlayabilirsiniz.");
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _loopbackSimCts?.Cancel();
        _loopbackSimUdp?.Dispose();
        _clipboardService.Dispose();
        _inputEngine.Dispose();
        await _network.DisposeAsync();
    }

    private void OnPeerDiscoveredOrUpdated(PeerDeviceNode peer)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_selectedPeer == null)
            {
                _selectedPeer = peer;
                _topology.AssignPeerToEdge(_selectedEdge, peer);
                AppendLog($"[Topoloji] '{peer.DeviceName}' ({peer.IpAddress}) otomatik olarak {_selectedEdge.ToString().ToUpper()} kenara atandı.");
            }
            else if (_selectedPeer.DeviceId == peer.DeviceId)
            {
                _topology.AssignPeerToEdge(_selectedEdge, peer);
            }

            RefreshPeersList();
        });
    }

    private void RefreshPeersList()
    {
        var peers = _network.DiscoveredPeers.OrderByDescending(p => p.LastSeen).ToList();
        PeersListBox.Items.Clear();
        foreach (var p in peers)
        {
            string edgeBadge = p.AssignedEdgeOnLocal != ScreenEdge.None
                ? $"[Kenar: {FormatEdgeTr(p.AssignedEdgeOnLocal)}]"
                : "[Atanmadı]";
            string rtt = p.LatencyMs > 0 ? $"{p.LatencyMs:F1} ms" : "<1.5 ms";
            string icon = p.Platform.Contains("android", StringComparison.OrdinalIgnoreCase) ? "📱" : "🐧";
            PeersListBox.Items.Add($"{icon} {p.DeviceName} ({p.IpAddress}:{p.UdpInputPort})  {edgeBadge}  ⚡ {rtt}");
        }
    }

    private void OnActiveTargetChanged(PeerDeviceNode? activePeer, ScreenEdge edge, float normalizedPos)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (activePeer != null)
            {
                ActiveFocusBadge.Background = new SolidColorBrush(Color.FromRgb(30, 58, 138));
                ActiveFocusBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248));
                ActiveFocusText.Foreground = new SolidColorBrush(Color.FromRgb(125, 211, 252));
                ActiveFocusText.Text = $"📱 Aktif Kontrol: {activePeer.DeviceName} ({FormatEdgeTr(edge)} Kenar, %{normalizedPos * 100:F0})";
                AppendLog($"[Kenar Geçişi] ➡️ İmleç ve klavye '{activePeer.DeviceName}' ekranına geçti (Konum: %{normalizedPos * 100:F0}).");
            }
            else
            {
                ActiveFocusBadge.Background = new SolidColorBrush(Color.FromRgb(30, 58, 47));
                ActiveFocusBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                ActiveFocusText.Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128));
                ActiveFocusText.Text = "🪟 Aktif Kontrol: Windows (Yerel)";
                AppendLog("[Kenar Geçişi] ⬅️ İmleç ve klavye Windows ana ekranına geri döndü.");
            }
        });
    }

    private void OnScreenLockToggled(bool isLocked)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (isLocked)
            {
                LockStateBadge.Background = new SolidColorBrush(Color.FromRgb(120, 53, 15));
                LockStateBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                LockStateText.Foreground = new SolidColorBrush(Color.FromRgb(253, 230, 138));
                LockStateText.Text = "🔒 Kenar Geçişi: KİLİTLİ (Oyun Modu Aktif)";
                AppendLog("[Kilit] Ekran geçişi kilitlendi. İmleç Windows ekranından çıkmayacak.");
            }
            else
            {
                LockStateBadge.Background = new SolidColorBrush(Color.FromRgb(31, 41, 55));
                LockStateBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(48, 54, 61));
                LockStateText.Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175));
                LockStateText.Text = "🔓 Kenar Geçişi: Açık (ScrollLock / Ctrl+Alt+L)";
                AppendLog("[Kilit] Ekran kilidi açıldı. Kenar geçişi aktif.");
            }
        });
    }

    private void OnShelfItemReceived(ShelfItemEntry item)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _shelfItems.Insert(0, item);
            RefreshShelfList();
        });
    }

    private void RefreshShelfList()
    {
        ShelfItemsListBox.Items.Clear();
        foreach (var item in _shelfItems)
        {
            string dirArrow = item.IsOutgoing ? "📤 Gönderildi" : "📥 Alındı";
            ShelfItemsListBox.Items.Add(
                $"{dirArrow}: {item.FileName} ({ConnectMeNetworkNode.FormatBytes(item.FileSizeBytes)}) — {item.SenderName} [{item.ReceivedAt:HH:mm:ss}]");
        }
    }

    // =========================================================================
    // UI Button Handlers
    // =========================================================================

    private void EdgeLeftBtn_Click(object sender, RoutedEventArgs e) => SetActiveEdge(ScreenEdge.Left);
    private void EdgeRightBtn_Click(object sender, RoutedEventArgs e) => SetActiveEdge(ScreenEdge.Right);
    private void EdgeTopBtn_Click(object sender, RoutedEventArgs e) => SetActiveEdge(ScreenEdge.Top);
    private void EdgeBottomBtn_Click(object sender, RoutedEventArgs e) => SetActiveEdge(ScreenEdge.Bottom);

    private void SetActiveEdge(ScreenEdge edge)
    {
        _selectedEdge = edge;
        if (_selectedPeer != null)
        {
            _topology.AssignPeerToEdge(_selectedEdge, _selectedPeer);
            RefreshPeersList();
        }
        UpdateEdgeButtonsVisual();
        AppendLog($"[Topoloji] Android geçiş kenarı '{FormatEdgeTr(edge)}' olarak güncellendi.");
    }

    private void UpdateEdgeButtonsVisual()
    {
        var inactiveBg = new SolidColorBrush(Color.FromRgb(31, 41, 55));
        var inactiveBorder = new SolidColorBrush(Color.FromRgb(55, 65, 81));
        var activeBg = new SolidColorBrush(Color.FromRgb(3, 105, 161));
        var activeBorder = new SolidColorBrush(Color.FromRgb(56, 189, 248));

        void StyleBtn(System.Windows.Controls.Button btn, bool active)
        {
            btn.Background = active ? activeBg : inactiveBg;
            btn.BorderBrush = active ? activeBorder : inactiveBorder;
            btn.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
        }

        StyleBtn(EdgeLeftBtn, _selectedEdge == ScreenEdge.Left);
        StyleBtn(EdgeRightBtn, _selectedEdge == ScreenEdge.Right);
        StyleBtn(EdgeTopBtn, _selectedEdge == ScreenEdge.Top);
        StyleBtn(EdgeBottomBtn, _selectedEdge == ScreenEdge.Bottom);

        AssignedEdgeSummaryText.Text = $"Aktif Geçiş Kenarı: {FormatEdgeTr(_selectedEdge)}";
    }

    private void ToggleLockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_inputEngine.ActiveRemotePeer != null)
        {
            _inputEngine.ReturnControlToLocal(ScreenEdge.None, 0.5f);
        }
        else
        {
            _topology.IsScreenLocked = !_topology.IsScreenLocked;
            OnScreenLockToggled(_topology.IsScreenLocked);
        }
    }

    private async void ScanDevicesBtn_Click(object sender, RoutedEventArgs e)
    {
        AppendLog("[Keşif] Yerel ağa (Wi-Fi) keşif sinyali gönderiliyor...");
        await _network.BroadcastDiscoveryBeaconAsync();
    }

    private void TestSwitchBtn_Click(object sender, RoutedEventArgs e)
    {
        var peer = _selectedPeer ?? _network.DiscoveredPeers.FirstOrDefault();
        if (peer == null)
        {
            AppendLog("[Uyarı] Henüz bağlı bir Android cihaz yok. Önce 'Android IP Bağla' veya 'Simülatör Ekle' butonunu kullanın.");
            return;
        }

        var targetEntrance = SpatialTopologyEngine.GetOppositeEdge(_selectedEdge);
        _inputEngine.SwitchControlToPeer(peer, _selectedEdge, targetEntrance, 0.5f);
    }

    private void AddManualPeerBtn_Click(object sender, RoutedEventArgs e)
    {
        string ip = ManualIpTextBox.Text.Trim();
        if (!IPAddress.TryParse(ip, out _))
        {
            AppendLog($"[Hata] Geçersiz IP adresi: '{ip}'. Örn: 192.168.1.45");
            return;
        }

        var peer = _network.RegisterManualPeer(ip, $"Android ({ip})", "android");
        _selectedPeer = peer;
        _topology.AssignPeerToEdge(_selectedEdge, peer);
        RefreshPeersList();
        AppendLog($"[Bağlantı] Android cihaz ({ip}) '{FormatEdgeTr(_selectedEdge)}' kenarına bağlandı! Fareyi o kenara götürerek geçebilirsiniz.");
    }

    private void AddLoopbackSimBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_loopbackSimUdp != null)
        {
            AppendLog("[Simülatör] Sanal Android simülatörü zaten aktif.");
            return;
        }

        const int simPort = 42860;
        _loopbackSimCts = new CancellationTokenSource();
        _loopbackSimUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, simPort));

        var simPeer = _network.RegisterManualPeer(
            "127.0.0.1",
            "Sanal Android (Test Simülatörü)",
            "android-sim",
            udpPort: simPort,
            tcpPort: _network.ControlTcpPort,
            screenWidth: 1080,
            screenHeight: 2400);

        _selectedPeer = simPeer;
        _topology.AssignPeerToEdge(_selectedEdge, simPeer);
        RefreshPeersList();

        _ = Task.Run(() => RunLoopbackAndroidSimulatorAsync(_loopbackSimUdp, _loopbackSimCts.Token));
        AppendLog($"[Simülatör] Sanal Android (1080x2400) '{FormatEdgeTr(_selectedEdge)}' kenarda başlatıldı! Fareyi ekranın o kenarına iterek geçişi test edin.");
    }

    private async Task RunLoopbackAndroidSimulatorAsync(UdpClient simUdp, CancellationToken ct)
    {
        float simX = 540f;
        float simY = 1200f;
        const float simW = 1080f;
        const float simH = 2400f;
        int moveCount = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var res = await simUdp.ReceiveAsync(ct).ConfigureAwait(false);
                var buf = res.Buffer;
                if (!WirePacketCodec.TryValidateHeader(buf, out var type))
                    continue;

                switch (type)
                {
                    case PacketType.EdgeHandOff:
                        if (WirePacketCodec.TryDecodeEdgeHandOff(buf, out var ho))
                        {
                            (simX, simY) = ho.TargetEntranceEdge switch
                            {
                                ScreenEdge.Left => (12f, ho.NormalizedPosition * simH),
                                ScreenEdge.Right => (simW - 12f, ho.NormalizedPosition * simH),
                                ScreenEdge.Top => (ho.NormalizedPosition * simW, 12f),
                                ScreenEdge.Bottom => (ho.NormalizedPosition * simW, simH - 12f),
                                _ => (simW / 2f, simH / 2f)
                            };
                            moveCount = 0;
                            AppendLog($"[Android Sim] İmleç Android ekranına girdi -> ({simX:F0}, {simY:F0})");
                        }
                        break;

                    case PacketType.MouseMove:
                        if (WirePacketCodec.TryDecodeMouseMove(buf, out var mm))
                        {
                            simX += mm.DeltaX;
                            simY += mm.DeltaY;
                            moveCount++;

                            // Check if cursor returned back across the edge facing Windows!
                            ScreenEdge returnEdgeOnWindows = ScreenEdge.None;
                            float normPos = 0.5f;

                            if (_selectedEdge == ScreenEdge.Right && simX < 0)
                            {
                                returnEdgeOnWindows = ScreenEdge.Right;
                                normPos = Math.Clamp(simY / simH, 0f, 1f);
                            }
                            else if (_selectedEdge == ScreenEdge.Left && simX > simW)
                            {
                                returnEdgeOnWindows = ScreenEdge.Left;
                                normPos = Math.Clamp(simY / simH, 0f, 1f);
                            }
                            else if (_selectedEdge == ScreenEdge.Bottom && simY < 0)
                            {
                                returnEdgeOnWindows = ScreenEdge.Bottom;
                                normPos = Math.Clamp(simX / simW, 0f, 1f);
                            }
                            else if (_selectedEdge == ScreenEdge.Top && simY > simH)
                            {
                                returnEdgeOnWindows = ScreenEdge.Top;
                                normPos = Math.Clamp(simX / simW, 0f, 1f);
                            }

                            simX = Math.Clamp(simX, 0f, simW);
                            simY = Math.Clamp(simY, 0f, simH);

                            if (returnEdgeOnWindows != ScreenEdge.None)
                            {
                                byte[] backPkt = WirePacketCodec.EncodeEdgeHandOff(
                                    new EdgeHandOffPacket(returnEdgeOnWindows, false, normPos));
                                await simUdp.SendAsync(backPkt, backPkt.Length, new IPEndPoint(IPAddress.Loopback, _network.InputUdpPort))
                                    .ConfigureAwait(false);
                            }
                            else if (moveCount % 25 == 0)
                            {
                                AppendLog($"[Android Sim] 120Hz İmleç Konumu: ({simX:F0}, {simY:F0}) / 1080x2400");
                            }
                        }
                        break;

                    case PacketType.MouseButton:
                        if (WirePacketCodec.TryDecodeMouseButton(buf, out var mb) && mb.IsPressed)
                        {
                            AppendLog($"[Android Sim] Fare Tıklaması: {mb.Button} @ ({simX:F0}, {simY:F0})");
                        }
                        break;

                    case PacketType.KeyEvent:
                        if (WirePacketCodec.TryDecodeKeyEvent(buf, out var ke) && ke.IsPressed)
                        {
                            string chDisp = ke.UnicodeChar != '\0' ? $"'{ke.UnicodeChar}'" : $"VK_{ke.VirtualKey}";
                            AppendLog($"[Android Sim] Klavye Tuşu Yazıldı: {chDisp} (Modifiers: {ke.Modifiers})");
                        }
                        break;

                    case PacketType.HeartbeatPing:
                        if (WirePacketCodec.TryDecodeHeartbeat(buf, out var ping))
                        {
                            byte[] pong = WirePacketCodec.EncodeHeartbeat(PacketType.HeartbeatPong, ping.TimestampMs);
                            await simUdp.SendAsync(pong, pong.Length, res.RemoteEndPoint).ConfigureAwait(false);
                        }
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Ignore
            }
        }
    }

    private void PeersListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        int idx = PeersListBox.SelectedIndex;
        var peers = _network.DiscoveredPeers.OrderByDescending(p => p.LastSeen).ToList();
        if (idx >= 0 && idx < peers.Count)
        {
            _selectedPeer = peers[idx];
            _topology.AssignPeerToEdge(_selectedEdge, _selectedPeer);
            RefreshPeersList();
        }
    }

    // =========================================================================
    // Drop Shelf & Clipboard Handlers
    // =========================================================================

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ||
            e.Data.GetDataPresent(System.Windows.DataFormats.UnicodeText))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.None;
        }
        e.Handled = true;
    }

    private async void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                await SendFilesToActivePeerAsync(files);
            }
        }
        else if (e.Data.GetDataPresent(System.Windows.DataFormats.UnicodeText))
        {
            string? text = e.Data.GetData(System.Windows.DataFormats.UnicodeText) as string;
            if (!string.IsNullOrEmpty(text))
            {
                await _network.BroadcastClipboardTextAsync(text);
                AppendLog($"[Drop Shelf] Bırakılan metin ({text.Length} krk) bağlı cihazların panosuna gönderildi.");
            }
        }
    }

    private async void PickFileToSendBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Android Cihaza Gönderilecek Dosyaları Seçin",
            Multiselect = true
        };

        if (dlg.ShowDialog(this) == true && dlg.FileNames.Length > 0)
        {
            await SendFilesToActivePeerAsync(dlg.FileNames);
        }
    }

    private async Task SendFilesToActivePeerAsync(IEnumerable<string> filePaths)
    {
        var peer = _selectedPeer ?? _network.DiscoveredPeers.FirstOrDefault();
        if (peer == null)
        {
            AppendLog("[Uyarı] Dosya göndermek için önce bir Android cihaz bağlayın veya Simülatör ekleyin.");
            return;
        }

        foreach (string path in filePaths)
        {
            if (File.Exists(path))
            {
                var entry = await _network.SendFileToPeerShelfAsync(peer, path);
                if (entry != null)
                {
                    _shelfItems.Insert(0, entry);
                    RefreshShelfList();
                }
            }
        }
    }

    private void OpenShelfFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _network.ShelfReceiveDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppendLog($"[Hata] Klasör açılamadı: {ex.Message}");
        }
    }

    private void ShelfItemsListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        int idx = ShelfItemsListBox.SelectedIndex;
        if (idx >= 0 && idx < _shelfItems.Count)
        {
            var item = _shelfItems[idx];
            if (File.Exists(item.LocalFilePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{item.LocalFilePath}\"",
                    UseShellExecute = true
                });
            }
        }
    }

    private void AutoClipboardCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_clipboardService != null && AutoClipboardCheckBox != null)
        {
            _clipboardService.IsAutoSyncEnabled = AutoClipboardCheckBox.IsChecked == true;
        }
    }

    private async void PushClipboardNowBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (System.Windows.Clipboard.ContainsText())
            {
                string txt = System.Windows.Clipboard.GetText();
                await _network.BroadcastClipboardTextAsync(txt);
                AppendLog($"[Evrensel Pano] Pano metni ({txt.Length} krk) tüm cihazlara gönderildi.");
            }
            else
            {
                AppendLog("[Evrensel Pano] Panoda gönderilecek metin bulunamadı.");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[Pano Uyarı] {ex.Message}");
        }
    }

    private void AppendLog(string message)
    {
        Dispatcher.InvokeAsync(() =>
        {
            TelemetryLogListBox.Items.Insert(0, message);
            while (TelemetryLogListBox.Items.Count > 150)
            {
                TelemetryLogListBox.Items.RemoveAt(TelemetryLogListBox.Items.Count - 1);
            }
        });
    }

    private static string FormatEdgeTr(ScreenEdge edge) => edge switch
    {
        ScreenEdge.Left => "⬅️ SOL",
        ScreenEdge.Right => "SAĞ ➡️",
        ScreenEdge.Top => "⬆️ ÜST",
        ScreenEdge.Bottom => "⬇️ ALT",
        _ => "YOK"
    };
}
