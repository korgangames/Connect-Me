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
        TestBleProximityCodec();
        TestSpatialTopologyEdgeSwitchingAndDeadCorners();
        TestMultiDevicePartialEdgeSegmentsAndCanvasSnapping();
        await TestTcpFrameCodecAsync();
        await TestEndToEndMutualSixDigitPinAndLoopbackAsync();

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
}
