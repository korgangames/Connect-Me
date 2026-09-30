using ConnectMe.Core.Protocol;

namespace ConnectMe.Core.Topology;

public enum PeerPairingState
{
    Discovered = 0,
    OutboundPinVerified = 1, // Biz karşı tarafın 6 haneli kodunu doğru girdik; karşı tarafın bizim kodumuzu girmesi bekleniyor
    InboundPinVerified = 2,  // Karşı taraf bizim 6 haneli kodumuzu doğru girdi; bizim onun kodunu girmemiz bekleniyor
    MutuallyPaired = 3       // İki taraf da birbirinin 6 haneli kodunu doğruladı (Aktif Bağlantı!)
}

public sealed class PeerDeviceNode
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Platform { get; set; } = "android"; // "windows", "linux-nobara", "android"
    public string IpAddress { get; set; } = string.Empty;
    public int UdpInputPort { get; set; } = ProtocolConstants.FastInputUdpPort;
    public int TcpControlPort { get; set; } = ProtocolConstants.DataControlTcpPort;
    public int ScreenWidth { get; set; } = 1080;
    public int ScreenHeight { get; set; } = 2400;

    // Mutual 6-Digit PIN Handshake State
    public bool MyEnteredPinVerifiedByRemote { get; set; }
    public bool RemoteEnteredMyPinVerified { get; set; }
    public bool IsMutuallyPaired => MyEnteredPinVerifiedByRemote && RemoteEnteredMyPinVerified;

    public PeerPairingState PairingState => (MyEnteredPinVerifiedByRemote, RemoteEnteredMyPinVerified) switch
    {
        (true, true) => PeerPairingState.MutuallyPaired,
        (true, false) => PeerPairingState.OutboundPinVerified,
        (false, true) => PeerPairingState.InboundPinVerified,
        _ => PeerPairingState.Discovered
    };

    /// <summary>
    /// Optional simulated PIN for local multi-device simulator nodes.
    /// Real remote devices validate their own PIN on their own host.
    /// </summary>
    public string? SimulatedLocalPin { get; set; }

    // 2D Display Arrangement & Partial Edge Segment Mapping
    public ScreenEdge AssignedEdgeOnLocal { get; set; } = ScreenEdge.Right;
    public float EdgeOffsetStart { get; set; } = 0.0f;
    public float EdgeOffsetEnd { get; set; } = 1.0f;

    // 2D GUI Canvas Coordinates
    public double CanvasX { get; set; }
    public double CanvasY { get; set; }
    public double CanvasWidth { get; set; } = 90;
    public double CanvasHeight { get; set; } = 130;
    public bool HasCustomCanvasPosition { get; set; }

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
/// Manages the 2D multi-device spatial arrangement around the local screen,
/// partial edge segments (multiple devices on the same or different edges),
/// magnetic edge snapping for the GUI canvas, dead-corner guards, and edge resistance.
/// </summary>
public sealed class SpatialTopologyEngine
{
    private readonly object _sync = new();
    private readonly Dictionary<string, PeerDeviceNode> _peersById = new();
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

    /// <summary>
    /// Assigns a device to a specific edge and fractional segment [offsetStart..offsetEnd] (0.0 to 1.0).
    /// Allows multiple devices to sit on different edges OR share different parts of the same edge!
    /// </summary>
    public void AssignPeerToEdgeSegment(
        PeerDeviceNode peer,
        ScreenEdge localEdge,
        float offsetStart = 0.0f,
        float offsetEnd = 1.0f)
    {
        if (localEdge == ScreenEdge.None || string.IsNullOrEmpty(peer.DeviceId))
            return;

        float start = Math.Clamp(Math.Min(offsetStart, offsetEnd), 0.0f, 0.95f);
        float end = Math.Clamp(Math.Max(offsetStart, offsetEnd), start + 0.05f, 1.0f);

        lock (_sync)
        {
            peer.AssignedEdgeOnLocal = localEdge;
            peer.EdgeOffsetStart = start;
            peer.EdgeOffsetEnd = end;
            _peersById[peer.DeviceId] = peer;
        }
    }

    public void AssignPeerToEdge(ScreenEdge localEdge, PeerDeviceNode peer)
        => AssignPeerToEdgeSegment(peer, localEdge, 0.0f, 1.0f);

    public void RemovePeer(string deviceId)
    {
        lock (_sync)
        {
            _peersById.Remove(deviceId);
        }
    }

    public IReadOnlyList<PeerDeviceNode> GetConfiguredPeers()
    {
        lock (_sync)
        {
            return _peersById.Values.ToList();
        }
    }

    public PeerDeviceNode? GetPeerOnEdge(ScreenEdge localEdge, float normalizedPos = 0.5f)
    {
        lock (_sync)
        {
            return FindMatchingPeerOnEdgeLocked(localEdge, normalizedPos);
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
    /// Magnetically snaps a dragged peer monitor box on the 2D Display Arrangement Canvas
    /// to the nearest border (Left, Right, Top, Bottom) of the Primary Screen rectangle,
    /// and computes its exact fractional edge segment [EdgeOffsetStart..EdgeOffsetEnd].
    /// </summary>
    public void SnapPeerBoxToPrimaryOnCanvas(
        PeerDeviceNode peer,
        double draggedLeft,
        double draggedTop,
        double peerBoxW,
        double peerBoxH,
        double primaryLeft,
        double primaryTop,
        double primaryW,
        double primaryH)
    {
        double peerCenterX = draggedLeft + peerBoxW / 2.0;
        double peerCenterY = draggedTop + peerBoxH / 2.0;
        double primCenterX = primaryLeft + primaryW / 2.0;
        double primCenterY = primaryTop + primaryH / 2.0;

        double normDx = (peerCenterX - primCenterX) / (primaryW / 2.0);
        double normDy = (peerCenterY - primCenterY) / (primaryH / 2.0);

        const double gap = 4.0;
        ScreenEdge snappedEdge;
        double finalX;
        double finalY;
        float segStart;
        float segEnd;

        if (Math.Abs(normDx) >= Math.Abs(normDy))
        {
            // Snap to Left or Right edge of the Primary monitor box
            snappedEdge = normDx < 0 ? ScreenEdge.Left : ScreenEdge.Right;
            finalX = snappedEdge == ScreenEdge.Left
                ? primaryLeft - peerBoxW - gap
                : primaryLeft + primaryW + gap;

            // Clamp Y so at least 25% of the peer box touches the primary monitor's vertical span
            double minY = primaryTop - peerBoxH * 0.70;
            double maxY = primaryTop + primaryH - peerBoxH * 0.30;
            finalY = Math.Clamp(draggedTop, minY, maxY);

            double overlapTop = Math.Max(primaryTop, finalY);
            double overlapBottom = Math.Min(primaryTop + primaryH, finalY + peerBoxH);
            segStart = (float)Math.Clamp((overlapTop - primaryTop) / primaryH, 0.0, 0.95);
            segEnd = (float)Math.Clamp((overlapBottom - primaryTop) / primaryH, segStart + 0.05, 1.0);
        }
        else
        {
            // Snap to Top or Bottom edge of the Primary monitor box
            snappedEdge = normDy < 0 ? ScreenEdge.Top : ScreenEdge.Bottom;
            finalY = snappedEdge == ScreenEdge.Top
                ? primaryTop - peerBoxH - gap
                : primaryTop + primaryH + gap;

            double minX = primaryLeft - peerBoxW * 0.70;
            double maxX = primaryLeft + primaryW - peerBoxW * 0.30;
            finalX = Math.Clamp(draggedLeft, minX, maxX);

            double overlapLeft = Math.Max(primaryLeft, finalX);
            double overlapRight = Math.Min(primaryLeft + primaryW, finalX + peerBoxW);
            segStart = (float)Math.Clamp((overlapLeft - primaryLeft) / primaryW, 0.0, 0.95);
            segEnd = (float)Math.Clamp((overlapRight - primaryLeft) / primaryW, segStart + 0.05, 1.0);
        }

        peer.CanvasX = finalX;
        peer.CanvasY = finalY;
        peer.CanvasWidth = peerBoxW;
        peer.CanvasHeight = peerBoxH;
        peer.HasCustomCanvasPosition = true;

        AssignPeerToEdgeSegment(peer, snappedEdge, segStart, segEnd);
    }

    /// <summary>
    /// Evaluates cursor position and movement vector to determine if the cursor
    /// should seamlessly cross over to a mutually paired device positioned at that edge segment.
    /// </summary>
    public EdgeTransitionResult EvaluateCursorStep(int cursorX, int cursorY, int deltaX, int deltaY)
    {
        lock (_sync)
        {
            if (IsScreenLocked || _peersById.Count == 0)
            {
                ResetPush();
                return default;
            }

            int relX = cursorX - LocalLeft;
            int relY = cursorY - LocalTop;

            ScreenEdge hitEdge = ScreenEdge.None;
            double outwardPush = 0;
            float localEdgeNormPos = 0.5f;

            bool inVerticalCorner = relY < CornerDeadZonePixels || relY > (LocalHeight - CornerDeadZonePixels);
            bool inHorizontalCorner = relX < CornerDeadZonePixels || relX > (LocalWidth - CornerDeadZonePixels);

            if (relX <= 1 && deltaX < 0 && !inVerticalCorner)
            {
                hitEdge = ScreenEdge.Left;
                outwardPush = -deltaX;
                localEdgeNormPos = Math.Clamp((float)relY / LocalHeight, 0f, 1f);
            }
            else if (relX >= LocalWidth - 2 && deltaX > 0 && !inVerticalCorner)
            {
                hitEdge = ScreenEdge.Right;
                outwardPush = deltaX;
                localEdgeNormPos = Math.Clamp((float)relY / LocalHeight, 0f, 1f);
            }
            else if (relY <= 1 && deltaY < 0 && !inHorizontalCorner)
            {
                hitEdge = ScreenEdge.Top;
                outwardPush = -deltaY;
                localEdgeNormPos = Math.Clamp((float)relX / LocalWidth, 0f, 1f);
            }
            else if (relY >= LocalHeight - 2 && deltaY > 0 && !inHorizontalCorner)
            {
                hitEdge = ScreenEdge.Bottom;
                outwardPush = deltaY;
                localEdgeNormPos = Math.Clamp((float)relX / LocalWidth, 0f, 1f);
            }

            if (hitEdge == ScreenEdge.None)
            {
                if (relX > 6 && relX < LocalWidth - 7 && relY > 6 && relY < LocalHeight - 7)
                {
                    ResetPush();
                }
                return default;
            }

            var targetPeer = FindMatchingPeerOnEdgeLocked(hitEdge, localEdgeNormPos);
            if (targetPeer == null)
            {
                ResetPush();
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
                var targetEntrance = GetOppositeEdge(hitEdge);
                float span = Math.Max(0.05f, targetPeer.EdgeOffsetEnd - targetPeer.EdgeOffsetStart);
                float peerRelativePos = Math.Clamp((localEdgeNormPos - targetPeer.EdgeOffsetStart) / span, 0f, 1f);

                ResetPush();
                return new EdgeTransitionResult(
                    ShouldTransition: true,
                    TargetPeer: targetPeer,
                    LocalExitEdge: hitEdge,
                    TargetEntranceEdge: targetEntrance,
                    NormalizedPosition: peerRelativePos);
            }

            return default;
        }
    }

    private PeerDeviceNode? FindMatchingPeerOnEdgeLocked(ScreenEdge edge, float localEdgeNormPos)
    {
        // Only mutually paired devices can receive edge transitions
        var candidates = _peersById.Values
            .Where(p => p.IsMutuallyPaired && p.AssignedEdgeOnLocal == edge)
            .ToList();

        if (candidates.Count == 0)
            return null;

        // First look for exact segment match [EdgeOffsetStart .. EdgeOffsetEnd]
        var exact = candidates.FirstOrDefault(
            p => localEdgeNormPos >= p.EdgeOffsetStart - 0.01f && localEdgeNormPos <= p.EdgeOffsetEnd + 0.01f);

        return exact;
    }

    /// <summary>
    /// Computes the exact pixel coordinate on the local screen when the cursor returns
    /// from a specific adjacent device along its assigned edge segment.
    /// </summary>
    public (int X, int Y) ComputeLocalEntryPoint(
        ScreenEdge localEntranceEdge,
        float peerNormalizedPosition,
        PeerDeviceNode? returningPeer = null)
    {
        lock (_sync)
        {
            float segStart = returningPeer?.EdgeOffsetStart ?? 0.0f;
            float segEnd = returningPeer?.EdgeOffsetEnd ?? 1.0f;
            float mappedNorm = segStart + Math.Clamp(peerNormalizedPosition, 0.0f, 1.0f) * (segEnd - segStart);
            float t = Math.Clamp(mappedNorm, 0.02f, 0.98f);
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
