using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
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
    private readonly List<UdpClient> _simSockets = new();
    private readonly CancellationTokenSource _simCts = new();

    private PeerDeviceNode? _selectedPeer;
    private int _simDeviceCounter;
    private int _simLocalMonitorStep;

    // 2D Canvas Dragging state
    private PeerDeviceNode? _draggingPeer;
    private Border? _draggingBorder;
    private Point _dragMouseOffset;

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
        _network.PeerPairingStatusChanged += OnPeerPairingStatusChanged;
        _network.ShelfItemReceived += OnShelfItemReceived;

        _inputEngine.ActiveTargetChanged += OnActiveTargetChanged;
        _inputEngine.ScreenLockToggled += OnScreenLockToggled;

        _network.Start();
        _inputEngine.StartHooks();
        _clipboardService.AttachToWindow(this);

        PinCodeText.Text = FormatPinDisplay(_network.PairingPin);

        var ips = ConnectMeNetworkNode.GetLocalIPv4Addresses();
        string ipText = string.Join(", ", ips.Select(i => i.ToString()));
        LocalNetworkInfoText.Text =
            $"v0.45 (v0-45) | IP: {ipText} | UDP: {_network.InputUdpPort} | TCP: {_network.ControlTcpPort}";

        var firstLan = ips.FirstOrDefault(i => !IPAddress.IsLoopback(i));
        if (firstLan != null)
        {
            var parts = firstLan.ToString().Split('.');
            if (parts.Length == 4)
            {
                ManualIpTextBox.Text = $"{parts[0]}.{parts[1]}.{parts[2]}.";
            }
        }

        RedrawDisplayArrangementCanvas();
        int monCount = _topology.LocalMonitors.Count;
        AppendLog($"[Sistem] Connect Me v0.45 hazır ({monCount} yerel monitör, toplam sanal masaüstü: {_topology.LocalWidth}x{_topology.LocalHeight}). Yerel 6 Haneli PIN: {_network.PairingPin}");
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _simCts.Cancel();
        foreach (var s in _simSockets)
        {
            try { s.Dispose(); } catch { }
        }
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
            }

            if (peer.IsMutuallyPaired)
            {
                EnsurePeerPlacedOnTopology(peer);
            }

            RefreshPeersList();
            UpdateSelectedPeerPairingPanel();
            RedrawDisplayArrangementCanvas();
        });
    }

    private void OnPeerPairingStatusChanged(PeerDeviceNode peer)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (peer.IsMutuallyPaired)
            {
                EnsurePeerPlacedOnTopology(peer);
            }

            RefreshPeersList();
            UpdateSelectedPeerPairingPanel();
            RedrawDisplayArrangementCanvas();
        });
    }

    private void EnsurePeerPlacedOnTopology(PeerDeviceNode peer)
    {
        var configured = _topology.GetConfiguredPeers();
        if (configured.Any(p => p.DeviceId == peer.DeviceId))
            return;

        ScreenEdge targetEdge = peer.AssignedEdgeOnLocal != ScreenEdge.None
            ? peer.AssignedEdgeOnLocal
            : (configured.Count % 4) switch
            {
                0 => ScreenEdge.Right,
                1 => ScreenEdge.Left,
                2 => ScreenEdge.Bottom,
                _ => ScreenEdge.Top
            };

        _topology.AssignPeerToEdgeSegment(peer, targetEdge, 0.0f, 1.0f);
        DistributePeersOnEdge(targetEdge);
    }

    private void DistributePeersOnEdge(ScreenEdge edge)
    {
        var sameEdgePeers = _topology.GetConfiguredPeers()
            .Where(p => p.IsMutuallyPaired && p.AssignedEdgeOnLocal == edge)
            .ToList();

        if (sameEdgePeers.Count == 0)
            return;

        float step = 1.0f / sameEdgePeers.Count;
        for (int i = 0; i < sameEdgePeers.Count; i++)
        {
            sameEdgePeers[i].HasCustomCanvasPosition = false;
            _topology.AssignPeerToEdgeSegment(sameEdgePeers[i], edge, i * step, (i + 1) * step);
        }
    }

    private void RefreshPeersList()
    {
        var peers = _network.DiscoveredPeers
            .OrderByDescending(p => p.IsMutuallyPaired)
            .ThenByDescending(p => p.LastSeen)
            .ToList();

        string? selectedId = _selectedPeer?.DeviceId;

        PeersListBox.SelectionChanged -= PeersListBox_SelectionChanged;
        PeersListBox.Items.Clear();

        int restoreIdx = -1;
        for (int i = 0; i < peers.Count; i++)
        {
            var p = peers[i];
            if (p.DeviceId == selectedId)
                restoreIdx = i;

            string icon = GetPlatformIcon(p.Platform);
            string multiMonTag = p.RemoteMonitorCount > 1 ? $" [{p.RemoteMonitorCount} Ekran]" : string.Empty;
            string monPrefix = !string.IsNullOrEmpty(p.AttachedLocalMonitorId) && _topology.LocalMonitors.Count > 1
                ? $"{p.AttachedLocalMonitorId} "
                : string.Empty;
            string trustStar = p.IsTrusted ? "⭐ " : "";
            string stateBadge = p.PairingState switch
            {
                PeerPairingState.MutuallyPaired => $"{trustStar}🟢 ONAYLI ({monPrefix}{FormatEdgeShortTr(p.AssignedEdgeOnLocal)} %{p.EdgeOffsetStart * 100:F0}-{p.EdgeOffsetEnd * 100:F0})",
                PeerPairingState.OutboundPinVerified => $"{trustStar}🟡 KARŞI ONAY BEKLİYOR",
                PeerPairingState.InboundPinVerified => $"{trustStar}🟠 ONAYINIZ BEKLENİYOR",
                _ => p.IsTrusted ? "⭐ ⚪ GÜVENİLİR (Bağlanıyor...)" : "⚪ PIN GEREKLİ"
            };

            var itemBlock = new TextBlock
            {
                Text = $"{icon} {p.DeviceName}{multiMonTag} ({p.IpAddress})\n   {stateBadge}",
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 3, 2, 3)
            };

            PeersListBox.Items.Add(itemBlock);
        }

        if (restoreIdx >= 0)
            PeersListBox.SelectedIndex = restoreIdx;
        PeersListBox.SelectionChanged += PeersListBox_SelectionChanged;
    }

    private void UpdateSelectedPeerPairingPanel()
    {
        if (_selectedPeer == null)
        {
            SelectedPeerTitleText.Text = "🔐 Çift Taraflı 6 Haneli Kod Onayı";
            SelectedPeerStatusText.Text = "Listeden bir cihaz seçip ekrandaki 6 haneli kodunu girin.";
            SimConfirmOurPinBtn.Visibility = Visibility.Collapsed;
            RevokeTrustBtn.Visibility = Visibility.Collapsed;
            return;
        }

        var p = _selectedPeer;
        string multiMonBadge = p.RemoteMonitorCount > 1 ? $" ({p.RemoteMonitorCount} Monitör)" : string.Empty;
        string trustBadge = p.IsTrusted ? " ⭐ [GÜVENİLİR]" : "";
        SelectedPeerTitleText.Text = $"🔐 {GetPlatformIcon(p.Platform)} {p.DeviceName}{multiMonBadge}{trustBadge}";

        string simHint = !string.IsNullOrEmpty(p.SimulatedLocalPin)
            ? $" (Kod: {p.SimulatedLocalPin})"
            : string.Empty;

        RememberDeviceCheckBox.IsChecked = p.IsTrusted || true;
        RevokeTrustBtn.Visibility = p.IsTrusted ? Visibility.Visible : Visibility.Collapsed;

        SelectedPeerStatusText.Text = p.PairingState switch
        {
            PeerPairingState.MutuallyPaired =>
                p.IsTrusted
                    ? "⭐ Bu cihaz güvenilir olarak kaydedildi. İki tarafta da program açık olduğu sürece PIN sormadan otomatik bağlanır. 2D Haritada dilediğiniz gibi konumlandırabilirsiniz."
                    : "✅ Çift taraflı 6 haneli PIN onayı tamamlandı! Sağdaki 2D Haritada istediğiniz monitörün kenarına sürükleyebilirsiniz.",
            PeerPairingState.OutboundPinVerified =>
                $"⏳ Karşı kod doğrulandı! Şimdi '{p.DeviceName}' üzerinde sizin kodunuzu ({_network.PairingPin}) onaylayın.",
            PeerPairingState.InboundPinVerified =>
                $"🔔 Karşı taraf sizin kodunuzu doğruladı! Şimdi onun 6 haneli kodunu{simHint} girip onaylayın:",
            _ =>
                p.IsTrusted
                    ? $"⭐ Güvenilir cihaz aranıyor ve otomatik bağlanılıyor... Veya PIN ile manuel doğrulamak için kodu{simHint} girin."
                    : $"'{p.DeviceName}' ekranındaki 6 haneli kodu{simHint} girin ve karşı cihazda da sizin kodunuzu ({_network.PairingPin}) onaylayın."
        };

        if (!string.IsNullOrEmpty(p.SimulatedLocalPin) && !p.RemoteEnteredMyPinVerified)
        {
            SimConfirmOurPinBtn.Visibility = Visibility.Visible;
            SimConfirmOurPinBtn.Content = $"📲 [Sim] Karşı Cihazda Kodumu ({_network.PairingPin}) Onayla";
        }
        else
        {
            SimConfirmOurPinBtn.Visibility = Visibility.Collapsed;
        }
    }

    // =========================================================================
    // 2D Interactive Multi-Monitor Display Arrangement Canvas + Zoom / Auto-Fit
    // =========================================================================

    private void DisplayArrangementCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateCanvasScaleTransformOrigin();
        RedrawDisplayArrangementCanvas();
    }

    private void CanvasZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CanvasScaleTransform == null || CanvasZoomPercentText == null)
            return;

        double scale = e.NewValue;
        UpdateCanvasScaleTransformOrigin();
        CanvasScaleTransform.ScaleX = scale;
        CanvasScaleTransform.ScaleY = scale;
        CanvasZoomPercentText.Text = $"{scale * 100:F0}%";
    }

    private void CanvasContainer_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        double delta = e.Delta > 0 ? 0.08 : -0.08;
        CanvasZoomSlider.Value = Math.Clamp(CanvasZoomSlider.Value + delta, CanvasZoomSlider.Minimum, CanvasZoomSlider.Maximum);
        e.Handled = true;
    }

    private void FitCanvasViewBtn_Click(object sender, RoutedEventArgs e)
    {
        int pairedCount = _topology.GetConfiguredPeers().Count(p => p.IsMutuallyPaired);
        int localMonCount = _topology.LocalMonitors.Count;
        CanvasZoomSlider.Value = (pairedCount >= 4 || localMonCount >= 3) ? 0.78 : 0.95;
        RedrawDisplayArrangementCanvas();
    }

    private void ToggleLocalMonitorsTestBtn_Click(object sender, RoutedEventArgs e)
    {
        _simLocalMonitorStep = (_simLocalMonitorStep + 1) % 3;
        var hardwareMonitors = Win32MonitorEnumerator.EnumerateLocalMonitors();
        var primary = hardwareMonitors.FirstOrDefault(m => m.IsPrimary) ?? hardwareMonitors[0];

        if (_simLocalMonitorStep == 0)
        {
            _topology.UpdateLocalMonitors(hardwareMonitors);
            _network.LocalMonitors = hardwareMonitors;
            AppendLog($"[Çoklu Monitör] Gerçek donanım ekranlarına dönüldü ({hardwareMonitors.Count} monitör).");
        }
        else if (_simLocalMonitorStep == 1)
        {
            // Dual-Monitor Setup: Primary + Right Secondary (1920x1080)
            var dualList = new List<PhysicalMonitorDescriptor>
            {
                primary,
                new()
                {
                    MonitorId = "DISPLAY2",
                    Name = "Monitör 2 (HDMI-1 Sağ)",
                    VirtualX = primary.Right,
                    VirtualY = primary.VirtualY,
                    Width = 1920,
                    Height = 1080,
                    ScaleFactor = 1.0,
                    IsPrimary = false,
                    IsSimulated = true
                }
            };
            _topology.UpdateLocalMonitors(dualList);
            _network.LocalMonitors = dualList;
            AppendLog("[Çoklu Monitör Testi] 2'li Monitör (Dual-Monitor: Ana Ekran + Sağ 1920x1080) aktif! İç birleşim çizgisinde imleç serbest geçer.");
        }
        else
        {
            // Triple-Monitor Setup: Left Portrait (1080x1920) + Primary + Right Secondary (1920x1080)
            var tripleList = new List<PhysicalMonitorDescriptor>
            {
                primary,
                new()
                {
                    MonitorId = "DISPLAY2",
                    Name = "Monitör 2 (HDMI-1 Sağ)",
                    VirtualX = primary.Right,
                    VirtualY = primary.VirtualY,
                    Width = 1920,
                    Height = 1080,
                    ScaleFactor = 1.0,
                    IsPrimary = false,
                    IsSimulated = true
                },
                new()
                {
                    MonitorId = "DISPLAY3",
                    Name = "Monitör 3 (DP-2 Dikey)",
                    VirtualX = primary.VirtualX - 1080,
                    VirtualY = primary.VirtualY - 240,
                    Width = 1080,
                    Height = 1920,
                    ScaleFactor = 1.0,
                    IsPrimary = false,
                    IsSimulated = true
                }
            };
            _topology.UpdateLocalMonitors(tripleList);
            _network.LocalMonitors = tripleList;
            CanvasZoomSlider.Value = 0.82;
            AppendLog("[Çoklu Monitör Testi] 3'lü Monitör (Sol Dikey 1080x1920 + Ana Ekran + Sağ 1920x1080) aktif! Uzak cihazları istediğiniz monitöre sürükleyebilirsiniz.");
        }

        // Reset custom canvas positions so peers re-align around the updated monitor layout
        foreach (var peer in _topology.GetConfiguredPeers())
        {
            peer.HasCustomCanvasPosition = false;
            peer.AttachedLocalMonitorId = _topology.GetOutermostMonitorForEdge(peer.AssignedEdgeOnLocal).MonitorId;
        }

        _ = _network.BroadcastTopologySyncAsync();
        RefreshPeersList();
        RedrawDisplayArrangementCanvas();
    }

    private void UpdateCanvasScaleTransformOrigin()
    {
        if (DisplayArrangementCanvas == null || CanvasScaleTransform == null)
            return;

        DisplayArrangementCanvas.RenderTransformOrigin = new Point(0.5, 0.5);
    }

    /// <summary>
    /// Computes the proportional 2D Canvas rectangles for all physical monitors of the local computer.
    /// </summary>
    private List<(PhysicalMonitorDescriptor Monitor, double Left, double Top, double Width, double Height)> GetLocalMonitorBoxesOnCanvas()
    {
        double cw = Math.Max(420, DisplayArrangementCanvas.ActualWidth);
        double ch = Math.Max(280, DisplayArrangementCanvas.ActualHeight);
        var monitors = _topology.LocalMonitors;

        if (monitors.Count <= 1)
        {
            var m = monitors.FirstOrDefault() ?? new PhysicalMonitorDescriptor { MonitorId = "DISPLAY1", Name = "Birincil Ekran", Width = 1920, Height = 1080, IsPrimary = true };
            double primW = 164;
            double primH = 98;
            double primLeft = (cw - primW) / 2.0;
            double primTop = (ch - primH) / 2.0;
            return [(m, primLeft, primTop, primW, primH)];
        }

        int minVx = monitors.Min(m => m.VirtualX);
        int minVy = monitors.Min(m => m.VirtualY);
        int maxVx = monitors.Max(m => m.Right);
        int maxVy = monitors.Max(m => m.Bottom);

        double totalVw = Math.Max(800, maxVx - minVx);
        double totalVh = Math.Max(600, maxVy - minVy);

        double maxClusterW = Math.Min(360, cw * 0.56);
        double maxClusterH = Math.Min(210, ch * 0.54);
        double scale = Math.Min(maxClusterW / totalVw, maxClusterH / totalVh);

        double clusterW = totalVw * scale;
        double clusterH = totalVh * scale;
        double clusterLeft = (cw - clusterW) / 2.0;
        double clusterTop = (ch - clusterH) / 2.0;

        var list = new List<(PhysicalMonitorDescriptor Monitor, double Left, double Top, double Width, double Height)>(monitors.Count);
        foreach (var m in monitors)
        {
            double l = clusterLeft + (m.VirtualX - minVx) * scale;
            double t = clusterTop + (m.VirtualY - minVy) * scale;
            double w = Math.Max(64, m.Width * scale);
            double h = Math.Max(54, m.Height * scale);
            list.Add((m, l, t, w, h));
        }

        return list;
    }

    private (double Width, double Height) GetScaledPeerBoxSize(PeerDeviceNode peer)
    {
        bool isPortrait = peer.ScreenHeight > peer.ScreenWidth;
        if (isPortrait)
        {
            return (62, 106); // Vertical Android Phone
        }
        if (peer.Platform.Contains("android", StringComparison.OrdinalIgnoreCase))
        {
            return (104, 70); // Android Tablet
        }
        if (peer.RemoteMonitorCount > 1)
        {
            return (148, 82); // Multi-Monitor Remote Desktop (Windows / Nobara KDE)
        }
        return (132, 78); // Single-Monitor Desktop or Laptop
    }

    private void RedrawDisplayArrangementCanvas()
    {
        if (DisplayArrangementCanvas == null || DisplayArrangementCanvas.ActualWidth < 50)
            return;

        DisplayArrangementCanvas.Children.Clear();
        DrawCanvasGridLines(DisplayArrangementCanvas.ActualWidth, DisplayArrangementCanvas.ActualHeight);

        var localBoxes = GetLocalMonitorBoxesOnCanvas();

        // 1. Draw all Local Physical Monitors of this Computer
        foreach (var (mon, mLeft, mTop, mW, mH) in localBoxes)
        {
            var monBorder = new Border
            {
                Width = mW,
                Height = mH,
                Background = mon.IsPrimary
                    ? new SolidColorBrush(Color.FromRgb(23, 37, 84))
                    : new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                BorderBrush = _inputEngine.ActiveRemotePeer == null
                    ? new SolidColorBrush(Color.FromRgb(34, 197, 94))
                    : new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                BorderThickness = new Thickness(mon.IsPrimary ? 2.5 : 1.8),
                CornerRadius = new CornerRadius(7),
                ToolTip = $"Bu Bilgisayar — {mon.Name}\nKonum: ({mon.VirtualX}, {mon.VirtualY}) | Çözünürlük: {mon.Width}x{mon.Height} | Ölçek: %{mon.ScaleFactor * 100:F0}"
            };

            var monStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(3)
            };

            monStack.Children.Add(new TextBlock
            {
                Text = mon.IsPrimary ? $"🪟 {mon.MonitorId} (Ana)" : $"🖥️ {mon.MonitorId}",
                FontWeight = FontWeights.Bold,
                FontSize = localBoxes.Count > 1 ? 10.5 : 12,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            monStack.Children.Add(new TextBlock
            {
                Text = $"{mon.Width}x{mon.Height}",
                FontSize = localBoxes.Count > 1 ? 9.5 : 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(147, 197, 253)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0)
            });

            if (mon.IsPrimary)
            {
                monStack.Children.Add(new TextBlock
                {
                    Text = _inputEngine.ActiveRemotePeer == null ? "● İmleç Yerelde" : "○ Uzak Ekranda",
                    FontSize = 9.5,
                    Foreground = _inputEngine.ActiveRemotePeer == null
                        ? new SolidColorBrush(Color.FromRgb(74, 222, 128))
                        : new SolidColorBrush(Color.FromRgb(156, 163, 175)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            monBorder.Child = monStack;
            Canvas.SetLeft(monBorder, mLeft);
            Canvas.SetTop(monBorder, mTop);
            DisplayArrangementCanvas.Children.Add(monBorder);
        }

        // Draw Internal Monitor Seam indicators between adjacent local physical monitors
        for (int i = 0; i < localBoxes.Count; i++)
        {
            for (int j = i + 1; j < localBoxes.Count; j++)
            {
                var a = localBoxes[i];
                var b = localBoxes[j];
                if (Math.Abs((a.Left + a.Width) - b.Left) < 3.0 || Math.Abs((b.Left + b.Width) - a.Left) < 3.0)
                {
                    double seamX = Math.Abs((a.Left + a.Width) - b.Left) < 3.0 ? b.Left : a.Left;
                    double seamTop = Math.Max(a.Top, b.Top) + 4;
                    double seamBottom = Math.Min(a.Top + a.Height, b.Top + b.Height) - 4;
                    if (seamBottom > seamTop)
                    {
                        DisplayArrangementCanvas.Children.Add(new Line
                        {
                            X1 = seamX, Y1 = seamTop, X2 = seamX, Y2 = seamBottom,
                            Stroke = new SolidColorBrush(Color.FromRgb(16, 185, 129)),
                            StrokeThickness = 3,
                            StrokeDashArray = new DoubleCollection([2, 2]),
                            ToolTip = "İç Monitör Birleşim Kenarı (İmleç işletim sistemi tarafından yerel ekranlar arasında serbestçe geçirilir)"
                        });
                    }
                }
            }
        }

        // 2. Draw all Mutually Paired Peer Display Boxes & Shared Portal Lines
        var pairedPeers = _topology.GetConfiguredPeers().Where(p => p.IsMutuallyPaired).ToList();
        foreach (var peer in pairedPeers)
        {
            var (boxW, boxH) = GetScaledPeerBoxSize(peer);
            peer.CanvasWidth = boxW;
            peer.CanvasHeight = boxH;

            var attachedMonBox = localBoxes.FirstOrDefault(b => b.Monitor.MonitorId == peer.AttachedLocalMonitorId);
            if (attachedMonBox.Monitor == null)
            {
                var outermost = _topology.GetOutermostMonitorForEdge(peer.AssignedEdgeOnLocal);
                attachedMonBox = localBoxes.FirstOrDefault(b => b.Monitor.MonitorId == outermost.MonitorId);
                if (attachedMonBox.Monitor == null)
                    attachedMonBox = localBoxes[0];
                peer.AttachedLocalMonitorId = attachedMonBox.Monitor.MonitorId;
            }

            if (!peer.HasCustomCanvasPosition)
            {
                ComputeCanvasPosFromEdgeAndSegment(
                    peer,
                    attachedMonBox.Left,
                    attachedMonBox.Top,
                    attachedMonBox.Width,
                    attachedMonBox.Height,
                    boxW,
                    boxH);
            }

            DrawSharedEdgePortalLine(
                peer,
                attachedMonBox.Left,
                attachedMonBox.Top,
                attachedMonBox.Width,
                attachedMonBox.Height);

            bool isSelected = _selectedPeer?.DeviceId == peer.DeviceId;
            bool isCursorHere = _inputEngine.ActiveRemotePeer?.DeviceId == peer.DeviceId;

            string multiMonInfo = peer.RemoteMonitorCount > 1
                ? $" ({peer.RemoteMonitorCount} Monitörlü Masaüstü)"
                : $" ({peer.ScreenWidth}x{peer.ScreenHeight})";

            var peerBorder = new Border
            {
                Width = boxW,
                Height = boxH,
                Background = isCursorHere
                    ? new SolidColorBrush(Color.FromRgb(6, 78, 59))
                    : isSelected
                        ? new SolidColorBrush(Color.FromRgb(30, 58, 138))
                        : new SolidColorBrush(Color.FromRgb(31, 41, 55)),
                BorderBrush = isCursorHere
                    ? new SolidColorBrush(Color.FromRgb(74, 222, 128))
                    : isSelected
                        ? new SolidColorBrush(Color.FromRgb(56, 189, 248))
                        : new SolidColorBrush(Color.FromRgb(107, 114, 128)),
                BorderThickness = new Thickness(isSelected || isCursorHere ? 2.2 : 1.5),
                CornerRadius = new CornerRadius(7),
                Cursor = Cursors.SizeAll,
                Tag = peer,
                ToolTip = $"{peer.DeviceName}{multiMonInfo}\nBağlı Yerel Monitör: {peer.AttachedLocalMonitorId} — {FormatEdgeTr(peer.AssignedEdgeOnLocal)} Kenar (%{peer.EdgeOffsetStart * 100:F0} - %{peer.EdgeOffsetEnd * 100:F0})\nSürükleyerek herhangi bir yerel monitörün dış kenarına yapıştırabilirsiniz."
            };

            var stack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(4)
            };

            stack.Children.Add(new TextBlock
            {
                Text = $"{GetPlatformIcon(peer.Platform)} {peer.DeviceName}",
                FontWeight = FontWeights.SemiBold,
                FontSize = 10.5,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = boxW - 10,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            string subLabel = localBoxes.Count > 1
                ? $"{peer.AttachedLocalMonitorId} {FormatEdgeShortTr(peer.AssignedEdgeOnLocal)} %{peer.EdgeOffsetStart * 100:F0}-{peer.EdgeOffsetEnd * 100:F0}"
                : $"{FormatEdgeShortTr(peer.AssignedEdgeOnLocal)} %{peer.EdgeOffsetStart * 100:F0}-{peer.EdgeOffsetEnd * 100:F0}";

            stack.Children.Add(new TextBlock
            {
                Text = subLabel,
                FontSize = 9.2,
                Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
            });

            if (peer.RemoteMonitorCount > 1)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = $"🖥️ {peer.RemoteMonitorCount} Monitör",
                    FontSize = 8.8,
                    Foreground = new SolidColorBrush(Color.FromRgb(167, 243, 208)),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }

            if (isCursorHere)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = "● AKTİF",
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128)),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }

            peerBorder.Child = stack;

            peerBorder.MouseLeftButtonDown += PeerBox_MouseLeftButtonDown;
            peerBorder.MouseMove += PeerBox_MouseMove;
            peerBorder.MouseLeftButtonUp += PeerBox_MouseLeftButtonUp;

            Canvas.SetLeft(peerBorder, peer.CanvasX);
            Canvas.SetTop(peerBorder, peer.CanvasY);
            DisplayArrangementCanvas.Children.Add(peerBorder);
        }
    }

    private void DrawCanvasGridLines(double width, double height)
    {
        var gridBrush = new SolidColorBrush(Color.FromArgb(22, 148, 163, 184));
        for (double x = 40; x < width; x += 40)
        {
            DisplayArrangementCanvas.Children.Add(new Line
            {
                X1 = x, Y1 = 0, X2 = x, Y2 = height,
                Stroke = gridBrush, StrokeThickness = 1
            });
        }
        for (double y = 40; y < height; y += 40)
        {
            DisplayArrangementCanvas.Children.Add(new Line
            {
                X1 = 0, Y1 = y, X2 = width, Y2 = y,
                Stroke = gridBrush, StrokeThickness = 1
            });
        }
    }

    private void ComputeCanvasPosFromEdgeAndSegment(
        PeerDeviceNode peer,
        double monLeft,
        double monTop,
        double monW,
        double monH,
        double boxW,
        double boxH)
    {
        const double gap = 6.0;
        float mid = (peer.EdgeOffsetStart + peer.EdgeOffsetEnd) / 2.0f;

        switch (peer.AssignedEdgeOnLocal)
        {
            case ScreenEdge.Left:
                peer.CanvasX = monLeft - boxW - gap;
                peer.CanvasY = monTop + mid * monH - boxH / 2.0;
                break;
            case ScreenEdge.Right:
                peer.CanvasX = monLeft + monW + gap;
                peer.CanvasY = monTop + mid * monH - boxH / 2.0;
                break;
            case ScreenEdge.Top:
                peer.CanvasX = monLeft + mid * monW - boxW / 2.0;
                peer.CanvasY = monTop - boxH - gap;
                break;
            case ScreenEdge.Bottom:
                peer.CanvasX = monLeft + mid * monW - boxW / 2.0;
                peer.CanvasY = monTop + monH + gap;
                break;
        }
        peer.HasCustomCanvasPosition = true;
    }

    private void DrawSharedEdgePortalLine(
        PeerDeviceNode peer,
        double monLeft,
        double monTop,
        double monW,
        double monH)
    {
        double x1 = 0, y1 = 0, x2 = 0, y2 = 0;
        switch (peer.AssignedEdgeOnLocal)
        {
            case ScreenEdge.Left:
                x1 = x2 = monLeft - 2;
                y1 = monTop + peer.EdgeOffsetStart * monH;
                y2 = monTop + peer.EdgeOffsetEnd * monH;
                break;
            case ScreenEdge.Right:
                x1 = x2 = monLeft + monW + 2;
                y1 = monTop + peer.EdgeOffsetStart * monH;
                y2 = monTop + peer.EdgeOffsetEnd * monH;
                break;
            case ScreenEdge.Top:
                y1 = y2 = monTop - 2;
                x1 = monLeft + peer.EdgeOffsetStart * monW;
                x2 = monLeft + peer.EdgeOffsetEnd * monW;
                break;
            case ScreenEdge.Bottom:
                y1 = y2 = monTop + monH + 2;
                x1 = monLeft + peer.EdgeOffsetStart * monW;
                x2 = monLeft + peer.EdgeOffsetEnd * monW;
                break;
        }

        var portalLine = new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
            StrokeThickness = 4.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        DisplayArrangementCanvas.Children.Add(portalLine);
    }

    private void PeerBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is PeerDeviceNode peer)
        {
            _selectedPeer = peer;
            _draggingPeer = peer;
            _draggingBorder = border;
            _dragMouseOffset = e.GetPosition(border);
            border.CaptureMouse();

            RefreshPeersList();
            UpdateSelectedPeerPairingPanel();
            e.Handled = true;
        }
    }

    private void PeerBox_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingPeer != null && _draggingBorder != null && e.LeftButton == MouseButtonState.Pressed)
        {
            Point canvasPt = e.GetPosition(DisplayArrangementCanvas);
            double newLeft = canvasPt.X - _dragMouseOffset.X;
            double newTop = canvasPt.Y - _dragMouseOffset.Y;

            var localBoxes = GetLocalMonitorBoxesOnCanvas();
            _topology.SnapPeerBoxToLocalMonitorsOnCanvas(
                _draggingPeer,
                newLeft,
                newTop,
                _draggingBorder.Width,
                _draggingBorder.Height,
                localBoxes);

            Canvas.SetLeft(_draggingBorder, _draggingPeer.CanvasX);
            Canvas.SetTop(_draggingBorder, _draggingPeer.CanvasY);

            CanvasSelectionInfoText.Text =
                $"🎯 {_draggingPeer.DeviceName} -> {_draggingPeer.AttachedLocalMonitorId} {FormatEdgeTr(_draggingPeer.AssignedEdgeOnLocal)} Kenar (%{_draggingPeer.EdgeOffsetStart * 100:F0} - %{_draggingPeer.EdgeOffsetEnd * 100:F0} kesiti)";
        }
    }

    private void PeerBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingBorder != null && _draggingPeer != null)
        {
            _draggingBorder.ReleaseMouseCapture();
            _network.SavePeerTopologyToTrustedStore(_draggingPeer);
            AppendLog(
                $"[Ekran Konfigürasyonu] '{_draggingPeer.DeviceName}' -> {_draggingPeer.AttachedLocalMonitorId} ({FormatEdgeTr(_draggingPeer.AssignedEdgeOnLocal)} kenar, %{_draggingPeer.EdgeOffsetStart * 100:F0}-%{_draggingPeer.EdgeOffsetEnd * 100:F0}) hizalandı.");
            _draggingBorder = null;
            _draggingPeer = null;
            RefreshPeersList();
            RedrawDisplayArrangementCanvas();
            e.Handled = true;
        }
    }

    // =========================================================================
    // Quick Edge Assignment & Auto-Arrange Buttons
    // =========================================================================

    private void QuickAssignLeft_Click(object sender, RoutedEventArgs e) => QuickAssignSelectedPeer(ScreenEdge.Left);
    private void QuickAssignRight_Click(object sender, RoutedEventArgs e) => QuickAssignSelectedPeer(ScreenEdge.Right);
    private void QuickAssignTop_Click(object sender, RoutedEventArgs e) => QuickAssignSelectedPeer(ScreenEdge.Top);
    private void QuickAssignBottom_Click(object sender, RoutedEventArgs e) => QuickAssignSelectedPeer(ScreenEdge.Bottom);

    private void QuickAssignSelectedPeer(ScreenEdge edge)
    {
        if (_selectedPeer == null || !_selectedPeer.IsMutuallyPaired)
        {
            AppendLog("[Uyarı] Kenar atamak için önce çift taraflı 6 haneli PIN onayı tamamlanmış bir cihaz seçin.");
            return;
        }

        var oldEdge = _selectedPeer.AssignedEdgeOnLocal;
        var outermostMon = _topology.GetOutermostMonitorForEdge(edge);
        _selectedPeer.HasCustomCanvasPosition = false;
        _selectedPeer.AttachedLocalMonitorId = outermostMon.MonitorId;
        _topology.AssignPeerToEdgeSegment(_selectedPeer, edge, 0.0f, 1.0f, outermostMon.MonitorId);
        if (oldEdge != edge && oldEdge != ScreenEdge.None)
        {
            DistributePeersOnEdge(oldEdge);
        }
        DistributePeersOnEdge(edge);
        _network.SavePeerTopologyToTrustedStore(_selectedPeer);

        RefreshPeersList();
        RedrawDisplayArrangementCanvas();
        AppendLog($"[Ekran Konfigürasyonu] '{_selectedPeer.DeviceName}' -> {outermostMon.MonitorId} {FormatEdgeTr(edge)} kenara taşındı.");
    }

    private void AutoArrangeScreensBtn_Click(object sender, RoutedEventArgs e)
    {
        var paired = _topology.GetConfiguredPeers().Where(p => p.IsMutuallyPaired).ToList();
        if (paired.Count == 0)
            return;

        ScreenEdge[] edgesOrder = [ScreenEdge.Right, ScreenEdge.Left, ScreenEdge.Bottom, ScreenEdge.Top];
        for (int i = 0; i < paired.Count; i++)
        {
            var edge = edgesOrder[i % edgesOrder.Length];
            paired[i].AssignedEdgeOnLocal = edge;
            paired[i].AttachedLocalMonitorId = _topology.GetOutermostMonitorForEdge(edge).MonitorId;
        }

        foreach (var edge in edgesOrder)
        {
            DistributePeersOnEdge(edge);
        }

        foreach (var p in paired)
        {
            _network.SavePeerTopologyToTrustedStore(p);
        }

        RefreshPeersList();
        RedrawDisplayArrangementCanvas();
        AppendLog("[Ekran Konfigürasyonu] Tüm bağlı ekranlar dış monitör kenarlarına dengeli şekilde dağıtıldı.");
    }

    // =========================================================================
    // Mutual 6-Digit PIN & Multi-Device Simulator Handlers
    // =========================================================================

    private void RegeneratePinBtn_Click(object sender, RoutedEventArgs e)
    {
        string newPin = _network.RegenerateLocalPin();
        PinCodeText.Text = FormatPinDisplay(newPin);
        UpdateSelectedPeerPairingPanel();
    }

    private async void VerifyRemotePinBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPeer == null)
        {
            AppendLog("[Uyarı] Lütfen önce sol listeden eşleşmek istediğiniz cihazı seçin.");
            return;
        }

        string enteredPin = RemotePinInputBox.Text;
        bool remember = RememberDeviceCheckBox.IsChecked ?? true;
        var (_, _, message) = await _network.SubmitRemotePinForPairingAsync(_selectedPeer, enteredPin, remember);
        CanvasSelectionInfoText.Text = message;
        if (_selectedPeer.IsMutuallyPaired)
        {
            EnsurePeerPlacedOnTopology(_selectedPeer);
            if (remember)
            {
                _network.SavePeerTopologyToTrustedStore(_selectedPeer);
            }
        }
        UpdateSelectedPeerPairingPanel();
        RefreshPeersList();
        RedrawDisplayArrangementCanvas();
    }

    private void RevokeTrustBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPeer == null)
            return;

        _network.RevokeTrustForPeer(_selectedPeer);
        CanvasSelectionInfoText.Text = $"🗑️ '{_selectedPeer.DeviceName}' için cihaz güveni kaldırıldı.";
        UpdateSelectedPeerPairingPanel();
        RefreshPeersList();
        RedrawDisplayArrangementCanvas();
    }

    private void SimConfirmOurPinBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPeer == null)
            return;

        var (_, _, msg) = _network.VerifyInboundPinFromSimulatedPeer(_selectedPeer, _network.PairingPin);
        CanvasSelectionInfoText.Text = msg;
        if (_selectedPeer.IsMutuallyPaired)
        {
            EnsurePeerPlacedOnTopology(_selectedPeer);
        }
        UpdateSelectedPeerPairingPanel();
        RefreshPeersList();
        RedrawDisplayArrangementCanvas();
    }

    private void AddMultiSimBtn_Click(object sender, RoutedEventArgs e)
    {
        _simDeviceCounter++;
        int simPort = 42860 + _simDeviceCounter;
        string simPin = RandomNumberGenerator.GetInt32(100000, 999999).ToString();

        (string name, string platform, int w, int h, ScreenEdge preferredEdge) = (_simDeviceCounter % 4) switch
        {
            1 => ($"Android Telefon #{_simDeviceCounter}", "android", 1080, 2400, ScreenEdge.Right),
            2 => ($"Nobara KDE PC #{_simDeviceCounter}", "linux-nobara", 4480, 1440, ScreenEdge.Left),
            3 => ($"Windows İş İstasyonu #{_simDeviceCounter}", "windows", 3840, 1080, ScreenEdge.Right),
            _ => ($"Android Tablet #{_simDeviceCounter}", "android", 2560, 1600, ScreenEdge.Bottom)
        };

        var simUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, simPort));
        _simSockets.Add(simUdp);

        var simPeer = _network.RegisterManualPeer(
            "127.0.0.1",
            name,
            platform,
            udpPort: simPort,
            tcpPort: _network.ControlTcpPort,
            screenWidth: w,
            screenHeight: h,
            customDeviceId: $"sim-{_simDeviceCounter}",
            simulatedPin: simPin,
            autoMutuallyPair: false);

        if (platform == "linux-nobara")
        {
            simPeer.RemoteMonitors =
            [
                new PhysicalMonitorDescriptor { MonitorId = "DP-1", Name = "DP-1 (Ana)", VirtualX = 0, VirtualY = 0, Width = 2560, Height = 1440, ScaleFactor = 1.25, IsPrimary = true },
                new PhysicalMonitorDescriptor { MonitorId = "HDMI-A-1", Name = "HDMI-A-1", VirtualX = 2560, VirtualY = 0, Width = 1920, Height = 1080, ScaleFactor = 1.0, IsPrimary = false }
            ];
        }
        else if (platform == "windows")
        {
            simPeer.RemoteMonitors =
            [
                new PhysicalMonitorDescriptor { MonitorId = "DISPLAY1", Name = "DISPLAY1", VirtualX = 0, VirtualY = 0, Width = 1920, Height = 1080, ScaleFactor = 1.0, IsPrimary = true },
                new PhysicalMonitorDescriptor { MonitorId = "DISPLAY2", Name = "DISPLAY2", VirtualX = 1920, VirtualY = 0, Width = 1920, Height = 1080, ScaleFactor = 1.0, IsPrimary = false }
            ];
        }

        simPeer.AssignedEdgeOnLocal = preferredEdge;
        simPeer.AttachedLocalMonitorId = _topology.GetOutermostMonitorForEdge(preferredEdge).MonitorId;
        _selectedPeer = simPeer;
        RemotePinInputBox.Text = simPin;

        _ = Task.Run(() => RunSimulatedDeviceNodeLoopAsync(simPeer, simUdp, _simCts.Token));

        RefreshPeersList();
        UpdateSelectedPeerPairingPanel();
        AppendLog(
            $"[Simülatör] '{name}' ({simPeer.RemoteMonitorCount} ekran, {w}x{h}) eklendi (Kod: {simPin}). Sol alttan '✅ Doğrula' ve '📲 Karşı Cihazda Onayla' ile eşleştirin.");
    }

    private async Task RunSimulatedDeviceNodeLoopAsync(PeerDeviceNode simPeer, UdpClient simUdp, CancellationToken ct)
    {
        float simX = simPeer.ScreenWidth / 2f;
        float simY = simPeer.ScreenHeight / 2f;
        float simW = simPeer.ScreenWidth;
        float simH = simPeer.ScreenHeight;
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
                            AppendLog($"[{simPeer.DeviceName}] İmleç ekrana girdi -> ({simX:F0}, {simY:F0})");
                        }
                        break;

                    case PacketType.MouseMove:
                        if (WirePacketCodec.TryDecodeMouseMove(buf, out var mm))
                        {
                            simX += mm.DeltaX;
                            simY += mm.DeltaY;
                            moveCount++;

                            ScreenEdge returnEdgeOnLocal = ScreenEdge.None;
                            float normPos = 0.5f;
                            var edge = simPeer.AssignedEdgeOnLocal;

                            if (edge == ScreenEdge.Right && simX < 0)
                            {
                                returnEdgeOnLocal = ScreenEdge.Right;
                                normPos = Math.Clamp(simY / simH, 0f, 1f);
                            }
                            else if (edge == ScreenEdge.Left && simX > simW)
                            {
                                returnEdgeOnLocal = ScreenEdge.Left;
                                normPos = Math.Clamp(simY / simH, 0f, 1f);
                            }
                            else if (edge == ScreenEdge.Bottom && simY < 0)
                            {
                                returnEdgeOnLocal = ScreenEdge.Bottom;
                                normPos = Math.Clamp(simX / simW, 0f, 1f);
                            }
                            else if (edge == ScreenEdge.Top && simY > simH)
                            {
                                returnEdgeOnLocal = ScreenEdge.Top;
                                normPos = Math.Clamp(simX / simW, 0f, 1f);
                            }

                            simX = Math.Clamp(simX, 0f, simW);
                            simY = Math.Clamp(simY, 0f, simH);

                            if (returnEdgeOnLocal != ScreenEdge.None)
                            {
                                byte[] backPkt = WirePacketCodec.EncodeEdgeHandOff(
                                    new EdgeHandOffPacket(returnEdgeOnLocal, false, normPos));
                                await simUdp.SendAsync(backPkt, backPkt.Length, new IPEndPoint(IPAddress.Loopback, _network.InputUdpPort))
                                    .ConfigureAwait(false);
                            }
                            else if (moveCount % 30 == 0)
                            {
                                AppendLog($"[{simPeer.DeviceName}] İmleç: ({simX:F0}, {simY:F0}) / {simW:F0}x{simH:F0}");
                            }
                        }
                        break;

                    case PacketType.MouseButton:
                        if (WirePacketCodec.TryDecodeMouseButton(buf, out var mb) && mb.IsPressed)
                        {
                            AppendLog($"[{simPeer.DeviceName}] Tıklama: {mb.Button} @ ({simX:F0}, {simY:F0})");
                        }
                        break;

                    case PacketType.KeyEvent:
                        if (WirePacketCodec.TryDecodeKeyEvent(buf, out var ke) && ke.IsPressed)
                        {
                            string chDisp = ke.UnicodeChar != '\0' ? $"'{ke.UnicodeChar}'" : $"VK_{ke.VirtualKey}";
                            AppendLog($"[{simPeer.DeviceName}] Klavye: {chDisp} (Mod: {ke.Modifiers})");
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

    // =========================================================================
    // Active Focus, Lock & Peer Selection
    // =========================================================================

    private void OnActiveTargetChanged(PeerDeviceNode? activePeer, ScreenEdge edge, float normalizedPos)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (activePeer != null)
            {
                ActiveFocusBadge.Background = new SolidColorBrush(Color.FromRgb(30, 58, 138));
                ActiveFocusBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248));
                ActiveFocusText.Foreground = new SolidColorBrush(Color.FromRgb(125, 211, 252));
                ActiveFocusText.Text = $"{GetPlatformIcon(activePeer.Platform)} Kontrol: {activePeer.DeviceName}";
                AppendLog($"[Kenar Geçişi] ➡️ İmleç ve klavye '{activePeer.DeviceName}' ekranına geçti.");
            }
            else
            {
                ActiveFocusBadge.Background = new SolidColorBrush(Color.FromRgb(30, 58, 47));
                ActiveFocusBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                ActiveFocusText.Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128));
                ActiveFocusText.Text = "🪟 Kontrol: Bu Bilgisayar (Yerel)";
                AppendLog("[Kenar Geçişi] ⬅️ İmleç ve klavye ana ekrana geri döndü.");
            }
            RedrawDisplayArrangementCanvas();
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
                LockStateText.Text = "🔒 Geçiş: KİLİTLİ (Oyun Modu)";
            }
            else
            {
                LockStateBadge.Background = new SolidColorBrush(Color.FromRgb(31, 41, 55));
                LockStateBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(48, 54, 61));
                LockStateText.Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175));
                LockStateText.Text = "🔓 Geçiş: Açık (ScrollLock / Ctrl+Alt+L)";
            }
        });
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
        AppendLog("[Keşif] Yerel ağdaki tüm bilgisayar ve Android cihazlara keşif sinyali gönderiliyor...");
        await _network.BroadcastDiscoveryBeaconAsync();
    }

    private void TestSwitchBtn_Click(object sender, RoutedEventArgs e)
    {
        var peer = (_selectedPeer != null && _selectedPeer.IsMutuallyPaired)
            ? _selectedPeer
            : _network.MutuallyPairedPeers.FirstOrDefault();

        if (peer == null)
        {
            AppendLog("[Uyarı] Geçiş yapmak için önce en az bir cihazla çift taraflı 6 haneli PIN onayını tamamlayın.");
            return;
        }

        var targetEntrance = SpatialTopologyEngine.GetOppositeEdge(peer.AssignedEdgeOnLocal);
        _inputEngine.SwitchControlToPeer(peer, peer.AssignedEdgeOnLocal, targetEntrance, 0.5f);
    }

    private void AddManualPeerBtn_Click(object sender, RoutedEventArgs e)
    {
        string ip = ManualIpTextBox.Text.Trim();
        if (!IPAddress.TryParse(ip, out _))
        {
            AppendLog($"[Hata] Geçersiz IP adresi: '{ip}'. Örn: 192.168.1.45");
            return;
        }

        var peer = _network.RegisterManualPeer(ip, $"Cihaz ({ip})", "android");
        _selectedPeer = peer;
        RefreshPeersList();
        UpdateSelectedPeerPairingPanel();
        AppendLog($"[Keşif] '{ip}' eklendi. Şimdi o cihazın ekranındaki 6 haneli kodu girerek doğrulayın.");
    }

    private void PeersListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int idx = PeersListBox.SelectedIndex;
        var peers = _network.DiscoveredPeers
            .OrderByDescending(p => p.IsMutuallyPaired)
            .ThenByDescending(p => p.LastSeen)
            .ToList();

        if (idx >= 0 && idx < peers.Count)
        {
            _selectedPeer = peers[idx];
            if (!string.IsNullOrEmpty(_selectedPeer.SimulatedLocalPin) && !_selectedPeer.MyEnteredPinVerifiedByRemote)
            {
                RemotePinInputBox.Text = _selectedPeer.SimulatedLocalPin;
            }
            UpdateSelectedPeerPairingPanel();
            RedrawDisplayArrangementCanvas();
        }
    }

    // =========================================================================
    // Drop Shelf & Clipboard Handlers
    // =========================================================================

    private void OnShelfItemReceived(ShelfItemEntry item)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _shelfItems.Insert(0, item);
            RefreshShelfList();
            DropShelfTabItem.Header = $"🧲 Ortak Cep ({_shelfItems.Count}) & Pano";
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

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetDataPresent(DataFormats.UnicodeText))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                await SendFilesToPairedPeersAsync(files);
            }
        }
        else if (e.Data.GetDataPresent(DataFormats.UnicodeText))
        {
            string? text = e.Data.GetData(DataFormats.UnicodeText) as string;
            if (!string.IsNullOrEmpty(text))
            {
                await _network.BroadcastClipboardTextAsync(text);
                AppendLog($"[Drop Shelf] Bırakılan metin ({text.Length} krk) tüm onaylı cihazların panosuna gönderildi.");
            }
        }
    }

    private async void PickFileToSendBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Bağlı Cihazlara Gönderilecek Dosyaları Seçin",
            Multiselect = true
        };

        if (dlg.ShowDialog(this) == true && dlg.FileNames.Length > 0)
        {
            await SendFilesToPairedPeersAsync(dlg.FileNames);
        }
    }

    private async Task SendFilesToPairedPeersAsync(IEnumerable<string> filePaths)
    {
        var targets = new List<PeerDeviceNode>();
        if (_selectedPeer != null && _selectedPeer.IsMutuallyPaired)
        {
            targets.Add(_selectedPeer);
        }
        else
        {
            targets.AddRange(_network.MutuallyPairedPeers);
        }

        if (targets.Count == 0)
        {
            AppendLog("[Uyarı] Dosya göndermek için önce en az bir cihazla çift taraflı 6 haneli PIN onayını tamamlayın.");
            return;
        }

        foreach (string path in filePaths)
        {
            if (File.Exists(path))
            {
                foreach (var peer in targets)
                {
                    var entry = await _network.SendFileToPeerShelfAsync(peer, path);
                    if (entry != null)
                    {
                        _shelfItems.Insert(0, entry);
                        RefreshShelfList();
                        DropShelfTabItem.Header = $"🧲 Ortak Cep ({_shelfItems.Count}) & Pano";
                    }
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

    private void ShelfItemsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
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
            if (Clipboard.ContainsText())
            {
                string txt = Clipboard.GetText();
                await _network.BroadcastClipboardTextAsync(txt);
                AppendLog($"[Evrensel Pano] Pano metni ({txt.Length} krk) tüm onaylı cihazlara gönderildi.");
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
            LatestLogTickerText.Text = message;
            TelemetryLogListBox.Items.Insert(0, message);
            while (TelemetryLogListBox.Items.Count > 150)
            {
                TelemetryLogListBox.Items.RemoveAt(TelemetryLogListBox.Items.Count - 1);
            }
        });
    }

    private static string GetPlatformIcon(string platform)
    {
        if (platform.Contains("android", StringComparison.OrdinalIgnoreCase))
            return "📱";
        if (platform.Contains("linux", StringComparison.OrdinalIgnoreCase) || platform.Contains("nobara", StringComparison.OrdinalIgnoreCase))
            return "🐧";
        return "🪟";
    }

    private static string FormatPinDisplay(string pin) =>
        pin.Length == 6 ? $"{pin[..3]} {pin[3..]}" : pin;

    private static string FormatEdgeTr(ScreenEdge edge) => edge switch
    {
        ScreenEdge.Left => "⬅️ SOL",
        ScreenEdge.Right => "SAĞ ➡️",
        ScreenEdge.Top => "⬆️ ÜST",
        ScreenEdge.Bottom => "⬇️ ALT",
        _ => "YOK"
    };

    private static string FormatEdgeShortTr(ScreenEdge edge) => edge switch
    {
        ScreenEdge.Left => "SOL",
        ScreenEdge.Right => "SAĞ",
        ScreenEdge.Top => "ÜST",
        ScreenEdge.Bottom => "ALT",
        _ => "-"
    };
}
