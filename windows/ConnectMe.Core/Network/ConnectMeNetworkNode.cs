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
    private UdpClient? _audioUdp;
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
    public int AudioUdpPort { get; private set; } = ProtocolConstants.AudioStreamUdpPort;

    public int LocalScreenWidth { get; set; } = 1920;
    public int LocalScreenHeight { get; set; } = 1080;
    public List<PhysicalMonitorDescriptor> LocalMonitors { get; set; } = new();

    // Events
    public event Action<PeerDeviceNode>? PeerDiscoveredOrUpdated;
    public event Action<PeerDeviceNode>? PeerPairingStatusChanged;
    public event Action<EdgeHandOffPacket, IPEndPoint>? RemoteEdgeHandOffReceived;
    public event Action<MouseMovePacket>? RemoteMouseMoveReceived;
    public event Action<MouseButtonPacket>? RemoteMouseButtonReceived;
    public event Action<MouseScrollPacket>? RemoteMouseScrollReceived;
    public event Action<KeyEventPacket>? RemoteKeyEventReceived;
    public event Action<AudioChunkPacket, IPEndPoint>? RemoteAudioChunkReceived;
    public event Action<string, string>? RemoteClipboardTextReceived;
    public event Action<byte[], string>? RemoteClipboardImageReceived;
    public event Action<ShelfItemEntry>? ShelfItemReceived;
    public event Action<string>? LogMessage;

    public TrustedDeviceStore TrustStore { get; }

    public ConnectMeNetworkNode(
        string? deviceId = null,
        string? deviceName = null,
        string platform = "windows",
        string? shelfDirectory = null,
        string? fixedPin = null,
        TrustedDeviceStore? trustStore = null)
    {
        LocalDeviceId = deviceId ?? TrustedDeviceStore.GetOrCreatePersistentDeviceId();
        LocalDeviceName = deviceName ?? Environment.MachineName;
        LocalPlatform = platform;
        PairingPin = fixedPin ?? RandomNumberGenerator.GetInt32(100000, 999999).ToString();
        TrustStore = trustStore ?? new TrustedDeviceStore();

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
        _inputUdp = new UdpClient();
        _inputUdp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _inputUdp.Client.Bind(new IPEndPoint(IPAddress.Any, InputUdpPort));
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

        // 4. Bind Audio Stream UDP Socket (Port 42852)
        try
        {
            _audioUdp = new UdpClient();
            _audioUdp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _audioUdp.Client.Bind(new IPEndPoint(IPAddress.Any, AudioUdpPort));
            _audioUdp.Client.ReceiveBufferSize = 512 * 1024;
            _audioUdp.Client.SendBufferSize = 512 * 1024;
            AudioUdpPort = ((IPEndPoint)_audioUdp.Client.LocalEndPoint!).Port;
            _ = Task.Run(() => AudioReceiveLoopAsync(_cts.Token));
            Log($"[Ses & Kulaklık Köprüsü] UDP Ses Alıcısı {AudioUdpPort} portunda hazır (Merkezi Kulaklık Modu).");
        }
        catch (Exception ex)
        {
            Log($"[Ses Uyarı] Ses UDP portu {AudioUdpPort} açılamadı: {ex.Message}");
        }
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
    /// If rememberDevice is true, exchanges a persistent TrustToken so subsequent sessions
    /// reconnect automatically without requiring manual 6-digit PIN entry!
    /// Both devices must verify each other's 6-digit PIN to achieve MutuallyPaired status!
    /// </summary>
    public async Task<(bool Accepted, bool IsNowMutuallyPaired, string Message)> SubmitRemotePinForPairingAsync(
        PeerDeviceNode peer,
        string enteredRemotePin,
        bool rememberDevice = true)
    {
        string cleanPin = enteredRemotePin.Trim().Replace("-", "").Replace(" ", "");
        if (cleanPin.Length != 6 || !cleanPin.All(char.IsDigit))
        {
            return (false, false, "Lütfen karşı cihazdaki 6 haneli rakam kodunu eksiksiz girin.");
        }

        string? generatedTrustToken = rememberDevice ? TrustedDeviceStore.GenerateTrustToken() : null;

        // Handle local simulated device verification
        if (!string.IsNullOrEmpty(peer.SimulatedLocalPin))
        {
            if (cleanPin == peer.SimulatedLocalPin)
            {
                peer.MyEnteredPinVerifiedByRemote = true;
                if (rememberDevice && generatedTrustToken != null)
                {
                    peer.IsTrusted = true;
                    peer.TrustToken = generatedTrustToken;
                    TrustStore.AddOrUpdateTrustedDevice(new TrustedDeviceRecord
                    {
                        DeviceId = peer.DeviceId,
                        DeviceName = peer.DeviceName,
                        Platform = peer.Platform,
                        TrustToken = generatedTrustToken,
                        AssignedEdge = peer.AssignedEdgeOnLocal,
                        EdgeOffsetStart = peer.EdgeOffsetStart,
                        EdgeOffsetEnd = peer.EdgeOffsetEnd,
                        AttachedLocalMonitorId = peer.AttachedLocalMonitorId,
                        CanvasX = peer.CanvasX,
                        CanvasY = peer.CanvasY,
                        HasCustomCanvasPosition = peer.HasCustomCanvasPosition,
                        AutoConnect = true
                    });
                }

                PeerPairingStatusChanged?.Invoke(peer);
                PeerDiscoveredOrUpdated?.Invoke(peer);
                string trustInfo = rememberDevice ? " [⭐ Cihaza güvenildi & hatırlandı]" : "";
                string statusMsg = peer.IsMutuallyPaired
                    ? $"✅ '{peer.DeviceName}' ile ÇİFT TARAFLI eşleşme tamamlandı!{trustInfo} Ekran kanvasında konumlandırabilirsiniz."
                    : $"✅ '{peer.DeviceName}' cihazının kodu ({cleanPin}) doğrulandı!{trustInfo} Şimdi karşı cihazdan da sizin kodunuzun ({PairingPin}) onaylanması bekleniyor.";
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
            using var client = CreateSubnetBoundTcpClient(peer.IpAddress);
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
                SenderMonitors = LocalMonitors.Count > 0 ? LocalMonitors.ToList() : null,
                TargetPin = cleanPin,
                RequestTrust = rememberDevice,
                TrustToken = generatedTrustToken,
                ReturnEdge = (int)SpatialTopologyEngine.GetOppositeEdge(peer.AssignedEdgeOnLocal)
            };

            await TcpFrameCodec.WriteFrameAsync(stream, reqHeader, null, 0, timeoutCts.Token).ConfigureAwait(false);
            var (respHeader, _) = await TcpFrameCodec.ReadHeaderAsync(stream, timeoutCts.Token).ConfigureAwait(false);

            if (respHeader != null && respHeader.Type == "PAIR_VERIFY_ACK")
            {
                peer.MyEnteredPinVerifiedByRemote = true;
                if (respHeader.SenderMonitors != null && respHeader.SenderMonitors.Count > 0)
                {
                    peer.RemoteMonitors = respHeader.SenderMonitors;
                }
                if (respHeader.IsMutualComplete == true)
                {
                    peer.RemoteEnteredMyPinVerified = true;
                }

                if (rememberDevice)
                {
                    string effectiveToken = !string.IsNullOrWhiteSpace(respHeader.TrustToken)
                        ? respHeader.TrustToken
                        : (generatedTrustToken ?? string.Empty);

                    peer.PendingTrustToken = effectiveToken;

                    if (peer.IsMutuallyPaired)
                    {
                        peer.IsTrusted = true;
                        peer.TrustToken = effectiveToken;
                        TrustStore.AddOrUpdateTrustedDevice(new TrustedDeviceRecord
                        {
                            DeviceId = peer.DeviceId,
                            DeviceName = peer.DeviceName,
                            Platform = peer.Platform,
                            TrustToken = effectiveToken,
                            AssignedEdge = peer.AssignedEdgeOnLocal,
                            EdgeOffsetStart = peer.EdgeOffsetStart,
                            EdgeOffsetEnd = peer.EdgeOffsetEnd,
                            AttachedLocalMonitorId = peer.AttachedLocalMonitorId,
                            CanvasX = peer.CanvasX,
                            CanvasY = peer.CanvasY,
                            HasCustomCanvasPosition = peer.HasCustomCanvasPosition,
                            AutoConnect = true
                        });
                    }
                }

                PeerPairingStatusChanged?.Invoke(peer);
                PeerDiscoveredOrUpdated?.Invoke(peer);

                string trustBadge = rememberDevice ? " [⭐ Bu Cihaz Hatırlandı - Bir Sonraki Seferde Otomatik Bağlanacak]" : "";
                string msg = peer.IsMutuallyPaired
                    ? $"✅ '{peer.DeviceName}' ile ÇİFT TARAFLI 6 haneli kod doğrulaması tamamlandı!{trustBadge}"
                    : $"✅ '{peer.DeviceName}' kodu doğrulandı. Karşı cihazda da sizin kodunuzun ({PairingPin}) girilmesi bekleniyor.{trustBadge}";
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

    public bool RevokeTrustForPeer(PeerDeviceNode peer)
    {
        bool revoked = TrustStore.RevokeTrust(peer.DeviceId);
        peer.IsTrusted = false;
        peer.TrustToken = string.Empty;
        Log($"[Güvenlik] 🗑️ '{peer.DeviceName}' için cihaz güveni kaldırıldı. Artık tekrar 6 haneli kod gerekecektir.");
        PeerDiscoveredOrUpdated?.Invoke(peer);
        PeerPairingStatusChanged?.Invoke(peer);
        return revoked;
    }

    public void SavePeerTopologyToTrustedStore(PeerDeviceNode peer)
    {
        if (!peer.IsTrusted)
            return;

        if (TrustStore.IsDeviceTrusted(peer.DeviceId, out var rec) && rec != null)
        {
            rec.AssignedEdge = peer.AssignedEdgeOnLocal;
            rec.EdgeOffsetStart = peer.EdgeOffsetStart;
            rec.EdgeOffsetEnd = peer.EdgeOffsetEnd;
            rec.AttachedLocalMonitorId = peer.AttachedLocalMonitorId;
            rec.CanvasX = peer.CanvasX;
            rec.CanvasY = peer.CanvasY;
            rec.HasCustomCanvasPosition = peer.HasCustomCanvasPosition;
            TrustStore.AddOrUpdateTrustedDevice(rec);
        }
    }

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAutoConnectAttempt = new();

    public async Task<bool> TryAutoReconnectTrustedPeerAsync(PeerDeviceNode peer, string trustToken)
    {
        if (peer.IsMutuallyPaired)
            return true;

        if (_lastAutoConnectAttempt.TryGetValue(peer.DeviceId, out var lastAttempt) &&
            DateTimeOffset.UtcNow - lastAttempt < TimeSpan.FromSeconds(5))
        {
            return false;
        }
        _lastAutoConnectAttempt[peer.DeviceId] = DateTimeOffset.UtcNow;

        try
        {
            using var client = CreateSubnetBoundTcpClient(peer.IpAddress);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await client.ConnectAsync(peer.IpAddress, peer.TcpControlPort, timeoutCts.Token).ConfigureAwait(false);
            await using var stream = client.GetStream();

            var req = new TcpControlHeader
            {
                Type = "TRUSTED_RECONNECT",
                SenderId = LocalDeviceId,
                SenderName = LocalDeviceName,
                SenderPlatform = LocalPlatform,
                SenderUdpPort = InputUdpPort,
                SenderTcpPort = ControlTcpPort,
                SenderScreenWidth = LocalScreenWidth,
                SenderScreenHeight = LocalScreenHeight,
                SenderMonitors = LocalMonitors.Count > 0 ? LocalMonitors.ToList() : null,
                TrustToken = trustToken,
                ReturnEdge = (int)SpatialTopologyEngine.GetOppositeEdge(peer.AssignedEdgeOnLocal)
            };

            await TcpFrameCodec.WriteFrameAsync(stream, req, null, 0, timeoutCts.Token).ConfigureAwait(false);
            var (resp, _) = await TcpFrameCodec.ReadHeaderAsync(stream, timeoutCts.Token).ConfigureAwait(false);

            if (resp != null && resp.Type == "TRUSTED_RECONNECT_ACK" && resp.IsMutualComplete == true)
            {
                peer.MyEnteredPinVerifiedByRemote = true;
                peer.RemoteEnteredMyPinVerified = true;
                peer.IsTrusted = true;
                peer.TrustToken = trustToken;
                if (resp.SenderMonitors is { Count: > 0 })
                {
                    peer.RemoteMonitors = resp.SenderMonitors;
                }

                if (TrustStore.IsDeviceTrusted(peer.DeviceId, out var rec) && rec != null)
                {
                    rec.LastConnectedAt = DateTimeOffset.UtcNow;
                    TrustStore.AddOrUpdateTrustedDevice(rec);
                }

                Log($"[Otomatik Bağlantı] ⭐ '{peer.DeviceName}' ile güvenli otomatik bağlantı sağlandı (PIN gerekmedi).");
                PeerPairingStatusChanged?.Invoke(peer);
                PeerDiscoveredOrUpdated?.Invoke(peer);
                return true;
            }
        }
        catch
        {
            // Peer may not be reachable yet
        }

        return false;
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
        if (peer.IsMutuallyPaired && !string.IsNullOrWhiteSpace(peer.PendingTrustToken))
        {
            peer.IsTrusted = true;
            peer.TrustToken = peer.PendingTrustToken;
            TrustStore.AddOrUpdateTrustedDevice(new TrustedDeviceRecord
            {
                DeviceId = peer.DeviceId,
                DeviceName = peer.DeviceName,
                Platform = peer.Platform,
                TrustToken = peer.PendingTrustToken,
                AssignedEdge = peer.AssignedEdgeOnLocal,
                EdgeOffsetStart = peer.EdgeOffsetStart,
                EdgeOffsetEnd = peer.EdgeOffsetEnd,
                AttachedLocalMonitorId = peer.AttachedLocalMonitorId,
                CanvasX = peer.CanvasX,
                CanvasY = peer.CanvasY,
                HasCustomCanvasPosition = peer.HasCustomCanvasPosition,
                AutoConnect = true
            });
        }
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

    private readonly ConcurrentDictionary<IPAddress, Socket> _subnetBoundUdpSockets = new();

    private Socket GetOrCreateSubnetBoundUdpSocket(IPAddress targetIp)
    {
        var localIp = FindBestLocalIpForTarget(targetIp);
        if (localIp == null)
        {
            return _inputUdp?.Client ?? throw new InvalidOperationException("Input UDP socket is not initialized.");
        }

        return _subnetBoundUdpSockets.GetOrAdd(localIp, ip =>
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            s.SendBufferSize = 256 * 1024;
            try
            {
                s.Bind(new IPEndPoint(ip, InputUdpPort));
            }
            catch
            {
                s.Bind(new IPEndPoint(ip, 0));
            }
            return s;
        });
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
        Log($"[Kenar Geçişi UDP] '{peer.DeviceName}' ({peer.IpAddress}:{peer.UdpInputPort}) ekranına giriş paketi iletildi ({targetEntranceEdge}, %{(int)(normalizedPosition * 100)}).");
    }

    private void SendUdpFireAndForget(PeerDeviceNode peer, byte[] packet)
    {
        try
        {
            if (IPAddress.TryParse(peer.IpAddress, out var ip))
            {
                var socket = GetOrCreateSubnetBoundUdpSocket(ip);
                socket.SendTo(packet, new IPEndPoint(ip, peer.UdpInputPort));
            }
        }
        catch (Exception ex)
        {
            Log($"[UDP Uyarı] '{peer.DeviceName}' ({peer.IpAddress}:{peer.UdpInputPort}) paket iletilemedi: {ex.Message}");
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

    /// <summary>
    /// Notifies remote peer (Android / Linux) about its updated layout edge relative to local machine.
    /// </summary>
    public void SendEdgeConfigToPeer(PeerDeviceNode peer)
    {
        if (!peer.IsMutuallyPaired)
            return;

        var oppositeEdge = SpatialTopologyEngine.GetOppositeEdge(peer.AssignedEdgeOnLocal);
        var header = new TcpControlHeader
        {
            Type = "EDGE_CONFIG",
            SenderId = LocalDeviceId,
            SenderName = LocalDeviceName,
            SenderPlatform = LocalPlatform,
            ReturnEdge = (int)oppositeEdge
        };

        _ = Task.Run(async () =>
        {
            await SendTcpFrameToPeerAsync(peer, header).ConfigureAwait(false);
        });
    }

    private async Task<bool> SendTcpFrameToPeerAsync(
        PeerDeviceNode peer,
        TcpControlHeader header,
        Stream? binaryPayload = null,
        long binaryLength = 0)
    {
        try
        {
            using var client = CreateSubnetBoundTcpClient(peer.IpAddress);
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
                        RemoteMonitors = beacon.Monitors ?? new List<PhysicalMonitorDescriptor>(),
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
                        if (beacon.Monitors is { Count: > 0 })
                        {
                            existing.RemoteMonitors = beacon.Monitors;
                        }
                        existing.LastSeen = DateTimeOffset.UtcNow;
                        return existing;
                    });

                // If a temporary manual placeholder existed for this IP under a synthetic ID, transfer state & clean up placeholder
                var placeholder = _peers.Values.FirstOrDefault(p => p.IpAddress == senderIp && p.DeviceId != beacon.DeviceId);
                if (placeholder != null)
                {
                    if (placeholder.MyEnteredPinVerifiedByRemote) peer.MyEnteredPinVerifiedByRemote = true;
                    if (placeholder.RemoteEnteredMyPinVerified) peer.RemoteEnteredMyPinVerified = true;
                    if (placeholder.IsTrusted) { peer.IsTrusted = true; peer.TrustToken = placeholder.TrustToken; }
                    if (placeholder.HasCustomCanvasPosition)
                    {
                        peer.HasCustomCanvasPosition = true;
                        peer.CanvasX = placeholder.CanvasX;
                        peer.CanvasY = placeholder.CanvasY;
                    }
                    peer.AssignedEdgeOnLocal = placeholder.AssignedEdgeOnLocal;
                    peer.EdgeOffsetStart = placeholder.EdgeOffsetStart;
                    peer.EdgeOffsetEnd = placeholder.EdgeOffsetEnd;
                    if (!string.IsNullOrEmpty(placeholder.AttachedLocalMonitorId))
                        peer.AttachedLocalMonitorId = placeholder.AttachedLocalMonitorId;

                    _peers.TryRemove(placeholder.DeviceId, out _);
                }

                if (TrustStore.IsDeviceTrusted(peer.DeviceId, out var trustedRec) && trustedRec != null)
                {
                    peer.IsTrusted = true;
                    peer.TrustToken = trustedRec.TrustToken;
                    if (!peer.IsMutuallyPaired)
                    {
                        peer.AssignedEdgeOnLocal = trustedRec.AssignedEdge;
                        peer.EdgeOffsetStart = trustedRec.EdgeOffsetStart;
                        peer.EdgeOffsetEnd = trustedRec.EdgeOffsetEnd;
                        peer.AttachedLocalMonitorId = trustedRec.AttachedLocalMonitorId;
                        peer.CanvasX = trustedRec.CanvasX;
                        peer.CanvasY = trustedRec.CanvasY;
                        peer.HasCustomCanvasPosition = trustedRec.HasCustomCanvasPosition;
                        _ = TryAutoReconnectTrustedPeerAsync(peer, trustedRec.TrustToken);
                    }
                }

                if (isNew)
                {
                    string trustInfo = peer.IsTrusted ? " [⭐ GÜVENİLİR - Otomatik Bağlanıyor...]" : " — Bağlanmak için çift taraflı 6 haneli PIN girin.";
                    Log($"[Keşif] Cihaz bulundu: {peer.DeviceName} ({peer.Platform}) @ {peer.IpAddress}{trustInfo}");
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

                    case PacketType.AudioChunk:
                        if (WirePacketCodec.TryDecodeAudioChunk(buf, out var ac))
                            RemoteAudioChunkReceived?.Invoke(ac, res.RemoteEndPoint);
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

    private async Task AudioReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _audioUdp != null)
        {
            try
            {
                var res = await _audioUdp.ReceiveAsync(ct).ConfigureAwait(false);
                byte[] buf = res.Buffer;
                if (WirePacketCodec.TryDecodeAudioChunk(buf, out var ac))
                {
                    RemoteAudioChunkReceived?.Invoke(ac, res.RemoteEndPoint);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch
            {
                // Audio packet drop resilience
            }
        }
    }

    public void SendAudioChunk(PeerDeviceNode peer, in AudioChunkPacket packet)
    {
        if (string.IsNullOrEmpty(peer.IpAddress)) return;
        byte[] payload = WirePacketCodec.EncodeAudioChunk(packet);
        try
        {
            if (_audioUdp != null)
            {
                _audioUdp.Send(payload, payload.Length, peer.IpAddress, ProtocolConstants.AudioStreamUdpPort);
            }
            else if (_inputUdp != null)
            {
                _inputUdp.Send(payload, payload.Length, peer.IpAddress, ProtocolConstants.AudioStreamUdpPort);
            }
        }
        catch
        {
            // Drop on network error
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
                            RemoteMonitors = header.SenderMonitors ?? new List<PhysicalMonitorDescriptor>(),
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
                            if (header.SenderMonitors is { Count: > 0 })
                                existing.RemoteMonitors = header.SenderMonitors;
                            existing.LastSeen = DateTimeOffset.UtcNow;
                            return existing;
                        });

                    string submittedPin = (header.TargetPin ?? string.Empty).Trim();
                    if (submittedPin == PairingPin)
                    {
                        peer.RemoteEnteredMyPinVerified = true;
                        bool requestedTrust = header.RequestTrust == true && !string.IsNullOrWhiteSpace(header.TrustToken);
                        if (requestedTrust)
                        {
                            peer.PendingTrustToken = header.TrustToken!;
                        }

                        if (peer.IsMutuallyPaired)
                        {
                            string effectiveToken = !string.IsNullOrWhiteSpace(peer.PendingTrustToken)
                                ? peer.PendingTrustToken
                                : (header.TrustToken ?? peer.TrustToken);

                            if (!string.IsNullOrWhiteSpace(effectiveToken))
                            {
                                peer.IsTrusted = true;
                                peer.TrustToken = effectiveToken;
                                var record = new TrustedDeviceRecord
                                {
                                    DeviceId = peer.DeviceId,
                                    DeviceName = peer.DeviceName,
                                    Platform = peer.Platform,
                                    TrustToken = effectiveToken,
                                    AssignedEdge = peer.AssignedEdgeOnLocal,
                                    EdgeOffsetStart = peer.EdgeOffsetStart,
                                    EdgeOffsetEnd = peer.EdgeOffsetEnd,
                                    AttachedLocalMonitorId = peer.AttachedLocalMonitorId,
                                    CanvasX = peer.CanvasX,
                                    CanvasY = peer.CanvasY,
                                    HasCustomCanvasPosition = peer.HasCustomCanvasPosition,
                                    AutoConnect = true
                                };
                                TrustStore.AddOrUpdateTrustedDevice(record);
                            }
                        }

                        var ack = new TcpControlHeader
                        {
                            Type = "PAIR_VERIFY_ACK",
                            SenderId = LocalDeviceId,
                            SenderName = LocalDeviceName,
                            SenderMonitors = LocalMonitors.Count > 0 ? LocalMonitors.ToList() : null,
                            RequestTrust = requestedTrust,
                            TrustToken = requestedTrust ? header.TrustToken : null,
                            IsMutualComplete = peer.IsMutuallyPaired
                        };
                        await TcpFrameCodec.WriteFrameAsync(stream, ack, null, 0, ct).ConfigureAwait(false);

                        string trustNote = requestedTrust ? " (⭐ Cihaz güvenildi & hatırlandı)" : "";
                        if (peer.IsMutuallyPaired)
                        {
                            Log($"[Çift Taraflı Eşleşme] ✅ '{peer.DeviceName}' ({remoteIp}) ile karşılıklı 6 haneli PIN onayı tamamlandı!{trustNote}");
                        }
                        else
                        {
                            Log($"[PIN İsteği] 🔔 '{peer.DeviceName}' sizin 6 haneli kodunuzu ({PairingPin}) doğru girdi!{trustNote} Bağlantıyı tamamlamak için onun 6 haneli kodunu girip onaylayın.");
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
                        Log($"[Güvenlik] ⚠️ '{peer.DeviceName}' ({remoteIp}) hatalı PIN kodu denedi (Girilen: '{submittedPin}', Beklenen: '{PairingPin}').");
                    }
                    break;
                }

                case "TRUSTED_RECONNECT":
                {
                    string peerId = !string.IsNullOrEmpty(header.SenderId)
                        ? header.SenderId
                        : $"{header.SenderPlatform ?? "peer"}-{remoteIp}:{header.SenderUdpPort ?? ProtocolConstants.FastInputUdpPort}";

                    bool isTokenValid = TrustStore.VerifyTrustToken(peerId, header.TrustToken);
                    if (isTokenValid && TrustStore.IsDeviceTrusted(peerId, out var rec) && rec != null)
                    {
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
                                RemoteMonitors = header.SenderMonitors ?? new List<PhysicalMonitorDescriptor>(),
                                IsTrusted = true,
                                TrustToken = header.TrustToken ?? string.Empty,
                                MyEnteredPinVerifiedByRemote = true,
                                RemoteEnteredMyPinVerified = true,
                                AssignedEdgeOnLocal = rec.AssignedEdge,
                                EdgeOffsetStart = rec.EdgeOffsetStart,
                                EdgeOffsetEnd = rec.EdgeOffsetEnd,
                                AttachedLocalMonitorId = rec.AttachedLocalMonitorId,
                                CanvasX = rec.CanvasX,
                                CanvasY = rec.CanvasY,
                                HasCustomCanvasPosition = rec.HasCustomCanvasPosition,
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
                                if (header.SenderMonitors is { Count: > 0 })
                                    existing.RemoteMonitors = header.SenderMonitors;
                                existing.IsTrusted = true;
                                existing.TrustToken = header.TrustToken ?? string.Empty;
                                existing.MyEnteredPinVerifiedByRemote = true;
                                existing.RemoteEnteredMyPinVerified = true;
                                existing.AssignedEdgeOnLocal = rec.AssignedEdge;
                                existing.EdgeOffsetStart = rec.EdgeOffsetStart;
                                existing.EdgeOffsetEnd = rec.EdgeOffsetEnd;
                                existing.AttachedLocalMonitorId = rec.AttachedLocalMonitorId;
                                existing.CanvasX = rec.CanvasX;
                                existing.CanvasY = rec.CanvasY;
                                existing.HasCustomCanvasPosition = rec.HasCustomCanvasPosition;
                                existing.LastSeen = DateTimeOffset.UtcNow;
                                return existing;
                            });

                        rec.LastConnectedAt = DateTimeOffset.UtcNow;
                        TrustStore.AddOrUpdateTrustedDevice(rec);

                        var ack = new TcpControlHeader
                        {
                            Type = "TRUSTED_RECONNECT_ACK",
                            SenderId = LocalDeviceId,
                            SenderName = LocalDeviceName,
                            SenderMonitors = LocalMonitors.Count > 0 ? LocalMonitors.ToList() : null,
                            TrustToken = header.TrustToken,
                            IsMutualComplete = true
                        };
                        await TcpFrameCodec.WriteFrameAsync(stream, ack, null, 0, ct).ConfigureAwait(false);
                        Log($"[Otomatik Bağlantı] ⭐ Güvenilir cihaz '{peer.DeviceName}' ({remoteIp}) PIN'siz otomatik bağlandı!");
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
                        Log($"[Güvenlik] ⚠️ '{header.SenderName}' ({remoteIp}) geçersiz güven belirteci ile otomatik bağlanmaya çalıştı.");
                    }
                    break;
                }

                case "TOPOLOGY_SYNC":
                {
                    if (header.SenderMonitors is { Count: > 0 })
                    {
                        var peer = _peers.Values.FirstOrDefault(p => p.DeviceId == header.SenderId || p.IpAddress == remoteIp);
                        if (peer != null)
                        {
                            peer.RemoteMonitors = header.SenderMonitors;
                            peer.LastSeen = DateTimeOffset.UtcNow;
                            Log($"[Çoklu Monitör Senk] '{peer.DeviceName}' {peer.RemoteMonitors.Count} fiziksel ekran düzenini bildirdi.");
                            PeerDiscoveredOrUpdated?.Invoke(peer);
                        }
                    }
                    break;
                }

                case "EDGE_RETURN":
                {
                    var returnEdge = (ScreenEdge)(header.ReturnEdge ?? 0);
                    float returnPos = (float)(header.NormalizedPosition ?? 0.5);
                    Log($"[Kenar Dönüşü] '{header.SenderName}' ({remoteIp}) TCP sinyaliyle yerel masaüstüne dönüş yaptı ({returnEdge}, %{(int)(returnPos * 100)}).");
                    RemoteEdgeHandOffReceived?.Invoke(
                        new EdgeHandOffPacket(returnEdge, false, returnPos),
                        new IPEndPoint(IPAddress.Parse(remoteIp), header.SenderUdpPort ?? InputUdpPort));
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

    public async Task BroadcastTopologySyncAsync()
    {
        if (LocalMonitors.Count == 0)
            return;

        var header = new TcpControlHeader
        {
            Type = "TOPOLOGY_SYNC",
            SenderId = LocalDeviceId,
            SenderName = LocalDeviceName,
            SenderPlatform = LocalPlatform,
            SenderScreenWidth = LocalScreenWidth,
            SenderScreenHeight = LocalScreenHeight,
            SenderMonitors = LocalMonitors.ToList()
        };

        foreach (var peer in _peers.Values.Where(p => p.IsMutuallyPaired))
        {
            await SendTcpFrameToPeerAsync(peer, header).ConfigureAwait(false);
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
        Monitors = LocalMonitors.Count > 0 ? LocalMonitors.ToList() : null,
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

    public static IPAddress? FindBestLocalIpForTarget(IPAddress targetIp)
    {
        if (targetIp.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(targetIp))
            return null;

        byte[] targetBytes = targetIp.GetAddressBytes();

        try
        {
            foreach (var netInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (netInterface.OperationalStatus != OperationalStatus.Up ||
                    netInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var ipProps = netInterface.GetIPProperties();
                foreach (var unicast in ipProps.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    byte[] localBytes = unicast.Address.GetAddressBytes();
                    byte[] maskBytes = unicast.IPv4Mask?.GetAddressBytes() ?? new byte[] { 255, 255, 255, 0 };

                    bool inSameSubnet = true;
                    for (int i = 0; i < 4; i++)
                    {
                        if ((localBytes[i] & maskBytes[i]) != (targetBytes[i] & maskBytes[i]))
                        {
                            inSameSubnet = false;
                            break;
                        }
                    }

                    if (inSameSubnet)
                    {
                        return unicast.Address;
                    }
                }
            }
        }
        catch
        {
            // Ignore interface query failure
        }

        return null;
    }

    private static TcpClient CreateSubnetBoundTcpClient(string remoteIp)
    {
        var client = new TcpClient();
        try
        {
            if (IPAddress.TryParse(remoteIp, out var targetIp))
            {
                var localIp = FindBestLocalIpForTarget(targetIp);
                if (localIp != null)
                {
                    client.Client.Bind(new IPEndPoint(localIp, 0));
                }
            }
        }
        catch
        {
            // If explicit bind fails, client falls back to OS default
        }
        return client;
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F2} MB"
    };

    public void Log(string msg) => LogMessage?.Invoke($"{DateTime.Now:HH:mm:ss} {msg}");

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _discoveryUdp?.Dispose();
        _inputUdp?.Dispose();
        _audioUdp?.Dispose();
        foreach (var s in _subnetBoundUdpSockets.Values)
        {
            try { s.Dispose(); } catch { }
        }
        _subnetBoundUdpSockets.Clear();
        _tcpListener?.Stop();
        _cts.Dispose();
    }
}
