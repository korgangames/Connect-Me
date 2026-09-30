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
            $"Yerel IP: {ipText}  |  UDP Girdi: {_network.InputUdpPort}  |  TCP Veri & PIN: {_network.ControlTcpPort}  |  Keşif: {_network.DiscoveryPort}";

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
        AppendLog($"[Sistem] Connect Me Çoklu Cihaz Motoru hazır ({_topology.LocalWidth}x{_topology.LocalHeight}).");
        AppendLog($"[Güvenlik] Bu cihazın 6 haneli eşleşme kodu: {_network.PairingPin}. Bağlantı için iki tarafın da birbirinin kodunu onaylaması gerekir.");
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

        // Default placement based on how many devices are already arranged
        int count = configured.Count;
        ScreenEdge defaultEdge = (count % 4) switch
        {
            0 => ScreenEdge.Right,
            1 => ScreenEdge.Left,
            2 => ScreenEdge.Bottom,
            _ => ScreenEdge.Top
        };

        _topology.AssignPeerToEdgeSegment(peer, defaultEdge, 0.0f, 1.0f);
    }

    private void RefreshPeersList()
    {
        var peers = _network.DiscoveredPeers.OrderByDescending(p => p.IsMutuallyPaired).ThenByDescending(p => p.LastSeen).ToList();
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
            string stateBadge = p.PairingState switch
            {
                PeerPairingState.MutuallyPaired => $"🟢 ONAYLI [{FormatEdgeTr(p.AssignedEdgeOnLocal)} %{p.EdgeOffsetStart * 100:F0}-%{p.EdgeOffsetEnd * 100:F0}]",
                PeerPairingState.OutboundPinVerified => "🟡 KARŞI ONAY BEKLİYOR",
                PeerPairingState.InboundPinVerified => "🟠 SİZİN ONAYINIZ BEKLENİYOR",
                _ => "⚪ EŞLEŞMEDİ (PIN Gerekli)"
            };

            PeersListBox.Items.Add($"{icon} {p.DeviceName} ({p.IpAddress})  —  {stateBadge}");
        }

        if (restoreIdx >= 0)
            PeersListBox.SelectedIndex = restoreIdx;
        PeersListBox.SelectionChanged += PeersListBox_SelectionChanged;
    }

    private void UpdateSelectedPeerPairingPanel()
    {
        if (_selectedPeer == null)
        {
            SelectedPeerTitleText.Text = "🔐 Seçili Cihaz ile Çift Taraflı 6 Haneli Kod Onayı";
            SelectedPeerStatusText.Text = "Listeden bir cihaz seçin ve o cihazın ekranındaki 6 haneli kodu aşağıya girin.";
            SimConfirmOurPinBtn.Visibility = Visibility.Collapsed;
            return;
        }

        var p = _selectedPeer;
        SelectedPeerTitleText.Text = $"🔐 {GetPlatformIcon(p.Platform)} {p.DeviceName} ({p.IpAddress})";

        string simHint = !string.IsNullOrEmpty(p.SimulatedLocalPin)
            ? $" (Simülatör Ekran Kodu: {p.SimulatedLocalPin})"
            : string.Empty;

        SelectedPeerStatusText.Text = p.PairingState switch
        {
            PeerPairingState.MutuallyPaired =>
                "✅ Çift taraflı 6 haneli PIN doğrulaması tamamlandı! Sağdaki 2D Ekran Kanvasında sürükleyerek kenar konumunu ayarlayabilirsiniz.",
            PeerPairingState.OutboundPinVerified =>
                $"⏳ Karşı cihazın kodunu doğruladınız! Şimdi '{p.DeviceName}' cihazında sizin 6 haneli kodunuzun ({_network.PairingPin}) girilmesi bekleniyor.",
            PeerPairingState.InboundPinVerified =>
                $"🔔 '{p.DeviceName}' sizin 6 haneli kodunuzu ({_network.PairingPin}) doğruladı! Şimdi siz de onun 6 haneli kodunu{simHint} aşağıya girip onaylayın:",
            _ =>
                $"Bağlanmak için '{p.DeviceName}' ekranındaki 6 haneli kodu{simHint} girin VE karşı cihazda da sizin kodunuzu ({_network.PairingPin}) onaylayın."
        };

        if (!string.IsNullOrEmpty(p.SimulatedLocalPin) && !p.RemoteEnteredMyPinVerified)
        {
            SimConfirmOurPinBtn.Visibility = Visibility.Visible;
            SimConfirmOurPinBtn.Content = $"📲 [Simülatör] '{p.DeviceName}' Cihazında Benim Kodumu ({_network.PairingPin}) Onayla";
        }
        else
        {
            SimConfirmOurPinBtn.Visibility = Visibility.Collapsed;
        }
    }

    // =========================================================================
    // 2D Interactive Display Arrangement Canvas (Windows Display Settings Style)
    // =========================================================================

    private void DisplayArrangementCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RedrawDisplayArrangementCanvas();
    }

    private (double Left, double Top, double Width, double Height) GetPrimaryBoxRectOnCanvas()
    {
        double cw = Math.Max(360, DisplayArrangementCanvas.ActualWidth);
        double ch = Math.Max(260, DisplayArrangementCanvas.ActualHeight);
        double primW = 156;
        double primH = 94;
        double primLeft = (cw - primW) / 2.0;
        double primTop = (ch - primH) / 2.0;
        return (primLeft, primTop, primW, primH);
    }

    private (double Width, double Height) GetScaledPeerBoxSize(PeerDeviceNode peer)
    {
        bool isPortrait = peer.ScreenHeight > peer.ScreenWidth;
        if (isPortrait)
        {
            return (56, 102); // Vertical Android Phone
        }
        if (peer.Platform.Contains("android", StringComparison.OrdinalIgnoreCase))
        {
            return (96, 66); // Android Tablet
        }
        return (124, 74); // Windows / Nobara Linux Desktop or Laptop Monitor
    }

    private void RedrawDisplayArrangementCanvas()
    {
        if (DisplayArrangementCanvas == null || DisplayArrangementCanvas.ActualWidth < 50)
            return;

        DisplayArrangementCanvas.Children.Clear();
        var (primLeft, primTop, primW, primH) = GetPrimaryBoxRectOnCanvas();

        // Draw subtle grid background lines
        DrawCanvasGridLines(DisplayArrangementCanvas.ActualWidth, DisplayArrangementCanvas.ActualHeight);

        // 1. Draw Primary Local Monitor Box in the Center
        var primaryBorder = new Border
        {
            Width = primW,
            Height = primH,
            Background = new SolidColorBrush(Color.FromRgb(23, 37, 84)),
            BorderBrush = _inputEngine.ActiveRemotePeer == null
                ? new SolidColorBrush(Color.FromRgb(34, 197, 94))
                : new SolidColorBrush(Color.FromRgb(56, 189, 248)),
            BorderThickness = new Thickness(2.5),
            CornerRadius = new CornerRadius(8)
        };

        var primStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        primStack.Children.Add(new TextBlock
        {
            Text = "🪟 BU BİLGİSAYAR",
            FontWeight = FontWeights.Bold,
            FontSize = 12,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        primStack.Children.Add(new TextBlock
        {
            Text = $"{_topology.LocalWidth} x {_topology.LocalHeight}",
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromRgb(147, 197, 253)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0)
        });
        primStack.Children.Add(new TextBlock
        {
            Text = _inputEngine.ActiveRemotePeer == null ? "● İmleç Burada" : "○ Uzak Ekranda",
            FontSize = 10,
            Foreground = _inputEngine.ActiveRemotePeer == null
                ? new SolidColorBrush(Color.FromRgb(74, 222, 128))
                : new SolidColorBrush(Color.FromRgb(156, 163, 175)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0)
        });

        primaryBorder.Child = primStack;
        Canvas.SetLeft(primaryBorder, primLeft);
        Canvas.SetTop(primaryBorder, primTop);
        DisplayArrangementCanvas.Children.Add(primaryBorder);

        // 2. Draw all Mutually Paired Peer Display Boxes & Shared Portal Lines
        var pairedPeers = _topology.GetConfiguredPeers().Where(p => p.IsMutuallyPaired).ToList();
        foreach (var peer in pairedPeers)
        {
            var (boxW, boxH) = GetScaledPeerBoxSize(peer);
            peer.CanvasWidth = boxW;
            peer.CanvasHeight = boxH;

            if (!peer.HasCustomCanvasPosition)
            {
                ComputeCanvasPosFromEdgeAndSegment(peer, primLeft, primTop, primW, primH, boxW, boxH);
            }

            // Draw glowing shared edge portal line between Primary Screen and this Peer
            DrawSharedEdgePortalLine(peer, primLeft, primTop, primW, primH);

            bool isSelected = _selectedPeer?.DeviceId == peer.DeviceId;
            bool isCursorHere = _inputEngine.ActiveRemotePeer?.DeviceId == peer.DeviceId;

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
                ToolTip = $"{peer.DeviceName} ({peer.ScreenWidth}x{peer.ScreenHeight}) — Kenar: {FormatEdgeTr(peer.AssignedEdgeOnLocal)} (%{peer.EdgeOffsetStart * 100:F0} - %{peer.EdgeOffsetEnd * 100:F0})\nSürükleyerek başka bir kenara veya hizaya taşıyabilirsiniz."
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

            stack.Children.Add(new TextBlock
            {
                Text = $"{FormatEdgeShortTr(peer.AssignedEdgeOnLocal)} %{peer.EdgeOffsetStart * 100:F0}-{peer.EdgeOffsetEnd * 100:F0}",
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
            });

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

            // Attach Interactive Drag Events
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
        var gridBrush = new SolidColorBrush(Color.FromArgb(25, 148, 163, 184));
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
        double primLeft,
        double primTop,
        double primW,
        double primH,
        double boxW,
        double boxH)
    {
        const double gap = 4.0;
        float mid = (peer.EdgeOffsetStart + peer.EdgeOffsetEnd) / 2.0f;

        switch (peer.AssignedEdgeOnLocal)
        {
            case ScreenEdge.Left:
                peer.CanvasX = primLeft - boxW - gap;
                peer.CanvasY = primTop + mid * primH - boxH / 2.0;
                break;
            case ScreenEdge.Right:
                peer.CanvasX = primLeft + primW + gap;
                peer.CanvasY = primTop + mid * primH - boxH / 2.0;
                break;
            case ScreenEdge.Top:
                peer.CanvasX = primLeft + mid * primW - boxW / 2.0;
                peer.CanvasY = primTop - boxH - gap;
                break;
            case ScreenEdge.Bottom:
                peer.CanvasX = primLeft + mid * primW - boxW / 2.0;
                peer.CanvasY = primTop + primH + gap;
                break;
        }
        peer.HasCustomCanvasPosition = true;
    }

    private void DrawSharedEdgePortalLine(
        PeerDeviceNode peer,
        double primLeft,
        double primTop,
        double primW,
        double primH)
    {
        double x1 = 0, y1 = 0, x2 = 0, y2 = 0;
        switch (peer.AssignedEdgeOnLocal)
        {
            case ScreenEdge.Left:
                x1 = x2 = primLeft - 2;
                y1 = primTop + peer.EdgeOffsetStart * primH;
                y2 = primTop + peer.EdgeOffsetEnd * primH;
                break;
            case ScreenEdge.Right:
                x1 = x2 = primLeft + primW + 2;
                y1 = primTop + peer.EdgeOffsetStart * primH;
                y2 = primTop + peer.EdgeOffsetEnd * primH;
                break;
            case ScreenEdge.Top:
                y1 = y2 = primTop - 2;
                x1 = primLeft + peer.EdgeOffsetStart * primW;
                x2 = primLeft + peer.EdgeOffsetEnd * primW;
                break;
            case ScreenEdge.Bottom:
                y1 = y2 = primTop + primH + 2;
                x1 = primLeft + peer.EdgeOffsetStart * primW;
                x2 = primLeft + peer.EdgeOffsetEnd * primW;
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

            var (primLeft, primTop, primW, primH) = GetPrimaryBoxRectOnCanvas();
            _topology.SnapPeerBoxToPrimaryOnCanvas(
                _draggingPeer,
                newLeft,
                newTop,
                _draggingBorder.Width,
                _draggingBorder.Height,
                primLeft,
                primTop,
                primW,
                primH);

            Canvas.SetLeft(_draggingBorder, _draggingPeer.CanvasX);
            Canvas.SetTop(_draggingBorder, _draggingPeer.CanvasY);

            CanvasSelectionInfoText.Text =
                $"🎯 { _draggingPeer.DeviceName }: {FormatEdgeTr(_draggingPeer.AssignedEdgeOnLocal)} Kenar (%{ _draggingPeer.EdgeOffsetStart * 100:F0} - %{_draggingPeer.EdgeOffsetEnd * 100:F0} kesiti)";
        }
    }

    private void PeerBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingBorder != null && _draggingPeer != null)
        {
            _draggingBorder.ReleaseMouseCapture();
            AppendLog(
                $"[Ekran Konfigürasyonu] '{_draggingPeer.DeviceName}' -> {FormatEdgeTr(_draggingPeer.AssignedEdgeOnLocal)} kenarına (%{_draggingPeer.EdgeOffsetStart * 100:F0} - %{_draggingPeer.EdgeOffsetEnd * 100:F0}) hizalandı.");
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

        _selectedPeer.HasCustomCanvasPosition = false;
        _topology.AssignPeerToEdgeSegment(_selectedPeer, edge, 0.0f, 1.0f);
        RefreshPeersList();
        RedrawDisplayArrangementCanvas();
        AppendLog($"[Ekran Konfigürasyonu] '{_selectedPeer.DeviceName}' -> {FormatEdgeTr(edge)} kenara atandı.");
    }

    private void AutoArrangeScreensBtn_Click(object sender, RoutedEventArgs e)
    {
        var paired = _topology.GetConfiguredPeers().Where(p => p.IsMutuallyPaired).ToList();
        if (paired.Count == 0)
            return;

        // Group by assigned edge and distribute segments evenly if multiple devices share an edge!
        foreach (var group in paired.GroupBy(p => p.AssignedEdgeOnLocal))
        {
            var list = group.ToList();
            float step = 1.0f / list.Count;
            for (int i = 0; i < list.Count; i++)
            {
                list[i].HasCustomCanvasPosition = false;
                _topology.AssignPeerToEdgeSegment(list[i], group.Key, i * step, (i + 1) * step);
            }
        }

        RefreshPeersList();
        RedrawDisplayArrangementCanvas();
        AppendLog("[Ekran Konfigürasyonu] Tüm bağlı ekranlar kenarlara eşit aralıklarla otomatik hizalandı.");
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
        var (_, _, message) = await _network.SubmitRemotePinForPairingAsync(_selectedPeer, enteredPin);
        CanvasSelectionInfoText.Text = message;
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
            1 => ($"Android Telefon #{_simDeviceCounter} (S24)", "android", 1080, 2400, ScreenEdge.Right),
            2 => ($"Nobara Linux PC #{_simDeviceCounter} (KDE)", "linux-nobara", 2560, 1440, ScreenEdge.Left),
            3 => ($"Windows Laptop #{_simDeviceCounter}", "windows", 1920, 1080, ScreenEdge.Right),
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

        simPeer.AssignedEdgeOnLocal = preferredEdge;
        _selectedPeer = simPeer;
        RemotePinInputBox.Text = simPin;

        _ = Task.Run(() => RunSimulatedDeviceNodeLoopAsync(simPeer, simUdp, _simCts.Token));

        RefreshPeersList();
        UpdateSelectedPeerPairingPanel();
        AppendLog(
            $"[Simülatör] '{name}' ({w}x{h}) oluşturuldu! Karşı Cihazın 6 Haneli Kodu: {simPin}. (Kodu kutuya otomatik yazıldı, '✅ Kodu Doğrula' ve '📲 Karşı Cihazda Da Onayla' butonlarına basarak çift taraflı eşleşmeyi tamamlayın!)");
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
                ActiveFocusText.Text = $"{GetPlatformIcon(activePeer.Platform)} Aktif Kontrol: {activePeer.DeviceName} ({FormatEdgeTr(edge)} Kenar)";
                AppendLog($"[Kenar Geçişi] ➡️ İmleç ve klavye '{activePeer.DeviceName}' ekranına geçti (Hiza: %{normalizedPos * 100:F0}).");
            }
            else
            {
                ActiveFocusBadge.Background = new SolidColorBrush(Color.FromRgb(30, 58, 47));
                ActiveFocusBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                ActiveFocusText.Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128));
                ActiveFocusText.Text = "🪟 Aktif Kontrol: Bu Bilgisayar (Yerel)";
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
                LockStateText.Text = "🔒 Kenar Geçişi: KİLİTLİ (Oyun Modu)";
            }
            else
            {
                LockStateBadge.Background = new SolidColorBrush(Color.FromRgb(31, 41, 55));
                LockStateBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(48, 54, 61));
                LockStateText.Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175));
                LockStateText.Text = "🔓 Kenar Geçişi: Açık (ScrollLock / Ctrl+Alt+L)";
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
        var peers = _network.DiscoveredPeers.OrderByDescending(p => p.IsMutuallyPaired).ThenByDescending(p => p.LastSeen).ToList();
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
        });
    }

    private void RefreshShelfList()
    {
        ShelfItemsListBox.Items.Clear();
        foreach (var item in _shelfItems)
        {
            string dirArrow = item.IsOutgoing ? "📤" : "📥";
            ShelfItemsListBox.Items.Add(
                $"{dirArrow} {item.FileName} ({ConnectMeNetworkNode.FormatBytes(item.FileSizeBytes)}) — {item.SenderName}");
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
