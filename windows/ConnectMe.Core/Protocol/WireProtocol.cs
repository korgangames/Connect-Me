using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectMe.Core.Protocol;

/// <summary>
/// Connect Me Wire Protocol v1.2 constants and port allocations.
/// Shared across Windows, Android, and Nobara Linux (KDE Plasma).
/// </summary>
public static class ProtocolConstants
{
    public const byte MagicByte0 = 0x43; // 'C'
    public const byte MagicByte1 = 0x4D; // 'M'
    public const byte ProtocolVersion = 0x01;

    public const int DiscoveryUdpPort = 42849;
    public const int FastInputUdpPort = 42850;
    public const int DataControlTcpPort = 42851;

    public const ushort BleManufacturerId = 0xFFFF;
}

public enum PacketType : byte
{
    MouseMove = 0x01,
    MouseButton = 0x02,
    MouseScroll = 0x03,
    KeyEvent = 0x04,
    EdgeHandOff = 0x05,
    HeartbeatPing = 0x06,
    HeartbeatPong = 0x07
}

public enum ScreenEdge : byte
{
    None = 0,
    Left = 1,
    Right = 2,
    Top = 3,
    Bottom = 4
}

public enum MouseButtonCode : byte
{
    Left = 1,
    Right = 2,
    Middle = 3,
    XButton1 = 4,
    XButton2 = 5
}

[Flags]
public enum KeyModifiers : byte
{
    None = 0,
    Shift = 1 << 0,
    Ctrl = 1 << 1,
    Alt = 1 << 2,
    Meta = 1 << 3
}

public readonly record struct MouseMovePacket(ushort Sequence, short DeltaX, short DeltaY);
public readonly record struct MouseButtonPacket(MouseButtonCode Button, bool IsPressed);
public readonly record struct MouseScrollPacket(short ScrollX, short ScrollY);
public readonly record struct KeyEventPacket(
    ushort VirtualKey,
    ushort ScanCode,
    bool IsPressed,
    KeyModifiers Modifiers,
    char UnicodeChar);
public readonly record struct EdgeHandOffPacket(
    ScreenEdge TargetEntranceEdge,
    bool IsDraggingShelfItem,
    float NormalizedPosition);
public readonly record struct HeartbeatPacket(PacketType Type, long TimestampMs);

/// <summary>
/// High-speed binary serializer/deserializer for ConnectMe UDP Fast-Path packets.
/// All multi-byte fields use Little-Endian encoding.
/// </summary>
public static class WirePacketCodec
{
    private static void WriteHeader(Span<byte> buffer, PacketType type)
    {
        buffer[0] = ProtocolConstants.MagicByte0;
        buffer[1] = ProtocolConstants.MagicByte1;
        buffer[2] = ProtocolConstants.ProtocolVersion;
        buffer[3] = (byte)type;
    }

    public static bool TryValidateHeader(ReadOnlySpan<byte> buffer, out PacketType type)
    {
        type = default;
        if (buffer.Length < 4)
            return false;

        if (buffer[0] != ProtocolConstants.MagicByte0 ||
            buffer[1] != ProtocolConstants.MagicByte1 ||
            buffer[2] != ProtocolConstants.ProtocolVersion)
        {
            return false;
        }

        type = (PacketType)buffer[3];
        return true;
    }

    public static byte[] EncodeMouseMove(in MouseMovePacket packet)
    {
        byte[] buf = new byte[10];
        WriteHeader(buf, PacketType.MouseMove);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4, 2), packet.Sequence);
        BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(6, 2), packet.DeltaX);
        BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(8, 2), packet.DeltaY);
        return buf;
    }

    public static bool TryDecodeMouseMove(ReadOnlySpan<byte> buf, out MouseMovePacket packet)
    {
        packet = default;
        if (buf.Length < 10 || !TryValidateHeader(buf, out var type) || type != PacketType.MouseMove)
            return false;

        ushort seq = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(4, 2));
        short dx = BinaryPrimitives.ReadInt16LittleEndian(buf.Slice(6, 2));
        short dy = BinaryPrimitives.ReadInt16LittleEndian(buf.Slice(8, 2));
        packet = new MouseMovePacket(seq, dx, dy);
        return true;
    }

    public static byte[] EncodeMouseButton(in MouseButtonPacket packet)
    {
        byte[] buf = new byte[6];
        WriteHeader(buf, PacketType.MouseButton);
        buf[4] = (byte)packet.Button;
        buf[5] = packet.IsPressed ? (byte)1 : (byte)0;
        return buf;
    }

    public static bool TryDecodeMouseButton(ReadOnlySpan<byte> buf, out MouseButtonPacket packet)
    {
        packet = default;
        if (buf.Length < 6 || !TryValidateHeader(buf, out var type) || type != PacketType.MouseButton)
            return false;

        packet = new MouseButtonPacket((MouseButtonCode)buf[4], buf[5] != 0);
        return true;
    }

    public static byte[] EncodeMouseScroll(in MouseScrollPacket packet)
    {
        byte[] buf = new byte[8];
        WriteHeader(buf, PacketType.MouseScroll);
        BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(4, 2), packet.ScrollX);
        BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(6, 2), packet.ScrollY);
        return buf;
    }

    public static bool TryDecodeMouseScroll(ReadOnlySpan<byte> buf, out MouseScrollPacket packet)
    {
        packet = default;
        if (buf.Length < 8 || !TryValidateHeader(buf, out var type) || type != PacketType.MouseScroll)
            return false;

        short sx = BinaryPrimitives.ReadInt16LittleEndian(buf.Slice(4, 2));
        short sy = BinaryPrimitives.ReadInt16LittleEndian(buf.Slice(6, 2));
        packet = new MouseScrollPacket(sx, sy);
        return true;
    }

    public static byte[] EncodeKeyEvent(in KeyEventPacket packet)
    {
        byte[] buf = new byte[12];
        WriteHeader(buf, PacketType.KeyEvent);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4, 2), packet.VirtualKey);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(6, 2), packet.ScanCode);
        buf[8] = packet.IsPressed ? (byte)1 : (byte)0;
        buf[9] = (byte)packet.Modifiers;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(10, 2), packet.UnicodeChar);
        return buf;
    }

    public static bool TryDecodeKeyEvent(ReadOnlySpan<byte> buf, out KeyEventPacket packet)
    {
        packet = default;
        if (buf.Length < 12 || !TryValidateHeader(buf, out var type) || type != PacketType.KeyEvent)
            return false;

        ushort vk = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(4, 2));
        ushort sc = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(6, 2));
        bool pressed = buf[8] != 0;
        var mods = (KeyModifiers)buf[9];
        char ch = (char)BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(10, 2));
        packet = new KeyEventPacket(vk, sc, pressed, mods, ch);
        return true;
    }

    public static byte[] EncodeEdgeHandOff(in EdgeHandOffPacket packet)
    {
        byte[] buf = new byte[10];
        WriteHeader(buf, PacketType.EdgeHandOff);
        buf[4] = (byte)packet.TargetEntranceEdge;
        buf[5] = packet.IsDraggingShelfItem ? (byte)1 : (byte)0;
        float clamped = Math.Clamp(packet.NormalizedPosition, 0f, 1f);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(6, 4), clamped);
        return buf;
    }

    public static bool TryDecodeEdgeHandOff(ReadOnlySpan<byte> buf, out EdgeHandOffPacket packet)
    {
        packet = default;
        if (buf.Length < 10 || !TryValidateHeader(buf, out var type) || type != PacketType.EdgeHandOff)
            return false;

        var edge = (ScreenEdge)buf[4];
        bool dragging = buf[5] != 0;
        float pos = BinaryPrimitives.ReadSingleLittleEndian(buf.Slice(6, 4));
        packet = new EdgeHandOffPacket(edge, dragging, Math.Clamp(pos, 0f, 1f));
        return true;
    }

    public static byte[] EncodeHeartbeat(PacketType type, long timestampMs)
    {
        byte[] buf = new byte[12];
        WriteHeader(buf, type);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(4, 8), timestampMs);
        return buf;
    }

    public static bool TryDecodeHeartbeat(ReadOnlySpan<byte> buf, out HeartbeatPacket packet)
    {
        packet = default;
        if (buf.Length < 12 || !TryValidateHeader(buf, out var type))
            return false;
        if (type != PacketType.HeartbeatPing && type != PacketType.HeartbeatPong)
            return false;

        long ts = BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(4, 8));
        packet = new HeartbeatPacket(type, ts);
        return true;
    }
}

/// <summary>
/// Represents a single physical monitor within a computer's virtual desktop space
/// (Windows Virtual Screen or Linux Wayland/KScreen/RandR output layout).
/// Coordinates (VirtualX, VirtualY) may be negative when secondary monitors are placed left/above the primary monitor.
/// </summary>
public sealed class PhysicalMonitorDescriptor
{
    [JsonPropertyName("monitorId")]
    public string MonitorId { get; set; } = "DISPLAY1";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Monitör 1";

    [JsonPropertyName("virtualX")]
    public int VirtualX { get; set; }

    [JsonPropertyName("virtualY")]
    public int VirtualY { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; } = 1920;

    [JsonPropertyName("height")]
    public int Height { get; set; } = 1080;

    [JsonPropertyName("scaleFactor")]
    public double ScaleFactor { get; set; } = 1.0;

    [JsonPropertyName("isPrimary")]
    public bool IsPrimary { get; set; } = true;

    [JsonIgnore]
    public bool IsSimulated { get; set; }

    [JsonIgnore]
    public int Right => VirtualX + Width;

    [JsonIgnore]
    public int Bottom => VirtualY + Height;

    public bool ContainsPoint(int x, int y) =>
        x >= VirtualX && x < Right && y >= VirtualY && y < Bottom;
}

/// <summary>
/// UDP Discovery Beacon exchanged on port 42849.
/// Note: The device's 6-digit PIN is NEVER broadcast in discovery beacons for security;
/// each user must read the 6-digit PIN from the target device's screen and enter it.
/// </summary>
public sealed class DiscoveryBeacon
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "DISCOVER_BEACON";

    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "windows"; // "windows", "android", "linux-nobara"

    [JsonPropertyName("udpInputPort")]
    public int UdpInputPort { get; set; } = ProtocolConstants.FastInputUdpPort;

    [JsonPropertyName("tcpControlPort")]
    public int TcpControlPort { get; set; } = ProtocolConstants.DataControlTcpPort;

    [JsonPropertyName("screenWidth")]
    public int ScreenWidth { get; set; } = 1920;

    [JsonPropertyName("screenHeight")]
    public int ScreenHeight { get; set; } = 1080;

    [JsonPropertyName("monitors")]
    public List<PhysicalMonitorDescriptor>? Monitors { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>
/// Control, Mutual PIN Pairing, Multi-Monitor Topology Sync, Clipboard, and Drop Shelf message header sent over TCP port 42851.
/// Followed by optional binary payload of length BinaryPayloadLength.
/// </summary>
public sealed class TcpControlHeader
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
    // Supported types:
    // - "PAIR_REQUEST": Sender submits TargetPin (the receiver's 6-digit PIN) to verify sender -> receiver
    // - "PAIR_VERIFY_ACK": Receiver confirms TargetPin matched receiver's LocalPin (and includes whether mutual pairing is now complete)
    // - "PAIR_REJECT": Receiver rejects wrong 6-digit PIN
    // - "TRUSTED_RECONNECT": Sender uses pre-shared persistent trustToken to auto-connect without 6-digit PIN
    // - "TRUSTED_RECONNECT_ACK": Receiver confirms valid trustToken and completes auto-pairing
    // - "TOPOLOGY_SYNC": Sender broadcasts its updated multi-monitor layout (SenderMonitors)
    // - "CLIPBOARD_TEXT": Universal clipboard text sync
    // - "CLIPBOARD_IMAGE": Universal clipboard PNG sync
    // - "SHELF_FILE": Drop Shelf file transfer

    [JsonPropertyName("senderId")]
    public string SenderId { get; set; } = string.Empty;

    [JsonPropertyName("senderName")]
    public string SenderName { get; set; } = string.Empty;

    [JsonPropertyName("senderPlatform")]
    public string? SenderPlatform { get; set; }

    [JsonPropertyName("senderUdpPort")]
    public int? SenderUdpPort { get; set; }

    [JsonPropertyName("senderTcpPort")]
    public int? SenderTcpPort { get; set; }

    [JsonPropertyName("senderScreenWidth")]
    public int? SenderScreenWidth { get; set; }

    [JsonPropertyName("senderScreenHeight")]
    public int? SenderScreenHeight { get; set; }

    [JsonPropertyName("senderMonitors")]
    public List<PhysicalMonitorDescriptor>? SenderMonitors { get; set; }

    [JsonPropertyName("targetPin")]
    public string? TargetPin { get; set; }

    [JsonPropertyName("trustToken")]
    public string? TrustToken { get; set; }

    [JsonPropertyName("requestTrust")]
    public bool? RequestTrust { get; set; }

    [JsonPropertyName("isMutualComplete")]
    public bool? IsMutualComplete { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("fileName")]
    public string? FileName { get; set; }

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    [JsonPropertyName("contentHash")]
    public string? ContentHash { get; set; }
}

public static class TcpFrameCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task WriteFrameAsync(
        Stream stream,
        TcpControlHeader header,
        Stream? binaryPayload = null,
        long binaryLength = 0,
        CancellationToken ct = default)
    {
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions);
        byte[] prefix = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(0, 4), jsonBytes.Length);
        BinaryPrimitives.WriteInt64LittleEndian(prefix.AsSpan(4, 8), binaryLength);

        await stream.WriteAsync(prefix, ct).ConfigureAwait(false);
        await stream.WriteAsync(jsonBytes, ct).ConfigureAwait(false);

        if (binaryPayload != null && binaryLength > 0)
        {
            byte[] buffer = new byte[64 * 1024];
            long remaining = binaryLength;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int read = await binaryPayload.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
                if (read <= 0)
                    break;
                await stream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                remaining -= read;
            }
        }

        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<(TcpControlHeader? Header, long BinaryLength)> ReadHeaderAsync(
        Stream stream,
        CancellationToken ct = default)
    {
        byte[] prefix = new byte[12];
        int readPrefix = await ReadExactAsync(stream, prefix, ct).ConfigureAwait(false);
        if (readPrefix < 12)
            return (null, 0);

        int jsonLen = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(0, 4));
        long binLen = BinaryPrimitives.ReadInt64LittleEndian(prefix.AsSpan(4, 8));

        if (jsonLen <= 0 || jsonLen > 10 * 1024 * 1024)
            return (null, 0);

        byte[] jsonBytes = new byte[jsonLen];
        int readJson = await ReadExactAsync(stream, jsonBytes, ct).ConfigureAwait(false);
        if (readJson < jsonLen)
            return (null, 0);

        var header = JsonSerializer.Deserialize<TcpControlHeader>(jsonBytes, JsonOptions);
        return (header, binLen);
    }

    public static async Task<int> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.Slice(totalRead), ct).ConfigureAwait(false);
            if (read == 0)
                break;
            totalRead += read;
        }
        return totalRead;
    }
}
