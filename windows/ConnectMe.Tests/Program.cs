using System.Net;
using System.Text;
using ConnectMe.Core.Network;
using ConnectMe.Core.Protocol;
using ConnectMe.Core.Topology;

namespace ConnectMe.Tests;

public static class Program
{
    private static int _passed;
    private static int _failed;

    public static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== Connect Me — Çoklu Cihaz, 2D Ekran Kanvası, Çift Taraflı PIN ve Protokol Test Paketi ===");

        TestWirePacketMouseMove();
        TestWirePacketMouseButtonAndScroll();
        TestWirePacketKeyEventWithTurkishUnicode();
        TestWirePacketEdgeHandOff();
        TestWirePacketAudioChunk();
        TestBleProximityCodec();
        TestSpatialTopologyEdgeSwitchingAndDeadCorners();
        TestMultiDevicePartialEdgeSegmentsAndCanvasSnapping();
        TestMultiMonitorInternalSeamPassThrough();
        TestMultiMonitorNegativeCoordinateReturn();
        TestLinuxKScreenDoctorJsonParser();
        TestLinuxXRandrOutputParser();
        TestMultiMonitorCanvasMagneticSnapping();
        TestTrustedDeviceStorePersistence();
        await TestTcpFrameCodecAsync();
        await TestEndToEndMutualSixDigitPinAndLoopbackAsync();
        await TestTrustedDeviceZeroPinAutoReconnectFlowAsync();
        await TestInboundPairingRequestAndEdgeConfigAsync();

        Console.WriteLine($"\nSONUÇ: {_passed} Başarılı, {_failed} Başarısız.");
        return _failed == 0 ? 0 : 1;
    }

    private static void AssertTrue(bool condition, string name)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  [PASS] {name}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  [FAIL] {name}");
        }
    }

    private static void TestWirePacketMouseMove()
    {
        var orig = new MouseMovePacket(Sequence: 42, DeltaX: -18, DeltaY: 35);
        byte[] bytes = WirePacketCodec.EncodeMouseMove(orig);
        bool ok = WirePacketCodec.TryDecodeMouseMove(bytes, out var decoded);
        AssertTrue(
            ok && bytes.Length == 10 && decoded == orig,
            "UDP MouseMove ikili serileştirme ve çözümleme (10 bayt)");
    }

    private static void TestWirePacketMouseButtonAndScroll()
    {
        var btnOrig = new MouseButtonPacket(MouseButtonCode.Right, true);
        byte[] btnBytes = WirePacketCodec.EncodeMouseButton(btnOrig);
        bool btnOk = WirePacketCodec.TryDecodeMouseButton(btnBytes, out var btnDec);

        var scrOrig = new MouseScrollPacket(0, -120);
        byte[] scrBytes = WirePacketCodec.EncodeMouseScroll(scrOrig);
        bool scrOk = WirePacketCodec.TryDecodeMouseScroll(scrBytes, out var scrDec);

        AssertTrue(
            btnOk && btnDec == btnOrig && scrOk && scrDec == scrOrig,
            "UDP MouseButton (6B) ve MouseScroll (8B) serileştirme");
    }

    private static void TestWirePacketKeyEventWithTurkishUnicode()
    {
        var keyOrig = new KeyEventPacket(
            VirtualKey: 0xDE,
            ScanCode: 40,
            IsPressed: true,
            Modifiers: KeyModifiers.Shift,
            UnicodeChar: 'Ş');

        byte[] bytes = WirePacketCodec.EncodeKeyEvent(keyOrig);
        bool ok = WirePacketCodec.TryDecodeKeyEvent(bytes, out var decoded);
        AssertTrue(
            ok && bytes.Length == 12 && decoded.UnicodeChar == 'Ş' && decoded.Modifiers == KeyModifiers.Shift,
            "UDP KeyEvent Türkçe Unicode ('Ş') ve Modifier serileştirme (12 bayt)");
    }

    private static void TestWirePacketEdgeHandOff()
    {
        var orig = new EdgeHandOffPacket(ScreenEdge.Left, IsDraggingShelfItem: true, NormalizedPosition: 0.725f);
        byte[] bytes = WirePacketCodec.EncodeEdgeHandOff(orig);
        bool ok = WirePacketCodec.TryDecodeEdgeHandOff(bytes, out var decoded);
        AssertTrue(
            ok && decoded.TargetEntranceEdge == ScreenEdge.Left &&
            decoded.IsDraggingShelfItem &&
            Math.Abs(decoded.NormalizedPosition - 0.725f) < 0.0001f,
            "UDP EdgeHandOff orantısal kenar koordinatı serileştirme");
    }

    private static void TestWirePacketAudioChunk()
    {
        byte[] samplePcm = new byte[960];
        for (int i = 0; i < samplePcm.Length; i++)
        {
            samplePcm[i] = (byte)(i % 256);
        }

        var orig = new AudioChunkPacket(
            Channels: 2,
            SampleRate: 48000,
            BitsPerSample: 16,
            Sequence: 1234,
            PcmData: samplePcm);

        byte[] bytes = WirePacketCodec.EncodeAudioChunk(orig);
        bool ok = WirePacketCodec.TryDecodeAudioChunk(bytes, out var decoded);

        AssertTrue(
            ok &&
            bytes.Length == 14 + samplePcm.Length &&
            decoded.Channels == 2 &&
            decoded.SampleRate == 48000 &&
            decoded.BitsPerSample == 16 &&
            decoded.Sequence == 1234 &&
            decoded.PcmData.SequenceEqual(samplePcm),
            "UDP AudioChunk ikili serileştirme ve çözümleme (14 bayt başlık + 960 bayt PCM)");
    }

    private static void TestBleProximityCodec()
    {
        var ip = IPAddress.Parse("192.168.1.42");
        byte[] adv = BleProximityCodec.EncodeAdvPayload(2, ip, 42850, "android-pixel-9");
        bool ok = BleProximityCodec.TryDecodeAdvPayload(
            adv,
            out byte platform,
            out IPAddress decodedIp,
            out ushort port,
            out string hash);

        AssertTrue(
            ok && platform == 2 && decodedIp.Equals(ip) && port == 42850 && hash.Length == 12,
            "Bluetooth LE (BLE) 16-bayt yakınlık ve IP keşif paketi kodlama");
    }

    private static void TestSpatialTopologyEdgeSwitchingAndDeadCorners()
    {
        var topo = new SpatialTopologyEngine
        {
            CornerDeadZonePixels = 24,
            EdgeResistancePixels = 25.0
        };
        topo.UpdateLocalScreenBounds(0, 0, 2560, 1440);

        var androidPeer = new PeerDeviceNode
        {
            DeviceId = "android-1",
            DeviceName = "Galaxy S24",
            Platform = "android",
            ScreenWidth = 1080,
            ScreenHeight = 2400,
            MyEnteredPinVerifiedByRemote = true,
            RemoteEnteredMyPinVerified = true
        };
        topo.AssignPeerToEdge(ScreenEdge.Right, androidPeer);

        // 1) Top-right corner ('X' window close area at Y=5) MUST be blocked by Dead-Corner guard
        var cornerAttempt = topo.EvaluateCursorStep(2559, 5, deltaX: 50, deltaY: 0);
        AssertTrue(!cornerAttempt.ShouldTransition, "Köşe Koruması (Dead-Corner): Pencere kapatma ('X') köşesinde yanlışlıkla geçiş engellendi");

        // 2) Middle of right edge (Y=720 -> 50% height): push 15px (below 25px threshold) -> should NOT transition yet
        var step1 = topo.EvaluateCursorStep(2559, 720, deltaX: 15, deltaY: 0);
        // Push another 15px (total 30px >= 25px threshold) -> SHOULD transition to Android's Left edge at 0.50!
        var step2 = topo.EvaluateCursorStep(2559, 720, deltaX: 15, deltaY: 0);

        AssertTrue(
            !step1.ShouldTransition &&
            step2.ShouldTransition &&
            step2.TargetEntranceEdge == ScreenEdge.Left &&
            Math.Abs(step2.NormalizedPosition - 0.5f) < 0.01f,
            "Kenar Direnci (Edge Resistance) ve Sağ Kenardan -> Android Sol Kenarına (%50 hizalı) geçiş");

        // 3) Return from Android back to Windows Right edge at 75% height
        var (retX, retY) = topo.ComputeLocalEntryPoint(ScreenEdge.Right, 0.75f, androidPeer);
        AssertTrue(
            retX == 2560 - 8 && retY == 1080,
            $"Android'den Windows'a dönüş koordinat eşlemesi ({retX}, {retY})");
    }

    private static void TestMultiDevicePartialEdgeSegmentsAndCanvasSnapping()
    {
        var topo = new SpatialTopologyEngine
        {
            CornerDeadZonePixels = 20,
            EdgeResistancePixels = 20.0
        };
        topo.UpdateLocalScreenBounds(0, 0, 2560, 1440);

        // Place TWO different devices on the SAME Right edge + ONE Nobara Linux PC on the Left edge!
        var laptopPeer = new PeerDeviceNode
        {
            DeviceId = "win-laptop",
            DeviceName = "İkinci Windows Laptop",
            Platform = "windows",
            MyEnteredPinVerifiedByRemote = true,
            RemoteEnteredMyPinVerified = true
        };
        var phonePeer = new PeerDeviceNode
        {
            DeviceId = "android-phone",
            DeviceName = "Android Telefon",
            Platform = "android",
            MyEnteredPinVerifiedByRemote = true,
            RemoteEnteredMyPinVerified = true
        };
        var nobaraPeer = new PeerDeviceNode
        {
            DeviceId = "nobara-kde",
            DeviceName = "Nobara KDE PC",
            Platform = "linux-nobara",
            MyEnteredPinVerifiedByRemote = true,
            RemoteEnteredMyPinVerified = true
        };

        // Right Top [0.0 .. 0.5] -> Laptop, Right Bottom [0.5 .. 1.0] -> Android Phone, Left [0.0 .. 1.0] -> Nobara
        topo.AssignPeerToEdgeSegment(laptopPeer, ScreenEdge.Right, 0.0f, 0.5f);
        topo.AssignPeerToEdgeSegment(phonePeer, ScreenEdge.Right, 0.5f, 1.0f);
        topo.AssignPeerToEdgeSegment(nobaraPeer, ScreenEdge.Left, 0.0f, 1.0f);

        // Hit Right edge at Y=360 (25% height -> inside [0.0..0.5]) -> should route to Laptop at 50% of Laptop's left edge!
        var hitTopRight = topo.EvaluateCursorStep(2559, 360, deltaX: 30, deltaY: 0);
        // Hit Right edge at Y=1080 (75% height -> inside [0.5..1.0]) -> should route to Android Phone at 50% of Phone's left edge!
        var hitBotRight = topo.EvaluateCursorStep(2559, 1080, deltaX: 30, deltaY: 0);
        // Hit Left edge at Y=720 (50% height) -> should route to Nobara Linux PC!
        var hitLeft = topo.EvaluateCursorStep(0, 720, deltaX: -30, deltaY: 0);

        AssertTrue(
            hitTopRight.ShouldTransition && hitTopRight.TargetPeer?.DeviceId == "win-laptop" && Math.Abs(hitTopRight.NormalizedPosition - 0.5f) < 0.02f &&
            hitBotRight.ShouldTransition && hitBotRight.TargetPeer?.DeviceId == "android-phone" && Math.Abs(hitBotRight.NormalizedPosition - 0.5f) < 0.02f &&
            hitLeft.ShouldTransition && hitLeft.TargetPeer?.DeviceId == "nobara-kde",
            "Çoklu Cihaz Kısmi Kenar Segmenti: Sağ Üst -> Laptop, Sağ Alt -> Android, Sol -> Nobara KDE yönlendirmesi");

        // Test 2D Display Arrangement Canvas Magnetic Snapping
        topo.SnapPeerBoxToPrimaryOnCanvas(
            phonePeer,
            draggedLeft: 320,
            draggedTop: 150,
            peerBoxW: 56,
            peerBoxH: 50,
            primaryLeft: 150,
            primaryTop: 100,
            primaryW: 160,
            primaryH: 100);

        AssertTrue(
            phonePeer.AssignedEdgeOnLocal == ScreenEdge.Right &&
            Math.Abs(phonePeer.EdgeOffsetStart - 0.5f) < 0.01f &&
            Math.Abs(phonePeer.EdgeOffsetEnd - 1.0f) < 0.01f,
            "2D Ekran Konfigürasyonu GUI Manyetik Kenar Yapışması (SnapPeerBoxToPrimaryOnCanvas)");
    }

    private static void TestMultiMonitorInternalSeamPassThrough()
    {
        var topo = new SpatialTopologyEngine
        {
            CornerDeadZonePixels = 16,
            EdgeResistancePixels = 20.0
        };

        // Dual-Monitor Setup: Monitor 1 (0, 0, 2560, 1440) + Monitor 2 (2560, 0, 1920, 1080)
        var mon1 = new PhysicalMonitorDescriptor { MonitorId = "DISPLAY1", Name = "Monitör 1", VirtualX = 0, VirtualY = 0, Width = 2560, Height = 1440, IsPrimary = true };
        var mon2 = new PhysicalMonitorDescriptor { MonitorId = "DISPLAY2", Name = "Monitör 2", VirtualX = 2560, VirtualY = 0, Width = 1920, Height = 1080, IsPrimary = false };
        topo.UpdateLocalMonitors([mon1, mon2]);

        var androidPeer = new PeerDeviceNode
        {
            DeviceId = "android-dual",
            DeviceName = "Android Tablet",
            Platform = "android",
            MyEnteredPinVerifiedByRemote = true,
            RemoteEnteredMyPinVerified = true
        };
        // Dock Android against Monitor 2's Right outer edge
        topo.AssignPeerToEdge(ScreenEdge.Right, androidPeer, targetLocalMonitorId: "DISPLAY2");

        // 1. Internal Seam: Moving from Monitor 1 rightwards across X=2559 into Monitor 2 at Y=500 MUST NOT switch to Android!
        bool isSeam = topo.IsPointOnInternalMonitorSeam(mon1, ScreenEdge.Right, 2559, 500);
        var seamAttempt = topo.EvaluateCursorStep(2559, 500, deltaX: 40, deltaY: 0);

        // 2. Outer Edge: Moving from Monitor 2 rightwards across X=4479 (2560 + 1920 - 1) at Y=500 MUST transition to Android!
        bool isMon2OuterSeam = topo.IsPointOnInternalMonitorSeam(mon2, ScreenEdge.Right, 4479, 500);
        var outerTransition = topo.EvaluateCursorStep(4479, 500, deltaX: 30, deltaY: 0);

        AssertTrue(
            isSeam && !seamAttempt.ShouldTransition &&
            !isMon2OuterSeam && outerTransition.ShouldTransition &&
            outerTransition.TargetPeer?.DeviceId == "android-dual" &&
            outerTransition.SourceMonitorId == "DISPLAY2",
            "Çoklu Monitör İç Birleşim Koruması: Monitör 1 -> Monitör 2 geçişinde imleç serbest, Monitör 2 dış kenarında Android'e geçiş");
    }

    private static void TestMultiMonitorNegativeCoordinateReturn()
    {
        var topo = new SpatialTopologyEngine();
        // Triple-Monitor Setup: Monitor 3 is on the left at VirtualX = -1080
        var mon1 = new PhysicalMonitorDescriptor { MonitorId = "DISPLAY1", Name = "Monitör 1", VirtualX = 0, VirtualY = 0, Width = 2560, Height = 1440, IsPrimary = true };
        var mon3 = new PhysicalMonitorDescriptor { MonitorId = "DISPLAY3", Name = "Monitör 3 Sol Dikey", VirtualX = -1080, VirtualY = -240, Width = 1080, Height = 1920, IsPrimary = false };
        topo.UpdateLocalMonitors([mon1, mon3]);

        var nobaraPeer = new PeerDeviceNode
        {
            DeviceId = "nobara-left",
            DeviceName = "Nobara KDE",
            Platform = "linux-nobara",
            AttachedLocalMonitorId = "DISPLAY3",
            AssignedEdgeOnLocal = ScreenEdge.Left,
            MyEnteredPinVerifiedByRemote = true,
            RemoteEnteredMyPinVerified = true
        };

        // When returning from Nobara through the left edge of Monitor 3 at 50% height
        var (retX, retY) = topo.ComputeLocalEntryPoint(ScreenEdge.Left, 0.50f, nobaraPeer);

        // Expected X: -1080 + 8 = -1072, Expected Y: -240 + 960 = 720
        AssertTrue(
            retX == -1072 && retY == 720,
            $"Çoklu Monitör Negatif Koordinat Geri Dönüşü (DISPLAY3 sol dikey ekran: {retX}, {retY})");
    }

    private static void TestLinuxKScreenDoctorJsonParser()
    {
        string sampleKdeJson = """
        {
            "outputs": [
                {
                    "connected": true,
                    "enabled": true,
                    "name": "DP-1",
                    "primary": true,
                    "scale": 1.25,
                    "pos": { "x": 0, "y": 0 },
                    "size": { "width": 2560, "height": 1440 }
                },
                {
                    "connected": true,
                    "enabled": true,
                    "name": "HDMI-A-1",
                    "primary": false,
                    "scale": 1.0,
                    "pos": { "x": 2560, "y": 0 },
                    "size": { "width": 1920, "height": 1080 }
                }
            ]
        }
        """;

        var monitors = CrossPlatformMonitorDetector.ParseKScreenDoctorJson(sampleKdeJson);
        AssertTrue(
            monitors.Count == 2 &&
            monitors[0].MonitorId == "DP-1" && monitors[0].Width == 2560 && monitors[0].IsPrimary &&
            monitors[1].MonitorId == "HDMI-A-1" && monitors[1].VirtualX == 2560 && monitors[1].Height == 1080,
            "Linux KDE Plasma Wayland (kscreen-doctor -j) Çoklu Monitör JSON Ayrıştırıcı");
    }

    private static void TestLinuxXRandrOutputParser()
    {
        string sampleXRandr = """
        Screen 0: minimum 320 x 200, current 4480 x 1440, maximum 16384 x 16384
        DP-1 connected primary 2560x1440+0+0 (normal left inverted right x axis y axis) 597mm x 336mm
           2560x1440    143.91*+
        HDMI-A-1 connected 1920x1080+2560+0 (normal left inverted right x axis y axis) 527mm x 296mm
           1920x1080     60.00*+
        DP-2 disconnected (normal left inverted right x axis y axis)
        """;

        var monitors = CrossPlatformMonitorDetector.ParseXRandrOutput(sampleXRandr);
        AssertTrue(
            monitors.Count == 2 &&
            monitors[0].MonitorId == "DP-1" && monitors[0].IsPrimary && monitors[0].Width == 2560 &&
            monitors[1].MonitorId == "HDMI-A-1" && monitors[1].VirtualX == 2560 && monitors[1].Width == 1920,
            "Linux X11/XWayland (xrandr --query) Çoklu Monitör Çıktı Ayrıştırıcı");
    }

    private static void TestMultiMonitorCanvasMagneticSnapping()
    {
        var topo = new SpatialTopologyEngine();
        var mon1 = new PhysicalMonitorDescriptor { MonitorId = "DISPLAY1", Name = "Monitör 1", Width = 2560, Height = 1440, IsPrimary = true };
        var mon2 = new PhysicalMonitorDescriptor { MonitorId = "DISPLAY2", Name = "Monitör 2", VirtualX = 2560, VirtualY = 0, Width = 1920, Height = 1080, IsPrimary = false };
        topo.UpdateLocalMonitors([mon1, mon2]);

        var phonePeer = new PeerDeviceNode
        {
            DeviceId = "phone-mon2",
            DeviceName = "Android Telefon",
            Platform = "android"
        };

        // Canvas monitor cluster: Mon 1 at (100, 100, 140, 90), Mon 2 at (244, 100, 110, 70)
        var monitorBoxes = new List<(PhysicalMonitorDescriptor, double, double, double, double)>
        {
            (mon1, 100, 100, 140, 90),
            (mon2, 244, 100, 110, 70)
        };

        // Drag phone near the RIGHT edge of Monitör 2 (draggedLeft = 360, draggedTop = 110)
        topo.SnapPeerBoxToLocalMonitorsOnCanvas(
            phonePeer,
            draggedLeft: 360,
            draggedTop: 110,
            peerBoxW: 50,
            peerBoxH: 70,
            monitorCanvasBoxes: monitorBoxes);

        AssertTrue(
            phonePeer.AttachedLocalMonitorId == "DISPLAY2" &&
            phonePeer.AssignedEdgeOnLocal == ScreenEdge.Right,
            "2D Çoklu Monitör Kanvasında İstenen Monitöre (DISPLAY2 Sağ Kenar) Manyetik Kenetleme");
    }

    private static async Task TestTcpFrameCodecAsync()
    {
        using var ms = new MemoryStream();
        byte[] sampleFile = Encoding.UTF8.GetBytes("Connect Me Drop Shelf Test İçeriği 🚀");
        var header = new TcpControlHeader
        {
            Type = "SHELF_FILE",
            SenderId = "win-pc",
            SenderName = "Egemen-PC",
            FileName = "test.txt"
        };

        using var payloadIn = new MemoryStream(sampleFile);
        await TcpFrameCodec.WriteFrameAsync(ms, header, payloadIn, sampleFile.Length);

        ms.Position = 0;
        var (readHdr, binLen) = await TcpFrameCodec.ReadHeaderAsync(ms);
        byte[] receivedPayload = new byte[binLen];
        await TcpFrameCodec.ReadExactAsync(ms, receivedPayload, CancellationToken.None);

        AssertTrue(
            readHdr != null &&
            readHdr.FileName == "test.txt" &&
            Encoding.UTF8.GetString(receivedPayload) == "Connect Me Drop Shelf Test İçeriği 🚀",
            "TCP Çerçeveli (Framed) Kontrol Başlığı + Drop Shelf Dosya Akışı");
    }

    private static async Task TestEndToEndMutualSixDigitPinAndLoopbackAsync()
    {
        string tempShelf = Path.Combine(Path.GetTempPath(), "ConnectMeTestShelf_" + Guid.NewGuid().ToString("N")[..6]);
        await using var nodeA = new ConnectMeNetworkNode("node-a", "Windows-PC", "windows", tempShelf, fixedPin: "482910");
        await using var nodeB = new ConnectMeNetworkNode("node-b", "Android-S24", "android", tempShelf, fixedPin: "739104");

        var tcsClipboard = new TaskCompletionSource<string>();
        var tcsHandOff = new TaskCompletionSource<EdgeHandOffPacket>();

        nodeB.RemoteClipboardTextReceived += (txt, _) => tcsClipboard.TrySetResult(txt);
        nodeB.RemoteEdgeHandOffReceived += (pkt, _) => tcsHandOff.TrySetResult(pkt);

        nodeA.Start(discoveryPort: 42949, inputUdpPort: 42950, controlTcpPort: 42951);
        nodeB.Start(discoveryPort: 42959, inputUdpPort: 42960, controlTcpPort: 42961);

        var peerBOnA = nodeA.RegisterManualPeer("127.0.0.1", "Android-S24", "android", udpPort: 42960, tcpPort: 42961, customDeviceId: "node-b");
        var peerAOnB = nodeB.RegisterManualPeer("127.0.0.1", "Windows-PC", "windows", udpPort: 42950, tcpPort: 42951, customDeviceId: "node-a");

        // 1) Wrong 6-digit PIN must be rejected
        var wrongAttempt = await nodeA.SubmitRemotePinForPairingAsync(peerBOnA, "000000");
        AssertTrue(!wrongAttempt.Accepted && !peerBOnA.IsMutuallyPaired, "Çift Taraflı 6 Haneli PIN: Hatalı kod (000000) reddedildi");

        // 2) Node A enters Node B's correct 6-digit PIN (739104) -> Outbound verified, but NOT yet mutually paired!
        var side1 = await nodeA.SubmitRemotePinForPairingAsync(peerBOnA, "739104");
        AssertTrue(
            side1.Accepted && !side1.IsNowMutuallyPaired && peerBOnA.PairingState == PeerPairingState.OutboundPinVerified,
            "Çift Taraflı 6 Haneli PIN (Adım 1): Tek taraflı kod girişi kabul edildi ancak karşı onay bekleniyor");

        // 3) Node B enters Node A's correct 6-digit PIN (482910) -> Mutual Pairing COMPLETE on both sides!
        var side2 = await nodeB.SubmitRemotePinForPairingAsync(peerAOnB, "482910");
        // Re-confirm from A or check state
        await nodeA.SubmitRemotePinForPairingAsync(peerBOnA, "739104");

        AssertTrue(
            side2.Accepted && peerAOnB.IsMutuallyPaired && peerBOnA.IsMutuallyPaired,
            "Çift Taraflı 6 Haneli PIN (Adım 2): İki taraf da birbirinin kodunu girdi ve karşılıklı eşleşme tamamlandı");

        // 4) Now that both sides are mutually paired, EdgeHandOff and Clipboard must flow!
        nodeA.SendEdgeHandOff(peerBOnA, ScreenEdge.Left, 0.42f);
        await nodeA.BroadcastClipboardTextAsync("Merhaba Çoklu Cihaz Evrensel Pano!");

        var handOffTask = await Task.WhenAny(tcsHandOff.Task, Task.Delay(2000));
        var clipTask = await Task.WhenAny(tcsClipboard.Task, Task.Delay(2000));

        AssertTrue(
            handOffTask == tcsHandOff.Task &&
            Math.Abs(tcsHandOff.Task.Result.NormalizedPosition - 0.42f) < 0.001f &&
            clipTask == tcsClipboard.Task &&
            tcsClipboard.Task.Result == "Merhaba Çoklu Cihaz Evrensel Pano!",
            "Çift Taraflı PIN Onayı Sonrası Uçtan Uca UDP Kenar Geçişi ve TCP Pano Senkronizasyonu");

        try { Directory.Delete(tempShelf, true); } catch { }
    }

    private static void TestTrustedDeviceStorePersistence()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"connectme_trust_test_{Guid.NewGuid():N}.json");
        try
        {
            var store = new TrustedDeviceStore(tempFile);
            string token = TrustedDeviceStore.GenerateTrustToken();
            AssertTrue(token.Length == 48, "TrustedDeviceStore: 24-baytlık (48 karakter hex) kriptografik belirteç üretildi");

            store.AddOrUpdateTrustedDevice(new TrustedDeviceRecord
            {
                DeviceId = "android-device-1",
                DeviceName = "Pixel 9 Pro",
                Platform = "android",
                TrustToken = token,
                AssignedEdge = ScreenEdge.Left,
                EdgeOffsetStart = 0.2f,
                EdgeOffsetEnd = 0.8f,
                AttachedLocalMonitorId = "DISPLAY1",
                AutoConnect = true
            });

            AssertTrue(store.IsDeviceTrusted("android-device-1", out var rec) && rec != null && rec.DeviceName == "Pixel 9 Pro",
                "TrustedDeviceStore: Güvenilir cihaz eklendi ve hafızada doğrulandı");
            AssertTrue(store.VerifyTrustToken("android-device-1", token),
                "TrustedDeviceStore: Kriptografik güven belirteci doğru eşleşti");
            AssertTrue(!store.VerifyTrustToken("android-device-1", "wrong-token"),
                "TrustedDeviceStore: Yanlış güven belirteci reddedildi");

            // Reload from disk into a fresh store instance to verify JSON persistence
            var storeReloaded = new TrustedDeviceStore(tempFile);
            AssertTrue(storeReloaded.IsDeviceTrusted("android-device-1", out var reloadedRec) &&
                       reloadedRec != null &&
                       reloadedRec.AssignedEdge == ScreenEdge.Left &&
                       Math.Abs(reloadedRec.EdgeOffsetStart - 0.2f) < 0.001f,
                "TrustedDeviceStore: Diske JSON olarak kaydedildi ve yeni store örneğinde tam topolojiyle okundu");

            // Test trust revocation
            bool revoked = storeReloaded.RevokeTrust("android-device-1");
            AssertTrue(revoked && !storeReloaded.IsDeviceTrusted("android-device-1", out _),
                "TrustedDeviceStore: Cihaz güveni başarıyla kaldırıldı (unutuldu)");
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    private static async Task TestTrustedDeviceZeroPinAutoReconnectFlowAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"connectme_trust_net_{Guid.NewGuid():N}");
        string storeFileA = Path.Combine(tempDir, "trust_a.json");
        string storeFileB = Path.Combine(tempDir, "trust_b.json");
        Directory.CreateDirectory(tempDir);

        try
        {
            var storeA = new TrustedDeviceStore(storeFileA);
            var storeB = new TrustedDeviceStore(storeFileB);

            await using var nodeA = new ConnectMeNetworkNode(
                deviceId: "node-pc",
                deviceName: "Windows Workstation",
                platform: "windows",
                shelfDirectory: Path.Combine(tempDir, "shelfA"),
                fixedPin: "111222",
                trustStore: storeA);

            await using var nodeB = new ConnectMeNetworkNode(
                deviceId: "node-phone",
                deviceName: "Galaxy Phone",
                platform: "android",
                shelfDirectory: Path.Combine(tempDir, "shelfB"),
                fixedPin: "333444",
                trustStore: storeB);

            nodeA.Start(discoveryPort: 43049, inputUdpPort: 43050, controlTcpPort: 43051);
            nodeB.Start(discoveryPort: 43059, inputUdpPort: 43060, controlTcpPort: 43061);

            var peerBOnA = nodeA.RegisterManualPeer("127.0.0.1", "Galaxy Phone", "android", udpPort: 43060, tcpPort: 43061, customDeviceId: "node-phone");
            var peerAOnB = nodeB.RegisterManualPeer("127.0.0.1", "Windows Workstation", "windows", udpPort: 43050, tcpPort: 43051, customDeviceId: "node-pc");

            // Initial pairing with rememberDevice = true
            var step1 = await nodeA.SubmitRemotePinForPairingAsync(peerBOnA, "333444", rememberDevice: true);
            var step2 = await nodeB.SubmitRemotePinForPairingAsync(peerAOnB, "111222", rememberDevice: true);

            AssertTrue(step1.Accepted && step2.Accepted,
                "Sıfır-PIN Otomatik Bağlantı Hazırlığı: İlk eşleşmede iki taraf da kodu doğruladı ve güven belirteci paylaştı");
            AssertTrue(peerBOnA.IsTrusted && peerAOnB.IsTrusted,
                "Sıfır-PIN: İki tarafta da IsTrusted bayrağı aktifleşti ve diske kaydedildi");

            // Set custom edge and canvas layout
            peerBOnA.AssignedEdgeOnLocal = ScreenEdge.Left;
            peerBOnA.EdgeOffsetStart = 0.25f;
            peerBOnA.EdgeOffsetEnd = 0.75f;
            nodeA.SavePeerTopologyToTrustedStore(peerBOnA);

            // Now SIMULATE RESTART:
            // Reset peer pairing state to simulate closing and reopening the application!
            peerBOnA.MyEnteredPinVerifiedByRemote = false;
            peerBOnA.RemoteEnteredMyPinVerified = false;
            AssertTrue(!peerBOnA.IsMutuallyPaired, "Simülasyon: Uygulama kapatıldı ve bağlantı koptu");

            // Zero-PIN Reconnection: Node A attempts TRUSTED_RECONNECT using saved trust token
            bool autoConnected = await nodeA.TryAutoReconnectTrustedPeerAsync(peerBOnA, peerBOnA.TrustToken);

            AssertTrue(autoConnected && peerBOnA.IsMutuallyPaired && peerAOnB.IsMutuallyPaired,
                "Sıfır-PIN Otomatik Bağlantı: Hiçbir PIN girilmeden TRUSTED_RECONNECT ile otomatik bağlantı sağlandı!");

            // Verify edge layout was preserved
            AssertTrue(peerBOnA.AssignedEdgeOnLocal == ScreenEdge.Left && Math.Abs(peerBOnA.EdgeOffsetStart - 0.25f) < 0.001f,
                "Sıfır-PIN: Otomatik bağlanan cihazın ekran konfigürasyonu ve kenar konumu aynen korundu");

            // Test DisconnectPeer
            bool disconnectedEventFired = false;
            nodeA.PeerDisconnected += p => { if (p.DeviceId == peerBOnA.DeviceId) disconnectedEventFired = true; };

            bool discResult = nodeA.DisconnectPeer(peerBOnA);
            AssertTrue(discResult && !peerBOnA.IsMutuallyPaired, "Bağlantıyı Kes: Node A yerel eşleşme durumunu sıfırladı");
            AssertTrue(disconnectedEventFired, "Bağlantıyı Kes: Node A PeerDisconnected olayını tetikledi");

            // Give TCP DISCONNECT a moment to reach nodeB
            await Task.Delay(200);
            AssertTrue(!peerAOnB.IsMutuallyPaired, "Bağlantıyı Kes: Node B TCP DISCONNECT alarak eşleşme durumunu sıfırladı");

            // Verify auto-reconnect is suppressed until user manually reconnects
            bool suppressedReconnect = await nodeA.TryAutoReconnectTrustedPeerAsync(peerBOnA, peerBOnA.TrustToken);
            AssertTrue(!suppressedReconnect && !peerBOnA.IsMutuallyPaired, "Bağlantıyı Kes: Otomatik yeniden bağlanma kullanıcı manuel bağlanana kadar engellendi");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static async Task TestInboundPairingRequestAndEdgeConfigAsync()
    {
        string tempShelf = Path.Combine(Path.GetTempPath(), "ConnectMeReqTest_" + Guid.NewGuid().ToString("N")[..6]);
        await using var nodeA = new ConnectMeNetworkNode("win-pc-1", "Windows-Host", "windows", tempShelf, fixedPin: "112233");
        await using var nodeB = new ConnectMeNetworkNode("win-pc-2", "Windows-Secondary", "windows", tempShelf, fixedPin: "445566");

        nodeA.Start(discoveryPort: 43049, inputUdpPort: 43050, controlTcpPort: 43051);
        nodeB.Start(discoveryPort: 43059, inputUdpPort: 43060, controlTcpPort: 43061);

        var peerBOnA = nodeA.RegisterManualPeer("127.0.0.1", "Windows-Secondary", "windows", udpPort: 43060, tcpPort: 43061, customDeviceId: "win-pc-2");
        var peerAOnB = nodeB.RegisterManualPeer("127.0.0.1", "Windows-Host", "windows", udpPort: 43050, tcpPort: 43051, customDeviceId: "win-pc-1");

        // 1. Inbound Connection Request Event
        PeerDeviceNode? inboundRequestedPeerOnB = null;
        nodeB.RemotePinVerifiedWaitingLocalPin += p => inboundRequestedPeerOnB = p;

        // Node A submits Node B's PIN ("445566")
        var resA = await nodeA.SubmitRemotePinForPairingAsync(peerBOnA, "445566", rememberDevice: false);
        AssertTrue(resA.Accepted && !resA.IsNowMutuallyPaired, "Gelen İstek: Node A kodu girdiğinde Node B PIN'i kabul etti");

        // Wait a brief moment for TCP ACK & events
        await Task.Delay(150);
        AssertTrue(
            inboundRequestedPeerOnB != null &&
            inboundRequestedPeerOnB.DeviceId == "win-pc-1" &&
            inboundRequestedPeerOnB.RemoteEnteredMyPinVerified &&
            !inboundRequestedPeerOnB.IsMutuallyPaired,
            "Gelen İstek: Node B'de RemotePinVerifiedWaitingLocalPin olayı tetiklendi ve bağlantı isteği kartı için hazırlandı");

        // 2. Node B enters Node A's PIN ("112233")
        var resB = await nodeB.SubmitRemotePinForPairingAsync(peerAOnB, "112233", rememberDevice: false);
        await nodeA.SubmitRemotePinForPairingAsync(peerBOnA, "445566", rememberDevice: false);
        AssertTrue(resB.Accepted && peerAOnB.IsMutuallyPaired && peerBOnA.IsMutuallyPaired,
            "Gelen İstek: Node B kodu girdi ve çift taraflı eşleşme başarıyla tamamlandı");

        // 3. Test EDGE_CONFIG synchronization between 2 Windows PCs
        ScreenEdge configEdgeReceivedOnB = ScreenEdge.None;
        nodeB.RemoteEdgeConfigReceived += (p, e) => configEdgeReceivedOnB = e;

        peerBOnA.AssignedEdgeOnLocal = ScreenEdge.Right;
        nodeA.SendEdgeConfigToPeer(peerBOnA);

        await Task.Delay(200);
        AssertTrue(configEdgeReceivedOnB == ScreenEdge.Left && peerAOnB.AssignedEdgeOnLocal == ScreenEdge.Left,
            "Ekran Konfigürasyonu: Node A sağ kenara atayınca Node B karşılıklı sol kenar konfigürasyonunu (EDGE_CONFIG) aldı");

        // 4. Test SendEdgeReturnToPeer
        EdgeHandOffPacket? returnPkt = null;
        nodeA.RemoteEdgeHandOffReceived += (pkt, _) => returnPkt = pkt;

        nodeB.SendEdgeReturnToPeer(peerAOnB, ScreenEdge.Right, 0.75f);
        await Task.Delay(200);

        AssertTrue(
            returnPkt != null &&
            returnPkt.Value.TargetEntranceEdge == ScreenEdge.Right &&
            Math.Abs(returnPkt.Value.NormalizedPosition - 0.75f) < 0.01f,
            "Kenar Dönüşü (EDGE_RETURN): İkincil Windows bilgisayar imleç dönüş paketini ana bilgisayara başarıyla aktardı");

        try { Directory.Delete(tempShelf, true); } catch { }
    }
}
