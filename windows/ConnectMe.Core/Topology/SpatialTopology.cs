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

    // Multi-Monitor Peer Support (when the remote peer itself has 2+ monitors)
    public List<PhysicalMonitorDescriptor> RemoteMonitors { get; set; } = new();
    public int RemoteMonitorCount => Math.Max(1, RemoteMonitors.Count);

    // Mutual 6-Digit PIN Handshake State
    public bool MyEnteredPinVerifiedByRemote { get; set; }
    public bool RemoteEnteredMyPinVerified { get; set; }
    public bool IsMutuallyPaired => MyEnteredPinVerifiedByRemote && RemoteEnteredMyPinVerified;

    // Persistent Device Trust & Auto-Connect
    public bool IsTrusted { get; set; }
    public string TrustToken { get; set; } = string.Empty;
    public string? PendingTrustToken { get; set; }

    // Colony / Mesh Group State
    public string? ColonyId { get; set; }
    public string? ColonyName { get; set; }
    public List<ColonyMemberDescriptor> ColonyMembers { get; set; } = new();

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

    // Multi-Monitor Host Edge Attachment:
    // Specifies which physical monitor of the local PC this remote device is docked against.
    // If empty or "ALL", attaches to the outermost monitor in that direction.
    public string AttachedLocalMonitorId { get; set; } = string.Empty;

    // Colony Relative Docking:
    // When docked relative to another peer device (e.g. Device C attached to Device B's Right edge),
    // stores that peer's DeviceId. If empty or "LOCAL", attaches to the local machine.
    public string AttachedToDeviceId { get; set; } = string.Empty;

    // 2D Display Arrangement & Partial Edge Segment Mapping
    public ScreenEdge AssignedEdgeOnLocal { get; set; } = ScreenEdge.Right;
    public float EdgeOffsetStart { get; set; } = 0.0f;
    public float EdgeOffsetEnd { get; set; } = 1.0f;

    // Global Colony Mesh Coordinates
    public double ColonyX { get; set; }
    public double ColonyY { get; set; }

    // 2D GUI Canvas Coordinates
    public double CanvasX { get; set; }
    public double CanvasY { get; set; }
    public double CanvasWidth { get; set; } = 90;
    public double CanvasHeight { get; set; } = 130;
    public bool HasCustomCanvasPosition { get; set; }

    // Real-time Estimated Remote Cursor Position
    public int RemoteCursorX { get; set; }
    public int RemoteCursorY { get; set; }

    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
    public double LatencyMs { get; set; }
    public bool DiscoveredViaBle { get; set; }
}

public readonly record struct EdgeTransitionResult(
    bool ShouldTransition,
    PeerDeviceNode? TargetPeer,
    ScreenEdge LocalExitEdge,
    ScreenEdge TargetEntranceEdge,
    float NormalizedPosition,
    string SourceMonitorId = "");

/// <summary>
/// Represents a magnetic snap target on the 2D Display Arrangement Canvas.
/// Can be either a local computer's physical monitor or another peer card in the colony mesh.
/// </summary>
public readonly record struct CanvasAnchorBox(
    string Id,
    string Name,
    bool IsLocal,
    double Left,
    double Top,
    double Width,
    double Height);

/// <summary>
/// Manages the multi-monitor virtual desktop topology of the local computer (Windows or Linux),
/// distinguishing between internal seams (where the cursor moves freely between local monitors)
/// and exposed outer edges (where the cursor transitions seamlessly to remote devices).
/// Also manages partial edge segments, magnetic edge snapping, and dead-corner guards.
/// </summary>
public sealed class SpatialTopologyEngine
{
    private readonly object _sync = new();
    private readonly Dictionary<string, PeerDeviceNode> _peersById = new();
    private readonly List<PhysicalMonitorDescriptor> _localMonitors = new();

    private double _accumulatedPush;
    private ScreenEdge _pushingEdge = ScreenEdge.None;
    private string _pushingMonitorId = string.Empty;

    public int LocalLeft { get; private set; }
    public int LocalTop { get; private set; }
    public int LocalWidth { get; private set; } = 1920;
    public int LocalHeight { get; private set; } = 1080;

    /// <summary>
    /// All physical monitors that make up the local computer's virtual desktop.
    /// </summary>
    public IReadOnlyList<PhysicalMonitorDescriptor> LocalMonitors
    {
        get
        {
            lock (_sync)
            {
                return _localMonitors.ToList();
            }
        }
    }

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

    public SpatialTopologyEngine()
    {
        // Default single primary monitor
        ResetToSingleMonitor(1920, 1080);
    }

    /// <summary>
    /// Updates the local computer with a single monitor boundary (maintaining backward compatibility).
    /// </summary>
    public void UpdateLocalScreenBounds(int left, int top, int width, int height)
    {
        lock (_sync)
        {
            var primary = _localMonitors.FirstOrDefault(m => m.IsPrimary);
            if (primary == null)
            {
                primary = new PhysicalMonitorDescriptor
                {
                    MonitorId = "DISPLAY1",
                    Name = "Birincil Ekran",
                    VirtualX = left,
                    VirtualY = top,
                    Width = Math.Max(320, width),
                    Height = Math.Max(240, height),
                    IsPrimary = true
                };
                _localMonitors.Clear();
                _localMonitors.Add(primary);
            }
            else
            {
                primary.VirtualX = left;
                primary.VirtualY = top;
                primary.Width = Math.Max(320, width);
                primary.Height = Math.Max(240, height);
            }

            RecalculateVirtualDesktopBoundsLocked();
        }
    }

    /// <summary>
    /// Updates the full set of physical monitors connected to this computer
    /// (e.g. from Windows EnumDisplayMonitors or Linux kscreen-doctor / xrandr).
    /// </summary>
    public void UpdateLocalMonitors(IEnumerable<PhysicalMonitorDescriptor> monitors)
    {
        lock (_sync)
        {
            _localMonitors.Clear();
            foreach (var m in monitors)
            {
                _localMonitors.Add(m);
            }

            if (_localMonitors.Count == 0)
            {
                _localMonitors.Add(new PhysicalMonitorDescriptor
                {
                    MonitorId = "DISPLAY1",
                    Name = "Birincil Ekran",
                    VirtualX = 0,
                    VirtualY = 0,
                    Width = 1920,
                    Height = 1080,
                    IsPrimary = true
                });
            }

            RecalculateVirtualDesktopBoundsLocked();
        }
    }

    /// <summary>
    /// Resets the local configuration to a single standard monitor.
    /// </summary>
    public void ResetToSingleMonitor(int width = 1920, int height = 1080)
    {
        UpdateLocalMonitors(
        [
            new PhysicalMonitorDescriptor
            {
                MonitorId = "DISPLAY1",
                Name = "Birincil Monitör",
                VirtualX = 0,
                VirtualY = 0,
                Width = width,
                Height = height,
                ScaleFactor = 1.0,
                IsPrimary = true
            }
        ]);
    }

    /// <summary>
    /// Adds a simulated secondary/tertiary local monitor to test multi-monitor setups
    /// (e.g. a 2nd horizontal screen or a vertical portrait coding screen).
    /// </summary>
    public PhysicalMonitorDescriptor AddSimulatedLocalMonitor(string name, int vx, int vy, int w, int h, bool isPrimary = false)
    {
        lock (_sync)
        {
            string id = $"SIM-DISP-{_localMonitors.Count + 1}";
            var sim = new PhysicalMonitorDescriptor
            {
                MonitorId = id,
                Name = name,
                VirtualX = vx,
                VirtualY = vy,
                Width = w,
                Height = h,
                IsPrimary = isPrimary,
                IsSimulated = true
            };

            if (isPrimary)
            {
                foreach (var m in _localMonitors) m.IsPrimary = false;
            }

            _localMonitors.Add(sim);
            RecalculateVirtualDesktopBoundsLocked();
            return sim;
        }
    }

    private void RecalculateVirtualDesktopBoundsLocked()
    {
        if (_localMonitors.Count == 0)
            return;

        int minX = _localMonitors.Min(m => m.VirtualX);
        int minY = _localMonitors.Min(m => m.VirtualY);
        int maxX = _localMonitors.Max(m => m.Right);
        int maxY = _localMonitors.Max(m => m.Bottom);

        LocalLeft = minX;
        LocalTop = minY;
        LocalWidth = Math.Max(100, maxX - minX);
        LocalHeight = Math.Max(100, maxY - minY);
    }

    /// <summary>
    /// Finds which local physical monitor contains the virtual coordinate (x, y).
    /// </summary>
    public PhysicalMonitorDescriptor? FindMonitorContainingPoint(int x, int y)
    {
        lock (_sync)
        {
            return _localMonitors.FirstOrDefault(m => m.ContainsPoint(x, y));
        }
    }

    /// <summary>
    /// Finds which local physical monitor is nearest to the coordinate (x, y).
    /// </summary>
    public PhysicalMonitorDescriptor FindMonitorNearestPoint(int x, int y)
    {
        lock (_sync)
        {
            var match = _localMonitors.FirstOrDefault(m => m.ContainsPoint(x, y));
            if (match != null)
                return match;

            return _localMonitors
                .OrderBy(m => DistanceToMonitor(m, x, y))
                .FirstOrDefault() ?? _localMonitors[0];
        }
    }

    private static double DistanceToMonitor(PhysicalMonitorDescriptor m, int x, int y)
    {
        int dx = Math.Max(0, Math.Max(m.VirtualX - x, x - (m.Right - 1)));
        int dy = Math.Max(0, Math.Max(m.VirtualY - y, y - (m.Bottom - 1)));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Clamps an arbitrary cursor coordinate to the nearest valid local physical monitor rectangle.
    /// </summary>
    public (int X, int Y) ClampToVirtualDesktop(int x, int y)
    {
        lock (_sync)
        {
            if (_localMonitors.Count == 0)
                return (x, y);

            if (_localMonitors.Any(m => m.ContainsPoint(x, y)))
                return (x, y);

            var nearest = FindMonitorNearestPoint(x, y);
            int cx = Math.Clamp(x, nearest.VirtualX, nearest.Right - 1);
            int cy = Math.Clamp(y, nearest.VirtualY, nearest.Bottom - 1);
            return (cx, cy);
        }
    }

    /// <summary>
    /// Checks whether pushing outward along a given edge of <paramref name="monitor"/> at coordinate (x, y)
    /// would cross an INTERNAL SEAM into another local physical monitor of the same computer.
    /// When true, the OS handles cursor movement natively across the physical screens and Connect Me NEVER intercepts it!
    /// </summary>
    public bool IsPointOnInternalMonitorSeam(PhysicalMonitorDescriptor monitor, ScreenEdge edge, int cursorX, int cursorY)
    {
        lock (_sync)
        {
            const int probeDistance = 4;
            int testX = cursorX;
            int testY = cursorY;

            switch (edge)
            {
                case ScreenEdge.Left:
                    testX = monitor.VirtualX - probeDistance;
                    break;
                case ScreenEdge.Right:
                    testX = monitor.Right + probeDistance;
                    break;
                case ScreenEdge.Top:
                    testY = monitor.VirtualY - probeDistance;
                    break;
                case ScreenEdge.Bottom:
                    testY = monitor.Bottom + probeDistance;
                    break;
                default:
                    return false;
            }

            return _localMonitors.Any(m => m.MonitorId != monitor.MonitorId && m.ContainsPoint(testX, testY));
        }
    }

    public PhysicalMonitorDescriptor GetOutermostMonitorForEdge(ScreenEdge edge)
    {
        lock (_sync)
        {
            if (_localMonitors.Count == 0)
            {
                return new PhysicalMonitorDescriptor { MonitorId = "DISPLAY1", Name = "Birincil Ekran", Width = 1920, Height = 1080 };
            }

            return edge switch
            {
                ScreenEdge.Left => _localMonitors.OrderBy(m => m.VirtualX).First(),
                ScreenEdge.Right => _localMonitors.OrderByDescending(m => m.Right).First(),
                ScreenEdge.Top => _localMonitors.OrderBy(m => m.VirtualY).First(),
                ScreenEdge.Bottom => _localMonitors.OrderByDescending(m => m.Bottom).First(),
                _ => _localMonitors.FirstOrDefault(m => m.IsPrimary) ?? _localMonitors[0]
            };
        }
    }

    public void RegisterPeer(PeerDeviceNode peer)
    {
        if (peer == null || string.IsNullOrEmpty(peer.DeviceId))
            return;
        lock (_sync)
        {
            _peersById[peer.DeviceId] = peer;
        }
    }

    /// <summary>
    /// Assigns a device to a specific edge, local monitor, and fractional segment [offsetStart..offsetEnd] (0.0 to 1.0).
    /// Allows multiple devices to sit on different edges OR share different parts of the same monitor edge!
    /// </summary>
    public void AssignPeerToEdgeSegment(
        PeerDeviceNode peer,
        ScreenEdge localEdge,
        float offsetStart = 0.0f,
        float offsetEnd = 1.0f,
        string? targetLocalMonitorId = null)
    {
        if (peer == null || string.IsNullOrEmpty(peer.DeviceId))
            return;

        if (localEdge == ScreenEdge.None)
        {
            lock (_sync)
            {
                peer.AssignedEdgeOnLocal = ScreenEdge.None;
                _peersById[peer.DeviceId] = peer;
            }
            return;
        }

        float start = Math.Clamp(Math.Min(offsetStart, offsetEnd), 0.0f, 0.95f);
        float end = Math.Clamp(Math.Max(offsetStart, offsetEnd), start + 0.05f, 1.0f);

        lock (_sync)
        {
            peer.AssignedEdgeOnLocal = localEdge;
            peer.EdgeOffsetStart = start;
            peer.EdgeOffsetEnd = end;
            if (!string.IsNullOrEmpty(targetLocalMonitorId))
            {
                peer.AttachedLocalMonitorId = targetLocalMonitorId;
            }
            else if (string.IsNullOrEmpty(peer.AttachedLocalMonitorId))
            {
                peer.AttachedLocalMonitorId = GetOutermostMonitorForEdge(localEdge).MonitorId;
            }

            _peersById[peer.DeviceId] = peer;
        }
    }

    public void AssignPeerToEdge(ScreenEdge localEdge, PeerDeviceNode peer, string? targetLocalMonitorId = null)
        => AssignPeerToEdgeSegment(peer, localEdge, 0.0f, 1.0f, targetLocalMonitorId);

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

    public PeerDeviceNode? GetPeerOnEdge(ScreenEdge localEdge, float normalizedPos = 0.5f, string? monitorId = null)
    {
        lock (_sync)
        {
            return FindMatchingPeerOnEdgeLocked(localEdge, normalizedPos, monitorId);
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
    /// Snaps a dragged peer box on the 2D Display Arrangement Canvas to the nearest anchor box
    /// (either a local physical monitor OR any other peer card in the colony mesh).
    /// Enables arbitrary relative docking (e.g. Device C docked to Device B's Right edge, while Device B is docked to Device A).
    /// </summary>
    public (CanvasAnchorBox Anchor, ScreenEdge SnappedEdge, double FinalX, double FinalY, float SegStart, float SegEnd) SnapPeerBoxToAnchorBoxesOnCanvas(
        PeerDeviceNode peer,
        double draggedLeft,
        double draggedTop,
        double peerBoxW,
        double peerBoxH,
        IReadOnlyList<CanvasAnchorBox> anchorBoxes)
    {
        if (anchorBoxes == null || anchorBoxes.Count == 0)
            return default;

        double peerCenterX = draggedLeft + peerBoxW / 2.0;
        double peerCenterY = draggedTop + peerBoxH / 2.0;

        // Filter out peer's own box if present
        var validAnchors = anchorBoxes.Where(a => a.Id != peer.DeviceId).ToList();
        if (validAnchors.Count == 0)
            return default;

        // Find closest anchor box by center distance
        var bestAnchor = validAnchors
            .OrderBy(ab =>
            {
                double acx = ab.Left + ab.Width / 2.0;
                double acy = ab.Top + ab.Height / 2.0;
                double ddx = peerCenterX - acx;
                double ddy = peerCenterY - acy;
                return ddx * ddx + ddy * ddy;
            })
            .First();

        double normDx = (peerCenterX - (bestAnchor.Left + bestAnchor.Width / 2.0)) / (bestAnchor.Width / 2.0);
        double normDy = (peerCenterY - (bestAnchor.Top + bestAnchor.Height / 2.0)) / (bestAnchor.Height / 2.0);

        const double gap = 4.0;
        ScreenEdge snappedEdge;
        double finalX;
        double finalY;
        float segStart;
        float segEnd;

        if (Math.Abs(normDx) >= Math.Abs(normDy))
        {
            snappedEdge = normDx < 0 ? ScreenEdge.Left : ScreenEdge.Right;
            finalX = snappedEdge == ScreenEdge.Left
                ? bestAnchor.Left - peerBoxW - gap
                : bestAnchor.Left + bestAnchor.Width + gap;

            double minY = bestAnchor.Top - peerBoxH * 0.70;
            double maxY = bestAnchor.Top + bestAnchor.Height - peerBoxH * 0.30;
            finalY = Math.Clamp(draggedTop, minY, maxY);

            double overlapTop = Math.Max(bestAnchor.Top, finalY);
            double overlapBottom = Math.Min(bestAnchor.Top + bestAnchor.Height, finalY + peerBoxH);
            segStart = (float)Math.Clamp((overlapTop - bestAnchor.Top) / Math.Max(1.0, bestAnchor.Height), 0.0, 0.95);
            segEnd = (float)Math.Clamp((overlapBottom - bestAnchor.Top) / Math.Max(1.0, bestAnchor.Height), segStart + 0.05, 1.0);
        }
        else
        {
            snappedEdge = normDy < 0 ? ScreenEdge.Top : ScreenEdge.Bottom;
            finalY = snappedEdge == ScreenEdge.Top
                ? bestAnchor.Top - peerBoxH - gap
                : bestAnchor.Top + bestAnchor.Height + gap;

            double minX = bestAnchor.Left - peerBoxW * 0.70;
            double maxX = bestAnchor.Left + bestAnchor.Width - peerBoxW * 0.30;
            finalX = Math.Clamp(draggedLeft, minX, maxX);

            double overlapLeft = Math.Max(bestAnchor.Left, finalX);
            double overlapRight = Math.Min(bestAnchor.Left + bestAnchor.Width, finalX + peerBoxW);
            segStart = (float)Math.Clamp((overlapLeft - bestAnchor.Left) / Math.Max(1.0, bestAnchor.Width), 0.0, 0.95);
            segEnd = (float)Math.Clamp((overlapRight - bestAnchor.Left) / Math.Max(1.0, bestAnchor.Width), segStart + 0.05, 1.0);
        }

        peer.CanvasX = finalX;
        peer.CanvasY = finalY;
        peer.CanvasWidth = peerBoxW;
        peer.CanvasHeight = peerBoxH;
        peer.ColonyX = finalX;
        peer.ColonyY = finalY;
        peer.HasCustomCanvasPosition = true;

        if (bestAnchor.IsLocal)
        {
            peer.AttachedLocalMonitorId = bestAnchor.Id;
            peer.AttachedToDeviceId = string.Empty;
            AssignPeerToEdgeSegment(peer, snappedEdge, segStart, segEnd, bestAnchor.Id);
        }
        else
        {
            peer.AttachedToDeviceId = bestAnchor.Id;
            peer.AttachedLocalMonitorId = string.Empty;
            lock (_sync)
            {
                peer.AssignedEdgeOnLocal = snappedEdge;
                peer.EdgeOffsetStart = segStart;
                peer.EdgeOffsetEnd = segEnd;
                _peersById[peer.DeviceId] = peer;
            }
        }

        return (bestAnchor, snappedEdge, finalX, finalY, segStart, segEnd);
    }

    /// <summary>
    /// Snaps a dragged peer monitor box on the 2D Display Arrangement Canvas to the nearest exposed outer edge
    /// among all physical monitors of this computer, avoiding internal seams between physical screens.
    /// </summary>
    public void SnapPeerBoxToLocalMonitorsOnCanvas(
        PeerDeviceNode peer,
        double draggedLeft,
        double draggedTop,
        double peerBoxW,
        double peerBoxH,
        IReadOnlyList<(PhysicalMonitorDescriptor Monitor, double Left, double Top, double Width, double Height)> monitorCanvasBoxes)
    {
        if (monitorCanvasBoxes == null || monitorCanvasBoxes.Count == 0)
            return;

        var anchors = monitorCanvasBoxes.Select(m => new CanvasAnchorBox(
            m.Monitor.MonitorId,
            m.Monitor.Name,
            true,
            m.Left,
            m.Top,
            m.Width,
            m.Height)).ToList();

        SnapPeerBoxToAnchorBoxesOnCanvas(peer, draggedLeft, draggedTop, peerBoxW, peerBoxH, anchors);
    }

    /// <summary>
    /// Finds any peer in the colony that is spatially adjacent to the given node on the specified edge.
    /// Used for multi-hop cursor relay transitions (e.g. from Node B to Node C when cursor pushes Right).
    /// </summary>
    public PeerDeviceNode? FindAdjacentPeerOnEdge(string fromDeviceId, ScreenEdge edge, float normPos = 0.5f)
    {
        lock (_sync)
        {
            // Case 1: Peer C has AttachedToDeviceId == fromDeviceId and AssignedEdgeOnLocal == edge
            var attachedChildren = _peersById.Values
                .Where(p => p.IsMutuallyPaired &&
                            string.Equals(p.AttachedToDeviceId, fromDeviceId, StringComparison.OrdinalIgnoreCase) &&
                            p.AssignedEdgeOnLocal == edge)
                .ToList();

            if (attachedChildren.Count > 0)
            {
                var exact = attachedChildren.FirstOrDefault(p =>
                    normPos >= p.EdgeOffsetStart - 0.02f && normPos <= p.EdgeOffsetEnd + 0.02f);
                return exact ?? attachedChildren[0];
            }

            // Case 2: fromDeviceId itself is attached to another peer, and 'edge' is the edge leading to its parent (opposite edge)
            if (_peersById.TryGetValue(fromDeviceId, out var fromPeer) &&
                !string.IsNullOrEmpty(fromPeer.AttachedToDeviceId) &&
                GetOppositeEdge(fromPeer.AssignedEdgeOnLocal) == edge)
            {
                if (_peersById.TryGetValue(fromPeer.AttachedToDeviceId, out var parentPeer) && parentPeer.IsMutuallyPaired)
                {
                    return parentPeer;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Builds a synchronized list of ColonyNodeLayoutDescriptor for all nodes in the mesh.
    /// </summary>
    public List<ColonyNodeLayoutDescriptor> BuildColonyLayoutDescriptors(
        string localDeviceId,
        string localDeviceName,
        string localPlatform)
    {
        var list = new List<ColonyNodeLayoutDescriptor>();

        // 1. Local machine node
        list.Add(new ColonyNodeLayoutDescriptor
        {
            DeviceId = localDeviceId,
            DeviceName = localDeviceName,
            Platform = localPlatform,
            ScreenWidth = LocalWidth,
            ScreenHeight = LocalHeight,
            AttachedToDeviceId = string.Empty,
            AttachedEdge = (int)ScreenEdge.None,
            EdgeOffsetStart = 0.0,
            EdgeOffsetEnd = 1.0,
            ColonyX = 0,
            ColonyY = 0,
            CanvasWidth = 140,
            CanvasHeight = 90
        });

        // 2. All configured peers
        lock (_sync)
        {
            foreach (var peer in _peersById.Values)
            {
                list.Add(new ColonyNodeLayoutDescriptor
                {
                    DeviceId = peer.DeviceId,
                    DeviceName = peer.DeviceName,
                    Platform = peer.Platform,
                    ScreenWidth = peer.ScreenWidth,
                    ScreenHeight = peer.ScreenHeight,
                    AttachedToDeviceId = peer.AttachedToDeviceId,
                    AttachedEdge = (int)peer.AssignedEdgeOnLocal,
                    EdgeOffsetStart = peer.EdgeOffsetStart,
                    EdgeOffsetEnd = peer.EdgeOffsetEnd,
                    ColonyX = peer.ColonyX != 0 ? peer.ColonyX : peer.CanvasX,
                    ColonyY = peer.ColonyY != 0 ? peer.ColonyY : peer.CanvasY,
                    CanvasWidth = peer.CanvasWidth,
                    CanvasHeight = peer.CanvasHeight
                });
            }
        }

        return list;
    }

    /// <summary>
    /// Applies a received ColonyTopology layout from another node in the mesh.
    /// Updates relative attachments and canvas coordinates for all known peers.
    /// </summary>
    public void ApplyColonyTopology(
        IReadOnlyList<ColonyNodeLayoutDescriptor> descriptors,
        string senderDeviceId,
        string localDeviceId)
    {
        if (descriptors == null || descriptors.Count == 0)
            return;

        lock (_sync)
        {
            var myDesc = descriptors.FirstOrDefault(d => string.Equals(d.DeviceId, localDeviceId, StringComparison.OrdinalIgnoreCase));

            foreach (var desc in descriptors)
            {
                if (string.Equals(desc.DeviceId, localDeviceId, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (_peersById.TryGetValue(desc.DeviceId, out var peer))
                {
                    peer.DeviceName = desc.DeviceName;
                    peer.Platform = desc.Platform;
                    peer.ScreenWidth = desc.ScreenWidth;
                    peer.ScreenHeight = desc.ScreenHeight;
                    peer.CanvasWidth = desc.CanvasWidth;
                    peer.CanvasHeight = desc.CanvasHeight;
                    peer.ColonyX = desc.ColonyX;
                    peer.ColonyY = desc.ColonyY;
                    peer.CanvasX = desc.ColonyX;
                    peer.CanvasY = desc.ColonyY;
                    peer.HasCustomCanvasPosition = true;
                    peer.AttachedToDeviceId = desc.AttachedToDeviceId;
                    peer.AssignedEdgeOnLocal = (ScreenEdge)desc.AttachedEdge;
                    peer.EdgeOffsetStart = (float)desc.EdgeOffsetStart;
                    peer.EdgeOffsetEnd = (float)desc.EdgeOffsetEnd;

                    // If desc is attached to ME (local machine)
                    if (string.Equals(desc.AttachedToDeviceId, localDeviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        peer.AssignedEdgeOnLocal = (ScreenEdge)desc.AttachedEdge;
                    }
                    // Or if ME is attached to desc
                    else if (myDesc != null && string.Equals(myDesc.AttachedToDeviceId, desc.DeviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        peer.AssignedEdgeOnLocal = GetOppositeEdge((ScreenEdge)myDesc.AttachedEdge);
                        peer.EdgeOffsetStart = (float)myDesc.EdgeOffsetStart;
                        peer.EdgeOffsetEnd = (float)myDesc.EdgeOffsetEnd;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Snaps a dragged peer monitor box to the primary monitor (backward compatibility wrapper).
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
        var primaryMon = _localMonitors.FirstOrDefault(m => m.IsPrimary)
            ?? new PhysicalMonitorDescriptor { MonitorId = "DISPLAY1", Name = "Birincil Ekran", Width = 1920, Height = 1080 };

        SnapPeerBoxToLocalMonitorsOnCanvas(
            peer,
            draggedLeft,
            draggedTop,
            peerBoxW,
            peerBoxH,
            [(primaryMon, primaryLeft, primaryTop, primaryW, primaryH)]);
    }

    /// <summary>
    /// Evaluates cursor position and movement vector across the multi-monitor desktop.
    /// Distinguishes between internal seams (where cursor crosses between local physical monitors)
    /// and exposed outer edges (where cursor crosses seamlessly to remote devices).
    /// </summary>
    public EdgeTransitionResult EvaluateCursorStep(int cursorX, int cursorY, int deltaX, int deltaY)
    {
        lock (_sync)
        {
            if (IsScreenLocked || _peersById.Count == 0 || _localMonitors.Count == 0)
            {
                ResetPush();
                return default;
            }

            // 1. Identify which local physical monitor the cursor is currently inside or nearest to
            var currentMon = FindMonitorContainingPoint(cursorX, cursorY) ?? FindMonitorNearestPoint(cursorX, cursorY);

            int relX = cursorX - currentMon.VirtualX;
            int relY = cursorY - currentMon.VirtualY;

            ScreenEdge hitEdge = ScreenEdge.None;
            double outwardPush = 0;
            float localEdgeNormPos = 0.5f;

            bool inVerticalCorner = relY < CornerDeadZonePixels || relY > (currentMon.Height - CornerDeadZonePixels);
            bool inHorizontalCorner = relX < CornerDeadZonePixels || relX > (currentMon.Width - CornerDeadZonePixels);

            if (relX <= 1 && deltaX < 0 && !inVerticalCorner)
            {
                hitEdge = ScreenEdge.Left;
                outwardPush = -deltaX;
                localEdgeNormPos = Math.Clamp((float)relY / currentMon.Height, 0f, 1f);
            }
            else if (relX >= currentMon.Width - 2 && deltaX > 0 && !inVerticalCorner)
            {
                hitEdge = ScreenEdge.Right;
                outwardPush = deltaX;
                localEdgeNormPos = Math.Clamp((float)relY / currentMon.Height, 0f, 1f);
            }
            else if (relY <= 1 && deltaY < 0 && !inHorizontalCorner)
            {
                hitEdge = ScreenEdge.Top;
                outwardPush = -deltaY;
                localEdgeNormPos = Math.Clamp((float)relX / currentMon.Width, 0f, 1f);
            }
            else if (relY >= currentMon.Height - 2 && deltaY > 0 && !inHorizontalCorner)
            {
                hitEdge = ScreenEdge.Bottom;
                outwardPush = deltaY;
                localEdgeNormPos = Math.Clamp((float)relX / currentMon.Width, 0f, 1f);
            }

            if (hitEdge == ScreenEdge.None)
            {
                if (relX > 6 && relX < currentMon.Width - 7 && relY > 6 && relY < currentMon.Height - 7)
                {
                    ResetPush();
                }
                return default;
            }

            // 2. Critical Multi-Monitor Rule: Is this hit point an INTERNAL SEAM touching another local monitor?
            // If so, the OS moves the cursor naturally across the physical monitors of this computer; NEVER switch to a peer!
            if (IsPointOnInternalMonitorSeam(currentMon, hitEdge, cursorX, cursorY))
            {
                ResetPush();
                return default;
            }

            // 3. Search for a mutually-paired device assigned to this outer edge and this physical monitor
            var targetPeer = FindMatchingPeerOnEdgeLocked(hitEdge, localEdgeNormPos, currentMon.MonitorId);
            if (targetPeer == null)
            {
                ResetPush();
                return default;
            }

            if (_pushingEdge != hitEdge || _pushingMonitorId != currentMon.MonitorId)
            {
                _pushingEdge = hitEdge;
                _pushingMonitorId = currentMon.MonitorId;
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
                    NormalizedPosition: peerRelativePos,
                    SourceMonitorId: currentMon.MonitorId);
            }

            return default;
        }
    }

    private PeerDeviceNode? FindMatchingPeerOnEdgeLocked(ScreenEdge edge, float localEdgeNormPos, string? monitorId = null)
    {
        // Only mutually paired devices can receive edge transitions
        var candidates = _peersById.Values
            .Where(p => p.IsMutuallyPaired && p.AssignedEdgeOnLocal == edge)
            .ToList();

        if (candidates.Count == 0)
            return null;

        // If a specific physical monitor is specified, prioritize peers attached to that monitor
        if (!string.IsNullOrEmpty(monitorId))
        {
            var onThisMonitor = candidates.Where(p =>
                string.IsNullOrEmpty(p.AttachedLocalMonitorId) ||
                p.AttachedLocalMonitorId == monitorId ||
                p.AttachedLocalMonitorId == "ALL").ToList();

            if (onThisMonitor.Count > 0)
            {
                candidates = onThisMonitor;
            }
        }

        // Look for exact segment match [EdgeOffsetStart .. EdgeOffsetEnd]
        var exact = candidates.FirstOrDefault(
            p => localEdgeNormPos >= p.EdgeOffsetStart - 0.01f && localEdgeNormPos <= p.EdgeOffsetEnd + 0.01f);

        return exact;
    }

    /// <summary>
    /// Computes the exact pixel coordinate on the local multi-monitor virtual desktop when the cursor returns
    /// from a remote device back to the host computer. Accurately lands on the assigned physical monitor
    /// (including monitors with negative virtual desktop coordinates).
    /// </summary>
    public (int X, int Y) ComputeLocalEntryPoint(
        ScreenEdge localEntranceEdge,
        float peerNormalizedPosition,
        PeerDeviceNode? returningPeer = null)
    {
        lock (_sync)
        {
            ScreenEdge effectiveEdge = localEntranceEdge;
            if (effectiveEdge == ScreenEdge.None && returningPeer?.AssignedEdgeOnLocal is { } edge && edge != ScreenEdge.None)
            {
                effectiveEdge = edge;
            }

            PhysicalMonitorDescriptor? targetMon = null;

            if (!string.IsNullOrEmpty(returningPeer?.AttachedLocalMonitorId))
            {
                targetMon = _localMonitors.FirstOrDefault(m => m.MonitorId == returningPeer.AttachedLocalMonitorId);
            }

            targetMon ??= GetOutermostMonitorForEdge(effectiveEdge);

            float segStart = returningPeer?.EdgeOffsetStart ?? 0.0f;
            float segEnd = returningPeer?.EdgeOffsetEnd ?? 1.0f;
            float mappedNorm = segStart + Math.Clamp(peerNormalizedPosition, 0.0f, 1.0f) * (segEnd - segStart);
            float t = Math.Clamp(mappedNorm, 0.02f, 0.98f);
            const int insetPixels = 8;

            return effectiveEdge switch
            {
                ScreenEdge.Left => (
                    targetMon.VirtualX + insetPixels,
                    targetMon.VirtualY + (int)Math.Round(t * targetMon.Height)),
                ScreenEdge.Right => (
                    targetMon.Right - insetPixels,
                    targetMon.VirtualY + (int)Math.Round(t * targetMon.Height)),
                ScreenEdge.Top => (
                    targetMon.VirtualX + (int)Math.Round(t * targetMon.Width),
                    targetMon.VirtualY + insetPixels),
                ScreenEdge.Bottom => (
                    targetMon.VirtualX + (int)Math.Round(t * targetMon.Width),
                    targetMon.Bottom - insetPixels),
                _ => (targetMon.VirtualX + targetMon.Width / 2, targetMon.VirtualY + targetMon.Height / 2)
            };
        }
    }

    private void ResetPush()
    {
        _accumulatedPush = 0;
        _pushingEdge = ScreenEdge.None;
        _pushingMonitorId = string.Empty;
    }
}
