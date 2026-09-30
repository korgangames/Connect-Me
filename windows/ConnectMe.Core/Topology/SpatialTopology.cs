using ConnectMe.Core.Protocol;

namespace ConnectMe.Core.Topology;

public sealed class PeerDeviceNode
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Platform { get; set; } = "android";
    public string IpAddress { get; set; } = string.Empty;
    public int UdpInputPort { get; set; } = ProtocolConstants.FastInputUdpPort;
    public int TcpControlPort { get; set; } = ProtocolConstants.DataControlTcpPort;
    public int ScreenWidth { get; set; } = 1080;
    public int ScreenHeight { get; set; } = 2400;
    public ScreenEdge AssignedEdgeOnLocal { get; set; } = ScreenEdge.Right;
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
    public double LatencyMs { get; set; }
    public bool DiscoveredViaBle { get; set; }
}

public readonly record struct EdgeTransitionResult(
    bool ShouldTransition,
    PeerDeviceNode? TargetPeer,
    ScreenEdge LocalExitEdge,
    ScreenEdge TargetEntranceEdge,
    float NormalizedPosition);

/// <summary>
/// Manages the 2D spatial arrangement of devices around the local screen,
/// dead-corner guards, edge resistance, and proportional coordinate mapping.
/// </summary>
public sealed class SpatialTopologyEngine
{
    private readonly object _sync = new();
    private readonly Dictionary<ScreenEdge, PeerDeviceNode> _edgePeers = new();
    private double _accumulatedPush;
    private ScreenEdge _pushingEdge = ScreenEdge.None;

    public int LocalLeft { get; private set; }
    public int LocalTop { get; private set; }
    public int LocalWidth { get; private set; } = 1920;
    public int LocalHeight { get; private set; } = 1080;

    /// <summary>
    /// Pixels at each corner of the screen where edge switching is blocked
    /// so window close ('X') and Start/taskbar buttons are never accidentally crossed.
    /// </summary>
    public int CornerDeadZonePixels { get; set; } = 24;

    /// <summary>
    /// Cumulative pixels the user must push against an active edge before switching.
    /// Prevents accidental transitions when casually brushing the screen border.
    /// </summary>
    public double EdgeResistancePixels { get; set; } = 28.0;

    /// <summary>
    /// Global lock (e.g. Scroll Lock / Game Mode) that keeps the cursor on the current screen.
    /// </summary>
    public bool IsScreenLocked { get; set; }

    public void UpdateLocalScreenBounds(int left, int top, int width, int height)
    {
        lock (_sync)
        {
            LocalLeft = left;
            LocalTop = top;
            LocalWidth = Math.Max(100, width);
            LocalHeight = Math.Max(100, height);
        }
    }

    public void AssignPeerToEdge(ScreenEdge localEdge, PeerDeviceNode peer)
    {
        if (localEdge == ScreenEdge.None)
            return;

        lock (_sync)
        {
            // Remove existing assignment for the same device if moved to a new edge
            var existingEdges = _edgePeers
                .Where(kv => kv.Value.DeviceId == peer.DeviceId)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var e in existingEdges)
            {
                _edgePeers.Remove(e);
            }

            peer.AssignedEdgeOnLocal = localEdge;
            _edgePeers[localEdge] = peer;
        }
    }

    public void RemovePeer(string deviceId)
    {
        lock (_sync)
        {
            var keys = _edgePeers
                .Where(kv => kv.Value.DeviceId == deviceId)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var k in keys)
                _edgePeers.Remove(k);
        }
    }

    public PeerDeviceNode? GetPeerOnEdge(ScreenEdge localEdge)
    {
        lock (_sync)
        {
            return _edgePeers.TryGetValue(localEdge, out var peer) ? peer : null;
        }
    }

    public IReadOnlyDictionary<ScreenEdge, PeerDeviceNode> GetAssignedPeers()
    {
        lock (_sync)
        {
            return new Dictionary<ScreenEdge, PeerDeviceNode>(_edgePeers);
        }
    }

    public static ScreenEdge GetOppositeEdge(ScreenEdge edge) => edge switch
    {
        ScreenEdge.Left => ScreenEdge.Right,
        ScreenEdge.Right => ScreenEdge.Left,
        ScreenEdge.Top => ScreenEdge.Bottom,
        ScreenEdge.Bottom => ScreenEdge.Top,
        _ => ScreenEdge.None
    };

    /// <summary>
    /// Evaluates cursor position and movement vector to determine if the cursor
    /// should seamlessly cross over to an adjacent device.
    /// </summary>
    public EdgeTransitionResult EvaluateCursorStep(int cursorX, int cursorY, int deltaX, int deltaY)
    {
        lock (_sync)
        {
            if (IsScreenLocked || _edgePeers.Count == 0)
            {
                ResetPush();
                return default;
            }

            int relX = cursorX - LocalLeft;
            int relY = cursorY - LocalTop;

            // Detect if cursor is at any outer boundary and pushing outward
            ScreenEdge hitEdge = ScreenEdge.None;
            double outwardPush = 0;
            float normalizedPos = 0.5f;

            bool inVerticalCorner = relY < CornerDeadZonePixels || relY > (LocalHeight - CornerDeadZonePixels);
            bool inHorizontalCorner = relX < CornerDeadZonePixels || relX > (LocalWidth - CornerDeadZonePixels);

            if (relX <= 1 && deltaX < 0 && !inVerticalCorner && _edgePeers.ContainsKey(ScreenEdge.Left))
            {
                hitEdge = ScreenEdge.Left;
                outwardPush = -deltaX;
                normalizedPos = Math.Clamp((float)relY / LocalHeight, 0f, 1f);
            }
            else if (relX >= LocalWidth - 2 && deltaX > 0 && !inVerticalCorner && _edgePeers.ContainsKey(ScreenEdge.Right))
            {
                hitEdge = ScreenEdge.Right;
                outwardPush = deltaX;
                normalizedPos = Math.Clamp((float)relY / LocalHeight, 0f, 1f);
            }
            else if (relY <= 1 && deltaY < 0 && !inHorizontalCorner && _edgePeers.ContainsKey(ScreenEdge.Top))
            {
                hitEdge = ScreenEdge.Top;
                outwardPush = -deltaY;
                normalizedPos = Math.Clamp((float)relX / LocalWidth, 0f, 1f);
            }
            else if (relY >= LocalHeight - 2 && deltaY > 0 && !inHorizontalCorner && _edgePeers.ContainsKey(ScreenEdge.Bottom))
            {
                hitEdge = ScreenEdge.Bottom;
                outwardPush = deltaY;
                normalizedPos = Math.Clamp((float)relX / LocalWidth, 0f, 1f);
            }

            if (hitEdge == ScreenEdge.None)
            {
                // If moved away from the border, reset accumulated push
                if (relX > 6 && relX < LocalWidth - 7 && relY > 6 && relY < LocalHeight - 7)
                {
                    ResetPush();
                }
                return default;
            }

            if (_pushingEdge != hitEdge)
            {
                _pushingEdge = hitEdge;
                _accumulatedPush = 0;
            }

            _accumulatedPush += outwardPush;
            if (_accumulatedPush >= EdgeResistancePixels)
            {
                var targetPeer = _edgePeers[hitEdge];
                var targetEntrance = GetOppositeEdge(hitEdge);
                ResetPush();
                return new EdgeTransitionResult(
                    ShouldTransition: true,
                    TargetPeer: targetPeer,
                    LocalExitEdge: hitEdge,
                    TargetEntranceEdge: targetEntrance,
                    NormalizedPosition: normalizedPos);
            }

            return default;
        }
    }

    /// <summary>
    /// Computes the exact pixel coordinate on the local screen when the cursor returns
    /// from an adjacent device along the given local entrance edge.
    /// </summary>
    public (int X, int Y) ComputeLocalEntryPoint(ScreenEdge localEntranceEdge, float normalizedPosition)
    {
        lock (_sync)
        {
            float t = Math.Clamp(normalizedPosition, 0.02f, 0.98f);
            const int insetPixels = 8;

            return localEntranceEdge switch
            {
                ScreenEdge.Left => (
                    LocalLeft + insetPixels,
                    LocalTop + (int)Math.Round(t * LocalHeight)),
                ScreenEdge.Right => (
                    LocalLeft + LocalWidth - insetPixels,
                    LocalTop + (int)Math.Round(t * LocalHeight)),
                ScreenEdge.Top => (
                    LocalLeft + (int)Math.Round(t * LocalWidth),
                    LocalTop + insetPixels),
                ScreenEdge.Bottom => (
                    LocalLeft + (int)Math.Round(t * LocalWidth),
                    LocalTop + LocalHeight - insetPixels),
                _ => (LocalLeft + LocalWidth / 2, LocalTop + LocalHeight / 2)
            };
        }
    }

    private void ResetPush()
    {
        _accumulatedPush = 0;
        _pushingEdge = ScreenEdge.None;
    }
}
