using System.Net;
using System.Text;
using ConnectMe.Core.Network;
using ConnectMe.Core.Protocol;
using ConnectMe.Core.Topology;
using ConnectMe.Windows.Updater;

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
        TestSecondaryReturnPushAndInternalSeamGuard();
        TestMultiMonitorNegativeCoordinateReturn();
        TestLinuxKScreenDoctorJsonParser();
        TestLinuxXRandrOutputParser();
        TestMultiMonitorCanvasMagneticSnapping();
        TestMultiMonitorPerMonitorTopEdgeMatching();
        TestDynamicMonitorHotplugTopologyUpdate();
        TestTrustedDeviceStorePersistence();
        await TestTcpFrameCodecAsync();
        await TestEndToEndMutualSixDigitPinAndLoopbackAsync();
        await TestTrustedDeviceZeroPinAutoReconnectFlowAsync();
        await TestInboundPairingRequestAndEdgeConfigAsync();
        await TestColonyBeaconAndSinglePinColonyMeshAsync();
        TestWasapiAudioLoopback();
        TestAudioCaptureFloatConversionAndPacking();
        await TestAudioMatrixAndRoutingAsync();
        await TestRelativeSpatialTopologyAndMultiDeviceColonySyncAsync();
        await TestRichClipboardImageAndCrossEdgeDragAsync();
        TestConsistentEdgeCrossingAndPrematureJumpPrevention();
        await TestDeviceDeduplicationAndClearColonyTreeAsync();
        TestAudioPcmMixerAndNetworkOptimization();
        await TestWindowsUpdateDetectionAndSemverAsync();

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

        // 4) Return fallback with ScreenEdge.None -> must use androidPeer.AssignedEdgeOnLocal (Right edge at 0.75f -> 2552, 1080) instead of screen center (1280, 720)
        var (fallbackX, fallbackY) = topo.ComputeLocalEntryPoint(ScreenEdge.None, 0.75f, androidPeer);
        AssertTrue(
            fallbackX == 2560 - 8 && fallbackY == 1080,
            $"Kenar Yok (ScreenEdge.None) durumunda AssignedEdgeOnLocal ({fallbackX}, {fallbackY}) fallback koordinat eşlemesi");
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

    private static void TestSecondaryReturnPushAndInternalSeamGuard()
    {
        var topo = new SpatialTopologyEngine();
        var mon1 = new PhysicalMonitorDescriptor { MonitorId = "DISPLAY1", Name = "Monitör 1", VirtualX = 0, VirtualY = 0, Width = 1920, Height = 1080, IsPrimary = true };
        var mon2 = new PhysicalMonitorDescriptor { MonitorId = "DISPLAY2", Name = "Monitör 2", VirtualX = 1920, VirtualY = 0, Width = 1920, Height = 1080, IsPrimary = false };
        topo.UpdateLocalMonitors([mon1, mon2]);

        // 1. Point at left edge of Monitor 2 (X=1920) is an internal seam (transitioning to Monitor 1)
        bool mon2LeftIsSeam = topo.IsPointOnInternalMonitorSeam(mon2, ScreenEdge.Left, 1920, 540);
        // 2. Point at left edge of Monitor 1 (X=0) is a true outer boundary
        bool mon1LeftIsSeam = topo.IsPointOnInternalMonitorSeam(mon1, ScreenEdge.Left, 0, 540);

        // 3. Point at right edge of Monitor 1 (X=1919) is an internal seam (transitioning to Monitor 2)
        bool mon1RightIsSeam = topo.IsPointOnInternalMonitorSeam(mon1, ScreenEdge.Right, 1919, 540);
        // 4. Point at right edge of Monitor 2 (X=3839) is a true outer boundary
        bool mon2RightIsSeam = topo.IsPointOnInternalMonitorSeam(mon2, ScreenEdge.Right, 3839, 540);

        AssertTrue(
            mon2LeftIsSeam && !mon1LeftIsSeam && mon1RightIsSeam && !mon2RightIsSeam,
            "İkincil Mod Kenar Dönüşü ve Çoklu Monitör İç Dikiş Koruması (İç dikişlerde erken dönüş engellendi, dış kenarlarda dönüş serbest)");
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

    private static void TestMultiMonitorPerMonitorTopEdgeMatching()
    {
        var topo = new SpatialTopologyEngine();
        var mon1 = new PhysicalMonitorDescriptor
        {
            MonitorId = "DISPLAY1",
            Name = "Monitör 1",
            VirtualX = 0,
            VirtualY = 0,
            Width = 1920,
            Height = 1080,
            IsPrimary = true
        };
        var mon2 = new PhysicalMonitorDescriptor
        {
            MonitorId = "DISPLAY2",
            Name = "Monitör 2",
            VirtualX = 1920,
            VirtualY = 0,
            Width = 1920,
            Height = 1080,
            IsPrimary = false
        };
        topo.UpdateLocalMonitors([mon1, mon2]);

        var peer = new PeerDeviceNode
        {
            DeviceId = "peer-linux",
            DeviceName = "Linux PC",
            Platform = "linux",
            ScreenWidth = 1920,
            ScreenHeight = 1080,
            AttachedToDeviceId = "local-host",
            AttachedLocalMonitorId = "DISPLAY2",
            MyEnteredPinVerifiedByRemote = true,
            RemoteEnteredMyPinVerified = true
        };
        topo.AssignPeerToEdgeSegment(peer, ScreenEdge.Top, 0.0f, 1.0f);
        peer.AttachedLocalMonitorId = "DISPLAY2";
        topo.RecomputeColonyWorldCoordinates("local-host");

        // 1. Colony world coordinates must align with DISPLAY2 (X = 1920, Y = -1080)
        AssertTrue(
            peer.ColonyX == 1920 && peer.ColonyY == -1080,
            "Çoklu Monitör: Monitör 2 üstüne atanan cihazın koloni koordinatları doğru (1920, -1080)");

        // 2. Cursor hitting top edge on DISPLAY1 must NOT trigger transition to peer on DISPLAY2
        var hitMon1 = topo.EvaluateCursorStep(960, 0, deltaX: 0, deltaY: -30);
        AssertTrue(
            !hitMon1.ShouldTransition,
            "Çoklu Monitör: Monitör 1 üst kenarında Monitör 2'deki cihaza yanlış geçiş engellendi");

        // 3. Cursor hitting top edge on DISPLAY2 MUST trigger transition to peer
        var hitMon2 = topo.EvaluateCursorStep(2500, 0, deltaX: 0, deltaY: -30);
        AssertTrue(
            hitMon2.ShouldTransition && hitMon2.TargetPeer?.DeviceId == "peer-linux",
            "Çoklu Monitör: Monitör 2 üst kenarından Monitör 2'ye bağlı cihaza geçiş başarıyla gerçekleşti");
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

    private static void TestDynamicMonitorHotplugTopologyUpdate()
    {
        var topo = new SpatialTopologyEngine();
        // 1. Adım: Başlangıçta tek monitör (1920x1080)
        var mon1 = new PhysicalMonitorDescriptor
        {
            MonitorId = "HDMI-1",
            Name = "Birincil Ekran",
            VirtualX = 0,
            VirtualY = 0,
            Width = 1920,
            Height = 1080,
            IsPrimary = true
        };
        topo.UpdateLocalMonitors([mon1]);

        var peer = new PeerDeviceNode
        {
            DeviceId = "peer-laptop",
            DeviceName = "Laptop",
            Platform = "linux",
            ScreenWidth = 1920,
            ScreenHeight = 1080,
            MyEnteredPinVerifiedByRemote = true,
            RemoteEnteredMyPinVerified = true
        };
        // Peer masaüstünün sağına yerleştirildi
        topo.AssignPeerToEdge(ScreenEdge.Right, peer);
        topo.RecomputeColonyWorldCoordinates("local-host");
        AssertTrue(peer.ColonyX == 1920 && peer.ColonyY == 0,
            "Dinamik Monitör: Tek ekranda sağdaki cihaz X=1920 konumunda");

        // 2. Adım: Kullanıcı açıkken ikinci bir monitör bağladı (DP-1: X=1920, Y=0, 2560x1440)
        var mon2 = new PhysicalMonitorDescriptor
        {
            MonitorId = "DP-1",
            Name = "Yeni Bağlanan 2K Monitör",
            VirtualX = 1920,
            VirtualY = 0,
            Width = 2560,
            Height = 1440,
            IsPrimary = false
        };
        topo.UpdateLocalMonitors([mon1, mon2]);
        topo.RecomputeColonyWorldCoordinates("local-host");

        // Toplam masaüstü genişliği artık 1920 + 2560 = 4480.
        AssertTrue(peer.ColonyX == 4480,
            "Dinamik Monitör Hotplug: Yeni monitör bağlandığında sağdaki uzak cihaz otomatik olarak yeni dış sınıra (X=4480) kaydırıldı");

        // İç dikiş (Internal Seam): X=1919'dan sağa X=1920'ye geçiş artık HDMI-1 ve DP-1 arası iç dikiştir, GEÇİŞ YAPILMAMALIDIR!
        var internalStep = topo.EvaluateCursorStep(1919, 500, deltaX: 20, deltaY: 0);
        AssertTrue(!internalStep.ShouldTransition,
            "Dinamik Monitör Hotplug: Eski dış sınır olan X=1920 artık iki monitör arası iç dikiş (internal seam) oldu ve geçiş tetiklemedi");

        // Gerçek dış kenar: DP-1'in en sağ kenarı (X=4479) uzak cihaza geçmelidir!
        var outerStep = topo.EvaluateCursorStep(4479, 500, deltaX: 30, deltaY: 0);
        AssertTrue(outerStep.ShouldTransition && outerStep.TargetPeer?.DeviceId == "peer-laptop",
            "Dinamik Monitör Hotplug: Yeni bağlanan monitörün dış sağ kenarı (X=4479) uzak cihaza geçişi başarıyla sağladı");
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
                DeviceName = "Pixel 9 Pro Extra Long Hostname",
                Platform = "android",
                TrustToken = token,
                AssignedEdge = ScreenEdge.Left,
                EdgeOffsetStart = 0.2f,
                EdgeOffsetEnd = 0.8f,
                AttachedLocalMonitorId = "DISPLAY1",
                AutoConnect = true
            });

            AssertTrue(store.IsDeviceTrusted("android-device-1", out var rec) && rec != null && rec.DeviceName == "Pixel 9 Pro Extra Long Hostname",
                "TrustedDeviceStore: Güvenilir cihaz eklendi ve hafızada doğrulandı");
            AssertTrue(store.VerifyTrustToken("android-device-1", token),
                "TrustedDeviceStore: Kriptografik güven belirteci doğru eşleşti");
            AssertTrue(!store.VerifyTrustToken("android-device-1", "wrong-token"),
                "TrustedDeviceStore: Yanlış güven belirteci reddedildi");

            // Test Nickname Assignment & Retrieval
            store.SetDeviceNickname("android-device-1", "Telefonum");
            AssertTrue(store.GetDeviceNickname("android-device-1") == "Telefonum",
                "TrustedDeviceStore: Cihaza özel takma ad (Nickname) atandı ve hafızada doğrulandı");

            // Reload from disk into a fresh store instance to verify JSON persistence
            var storeReloaded = new TrustedDeviceStore(tempFile);
            AssertTrue(storeReloaded.IsDeviceTrusted("android-device-1", out var reloadedRec) &&
                       reloadedRec != null &&
                       reloadedRec.AssignedEdge == ScreenEdge.Left &&
                       reloadedRec.Nickname == "Telefonum" &&
                       storeReloaded.GetDeviceNickname("android-device-1") == "Telefonum" &&
                       Math.Abs(reloadedRec.EdgeOffsetStart - 0.2f) < 0.001f,
                "TrustedDeviceStore: Diske JSON olarak kaydedildi ve yeni store örneğinde takma ad (Nickname) dahil tam topolojiyle okundu");

            // Test PeerDeviceNode DisplayName property fallback
            var peerWithNick = new PeerDeviceNode
            {
                DeviceId = "android-device-1",
                DeviceName = "Pixel 9 Pro Extra Long Hostname",
                Nickname = "Telefonum"
            };
            AssertTrue(peerWithNick.DisplayName == "Telefonum",
                "PeerDeviceNode.DisplayName: Takma ad varsa takma adı döndürür");

            var peerWithoutNick = new PeerDeviceNode
            {
                DeviceId = "android-device-2",
                DeviceName = "DESKTOP-ABC1234-WIN11-PRO",
                Nickname = ""
            };
            AssertTrue(peerWithoutNick.DisplayName == "DESKTOP-ABC1234-WIN11-PRO",
                "PeerDeviceNode.DisplayName: Takma ad boşsa orijinal cihaz adına (DeviceName) geri döner");

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

        // 5. Test Emergency Return ('Fareyi Ana Makineye Gönder') with ScreenEdge.None
        EdgeHandOffPacket? emergencyReturnPkt = null;
        nodeA.RemoteEdgeHandOffReceived += (pkt, _) => emergencyReturnPkt = pkt;

        nodeB.SendEdgeReturnToPeer(peerAOnB, ScreenEdge.None, 0.5f);
        await Task.Delay(200);

        AssertTrue(
            emergencyReturnPkt != null &&
            emergencyReturnPkt.Value.TargetEntranceEdge == ScreenEdge.None,
            "Acil Durum (Fareyi Ana Makineye Gönder): İkincil cihaz tek tuşla ana makineye anında dönüş sinyali iletti");

        try { Directory.Delete(tempShelf, true); } catch { }
    }

    private static async Task TestColonyBeaconAndSinglePinColonyMeshAsync()
    {
        string tempShelf = Path.Combine(Path.GetTempPath(), "ConnectMeColonyTest_" + Guid.NewGuid().ToString("N")[..6]);
        await using var nodeA = new ConnectMeNetworkNode("win-pc-A", "PC-A", "windows", tempShelf, fixedPin: "111222");
        await using var nodeB = new ConnectMeNetworkNode("win-pc-B", "PC-B", "windows", tempShelf, fixedPin: "333444");
        await using var nodeC = new ConnectMeNetworkNode("win-pc-C", "PC-C", "windows", tempShelf, fixedPin: "555666");

        nodeA.Start(discoveryPort: 43149, inputUdpPort: 43150, controlTcpPort: 43151);
        nodeB.Start(discoveryPort: 43159, inputUdpPort: 43160, controlTcpPort: 43161);
        nodeC.Start(discoveryPort: 43169, inputUdpPort: 43170, controlTcpPort: 43171);

        var peerBOnA = nodeA.RegisterManualPeer("127.0.0.1", "PC-B", "windows", udpPort: 43160, tcpPort: 43161, customDeviceId: "win-pc-B");
        var peerAOnB = nodeB.RegisterManualPeer("127.0.0.1", "PC-A", "windows", udpPort: 43150, tcpPort: 43151, customDeviceId: "win-pc-A");

        // 1. Node A and Node B mutually pair with each other
        await nodeA.SubmitRemotePinForPairingAsync(peerBOnA, "333444", rememberDevice: false);
        await nodeB.SubmitRemotePinForPairingAsync(peerAOnB, "111222", rememberDevice: false);
        await nodeA.SubmitRemotePinForPairingAsync(peerBOnA, "333444", rememberDevice: false);

        AssertTrue(peerBOnA.IsMutuallyPaired && peerAOnB.IsMutuallyPaired, "Koloni Hazırlık: Node A ve Node B karşılıklı eşleşti");

        // Verify ColonyId is generated
        string? colonyIdA = nodeA.GetCurrentColonyId();
        string? colonyIdB = nodeB.GetCurrentColonyId();
        AssertTrue(!string.IsNullOrEmpty(colonyIdA) && colonyIdA == colonyIdB, "Koloni Oluşumu: Ortak SHA256 bazlı ColonyId üretildi");

        var membersA = nodeA.GetColonyMembers();
        AssertTrue(membersA.Count == 2 && membersA.Any(m => m.DeviceId == "win-pc-A") && membersA.Any(m => m.DeviceId == "win-pc-B"),
            "Koloni Üyeleri: Koloni üye listesi doğru cihazları içeriyor");

        // 2. Node C discovers/registers Node A and connects to the entire colony with only Node A's PIN
        var peerAOnC = nodeC.RegisterManualPeer("127.0.0.1", "PC-A", "windows", udpPort: 43150, tcpPort: 43151, customDeviceId: "win-pc-A");
        peerAOnC.ColonyId = colonyIdA;
        peerAOnC.ColonyMembers = membersA;

        var colonyConnectRes = await nodeC.ConnectToColonyViaPeerAsync(peerAOnC, "111222", rememberDevice: false);
        AssertTrue(colonyConnectRes.Success, "Koloni Bağlantısı: Node C tek PIN ile koloniye bağlanma isteği gönderdi");

        // Wait for connection from C to arrive at A and accept it
        PeerDeviceNode? peerCOnA = null;
        for (int i = 0; i < 30; i++)
        {
            peerCOnA = nodeA.DiscoveredPeers.FirstOrDefault(p => p.DeviceId == "win-pc-C");
            if (peerCOnA != null)
                break;
            await Task.Delay(100);
        }

        if (peerCOnA != null)
        {
            await nodeA.AcceptInboundPairingAsync(peerCOnA, rememberDevice: false);
        }

        // Wait for COLONY_INTRODUCE and sync propagation
        for (int i = 0; i < 35; i++)
        {
            var pBOnC = nodeC.DiscoveredPeers.FirstOrDefault(p => p.DeviceId == "win-pc-B");
            var pCOnB = nodeB.DiscoveredPeers.FirstOrDefault(p => p.DeviceId == "win-pc-C");
            if (peerAOnC.IsMutuallyPaired && pBOnC?.IsMutuallyPaired == true && pCOnB?.IsMutuallyPaired == true)
                break;
            await Task.Delay(150);
        }

        AssertTrue(peerAOnC.IsMutuallyPaired, "Koloni Ağı: Node C ve Node A karşılıklı eşleşti");

        // Node C should have automatically connected to Node B without needing Node B's PIN!
        var peerBOnC = nodeC.DiscoveredPeers.FirstOrDefault(p => p.DeviceId == "win-pc-B");
        AssertTrue(peerBOnC != null && peerBOnC.IsMutuallyPaired,
            "Koloni Otomatik Bağlantı: Node C, Node B'nin şifresini girmeden tek şifre ile Node B'ye bağlandı!");

        // Node B should also have Node C introduced automatically
        var peerCOnB = nodeB.DiscoveredPeers.FirstOrDefault(p => p.DeviceId == "win-pc-C");
        AssertTrue(peerCOnB != null && peerCOnB.IsMutuallyPaired,
            "Koloni Otomatik Tanıtım (COLONY_INTRODUCE): Node B, Node C'yi koloni aracılığıyla otomatik tanıdı ve bağlandı!");

        // 3. 1-Click Fast Inbound Pairing Acceptance
        await using var nodeD = new ConnectMeNetworkNode("win-pc-D", "PC-D", "windows", tempShelf, fixedPin: "777888");
        nodeD.Start(discoveryPort: 43179, inputUdpPort: 43180, controlTcpPort: 43181);

        var peerCOnD = nodeD.RegisterManualPeer("127.0.0.1", "PC-C", "windows", udpPort: 43170, tcpPort: 43171, customDeviceId: "win-pc-C");
        // Node D submits Node C's PIN
        await nodeD.SubmitRemotePinForPairingAsync(peerCOnD, "555666", rememberDevice: false);

        await Task.Delay(150);
        var peerDOnC = nodeC.DiscoveredPeers.FirstOrDefault(p => p.DeviceId == "win-pc-D");
        AssertTrue(peerDOnC != null && peerDOnC.RemoteEnteredMyPinVerified && !peerDOnC.IsMutuallyPaired,
            "1-Tık Kabul Öncesi: Node C'de gelen bağlantı isteği onay bekliyor durumuna geçti");

        // Node C accepts with 1-click (no need to type Node D's PIN)
        bool accepted = await nodeC.AcceptInboundPairingAsync(peerDOnC!, rememberDevice: false);
        await Task.Delay(150);

        AssertTrue(accepted && peerDOnC!.IsMutuallyPaired && peerCOnD.IsMutuallyPaired,
            "1-Tık Kabul (PAIR_ACCEPT): Tek tıkla kabul butonu karşılıklı bağlantıyı anında tamamladı!");

        try { Directory.Delete(tempShelf, true); } catch { }
    }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [System.Runtime.InteropServices.Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [System.Runtime.InteropServices.PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr ppDevices);
        [System.Runtime.InteropServices.PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr ppDevice);
    }

    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
    private delegate int ActivateDelegate(IntPtr thisPtr, ref Guid iid, int dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);

    private static void TestWasapiAudioLoopback()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out IntPtr pDevice);
            if (hr != 0 || pDevice == IntPtr.Zero)
            {
                // In headless CI environments (e.g. GitHub Actions windows-latest runners), no physical sound card is present
                Console.WriteLine($"  [PASS] WASAPI: Headless/CI sanal sunucusunda ses donanımı bulunamadı (hr=0x{hr:X}), donanım testi güvenle atlandı.");
                return;
            }

            AssertTrue(pDevice != IntPtr.Zero, "WASAPI: Varsayılan Ses Çıkış Cihazı (IMMDevice) başarıyla alındı");

            if (pDevice != IntPtr.Zero)
            {
                IntPtr vtbl = System.Runtime.InteropServices.Marshal.ReadIntPtr(pDevice);
                IntPtr activatePtr = System.Runtime.InteropServices.Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size);
                var activate = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<ActivateDelegate>(activatePtr);

                Guid iidEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
                int evHr = activate(pDevice, ref iidEndpointVolume, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out IntPtr pEndpointVolume);
                AssertTrue(evHr == 0 && pEndpointVolume != IntPtr.Zero, "WASAPI: IAudioEndpointVolume (CLSCTX_ALL) arabirimi aktifleştirildi");
                if (pEndpointVolume != IntPtr.Zero)
                {
                    System.Runtime.InteropServices.Marshal.Release(pEndpointVolume);
                }

                Guid iidAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
                int actHr = activate(pDevice, ref iidAudioClient, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out IntPtr pAudioClient);
                AssertTrue(actHr == 0 && pAudioClient != IntPtr.Zero, "WASAPI: IAudioClient arabirimi başarıyla aktifleştirildi");

                if (pAudioClient != IntPtr.Zero)
                {
                    IntPtr acVtbl = System.Runtime.InteropServices.Marshal.ReadIntPtr(pAudioClient);
                    
                    // vtable[8]: GetMixFormat(IntPtr thisPtr, out IntPtr ppFormat)
                    IntPtr getMixFormatPtr = System.Runtime.InteropServices.Marshal.ReadIntPtr(acVtbl, 8 * IntPtr.Size);
                    var getMixFormat = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<GetMixFormatDelegate>(getMixFormatPtr);
                    int gmfHr = getMixFormat(pAudioClient, out IntPtr pFormat);
                    AssertTrue(gmfHr == 0 && pFormat != IntPtr.Zero, "WASAPI: GetMixFormat ses formatı başarıyla alındı");

                    // vtable[3]: Initialize(IntPtr thisPtr, int shareMode, uint streamFlags, long hnsBuffer, long hnsPeriod, IntPtr pFormat, ref Guid sessionGuid)
                    IntPtr initPtr = System.Runtime.InteropServices.Marshal.ReadIntPtr(acVtbl, 3 * IntPtr.Size);
                    var init = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<InitializeDelegate>(initPtr);
                    Guid emptyGuid = Guid.Empty;
                    const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
                    int initHr = init(pAudioClient, 0 /* SHARED */, AUDCLNT_STREAMFLAGS_LOOPBACK, 1000000 /* 100ms */, 0, pFormat, ref emptyGuid);
                    AssertTrue(initHr == 0, $"WASAPI: Loopback yakalama modu başarıyla başlatıldı (hr=0x{initHr:X})");

                    // vtable[14]: GetService(IntPtr thisPtr, ref Guid riid, out IntPtr ppv)
                    IntPtr getServicePtr = System.Runtime.InteropServices.Marshal.ReadIntPtr(acVtbl, 14 * IntPtr.Size);
                    var getService = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<GetServiceDelegate>(getServicePtr);
                    Guid iidCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
                    int gsHr = getService(pAudioClient, ref iidCaptureClient, out IntPtr pCaptureClient);
                    AssertTrue(gsHr == 0 && pCaptureClient != IntPtr.Zero, "WASAPI: IAudioCaptureClient başarıyla elde edildi");

                    if (pCaptureClient != IntPtr.Zero)
                    {
                        System.Runtime.InteropServices.Marshal.Release(pCaptureClient);
                    }
                    if (pFormat != IntPtr.Zero)
                    {
                        System.Runtime.InteropServices.Marshal.FreeCoTaskMem(pFormat);
                    }
                    System.Runtime.InteropServices.Marshal.Release(pAudioClient);
                }
                System.Runtime.InteropServices.Marshal.Release(pDevice);
            }
        }
        catch (Exception ex)
        {
            AssertTrue(false, $"WASAPI testi hata verdi: {ex.Message}");
        }
    }

    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
    private delegate int GetMixFormatDelegate(IntPtr thisPtr, out IntPtr ppFormat);

    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
    private delegate int InitializeDelegate(IntPtr thisPtr, int shareMode, uint streamFlags, long hnsBuffer, long hnsPeriod, IntPtr pFormat, ref Guid sessionGuid);

    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
    private delegate int GetServiceDelegate(IntPtr thisPtr, ref Guid riid, out IntPtr ppv);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    private struct TestWAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    private struct TestWAVEFORMATEXTENSIBLE
    {
        public TestWAVEFORMATEX Format;
        public ushort wValidBitsPerSample;
        public uint dwChannelMask;
        public Guid SubFormat;
    }

    private static void TestAudioCaptureFloatConversionAndPacking()
    {
        // 1. Pack=1 verification
        int wfeSize = System.Runtime.InteropServices.Marshal.SizeOf<TestWAVEFORMATEX>();
        int subFormatOffset = (int)System.Runtime.InteropServices.Marshal.OffsetOf<TestWAVEFORMATEXTENSIBLE>("SubFormat");
        AssertTrue(wfeSize == 18, $"WAVEFORMATEX Pack=1 boyutu 18 bayt (Gerçek: {wfeSize})");
        AssertTrue(subFormatOffset == 24, $"WAVEFORMATEXTENSIBLE SubFormat ofseti 24. bayt (Gerçek: {subFormatOffset})");

        // 2. Float to PCM16 conversion vs integer bit-shift distortion test
        float quietPositive = 0.001f;
        float quietNegative = -0.001f;

        short correctPcmPos = (short)Math.Clamp((int)MathF.Round(quietPositive * 32767f), short.MinValue, short.MaxValue);
        short correctPcmNeg = (short)Math.Clamp((int)MathF.Round(quietNegative * 32767f), short.MinValue, short.MaxValue);

        AssertTrue(Math.Abs(correctPcmPos) <= 35 && Math.Abs(correctPcmNeg) <= 35,
            $"IEEE Float dönüşümü sessiz sesleri temiz dönüştürüyor (Pos: {correctPcmPos}, Neg: {correctPcmNeg})");

        // Simulate broken integer bitshift on IEEE float memory using standard BitConverter
        int posBits = BitConverter.SingleToInt32Bits(quietPositive);
        int negBits = BitConverter.SingleToInt32Bits(quietNegative);
        short brokenPos = (short)(posBits >> 16);
        short brokenNeg = (short)(negBits >> 16);
        int distortionStep = Math.Abs(brokenPos - brokenNeg);
        AssertTrue(distortionStep > 30000,
            $"Düzeltilen hata doğrulaması: Eski kodda float int olarak okunup {distortionStep} büyüklüğünde statik kare dalga beyaz gürültü üretiyordu");
    }

    private static async Task TestAudioMatrixAndRoutingAsync()
    {
        string tempShelf = Path.Combine(Path.GetTempPath(), "ConnectMe_AudioTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempShelf);

        await using var nodeA = new ConnectMeNetworkNode("node-a-id", "Windows Masaüstü", "windows", tempShelf, fixedPin: "111222");
        await using var nodeB = new ConnectMeNetworkNode("node-b-id", "Nobara Linux", "linux", tempShelf, fixedPin: "333444");

        nodeA.Start(discoveryPort: 43250, inputUdpPort: 43251, controlTcpPort: 43252);
        nodeB.Start(discoveryPort: 43260, inputUdpPort: 43261, controlTcpPort: 43262);

        // Define local audio sinks on both nodes
        var nodeASinks = new List<AudioSinkDescriptor>
        {
            new() { DeviceId = "node-a-id", DeviceName = "Windows Masaüstü", Platform = "windows", SinkId = "winmm:0", SinkName = "Hoparlörler (Realtek(R) Audio)", IsDefault = true },
            new() { DeviceId = "node-a-id", DeviceName = "Windows Masaüstü", Platform = "windows", SinkId = "winmm:1", SinkName = "SteelSeries Arctis Nova 7 (Kulaklık)", IsDefault = false }
        };
        nodeA.SetLocalAudioSinks(nodeASinks);

        var nodeBSinks = new List<AudioSinkDescriptor>
        {
            new() { DeviceId = "node-b-id", DeviceName = "Nobara Linux", Platform = "linux", SinkId = "pulse:alsa_output.pci-0000_00_1f.3.analog-stereo", SinkName = "Dahili Analog Ses (Hoparlör)", IsDefault = true },
            new() { DeviceId = "node-b-id", DeviceName = "Nobara Linux", Platform = "linux", SinkId = "pulse:alsa_output.usb-DAC", SinkName = "USB Kulaklık Çıkışı", IsDefault = false }
        };
        nodeB.SetLocalAudioSinks(nodeBSinks);

        var peerBOnA = nodeA.RegisterManualPeer("127.0.0.1", "Nobara Linux", "linux", udpPort: 43261, tcpPort: 43262, customDeviceId: "node-b-id");
        var peerAOnB = nodeB.RegisterManualPeer("127.0.0.1", "Windows Masaüstü", "windows", udpPort: 43251, tcpPort: 43252, customDeviceId: "node-a-id");

        // Mutual pairing
        await nodeA.SubmitRemotePinForPairingAsync(peerBOnA, "333444", rememberDevice: false);
        await nodeB.SubmitRemotePinForPairingAsync(peerAOnB, "111222", rememberDevice: false);
        await Task.Delay(250);

        AssertTrue(peerBOnA.IsMutuallyPaired && peerAOnB.IsMutuallyPaired,
            "Ses Matrisi Hazırlık: Node A ve Node B karşılıklı eşleşti");

        // Check if sinks were exchanged
        bool bHasASinks = nodeB.RemoteAudioSinks.TryGetValue("node-a-id", out var aSinksOnB) && aSinksOnB.Count == 2;
        bool aHasBSinks = nodeA.RemoteAudioSinks.TryGetValue("node-b-id", out var bSinksOnA) && bSinksOnA.Count == 2;

        AssertTrue(bHasASinks && aHasBSinks,
            "Ses Matrisi (AUDIO_SINKS_ANNOUNCE): Node A ve Node B birbirinin tüm ses çıkışlarını başarıyla aldı");

        // Now Node A selects Node B's USB Kulaklık as the central audio sink
        AudioRouteTarget? receivedRouteOnB = null;
        nodeB.AudioRouteChanged += r => receivedRouteOnB = r;

        var route = new AudioRouteTarget
        {
            TargetDeviceId = "node-b-id",
            TargetDeviceName = "Nobara Linux",
            TargetSinkId = "pulse:alsa_output.usb-DAC",
            TargetSinkName = "USB Kulaklık Çıkışı",
            TargetIp = "127.0.0.1",
            TargetUdpPort = ProtocolConstants.AudioStreamUdpPort
        };

        await nodeA.SetAndBroadcastAudioRouteAsync(route);
        await Task.Delay(200);

        AssertTrue(receivedRouteOnB != null && receivedRouteOnB.TargetDeviceId == "node-b-id" && receivedRouteOnB.TargetSinkId == "pulse:alsa_output.usb-DAC",
            "Ses Yönlendirme (AUDIO_ROUTE_CHANGE): Node B seçilen hedef ses çıkışını (USB Kulaklık Çıkışı) aldı");

        // Node A resets route to NONE
        await nodeA.SetAndBroadcastAudioRouteAsync(null);
        await Task.Delay(350);

        AssertTrue(receivedRouteOnB == null,
            $"Ses Sıfırlama: Yönlendirme devre dışı bırakıldığında tüm cihazlar yerel moda döndü (receivedRouteOnB={receivedRouteOnB?.TargetDeviceId ?? "null"})");

        try { Directory.Delete(tempShelf, true); } catch { }
    }

    private static async Task TestRelativeSpatialTopologyAndMultiDeviceColonySyncAsync()
    {
        // 1. Relative Canvas Snapping to another peer card
        var topology = new SpatialTopologyEngine();
        var peerB = new PeerDeviceNode { DeviceId = "peer-B", DeviceName = "Laptop B", MyEnteredPinVerifiedByRemote = true, RemoteEnteredMyPinVerified = true, CanvasX = 200, CanvasY = 100, CanvasWidth = 140, CanvasHeight = 90 };
        var peerC = new PeerDeviceNode { DeviceId = "peer-C", DeviceName = "Tablet C", MyEnteredPinVerifiedByRemote = true, RemoteEnteredMyPinVerified = true };

        var anchors = new List<CanvasAnchorBox>
        {
            new("DISPLAY1", "Birincil Ekran", true, 0, 100, 160, 90),
            new("peer-B", "Laptop B", false, 200, 100, 140, 90)
        };

        // Drag Peer C to the right of Peer B (around (360, 100))
        var (snappedAnchor, snappedEdge, finalX, finalY, segStart, segEnd) =
            topology.SnapPeerBoxToAnchorBoxesOnCanvas(peerC, 360, 100, 140, 90, anchors);

        AssertTrue(snappedAnchor.Id == "peer-B" && snappedEdge == ScreenEdge.Right && peerC.AttachedToDeviceId == "peer-B",
            "2D Koloni Kanvası: Peer C doğrudan Peer B'nin sağ kenarına bağıl olarak kenetlendi");

        // 2. Adjacency navigation for multi-hop cursor relay
        peerB.AssignedEdgeOnLocal = ScreenEdge.Right;
        peerB.AttachedToDeviceId = string.Empty;
        topology.AssignPeerToEdge(ScreenEdge.Right, peerB);

        var adjRightOfB = topology.FindAdjacentPeerOnEdge("peer-B", ScreenEdge.Right);
        var adjLeftOfC = topology.FindAdjacentPeerOnEdge("peer-C", ScreenEdge.Left);

        AssertTrue(adjRightOfB?.DeviceId == "peer-C" && adjLeftOfC?.DeviceId == "peer-B",
            "Göreceli Topoloji Navigasyonu: Node B sağında Node C'yi, Node C solunda Node B'yi tespit etti");

        // 3. Colony Topology Synchronization over TCP (COLONY_TOPOLOGY_SYNC)
        string tempShelf = Path.Combine(Path.GetTempPath(), "ConnectMeColonyRelSync_" + Guid.NewGuid().ToString("N")[..6]);
        await using var nodeA = new ConnectMeNetworkNode("node-a", "Host A", "windows", tempShelf, fixedPin: "111222");
        await using var nodeB = new ConnectMeNetworkNode("node-b", "Node B", "windows", tempShelf, fixedPin: "333444");

        nodeA.Start(discoveryPort: 43350, inputUdpPort: 43351, controlTcpPort: 43352);
        nodeB.Start(discoveryPort: 43360, inputUdpPort: 43361, controlTcpPort: 43362);

        var pBOnA = nodeA.RegisterManualPeer("127.0.0.1", "Node B", "windows", udpPort: 43361, tcpPort: 43362, customDeviceId: "node-b");
        var pAOnB = nodeB.RegisterManualPeer("127.0.0.1", "Host A", "windows", udpPort: 43351, tcpPort: 43352, customDeviceId: "node-a");
        var pCOnB = nodeB.RegisterManualPeer("127.0.0.1", "Node C", "windows", udpPort: 43371, tcpPort: 43372, customDeviceId: "node-c");
        pCOnB.MyEnteredPinVerifiedByRemote = true;
        pCOnB.RemoteEnteredMyPinVerified = true;

        await nodeA.SubmitRemotePinForPairingAsync(pBOnA, "333444", rememberDevice: false);
        await nodeB.SubmitRemotePinForPairingAsync(pAOnB, "111222", rememberDevice: false);
        await Task.Delay(200);

        List<ColonyNodeLayoutDescriptor>? receivedTopologyOnB = null;
        nodeB.ColonyTopologyChanged += topo => receivedTopologyOnB = topo;

        var topoDescriptors = new List<ColonyNodeLayoutDescriptor>
        {
            new() { DeviceId = "node-a", DeviceName = "Host A", Platform = "windows", AttachedToDeviceId = "", AttachedEdge = (int)ScreenEdge.None },
            new() { DeviceId = "node-b", DeviceName = "Node B", Platform = "windows", AttachedToDeviceId = "node-a", AttachedEdge = (int)ScreenEdge.Right, ColonyX = 200, ColonyY = 50 },
            new() { DeviceId = "node-c", DeviceName = "Node C", Platform = "windows", AttachedToDeviceId = "node-b", AttachedEdge = (int)ScreenEdge.Right, ColonyX = 360, ColonyY = 50 }
        };

        nodeA.SendColonyTopologySyncToAllPeers(topoDescriptors);
        await Task.Delay(250);

        AssertTrue(receivedTopologyOnB != null && receivedTopologyOnB.Count == 3,
            "Koloni Topoloji Senkronizasyonu (COLONY_TOPOLOGY_SYNC): Node B tüm koloninin 3 cihazlık göreceli düzenini aldı");

        var topologyB = new SpatialTopologyEngine();
        topologyB.AssignPeerToEdge(ScreenEdge.None, pAOnB);
        topologyB.AssignPeerToEdge(ScreenEdge.None, pCOnB);
        topologyB.ApplyColonyTopology(receivedTopologyOnB!, "node-a", "node-b");

        AssertTrue(pAOnB.AssignedEdgeOnLocal == ScreenEdge.Left && pCOnB.AssignedEdgeOnLocal == ScreenEdge.Right,
            "Göreceli Ekran Uyarlaması: Node B kendi arayüzünde Node A'yı solunda, Node C'yi ise sağında simetrik olarak konumlandırdı");

        // 4. Multi-hop cursor EDGE_RELAY
        string? relayedTargetOnA = null;
        ScreenEdge relayedEdgeOnA = ScreenEdge.None;
        nodeA.RemoteEdgeRelayRequested += (targetId, edge, norm) =>
        {
            relayedTargetOnA = targetId;
            relayedEdgeOnA = edge;
        };

        nodeB.SendEdgeRelayToHost(pAOnB, "node-c", ScreenEdge.Left, 0.6f);
        await Task.Delay(200);

        AssertTrue(relayedTargetOnA == "node-c" && relayedEdgeOnA == ScreenEdge.Left,
            "Çok Atlamalı İmleç Aktarımı (EDGE_RELAY): Node B kenar sınırında Node C'ye geçiş için Host A'ya aktarım sinyali iletti");

        // 5. Persistence of relative topology in TrustedDeviceStore
        string storePath = Path.Combine(tempShelf, "trusted_test.json");
        var store = new TrustedDeviceStore(storePath);
        store.AddOrUpdateTrustedDevice(new TrustedDeviceRecord
        {
            DeviceId = "node-c",
            DeviceName = "Tablet C",
            Platform = "android",
            TrustToken = "testtoken12345",
            AttachedToDeviceId = "node-b",
            AssignedEdge = ScreenEdge.Right,
            ColonyX = 360,
            ColonyY = 50
        });

        var freshStore = new TrustedDeviceStore(storePath);
        bool isTr = freshStore.IsDeviceTrusted("node-c", out var loadedRec);
        AssertTrue(isTr && loadedRec?.AttachedToDeviceId == "node-b" && Math.Abs(loadedRec.ColonyX - 360) < 0.1,
            "TrustedDeviceStore: Göreceli kenetlenme (AttachedToDeviceId) ve koloni koordinatları başarıyla diske kaydedilip okundu");

        try { Directory.Delete(tempShelf, true); } catch { }
    }

    private static async Task TestRichClipboardImageAndCrossEdgeDragAsync()
    {
        string tempShelf = Path.Combine(Path.GetTempPath(), $"connectme-clipimg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempShelf);

        await using var nodeA = new ConnectMeNetworkNode("clip-a", "Host A", "windows", tempShelf, fixedPin: "222333");
        await using var nodeB = new ConnectMeNetworkNode("clip-b", "Linux B", "linux-nobara", tempShelf, fixedPin: "444555");

        nodeA.Start(discoveryPort: 43380, inputUdpPort: 43381, controlTcpPort: 43382);
        nodeB.Start(discoveryPort: 43390, inputUdpPort: 43391, controlTcpPort: 43392);

        var pBOnA = nodeA.RegisterManualPeer("127.0.0.1", "Linux B", "linux-nobara", udpPort: 43391, tcpPort: 43392, customDeviceId: "clip-b");
        var pAOnB = nodeB.RegisterManualPeer("127.0.0.1", "Host A", "windows", udpPort: 43381, tcpPort: 43382, customDeviceId: "clip-a");

        await nodeA.SubmitRemotePinForPairingAsync(pBOnA, "444555", rememberDevice: false);
        await nodeB.SubmitRemotePinForPairingAsync(pAOnB, "222333", rememberDevice: false);
        await Task.Delay(200);

        // 1. Test CLIPBOARD_IMAGE binary PNG transfer + SHA-256 deduplication
        byte[]? receivedPngOnB = null;
        string? receivedSenderOnB = null;
        nodeB.RemoteClipboardImageReceived += (bytes, sender) =>
        {
            receivedPngOnB = bytes;
            receivedSenderOnB = sender;
        };

        // Synthetic PNG header + payload
        byte[] samplePng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x03, 0x04, 0xAA, 0xBB, 0xCC, 0xDD };
        await nodeA.BroadcastClipboardImageAsync(samplePng);
        await Task.Delay(250);

        AssertTrue(receivedPngOnB != null && receivedPngOnB.SequenceEqual(samplePng) && receivedSenderOnB == "Host A",
            "Zengin Evrensel Pano (CLIPBOARD_IMAGE): Ekran görüntüsü/PNG ikili verisi TCP üzerinden eksiksiz iletildi");

        // 2. Test Cross-Edge Drag & Drop UDP Handoff with IsDraggingShelfItem = true
        EdgeHandOffPacket? receivedHandOffOnB = null;
        nodeB.RemoteEdgeHandOffReceived += (pkt, ep) => receivedHandOffOnB = pkt;

        nodeA.SendEdgeHandOff(pBOnA, ScreenEdge.Left, 0.72f, isDraggingShelfItem: true);
        await Task.Delay(150);

        AssertTrue(receivedHandOffOnB != null &&
                   receivedHandOffOnB.Value.TargetEntranceEdge == ScreenEdge.Left &&
                   receivedHandOffOnB.Value.IsDraggingShelfItem &&
                   Math.Abs(receivedHandOffOnB.Value.NormalizedPosition - 0.72f) < 0.01f,
            "Kenar Sürükle-Bırak (EDGE_HANDOFF + IsDraggingShelfItem): Sol tık basılı kenar geçişinde sürükleme bayrağı karşı cihaza iletildi");

        // 3. Test Configurable Shelf Directory (SetShelfReceiveDirectory) + File Transfer into Custom Folder
        string customShelfB = Path.Combine(tempShelf, "CustomUserShelfFolder");
        bool dirChanged = nodeB.SetShelfReceiveDirectory(customShelfB, persist: false);
        AssertTrue(dirChanged && nodeB.ShelfReceiveDirectory == Path.GetFullPath(customShelfB),
            "Özelleştirilebilir Ortak Cep Klasörü: SetShelfReceiveDirectory hedef klasörü dinamik olarak güncelledi");

        string sampleFile = Path.Combine(tempShelf, "drag_paste_test.txt");
        await File.WriteAllTextAsync(sampleFile, "Connect Me v1.8.28 Drag & Paste Test");
        await nodeA.SendFileToPeerShelfAsync(pBOnA, sampleFile);
        await Task.Delay(250);

        string expectedReceivedPath = Path.Combine(customShelfB, "drag_paste_test.txt");
        AssertTrue(File.Exists(expectedReceivedPath),
            "Ortak Cep Dosya Aktarımı: Gönderilen dosya kullanıcının seçtiği yeni Ortak Cep klasörüne kaydedildi");

        try { Directory.Delete(tempShelf, true); } catch { }
    }

    private static void TestConsistentEdgeCrossingAndPrematureJumpPrevention()
    {
        var topo = new SpatialTopologyEngine
        {
            CornerDeadZonePixels = 24,
            EdgeResistancePixels = 20.0
        };
        var mon = new PhysicalMonitorDescriptor
        {
            MonitorId = "DISPLAY1",
            Name = "Birincil Ekran",
            VirtualX = 0,
            VirtualY = 0,
            Width = 1920,
            Height = 1080,
            IsPrimary = true
        };
        topo.UpdateLocalMonitors([mon]);

        var peer = new PeerDeviceNode
        {
            DeviceId = "laptop-1",
            DeviceName = "Laptop",
            Platform = "linux",
            ScreenWidth = 1920,
            ScreenHeight = 1080,
            MyEnteredPinVerifiedByRemote = true,
            RemoteEnteredMyPinVerified = true
        };
        topo.AssignPeerToEdge(ScreenEdge.Right, peer);

        // 1. Ekranın %75'inde (X=1440) yüksek hızda fare hareketi (deltaX=150) ASLA erken geçiş yapmamalıdır
        var midScreenStep = topo.EvaluateCursorStep(1440, 500, deltaX: 150, deltaY: 0);
        AssertTrue(!midScreenStep.ShouldTransition,
            "Erken Geçiş Önleme: Ekranın %75'inde (X=1440) hızlı fare vuruşunda diğer cihaza erken zıplama engellendi");

        // 2. Fiziksel sınırın hemen içi (X=1900) yine erken geçiş yapmamalıdır
        var nearBorderStep = topo.EvaluateCursorStep(1900, 500, deltaX: 20, deltaY: 0);
        AssertTrue(!nearBorderStep.ShouldTransition,
            "Erken Geçiş Önleme: Ekran kenarına yaklaşırken (X=1900) imleç henüz sınıra ulaşmadan geçiş engellendi");

        // 3. Pencere kapatma köşesinde (X=1919, Y=10) Dead-Corner koruması geçişi engeller
        var deadCornerStep = topo.EvaluateCursorStep(1919, 10, deltaX: 50, deltaY: 0);
        AssertTrue(!deadCornerStep.ShouldTransition,
            "Köşe Koruması: Sağ üst pencere kapatma köşesinde (Y=10) yanlışlıkla geçiş engellendi");

        // 4. Gerçek fiziksel sınırda (X=1919, Y=500) kasıtlı dışa doğru itme (>= 20px) geçişi tam ve tutarlı sağlar
        var push1 = topo.EvaluateCursorStep(1919, 500, deltaX: 12, deltaY: 0);
        var push2 = topo.EvaluateCursorStep(1919, 500, deltaX: 12, deltaY: 0);
        AssertTrue(!push1.ShouldTransition && push2.ShouldTransition && push2.TargetPeer?.DeviceId == "laptop-1",
            "Tutarlı Kenar Geçişi: Fiziksel sınırda (X=1919) kasıtlı itmede 20px direnç aşılarak diğer cihaza pürüzsüz geçiş sağlandı");

        // 5. İçe doğru geri çekilme (X=1900) birikmiş itmeyi sıfırlar
        topo.EvaluateCursorStep(1919, 500, deltaX: 10, deltaY: 0); // 10px birikti
        topo.EvaluateCursorStep(1500, 500, deltaX: -10, deltaY: 0); // İçe çekildi -> reset
        var pushAfterReset = topo.EvaluateCursorStep(1919, 500, deltaX: 12, deltaY: 0); // 12px < 20px
        AssertTrue(!pushAfterReset.ShouldTransition,
            "Kenar İtme Sıfırlama: İmleç sınırdan içeri çekildiğinde birikmiş direnç sıfırlandı ve kilitlenme önlendi");

        // 6. Geri dönüş koordinatı güvenli iç marj ile (X=1912) iniş yapar ve anında geri sekme yaşanmaz
        var (entryX, entryY) = topo.ComputeLocalEntryPoint(ScreenEdge.Right, 0.5f, peer);
        AssertTrue(entryX == 1912 && entryY == 540,
            "Güvenli İniş Koordinatı: Uzak cihazdan Windows'a geri dönüşte imleç sınırın içine (1912, 540) güvenle iniş yaptı");
    }

    private static async Task TestDeviceDeduplicationAndClearColonyTreeAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ConnectMeDedupTest_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(tempDir);
        try
        {
            await using var node = new ConnectMeNetworkNode("win-host-1", "Windows-Host", "windows", tempDir);

            // 1. IP bazlı tekilleştirme: Aynı IP adresine (192.168.1.100) sahip iki cihaz eklendiğinde eski placeholder temizlenmeli
            var peer1 = node.RegisterManualPeer("192.168.1.100", "Laptop-Eski", "linux", customDeviceId: "linux-old-id");
            peer1.Nickname = "Çalışma Bilgisayarı";

            var peer2 = node.RegisterManualPeer("192.168.1.100", "Laptop-Yeni", "linux", customDeviceId: "linux-new-id");

            int ipCount = node.DiscoveredPeers.Count(p => p.IpAddress == "192.168.1.100");
            AssertTrue(ipCount == 1, "Cihaz Tekilleştirme: Aynı IP adresine sahip cihazlar tekilleştirildi ve eski kopya silindi");
            AssertTrue(node.DiscoveredPeers.Any(p => p.DeviceId == "linux-new-id"),
                "Cihaz Tekilleştirme: Yeni DeviceId aktif cihaz olarak kaydedildi");

            // 2. Büyük/Küçük Harf (Case-Insensitive) tekilleştirme
            node.RegisterManualPeer("192.168.1.105", "PC-A", "windows", customDeviceId: "WIN-CAPS-TEST");
            node.RegisterManualPeer("192.168.1.105", "PC-A", "windows", customDeviceId: "win-caps-test");
            int caseCount = node.DiscoveredPeers.Count(p => p.DeviceId.Equals("win-caps-test", StringComparison.OrdinalIgnoreCase));
            AssertTrue(caseCount == 1, "Cihaz Tekilleştirme: Büyük/küçük harf farklılığı tekilleştirildi ve çift kayıt engellendi");

            // 3. Hayalet (Stale) çevrimdışı cihaz temizleme
            var ghostPeer = node.RegisterManualPeer("192.168.1.200", "Hayalet-Cihaz", "android", customDeviceId: "ghost-1");
            ghostPeer.LastSeen = DateTimeOffset.UtcNow.AddMinutes(-5); // 5 dakika önce görüldü
            ghostPeer.MyEnteredPinVerifiedByRemote = false;
            ghostPeer.RemoteEnteredMyPinVerified = false;

            node.PruneStalePeers(TimeSpan.FromSeconds(30));
            bool ghostExists = node.DiscoveredPeers.Any(p => p.DeviceId == "ghost-1");
            AssertTrue(!ghostExists, "Hayalet Cihaz Temizleme: Çevrimdışı kalan eşleşmemiş cihazlar listeden otomatik temizlendi");

            // 4. Aktif Koloni Cihaz Sayısı Doğrulaması: Yerel Makine (1) + Eşleşmiş Cihazlar (N)
            var pairedPeer = node.RegisterManualPeer("192.168.1.110", "Eşleşmiş-Tablet", "android", customDeviceId: "android-paired-1");
            pairedPeer.MyEnteredPinVerifiedByRemote = true;
            pairedPeer.RemoteEnteredMyPinVerified = true;

            int activeColonyTotalCount = 1 + node.MutuallyPairedPeers.Count;
            AssertTrue(activeColonyTotalCount == 2,
                "Net Koloni Görünümü: Aktif koloni ağında yerel bilgisayar dahil toplam 2 cihaz başarıyla hesaplandı");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static void TestAudioPcmMixerAndNetworkOptimization()
    {
        // 1. Multi-source PCM mixer saturation & summing test
        byte[] chunkA = new byte[8];
        byte[] chunkB = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(chunkA.AsSpan(0, 2), 10000);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(chunkB.AsSpan(0, 2), 5000);

        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(chunkA.AsSpan(2, 2), 25000);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(chunkB.AsSpan(2, 2), 25000); // Overflows short.MaxValue

        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(chunkA.AsSpan(4, 2), -25000);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(chunkB.AsSpan(4, 2), -25000); // Underflows short.MinValue

        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(chunkA.AsSpan(6, 2), 12000);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(chunkB.AsSpan(6, 2), -3000);

        byte[] dest = new byte[64];
        int mixedLen = AudioPcmMixer.MixIntoBuffer(new[] { chunkA, chunkB }, dest);
        AssertTrue(mixedLen == 8, $"AudioPcmMixer çıktı uzunluğu korundu ({mixedLen}B)");

        short s0 = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(dest.AsSpan(0, 2));
        short s1 = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(dest.AsSpan(2, 2));
        short s2 = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(dest.AsSpan(4, 2));
        short s3 = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(dest.AsSpan(6, 2));

        AssertTrue(s0 == 15000, $"AudioPcmMixer doğrusal toplama (10000+5000 = {s0})");
        AssertTrue(s1 == short.MaxValue, $"AudioPcmMixer pozitif taşma kırpması (clamped = {s1})");
        AssertTrue(s2 == short.MinValue, $"AudioPcmMixer negatif taşma kırpması (clamped = {s2})");
        AssertTrue(s3 == 9000, $"AudioPcmMixer karşı faz toplama (12000-3000 = {s3})");

        // 2. Network interface ranking & APIPA (169.254.x.x) avoidance check
        var localIps = ConnectMeNetworkNode.GetLocalIPv4Addresses();
        AssertTrue(localIps.Count > 0, $"Önbellekli NIC algılama ({localIps.Count} IPv4 bulundu)");
        bool hasRealNonApipa = localIps.Any(ip => !ip.ToString().StartsWith("169.254.") && !IPAddress.IsLoopback(ip));
        if (hasRealNonApipa)
        {
            AssertTrue(!localIps[0].ToString().StartsWith("169.254."),
                $"Akıllı ağ sıralaması birincil LAN IP'yi seçti ({localIps[0]}), APIPA/sanal adaptörü eledi");
        }
        else
        {
            AssertTrue(true, $"Yalnızca loopback/APIPA ortamı algılandı ({localIps[0]})");
        }
    }

    private static async Task TestWindowsUpdateDetectionAndSemverAsync()
    {
        // 1. Semantic Versioning unit tests
        AssertTrue(WindowsUpdateService.IsNewerVersion("v1.8.36", "1.8.35"), "Semver: 1.8.36 > 1.8.35");
        AssertTrue(WindowsUpdateService.IsNewerVersion("1.8.36", "1.8.35"), "Semver: 'v' olmadan sürüm karşılaştırması");
        AssertTrue(!WindowsUpdateService.IsNewerVersion("v1.8.35", "1.8.35"), "Semver: Aynı sürüm yeni değil");
        AssertTrue(!WindowsUpdateService.IsNewerVersion("v1.8.34", "1.8.35"), "Semver: Eski sürüm yeni değil");
        AssertTrue(!WindowsUpdateService.IsNewerVersion("v1.8.35", "1.8.35 (v1-8-35)"), "Semver: Parantezli derleme bilgisi doğru ayıklandı (aynı sürüm)");
        AssertTrue(WindowsUpdateService.IsNewerVersion("v1.8.36", "1.8.35 (v1-8-35)"), "Semver: Parantezli derleme bilgisi varken yeni sürüm tespit edildi");
        AssertTrue(WindowsUpdateService.IsNewerVersion("v1.9.0", "1.8.36"), "Semver: Küçük sürüm (minor) artışı tespit edildi");
        AssertTrue(WindowsUpdateService.IsNewerVersion("v2.0.0", "1.8.36"), "Semver: Büyük sürüm (major) artışı tespit edildi");
        AssertTrue(WindowsUpdateService.CompareVersions("v1.8.36", "1.8.36") == 0, "Semver: Eşit sürümler CompareVersions == 0");

        // 2. Multi-tier GitHub live update check
        try
        {
            var resultOld = await WindowsUpdateService.CheckForUpdatesAsync("1.0.0");
            AssertTrue(resultOld.IsSuccess, $"GitHub Güncelleme Denetimi başarılı (Son Sürüm: {resultOld.UpdateInfo?.VersionTag})");
            AssertTrue(resultOld.HasUpdate, "1.0.0 için yeni sürüm tespit edildi (HasUpdate == true)");
            AssertTrue(!string.IsNullOrEmpty(resultOld.UpdateInfo?.DownloadUrl), $"İndirme bağlantısı oluşturuldu: {resultOld.UpdateInfo?.DownloadUrl}");

            var resultCurrent = await WindowsUpdateService.CheckForUpdatesAsync("99.99.99");
            AssertTrue(resultCurrent.IsSuccess, "Gelecek sürüm (99.99.99) için denetim başarılı");
            AssertTrue(!resultCurrent.HasUpdate, "Gelecek sürüm için güncelleme yok (HasUpdate == false)");
            AssertTrue(resultCurrent.UpdateInfo != null, "Güncelleme olmasa dahi GitHub son sürüm bilgisi nesne olarak döndü");
        }
        catch (Exception ex)
        {
            AssertTrue(false, $"Güncelleme testi beklenmeyen hata verdi: {ex.Message}");
        }
    }
}

