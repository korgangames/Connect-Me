using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ConnectMe.Core.Protocol;
using ConnectMe.Core.Topology;

namespace ConnectMe.Core.Network;

public sealed record ShelfItemEntry(
    string Id,
    string FileName,
    string LocalFilePath,
    long FileSizeBytes,
    string SenderName,
    DateTimeOffset ReceivedAt,
    bool IsOutgoing);

/// <summary>
/// Compact 16-byte BLE Manufacturer Specific Data payload so devices
/// can discover each other's Wi-Fi IP & Port over Bluetooth Low Energy (BLE).
/// </summary>
public static class BleProximityCodec
{
    public static byte[] EncodeAdvPayload(byte platformCode, IPAddress ipv4, ushort udpPort, string deviceId)
    {
        byte[] buf = new byte[16];
        buf[0] = ProtocolConstants.MagicByte0;
        buf[1] = ProtocolConstants.MagicByte1;
        buf[2] = ProtocolConstants.ProtocolVersion;
        buf[3] = platformCode;

        byte[] ipBytes = ipv4.GetAddressBytes();
        if (ipBytes.Length == 4)
        {
            Buffer.BlockCopy(ipBytes, 0, buf, 4, 4);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8, 2), udpPort);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(deviceId));
        Buffer.BlockCopy(hash, 0, buf, 10, 6);
        return buf;
    }

    public static bool TryDecodeAdvPayload(
        ReadOnlySpan<byte> buf,
        out byte platformCode,
        out IPAddress ipv4,
        out ushort udpPort,
        out string shortDeviceHash)
    {
        platformCode = 0;
        ipv4 = IPAddress.None;
        udpPort = 0;
        shortDeviceHash = string.Empty;

        if (buf.Length < 16 ||
            buf[0] != ProtocolConstants.MagicByte0 ||
            buf[1] != ProtocolConstants.MagicByte1 ||
            buf[2] != ProtocolConstants.ProtocolVersion)
        {
            return false;
        }

        platformCode = buf[3];
        ipv4 = new IPAddress(buf.Slice(4, 4));
        udpPort = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(8, 2));
        shortDeviceHash = Convert.ToHexString(buf.Slice(10, 6));
        return true;
    }
}

/// <summary>
/// Unified Peer-to-Peer Multi-Device Network Engine managing:
/// - UDP Discovery Broadcast/Unicast (42849)
/// - Mutual 6-Digit PIN Handshake over TCP (42851)
/// - UDP Fast-Path Input Streaming & Edge Handoff (42850)
/// - TCP Framed Clipboard & Drop Shelf File Transfer (42851)
/// </summary>
public sealed class ConnectMeNetworkNode : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, PeerDeviceNode> _peers = new();
    private UdpClient? _discoveryUdp;
    private UdpClient? _inputUdp;
    private TcpListener? _tcpListener;
    private ushort _mouseSeq;
    private string _lastClipboardHash = string.Empty;

    public string LocalDeviceId { get; }
    public string LocalDeviceName { get; }
    public string LocalPlatform { get; }
    public string PairingPin { get; private set; }
    public string ShelfReceiveDirectory { get; }

    public int DiscoveryPort { get; private set; } = ProtocolConstants.DiscoveryUdpPort;
    public int InputUdpPort { get; private set; } = ProtocolConstants.FastInputUdpPort;
    public int ControlTcpPort { get; private set; } = ProtocolConstants.DataControlTcpPort;

    public int LocalScreenWidth { get; set; } = 1920;
    public int LocalScreenHeight { get; set; } = 1080;

    // Events
    public event Action<PeerDeviceNode>? PeerDiscoveredOrUpdated;
    public event Action<PeerDeviceNode>? PeerPairingStatusChanged;
    public event Action<EdgeHandOffPacket, IPEndPoint>? RemoteEdgeHandOffReceived;
    public event Action<MouseMovePacket>? RemoteMouseMoveReceived;
    public event Action<MouseButtonPacket>? RemoteMouseButtonReceived;
    public event Action<MouseScrollPacket>? RemoteMouseScrollReceived;
    public event Action<KeyEventPacket>? RemoteKeyEventReceived;
    public event Action<string, string>? RemoteClipboardTextReceived;
    public event Action<byte[], string>? RemoteClipboardImageReceived;
    public event Action<ShelfItemEntry>? ShelfItemReceived;
    public event Action<string>? LogMessage;

    public ConnectMeNetworkNode(
        string? deviceId = null,
        string? deviceName = null,
        string platform = "windows",
        string? shelfDirectory = null,
        string? fixedPin = null)
    {
        LocalDeviceId = deviceId ?? Guid.NewGuid().ToString("N")[..12];
        LocalDeviceName = deviceName ?? Environment.MachineName;
        LocalPlatform = platform;
        PairingPin = fixedPin ?? RandomNumberGenerator.GetInt32(100000, 999999).ToString();

        ShelfReceiveDirectory = shelfDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "ConnectMe-Shelf");
        Directory.CreateDirectory(ShelfReceiveDirectory);
    }

    public IReadOnlyCollection<PeerDeviceNode> DiscoveredPeers => _peers.Values.ToList();
    public IReadOnlyCollection<PeerDeviceNode> MutuallyPairedPeers => _peers.Values.Where(p => p.IsMutuallyPaired).ToList();

    public string RegenerateLocalPin()
    {
        PairingPin = RandomNumberGenerator.GetInt32(100000, 999999).ToString();
        Log($"[Güvenlik] Yerel 6 haneli eşleşme kodu yenilendi: {PairingPin}");
        return PairingPin;
    }

    public void Start(
        int discoveryPort = ProtocolConstants.DiscoveryUdpPort,
        int inputUdpPort = ProtocolConstants.FastInputUdpPort,
        int controlTcpPort = ProtocolConstants.DataControlTcpPort)
    {
        DiscoveryPort = discoveryPort;
        InputUdpPort = inputUdpPort;
        ControlTcpPort = controlTcpPort;

        // 1. Bind Discovery UDP Socket
        try
        {
            _discoveryUdp = new UdpClient();
            _discoveryUdp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _discoveryUdp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            _discoveryUdp.EnableBroadcast = true;
            _ = Task.Run(() => DiscoveryReceiveLoopAsync(_cts.Token));
            _ = Task.Run(() => DiscoveryBroadcastLoopAsync(_cts.Token));
            Log($"[Keşif] Çoklu Cihaz UDP Discovery {DiscoveryPort} portunda başlatıldı.");
        }
        catch (Exception ex)
        {
            Log($"[Uyarı] Discovery port {DiscoveryPort} bağlanamadı: {ex.Message}");
        }

        // 2. Bind Fast-Path Input UDP Socket
        _inputUdp = new UdpClient(new IPEndPoint(IPAddress.Any, InputUdpPort));
        _inputUdp.Client.ReceiveBufferSize = 256 * 1024;
        _inputUdp.Client.SendBufferSize = 256 * 1024;
        InputUdpPort = ((IPEndPoint)_inputUdp.Client.LocalEndPoint!).Port;
        _ = Task.Run(() => InputReceiveLoopAsync(_cts.Token));
        _ = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
        Log($"[Fast-Path] UDP Girdi Motoru {InputUdpPort} portunda aktif (<1.5ms mod).");

        // 3. Bind Data & Control TCP Listener
        _tcpListener = new TcpListener(IPAddress.Any, ControlTcpPort);
        _tcpListener.Start();
        ControlTcpPort = ((IPEndPoint)_tcpListener.LocalEndpoint).Port;
        _ = Task.Run(() => TcpAcceptLoopAsync(_cts.Token));
        Log($"[Veri & PIN Kanalı] TCP Sunucusu {ControlTcpPort} portunda aktif (Yerel PIN: {PairingPin}).");
    }

    public static List<IPAddress> GetLocalIPv4Addresses()
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        result.Add(ua.Address);
                    }
                }
            }
        }
        catch
        {
            // Fallback
        }

        if (result.Count == 0)
            result.Add(IPAddress.Loopback);

        return result;
    }

    public PeerDeviceNode RegisterManualPeer(
        string ipAddress,
        string deviceName = "Android Cihaz",
        string platform = "android",
        int udpPort = ProtocolConstants.FastInputUdpPort,
        int tcpPort = ProtocolConstants.DataControlTcpPort,
        int screenWidth = 1080,
        int screenHeight = 2400,
        bool viaBle = false,
        string? customDeviceId = null,
        string? simulatedPin = null,
        bool autoMutuallyPair = false)
    {
        string id = customDeviceId ?? $"{platform}-{ipAddress}:{udpPort}";
        var peer = _peers.AddOrUpdate(
            id,
            _ => new PeerDeviceNode
            {
                DeviceId = id,
                DeviceName = deviceName,
                Platform = platform,
                IpAddress = ipAddress,
                UdpInputPort = udpPort,
                TcpControlPort = tcpPort,
                ScreenWidth = screenWidth,
                ScreenHeight = screenHeight,
                LastSeen = DateTimeOffset.UtcNow,
                DiscoveredViaBle = viaBle,
                SimulatedLocalPin = simulatedPin,
                MyEnteredPinVerifiedByRemote = autoMutuallyPair,
                RemoteEnteredMyPinVerified = autoMutuallyPair
            },
            (_, existing) =>
            {
                existing.IpAddress = ipAddress;
                existing.DeviceName = deviceName;
                existing.UdpInputPort = udpPort;
                existing.TcpControlPort = tcpPort;
                existing.ScreenWidth = screenWidth;
                existing.ScreenHeight = screenHeight;
                existing.LastSeen = DateTimeOffset.UtcNow;
                existing.DiscoveredViaBle |= viaBle;
                if (simulatedPin != null)
                    existing.SimulatedLocalPin = simulatedPin;
                if (autoMutuallyPair)
                {
                    existing.MyEnteredPinVerifiedByRemote = true;
                    existing.RemoteEnteredMyPinVerified = true;
                }
                return existing;
            });

        if (IPAddress.TryParse(ipAddress, out var parsedIp) && !IPAddress.IsLoopback(parsedIp))
        {
            _ = SendDiscoveryBeaconToAsync(new IPEndPoint(parsedIp, DiscoveryPort));
        }

        PeerDiscoveredOrUpdated?.Invoke(peer);
        return peer;
    }

    // =========================================================================
    // Mutual 6-Digit PIN Handshake Engine
    // =========================================================================

    /// <summary>
    /// Submits the target device's 6-digit PIN to verify local -> remote trust.
    /// Both devices must verify each other's 6-digit PIN to achieve MutuallyPaired status!
    /// </summary>
    public async Task<(bool Accepted, bool IsNowMutuallyPaired, string Message)> SubmitRemotePinForPairingAsync(
        PeerDeviceNode peer,
        string enteredRemotePin)
    {
        string cleanPin = enteredRemotePin.Trim().Replace("-", "").Replace(" ", "");
        if (cleanPin.Length != 6 || !cleanPin.All(char.IsDigit))
        {
            return (false, false, "Lütfen karşı cihazdaki 6 haneli rakam kodunu eksiksiz girin.");
        }

        // Handle local simulated device verification
        if (!string.IsNullOrEmpty(peer.SimulatedLocalPin))
        {
            if (cleanPin == peer.SimulatedLocalPin)
            {
                peer.MyEnteredPinVerifiedByRemote = true;
                PeerPairingStatusChanged?.Invoke(peer);
                PeerDiscoveredOrUpdated?.Invoke(peer);
                string statusMsg = peer.IsMutuallyPaired
                    ? $"✅ '{peer.DeviceName}' ile ÇİFT TARAFLI eşleşme tamamlandı! Ekran kanvasında konumlandırabilirsiniz."
                    : $"✅ '{peer.DeviceName}' cihazının kodu ({cleanPin}) doğrulandı! Şimdi karşı cihazdan da sizin kodunuzun ({PairingPin}) onaylanması bekleniyor.";
                Log($"[PIN Doğrulama] {statusMsg}");
                return (true, peer.IsMutuallyPaired, statusMsg);
            }
            else
            {
                string errMsg = $"❌ Hatalı kod! '{peer.DeviceName}' ekranındaki 6 haneli kod ({peer.SimulatedLocalPin}) ile uyuşmadı.";
                Log($"[PIN Hata] {errMsg}");
                return (false, false, errMsg);
            }
        }

        // Real network peer over TCP 42851
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await client.ConnectAsync(peer.IpAddress, peer.TcpControlPort, timeoutCts.Token).ConfigureAwait(false);
            await using var stream = client.GetStream();

            var reqHeader = new TcpControlHeader
            {
                Type = "PAIR_REQUEST",
                SenderId = LocalDeviceId,
                SenderName = LocalDeviceName,
                SenderPlatform = LocalPlatform,
                SenderUdpPort = InputUdpPort,
                SenderTcpPort = ControlTcpPort,
                SenderScreenWidth = LocalScreenWidth,
                SenderScreenHeight = LocalScreenHeight,
                TargetPin = cleanPin
            };

            await TcpFrameCodec.WriteFrameAsync(stream, reqHeader, null, 0, timeoutCts.Token).ConfigureAwait(false);
            var (respHeader, _) = await TcpFrameCodec.ReadHeaderAsync(stream, timeoutCts.Token).ConfigureAwait(false);

            if (respHeader != null && respHeader.Type == "PAIR_VERIFY_ACK")
            {
                peer.MyEnteredPinVerifiedByRemote = true;
                if (respHeader.IsMutualComplete == true)
                {
                    peer.RemoteEnteredMyPinVerified = true;
                }

                PeerPairingStatusChanged?.Invoke(peer);
                PeerDiscoveredOrUpdated?.Invoke(peer);

                string msg = peer.IsMutuallyPaired
                    ? $"✅ '{peer.DeviceName}' ile ÇİFT TARAFLI 6 haneli kod doğrulaması tamamlandı!"
                    : $"✅ '{peer.DeviceName}' kodu doğrulandı. Karşı cihazda da sizin kodunuzun ({PairingPin}) girilmesi bekleniyor.";
                Log($"[PIN Doğrulama] {msg}");
                return (true, peer.IsMutuallyPaired, msg);
            }
            else
            {
                string msg = $"❌ '{peer.DeviceName}' girdiğiniz 6 haneli kodu ({cleanPin}) reddetti.";
                Log($"[PIN Red] {msg}");
                return (false, false, msg);
            }
        }
        catch (Exception ex)
        {
            string msg = $"❌ '{peer.DeviceName}' ({peer.IpAddress}:{peer.TcpControlPort}) bağlantı hatası: {ex.Message}";
            Log($"[PIN Hata] {msg}");
            return (false, false, msg);
        }
    }

    /// <summary>
    /// Verifies that a simulated peer entered our local 6-digit PIN (for testing both sides of mutual pairing).
    /// </summary>
    public (bool Accepted, bool IsNowMutuallyPaired, string Message) VerifyInboundPinFromSimulatedPeer(
        PeerDeviceNode peer,
        string pinEnteredOnPeerForUs)
    {
        string cleanPin = pinEnteredOnPeerForUs.Trim().Replace("-", "").Replace(" ", "");
        if (cleanPin != PairingPin)
        {
            string err = $"❌ Karşı cihazda girilen kod ({cleanPin}) sizin yerel kodunuzla ({PairingPin}) eşleşmedi!";
            Log($"[PIN Hata] {err}");
            return (false, false, err);
        }

        peer.RemoteEnteredMyPinVerified = true;
        PeerPairingStatusChanged?.Invoke(peer);
        PeerDiscoveredOrUpdated?.Invoke(peer);

        string okMsg = peer.IsMutuallyPaired
            ? $"✅ '{peer.DeviceName}' ile ÇİFT TARAFLI 6 haneli PIN onayı tamamlandı! Cihaz 2D Ekran Kanvasına eklendi."
            : $"✅ '{peer.DeviceName}' sizin kodunuzu ({PairingPin}) doğruladı. Şimdi siz de onun kodunu ({peer.SimulatedLocalPin}) girin.";
        Log($"[PIN Doğrulama] {okMsg}");
        return (true, peer.IsMutuallyPaired, okMsg);
    }

    public async Task BroadcastDiscoveryBeaconAsync()
    {
        if (_discoveryUdp == null)
            return;

        var beacon = CreateLocalBeacon();
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(beacon);

        var targets = new HashSet<IPEndPoint>
        {
            new(IPAddress.Broadcast, DiscoveryPort)
        };

        foreach (var ip in GetLocalIPv4Addresses())
        {
            byte[] b = ip.GetAddressBytes();
            if (b.Length == 4)
            {
                b[3] = 255;
                targets.Add(new IPEndPoint(new IPAddress(b), DiscoveryPort));
            }
        }

        foreach (var ep in targets)
        {
            try
            {
                await _discoveryUdp.SendAsync(data, data.Length, ep).ConfigureAwait(false);
            }
            catch
            {
                // Ignore unreachable broadcast interfaces
            }
        }
    }

    public async Task SendDiscoveryBeaconToAsync(IPEndPoint target)
    {
        if (_discoveryUdp == null)
            return;

        try
        {
            var beacon = CreateLocalBeacon();
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(beacon);
            await _discoveryUdp.SendAsync(data, data.Length, target).ConfigureAwait(false);
        }
        catch
        {
            // Ignore
        }
    }

    // =========================================================================
    // Fast-Path UDP Input Senders (<1ms)
    // =========================================================================

    public void SendMouseMove(PeerDeviceNode peer, short deltaX, short deltaY)
    {
        if (_inputUdp == null || !peer.IsMutuallyPaired || (deltaX == 0 && deltaY == 0))
            return;

        ushort seq = ++_mouseSeq;
        byte[] packet = WirePacketCodec.EncodeMouseMove(new MouseMovePacket(seq, deltaX, deltaY));
        SendUdpFireAndForget(peer, packet);
    }

    public void SendMouseButton(PeerDeviceNode peer, MouseButtonCode button, bool isPressed)
    {
        if (_inputUdp == null || !peer.IsMutuallyPaired)
            return;

        byte[] packet = WirePacketCodec.EncodeMouseButton(new MouseButtonPacket(button, isPressed));
        SendUdpFireAndForget(peer, packet);
    }

    public void SendMouseScroll(PeerDeviceNode peer, short scrollX, short scrollY)
    {
        if (_inputUdp == null || !peer.IsMutuallyPaired)
            return;

        byte[] packet = WirePacketCodec.EncodeMouseScroll(new MouseScrollPacket(scrollX, scrollY));
        SendUdpFireAndForget(peer, packet);
    }

    public void SendKeyEvent(
        PeerDeviceNode peer,
        ushort virtualKey,
        ushort scanCode,
        bool isPressed,
        KeyModifiers modifiers,
        char unicodeChar)
    {
        if (_inputUdp == null || !peer.IsMutuallyPaired)
            return;

        byte[] packet = WirePacketCodec.EncodeKeyEvent(
            new KeyEventPacket(virtualKey, scanCode, isPressed, modifiers, unicodeChar));
        SendUdpFireAndForget(peer, packet);
    }

    public void SendEdgeHandOff(
        PeerDeviceNode peer,
        ScreenEdge targetEntranceEdge,
        float normalizedPosition,
        bool isDraggingShelfItem = false)
    {
        if (_inputUdp == null || !peer.IsMutuallyPaired)
            return;

        byte[] packet = WirePacketCodec.EncodeEdgeHandOff(
            new EdgeHandOffPacket(targetEntranceEdge, isDraggingShelfItem, normalizedPosition));
        SendUdpFireAndForget(peer, packet);
        SendUdpFireAndForget(peer, packet);
    }

    private void SendUdpFireAndForget(PeerDeviceNode peer, byte[] packet)
    {
        try
        {
            if (IPAddress.TryParse(peer.IpAddress, out var ip))
            {
                _inputUdp?.Send(packet, packet.Length, new IPEndPoint(ip, peer.UdpInputPort));
            }
        }
        catch
        {
            // Ignore transient socket error
        }
    }

    // =========================================================================
    // Universal Clipboard & Drop Shelf TCP Senders (Mutually Paired Mesh)
    // =========================================================================

    public async Task BroadcastClipboardTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        string hash = ComputeSha256Hex(Encoding.UTF8.GetBytes(text));
        if (hash == _lastClipboardHash)
            return;

        _lastClipboardHash = hash;

        var header = new TcpControlHeader
        {
            Type = "CLIPBOARD_TEXT",
            SenderId = LocalDeviceId,
            SenderName = LocalDeviceName,
            Text = text,
            ContentHash = hash
        };

        foreach (var peer in _peers.Values.Where(p => p.IsMutuallyPaired))
        {
            await SendTcpFrameToPeerAsync(peer, header).ConfigureAwait(false);
        }
    }

    public async Task BroadcastClipboardImageAsync(byte[] pngBytes)
    {
        if (pngBytes.Length == 0)
            return;

        string hash = ComputeSha256Hex(pngBytes);
        if (hash == _lastClipboardHash)
            return;

        _lastClipboardHash = hash;

        var header = new TcpControlHeader
        {
            Type = "CLIPBOARD_IMAGE",
            SenderId = LocalDeviceId,
            SenderName = LocalDeviceName,
            MimeType = "image/png",
            ContentHash = hash
        };

        foreach (var peer in _peers.Values.Where(p => p.IsMutuallyPaired))
        {
            using var ms = new MemoryStream(pngBytes, writable: false);
            await SendTcpFrameToPeerAsync(peer, header, ms, pngBytes.Length).ConfigureAwait(false);
        }
    }

    public async Task<ShelfItemEntry?> SendFileToPeerShelfAsync(PeerDeviceNode peer, string filePath)
    {
        if (!peer.IsMutuallyPaired)
        {
            Log($"[Drop Shelf Uyarı] '{peer.DeviceName}' ile henüz çift taraflı 6 haneli PIN onayı tamamlanmadı!");
            return null;
        }

        var fi = new FileInfo(filePath);
        if (!fi.Exists)
            return null;

        var header = new TcpControlHeader
        {
            Type = "SHELF_FILE",
            SenderId = LocalDeviceId,
            SenderName = LocalDeviceName,
            FileName = fi.Name,
            MimeType = "application/octet-stream"
        };

        await using var fs = fi.OpenRead();
        bool sent = await SendTcpFrameToPeerAsync(peer, header, fs, fi.Length).ConfigureAwait(false);
        if (!sent)
            return null;

        var entry = new ShelfItemEntry(
            Id: Guid.NewGuid().ToString("N")[..8],
            FileName: fi.Name,
            LocalFilePath: fi.FullName,
            FileSizeBytes: fi.Length,
            SenderName: $"{LocalDeviceName} -> {peer.DeviceName}",
            ReceivedAt: DateTimeOffset.Now,
            IsOutgoing: true);

        Log($"[Drop Shelf] '{fi.Name}' ({FormatBytes(fi.Length)}) -> {peer.DeviceName} cihazına gönderildi.");
        return entry;
    }

    private async Task<bool> SendTcpFrameToPeerAsync(
        PeerDeviceNode peer,
        TcpControlHeader header,
        Stream? binaryPayload = null,
        long binaryLength = 0)
    {
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(peer.IpAddress, peer.TcpControlPort, timeoutCts.Token).ConfigureAwait(false);
            await using var netStream = client.GetStream();
            await TcpFrameCodec.WriteFrameAsync(netStream, header, binaryPayload, binaryLength, _cts.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Log($"[TCP Uyarı] {peer.DeviceName} ({peer.IpAddress}:{peer.TcpControlPort}) veri gönderilemedi: {ex.Message}");
            return false;
        }
    }

    // =========================================================================
    // Background Loops
    // =========================================================================

    private async Task DiscoveryBroadcastLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await BroadcastDiscoveryBeaconAsync().ConfigureAwait(false);
            await Task.Delay(3000, ct).ConfigureAwait(false);
        }
    }

    private async Task DiscoveryReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _discoveryUdp != null)
        {
            try
            {
                var res = await _discoveryUdp.ReceiveAsync(ct).ConfigureAwait(false);
                var beacon = JsonSerializer.Deserialize<DiscoveryBeacon>(res.Buffer);
                if (beacon == null || string.IsNullOrEmpty(beacon.DeviceId) || beacon.DeviceId == LocalDeviceId)
                    continue;

                string senderIp = res.RemoteEndPoint.Address.ToString();
                bool isNew = !_peers.ContainsKey(beacon.DeviceId);

                var peer = _peers.AddOrUpdate(
                    beacon.DeviceId,
                    _ => new PeerDeviceNode
                    {
                        DeviceId = beacon.DeviceId,
                        DeviceName = beacon.DeviceName,
                        Platform = beacon.Platform,
                        IpAddress = senderIp,
                        UdpInputPort = beacon.UdpInputPort,
                        TcpControlPort = beacon.TcpControlPort,
                        ScreenWidth = beacon.ScreenWidth,
                        ScreenHeight = beacon.ScreenHeight,
                        LastSeen = DateTimeOffset.UtcNow
                    },
                    (_, existing) =>
                    {
                        existing.DeviceName = beacon.DeviceName;
                        existing.Platform = beacon.Platform;
                        existing.IpAddress = senderIp;
                        existing.UdpInputPort = beacon.UdpInputPort;
                        existing.TcpControlPort = beacon.TcpControlPort;
                        existing.ScreenWidth = beacon.ScreenWidth;
                        existing.ScreenHeight = beacon.ScreenHeight;
                        existing.LastSeen = DateTimeOffset.UtcNow;
                        return existing;
                    });

                if (isNew)
                {
                    Log($"[Keşif] Cihaz bulundu: {peer.DeviceName} ({peer.Platform}) @ {peer.IpAddress} — Bağlanmak için çift taraflı 6 haneli PIN girin.");
                    await SendDiscoveryBeaconToAsync(new IPEndPoint(res.RemoteEndPoint.Address, DiscoveryPort))
                        .ConfigureAwait(false);
                }

                PeerDiscoveredOrUpdated?.Invoke(peer);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Ignore malformed UDP broadcast packets
            }
        }
    }

    private async Task InputReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _inputUdp != null)
        {
            try
            {
                var res = await _inputUdp.ReceiveAsync(ct).ConfigureAwait(false);
                byte[] buf = res.Buffer;
                if (!WirePacketCodec.TryValidateHeader(buf, out var type))
                    continue;

                switch (type)
                {
                    case PacketType.MouseMove:
                        if (WirePacketCodec.TryDecodeMouseMove(buf, out var mm))
                            RemoteMouseMoveReceived?.Invoke(mm);
                        break;

                    case PacketType.MouseButton:
                        if (WirePacketCodec.TryDecodeMouseButton(buf, out var mb))
                            RemoteMouseButtonReceived?.Invoke(mb);
                        break;

                    case PacketType.MouseScroll:
                        if (WirePacketCodec.TryDecodeMouseScroll(buf, out var ms))
                            RemoteMouseScrollReceived?.Invoke(ms);
                        break;

                    case PacketType.KeyEvent:
                        if (WirePacketCodec.TryDecodeKeyEvent(buf, out var ke))
                            RemoteKeyEventReceived?.Invoke(ke);
                        break;

                    case PacketType.EdgeHandOff:
                        if (WirePacketCodec.TryDecodeEdgeHandOff(buf, out var ho))
                            RemoteEdgeHandOffReceived?.Invoke(ho, res.RemoteEndPoint);
                        break;

                    case PacketType.HeartbeatPing:
                        if (WirePacketCodec.TryDecodeHeartbeat(buf, out var ping))
                        {
                            byte[] pong = WirePacketCodec.EncodeHeartbeat(PacketType.HeartbeatPong, ping.TimestampMs);
                            await _inputUdp.SendAsync(pong, pong.Length, res.RemoteEndPoint).ConfigureAwait(false);
                        }
                        break;

                    case PacketType.HeartbeatPong:
                        if (WirePacketCodec.TryDecodeHeartbeat(buf, out var pongRecv))
                        {
                            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            double rtt = Math.Max(0.2, nowMs - pongRecv.TimestampMs);
                            string senderIp = res.RemoteEndPoint.Address.ToString();
                            foreach (var p in _peers.Values.Where(p => p.IpAddress == senderIp && p.UdpInputPort == res.RemoteEndPoint.Port))
                            {
                                p.LatencyMs = rtt;
                                p.LastSeen = DateTimeOffset.UtcNow;
                                PeerDiscoveredOrUpdated?.Invoke(p);
                            }
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
                // Continue receiving
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            byte[] ping = WirePacketCodec.EncodeHeartbeat(PacketType.HeartbeatPing, now);
            foreach (var peer in _peers.Values.Where(p => p.IsMutuallyPaired))
            {
                SendUdpFireAndForget(peer, ping);
            }
            await Task.Delay(2000, ct).ConfigureAwait(false);
        }
    }

    private async Task TcpAcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _tcpListener != null)
        {
            try
            {
                var client = await _tcpListener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleTcpClientAsync(client, ct), ct);
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

    private async Task HandleTcpClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            var (header, binLen) = await TcpFrameCodec.ReadHeaderAsync(stream, ct).ConfigureAwait(false);
            if (header == null)
                return;

            string remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "127.0.0.1";

            switch (header.Type)
            {
                case "PAIR_REQUEST":
                {
                    string peerId = !string.IsNullOrEmpty(header.SenderId)
                        ? header.SenderId
                        : $"{header.SenderPlatform ?? "peer"}-{remoteIp}:{header.SenderUdpPort ?? ProtocolConstants.FastInputUdpPort}";

                    var peer = _peers.AddOrUpdate(
                        peerId,
                        _ => new PeerDeviceNode
                        {
                            DeviceId = peerId,
                            DeviceName = string.IsNullOrEmpty(header.SenderName) ? remoteIp : header.SenderName,
                            Platform = header.SenderPlatform ?? "android",
                            IpAddress = remoteIp,
                            UdpInputPort = header.SenderUdpPort ?? ProtocolConstants.FastInputUdpPort,
                            TcpControlPort = header.SenderTcpPort ?? ProtocolConstants.DataControlTcpPort,
                            ScreenWidth = header.SenderScreenWidth ?? 1080,
                            ScreenHeight = header.SenderScreenHeight ?? 2400,
                            LastSeen = DateTimeOffset.UtcNow
                        },
                        (_, existing) =>
                        {
                            if (!string.IsNullOrEmpty(header.SenderName))
                                existing.DeviceName = header.SenderName;
                            if (!string.IsNullOrEmpty(header.SenderPlatform))
                                existing.Platform = header.SenderPlatform;
                            existing.IpAddress = remoteIp;
                            if (header.SenderUdpPort.HasValue)
                                existing.UdpInputPort = header.SenderUdpPort.Value;
                            if (header.SenderTcpPort.HasValue)
                                existing.TcpControlPort = header.SenderTcpPort.Value;
                            if (header.SenderScreenWidth.HasValue)
                                existing.ScreenWidth = header.SenderScreenWidth.Value;
                            if (header.SenderScreenHeight.HasValue)
                                existing.ScreenHeight = header.SenderScreenHeight.Value;
                            existing.LastSeen = DateTimeOffset.UtcNow;
                            return existing;
                        });

                    string submittedPin = (header.TargetPin ?? string.Empty).Trim();
                    if (submittedPin == PairingPin)
                    {
                        peer.RemoteEnteredMyPinVerified = true;
                        var ack = new TcpControlHeader
                        {
                            Type = "PAIR_VERIFY_ACK",
                            SenderId = LocalDeviceId,
                            SenderName = LocalDeviceName,
                            IsMutualComplete = peer.IsMutuallyPaired
                        };
                        await TcpFrameCodec.WriteFrameAsync(stream, ack, null, 0, ct).ConfigureAwait(false);

                        if (peer.IsMutuallyPaired)
                        {
                            Log($"[Çift Taraflı Eşleşme] ✅ '{peer.DeviceName}' ({remoteIp}) ile karşılıklı 6 haneli PIN onayı tamamlandı!");
                        }
                        else
                        {
                            Log($"[PIN İsteği] 🔔 '{peer.DeviceName}' sizin 6 haneli kodunuzu ({PairingPin}) doğru girdi! Bağlantıyı tamamlamak için onun 6 haneli kodunu girip onaylayın.");
                        }

                        PeerPairingStatusChanged?.Invoke(peer);
                        PeerDiscoveredOrUpdated?.Invoke(peer);
                    }
                    else
                    {
                        var rej = new TcpControlHeader
                        {
                            Type = "PAIR_REJECT",
                            SenderId = LocalDeviceId,
                            SenderName = LocalDeviceName
                        };
                        await TcpFrameCodec.WriteFrameAsync(stream, rej, null, 0, ct).ConfigureAwait(false);
                        Log($"[Güvenlik] ⚠️ '{peer.DeviceName}' ({remoteIp}) hatalı PIN kodu denedi ({submittedPin}).");
                    }
                    break;
                }

                case "CLIPBOARD_TEXT":
                    if (!string.IsNullOrEmpty(header.Text))
                    {
                        string hash = header.ContentHash ?? ComputeSha256Hex(Encoding.UTF8.GetBytes(header.Text));
                        if (hash != _lastClipboardHash)
                        {
                            _lastClipboardHash = hash;
                            Log($"[Evrensel Pano] {header.SenderName} cihazından metin kopyalandı ({header.Text.Length} krk).");
                            RemoteClipboardTextReceived?.Invoke(header.Text, header.SenderName);
                        }
                    }
                    break;

                case "CLIPBOARD_IMAGE":
                    if (binLen > 0 && binLen <= 50 * 1024 * 1024)
                    {
                        byte[] imgBytes = new byte[binLen];
                        int read = await TcpFrameCodec.ReadExactAsync(stream, imgBytes, ct).ConfigureAwait(false);
                        if (read == binLen)
                        {
                            string hash = header.ContentHash ?? ComputeSha256Hex(imgBytes);
                            if (hash != _lastClipboardHash)
                            {
                                _lastClipboardHash = hash;
                                Log($"[Evrensel Pano] {header.SenderName} cihazından görsel alındı ({FormatBytes(binLen)}).");
                                RemoteClipboardImageReceived?.Invoke(imgBytes, header.SenderName);
                            }
                        }
                    }
                    break;

                case "SHELF_FILE":
                    if (binLen >= 0 && !string.IsNullOrWhiteSpace(header.FileName))
                    {
                        string safeName = Path.GetFileName(header.FileName);
                        string targetPath = GetUniqueFilePath(ShelfReceiveDirectory, safeName);
                        await using (var fs = File.Create(targetPath))
                        {
                            byte[] buffer = new byte[64 * 1024];
                            long remaining = binLen;
                            while (remaining > 0)
                            {
                                int toRead = (int)Math.Min(buffer.Length, remaining);
                                int r = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
                                if (r <= 0)
                                    break;
                                await fs.WriteAsync(buffer.AsMemory(0, r), ct).ConfigureAwait(false);
                                remaining -= r;
                            }
                        }

                        var entry = new ShelfItemEntry(
                            Id: Guid.NewGuid().ToString("N")[..8],
                            FileName: safeName,
                            LocalFilePath: targetPath,
                            FileSizeBytes: binLen,
                            SenderName: header.SenderName,
                            ReceivedAt: DateTimeOffset.Now,
                            IsOutgoing: false);

                        Log($"[Drop Shelf] {header.SenderName} -> '{safeName}' ({FormatBytes(binLen)}) ortak cebe indi.");
                        ShelfItemReceived?.Invoke(entry);
                    }
                    break;
            }
        }
    }

    private DiscoveryBeacon CreateLocalBeacon() => new()
    {
        DeviceId = LocalDeviceId,
        DeviceName = LocalDeviceName,
        Platform = LocalPlatform,
        UdpInputPort = InputUdpPort,
        TcpControlPort = ControlTcpPort,
        ScreenWidth = LocalScreenWidth,
        ScreenHeight = LocalScreenHeight,
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };

    private static string GetUniqueFilePath(string dir, string fileName)
    {
        string full = Path.Combine(dir, fileName);
        if (!File.Exists(full))
            return full;

        string name = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        int counter = 1;
        while (File.Exists(full))
        {
            full = Path.Combine(dir, $"{name} ({counter++}){ext}");
        }
        return full;
    }

    private static string ComputeSha256Hex(ReadOnlySpan<byte> data)
        => Convert.ToHexString(SHA256.HashData(data));

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F2} MB"
    };

    private void Log(string msg) => LogMessage?.Invoke($"{DateTime.Now:HH:mm:ss} {msg}");

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _discoveryUdp?.Dispose();
        _inputUdp?.Dispose();
        _tcpListener?.Stop();
        _cts.Dispose();
    }
}
