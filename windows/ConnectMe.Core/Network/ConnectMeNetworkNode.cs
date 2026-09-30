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
/// Compact 16-byte BLE Manufacturer Specific Data payload so Windows and Android
/// can discover each other's Wi-Fi IP & Port over Bluetooth Low Energy (BLE)
/// even when router AP isolation or broadcast filtering is active.
/// </summary>
public static class BleProximityCodec
{
    // Format: [0x43 'C'][0x4D 'M'][Ver=1][Platform: 1=Win, 2=Android, 3=Linux][IPv4 4B][UdpPort 2B LE][DeviceIdHash 6B]
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
/// Unified Peer-to-Peer Network Engine managing:
/// - UDP Discovery Broadcast/Unicast (42849)
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
    public string PairingPin { get; }
    public string ShelfReceiveDirectory { get; }

    public int DiscoveryPort { get; private set; } = ProtocolConstants.DiscoveryUdpPort;
    public int InputUdpPort { get; private set; } = ProtocolConstants.FastInputUdpPort;
    public int ControlTcpPort { get; private set; } = ProtocolConstants.DataControlTcpPort;

    public int LocalScreenWidth { get; set; } = 1920;
    public int LocalScreenHeight { get; set; } = 1080;

    // Events
    public event Action<PeerDeviceNode>? PeerDiscoveredOrUpdated;
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
        string? shelfDirectory = null)
    {
        LocalDeviceId = deviceId ?? Guid.NewGuid().ToString("N")[..12];
        LocalDeviceName = deviceName ?? Environment.MachineName;
        LocalPlatform = platform;
        PairingPin = RandomNumberGenerator.GetInt32(100000, 999999).ToString();

        ShelfReceiveDirectory = shelfDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "ConnectMe-Shelf");
        Directory.CreateDirectory(ShelfReceiveDirectory);
    }

    public IReadOnlyCollection<PeerDeviceNode> DiscoveredPeers => _peers.Values.ToList();

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
            Log($"[Keşif] UDP Discovery {DiscoveryPort} portunda başlatıldı.");
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
        Log($"[Veri Kanalı] Pano & Drop Shelf TCP Sunucusu {ControlTcpPort} portunda aktif.");
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
        bool viaBle = false)
    {
        string id = $"{platform}-{ipAddress}:{udpPort}";
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
                DiscoveredViaBle = viaBle
            },
            (_, existing) =>
            {
                existing.IpAddress = ipAddress;
                existing.DeviceName = deviceName;
                existing.UdpInputPort = udpPort;
                existing.TcpControlPort = tcpPort;
                existing.LastSeen = DateTimeOffset.UtcNow;
                existing.DiscoveredViaBle |= viaBle;
                return existing;
            });

        // Also send a direct unicast discovery beacon to that IP immediately
        _ = SendDiscoveryBeaconToAsync(new IPEndPoint(IPAddress.Parse(ipAddress), DiscoveryPort));
        PeerDiscoveredOrUpdated?.Invoke(peer);
        return peer;
    }

    public async Task BroadcastDiscoveryBeaconAsync()
    {
        if (_discoveryUdp == null)
            return;

        var beacon = CreateLocalBeacon();
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(beacon);

        // Broadcast to 255.255.255.255 and subnet broadcast addresses
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
        if (_inputUdp == null || (deltaX == 0 && deltaY == 0))
            return;

        ushort seq = ++_mouseSeq;
        byte[] packet = WirePacketCodec.EncodeMouseMove(new MouseMovePacket(seq, deltaX, deltaY));
        SendUdpFireAndForget(peer, packet);
    }

    public void SendMouseButton(PeerDeviceNode peer, MouseButtonCode button, bool isPressed)
    {
        if (_inputUdp == null)
            return;

        byte[] packet = WirePacketCodec.EncodeMouseButton(new MouseButtonPacket(button, isPressed));
        SendUdpFireAndForget(peer, packet);
    }

    public void SendMouseScroll(PeerDeviceNode peer, short scrollX, short scrollY)
    {
        if (_inputUdp == null)
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
        if (_inputUdp == null)
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
        if (_inputUdp == null)
            return;

        byte[] packet = WirePacketCodec.EncodeEdgeHandOff(
            new EdgeHandOffPacket(targetEntranceEdge, isDraggingShelfItem, normalizedPosition));
        // Send twice for UDP reliability on critical state transition
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
    // Universal Clipboard & Drop Shelf TCP Senders
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

        foreach (var peer in _peers.Values)
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

        foreach (var peer in _peers.Values)
        {
            using var ms = new MemoryStream(pngBytes, writable: false);
            await SendTcpFrameToPeerAsync(peer, header, ms, pngBytes.Length).ConfigureAwait(false);
        }
    }

    public async Task<ShelfItemEntry?> SendFileToPeerShelfAsync(PeerDeviceNode peer, string filePath)
    {
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
                    Log($"[Keşif] Yeni cihaz bulundu: {peer.DeviceName} ({peer.Platform}) @ {peer.IpAddress}");
                    // Immediately reply with our unicast beacon so the peer discovers us right away
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
                            foreach (var p in _peers.Values.Where(p => p.IpAddress == senderIp))
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
            foreach (var peer in _peers.Values)
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

            switch (header.Type)
            {
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
