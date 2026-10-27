using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using ConnectMe.Core.Network;
using ConnectMe.Core.Protocol;
using ConnectMe.Core.Topology;

namespace ConnectMe.Windows.Win32;

/// <summary>
/// Low-level Win32 Mouse & Keyboard Hook Engine (WH_MOUSE_LL / WH_KEYBOARD_LL)
/// and SendInput injector for seamless cross-device KVM switching.
/// </summary>
public sealed class Win32InputEngine : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    private const ushort HID_USAGE_GENERIC_MOUSE = 0x02;

    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP = 0x0208;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_XBUTTONUP = 0x020C;
    private const int WM_MOUSEHWHEEL = 0x020E;

    private const uint LLMHF_INJECTED = 0x00000001;
    private const uint LLKHF_INJECTED = 0x00000010;

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12; // Alt
    private const int VK_ESCAPE = 0x1B;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_SCROLL = 0x91;
    private const int VK_L = 0x4C;

    private readonly SpatialTopologyEngine _topology;
    private readonly ConnectMeNetworkNode _network;
    private readonly LowLevelProc _mouseProc;
    private readonly LowLevelProc _keyboardProc;

    private IntPtr _mouseHookId = IntPtr.Zero;
    private IntPtr _keyboardHookId = IntPtr.Zero;

    private int _lastCursorX;
    private int _lastCursorY;
    private int _anchorX;
    private int _anchorY;
    private int _lastRemoteAnchorX;
    private int _lastRemoteAnchorY;

    private bool _shiftDown;
    private bool _ctrlDown;
    private bool _altDown;
    private bool _winDown;

    private PeerDeviceNode? _controllingRemotePeer;
    private ScreenEdge _incomingEntryEdge = ScreenEdge.None;
    private double _incomingPushAccum;
    private DateTimeOffset _lastReturnToLocalTime;
    private DateTimeOffset _suppressAnchorJitterUntil = DateTimeOffset.MinValue;
    private int _suppressedEntryX;
    private int _suppressedEntryY;
    private long _lastInjectedCursorTime;

    private bool _rawInputActive;
    private IntPtr _attachedHwnd = IntPtr.Zero;
    private HwndSourceHook? _hwndSourceHook;

    public PeerDeviceNode? ActiveRemotePeer { get; private set; }
    public bool IsHooksInstalled => _mouseHookId != IntPtr.Zero && _keyboardHookId != IntPtr.Zero;

    public event Action<PeerDeviceNode?, ScreenEdge, float>? ActiveTargetChanged;
    public event Action<bool>? ScreenLockToggled;

    public Win32InputEngine(SpatialTopologyEngine topology, ConnectMeNetworkNode network)
    {
        _topology = topology;
        _network = network;

        // Keep delegate references alive so GC never collects hook callbacks
        _mouseProc = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;

        _network.RemoteEdgeHandOffReceived += OnRemoteEdgeHandOffReceived;
        _network.RemoteMouseMoveReceived += OnRemoteMouseMoveReceived;
        _network.RemoteMouseButtonReceived += OnRemoteMouseButtonReceived;
        _network.RemoteMouseScrollReceived += OnRemoteMouseScrollReceived;
        _network.RemoteKeyEventReceived += OnRemoteKeyEventReceived;
        _network.RemoteEdgeRelayRequested += OnRemoteEdgeRelayRequested;
        _network.PeerDisconnected += OnPeerDisconnected;
    }

    /// <summary>
    /// Attaches to the main window handle to receive high-precision Raw Input (WM_INPUT) directly from mouse hardware.
    /// Completely bypasses virtual cursor coordinates and eliminates jitter / reverse synthetic deltas.
    /// </summary>
    public void AttachToWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        _attachedHwnd = hwnd;

        try
        {
            var rids = new RAWINPUTDEVICE[]
            {
                new RAWINPUTDEVICE
                {
                    usUsagePage = HID_USAGE_PAGE_GENERIC,
                    usUsage = HID_USAGE_GENERIC_MOUSE,
                    dwFlags = RIDEV_INPUTSINK,
                    hwndTarget = hwnd
                }
            };

            bool success = RegisterRawInputDevices(rids, (uint)rids.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            if (success)
            {
                var source = HwndSource.FromHwnd(hwnd);
                if (source != null)
                {
                    _hwndSourceHook = RawInputHookCallback;
                    source.AddHook(_hwndSourceHook);
                    _rawInputActive = true;
                }
            }
        }
        catch { }
    }

    private IntPtr RawInputHookCallback(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_INPUT)
        {
            ProcessRawInput(lParam);
        }
        return IntPtr.Zero;
    }

    private void ProcessRawInput(IntPtr hRawInput)
    {
        var remote = ActiveRemotePeer;
        if (remote == null) return;

        uint dwSize = 0;
        int headerSize = Marshal.SizeOf<RAWINPUTHEADER>();
        GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref dwSize, (uint)headerSize);
        if (dwSize == 0) return;

        byte[] rawBuffer = new byte[dwSize];
        unsafe
        {
            fixed (byte* pBuf = rawBuffer)
            {
                if (GetRawInputData(hRawInput, RID_INPUT, (IntPtr)pBuf, ref dwSize, (uint)headerSize) == dwSize)
                {
                    uint dwType = *(uint*)pBuf;
                    if (dwType == 0 /* RIM_TYPEMOUSE */)
                    {
                        int offset = IntPtr.Size == 8 ? 24 : 16;
                        ushort flags = *(ushort*)(pBuf + offset);

                        // Only process relative mouse moves from physical sensor
                        if ((flags & 0x01 /* MOUSE_MOVE_ABSOLUTE */) == 0)
                        {
                            int dx = *(int*)(pBuf + offset + 12);
                            int dy = *(int*)(pBuf + offset + 16);

                            if (dx != 0 || dy != 0)
                            {
                                remote.RemoteCursorX = Math.Clamp(remote.RemoteCursorX + dx, 0, remote.ScreenWidth);
                                remote.RemoteCursorY = Math.Clamp(remote.RemoteCursorY + dy, 0, remote.ScreenHeight);

                                _network.SendMouseMove(
                                    remote,
                                    (short)Math.Clamp(dx, short.MinValue, short.MaxValue),
                                    (short)Math.Clamp(dy, short.MinValue, short.MaxValue));
                            }
                        }
                    }
                }
            }
        }
    }

    public void StartHooks()
    {
        if (IsHooksInstalled)
            return;

        RefreshMonitorBounds();
        if (GetCursorPos(out POINT pt))
        {
            _lastCursorX = pt.X;
            _lastCursorY = pt.Y;
        }

        using Process curProcess = Process.GetCurrentProcess();
        using ProcessModule? curModule = curProcess.MainModule;
        IntPtr hMod = curModule != null ? GetModuleHandle(curModule.ModuleName) : IntPtr.Zero;

        _mouseHookId = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);
        _keyboardHookId = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, hMod, 0);
    }

    public void StopHooks()
    {
        if (_mouseHookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHookId);
            _mouseHookId = IntPtr.Zero;
        }

        if (_keyboardHookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHookId);
            _keyboardHookId = IntPtr.Zero;
        }

        ActiveRemotePeer = null;
    }

    public void RefreshMonitorBounds()
    {
        var monitors = Win32MonitorEnumerator.EnumerateLocalMonitors();
        _topology.UpdateLocalMonitors(monitors);
        _network.LocalMonitors = monitors;
        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        _network.LocalScreenWidth = primary.Width;
        _network.LocalScreenHeight = primary.Height;
    }

    /// <summary>
    /// Manually or automatically switches input focus to the specified peer.
    /// </summary>
    public void SwitchControlToPeer(PeerDeviceNode peer, ScreenEdge localExitEdge, ScreenEdge targetEntranceEdge, float normalizedPosition)
    {
        ActiveRemotePeer = peer;
        var primary = _topology.LocalMonitors.FirstOrDefault(m => m.IsPrimary)
                      ?? _topology.LocalMonitors.FirstOrDefault()
                      ?? new PhysicalMonitorDescriptor { MonitorId = "P", Name = "Monitör P", VirtualX = 0, VirtualY = 0, Width = 1920, Height = 1080, IsPrimary = true };
        _anchorX = primary.VirtualX + primary.Width / 2;
        _anchorY = primary.VirtualY + primary.Height / 2;
        _lastRemoteAnchorX = _anchorX;
        _lastRemoteAnchorY = _anchorY;
        SetCursorPos(_anchorX, _anchorY);

        // Estimate remote cursor coordinates on entry (with comfortable 35px margin away from frame border)
        peer.RemoteCursorX = targetEntranceEdge switch
        {
            ScreenEdge.Left => 35,
            ScreenEdge.Right => Math.Max(0, peer.ScreenWidth - 35),
            ScreenEdge.Top => (int)(peer.ScreenWidth * Math.Clamp(normalizedPosition, 0.05f, 0.95f)),
            ScreenEdge.Bottom => (int)(peer.ScreenWidth * Math.Clamp(normalizedPosition, 0.05f, 0.95f)),
            _ => peer.ScreenWidth / 2
        };
        peer.RemoteCursorY = targetEntranceEdge switch
        {
            ScreenEdge.Top => 35,
            ScreenEdge.Bottom => Math.Max(0, peer.ScreenHeight - 35),
            ScreenEdge.Left => (int)(peer.ScreenHeight * Math.Clamp(normalizedPosition, 0.05f, 0.95f)),
            ScreenEdge.Right => (int)(peer.ScreenHeight * Math.Clamp(normalizedPosition, 0.05f, 0.95f)),
            _ => peer.ScreenHeight / 2
        };

        _network.SendEdgeHandOff(peer, targetEntranceEdge, normalizedPosition);
        ActiveTargetChanged?.Invoke(peer, localExitEdge, normalizedPosition);
    }

    /// <summary>
    /// Returns control back to the local Windows screen at the specified entrance edge and normalized coordinate.
    /// </summary>
    public void ReturnControlToLocal(ScreenEdge localEntranceEdge, float normalizedPosition, bool notifyPeer = false)
    {
        var returningPeer = ActiveRemotePeer;
        if (returningPeer == null)
            return;

        ActiveRemotePeer = null;
        _lastReturnToLocalTime = DateTimeOffset.UtcNow;

        // Release any held modifiers to ensure Windows host doesn't retain stuck keys
        ReleaseHeldModifiers();

        if (notifyPeer)
        {
            _network.SendEdgeReturnToPeer(returningPeer, localEntranceEdge, normalizedPosition);
        }

        ScreenEdge effectiveEdge = localEntranceEdge;
        if (effectiveEdge == ScreenEdge.None && returningPeer.AssignedEdgeOnLocal != ScreenEdge.None)
        {
            effectiveEdge = returningPeer.AssignedEdgeOnLocal;
        }

        float effectiveNorm = normalizedPosition;
        if ((Math.Abs(effectiveNorm - 0.5f) < 0.001f || effectiveNorm <= 0.0f || effectiveNorm >= 1.0f) &&
            returningPeer.ScreenWidth > 0 && returningPeer.ScreenHeight > 0)
        {
            if (effectiveEdge == ScreenEdge.Left || effectiveEdge == ScreenEdge.Right)
            {
                effectiveNorm = (float)returningPeer.RemoteCursorY / returningPeer.ScreenHeight;
            }
            else if (effectiveEdge == ScreenEdge.Top || effectiveEdge == ScreenEdge.Bottom)
            {
                effectiveNorm = (float)returningPeer.RemoteCursorX / returningPeer.ScreenWidth;
            }
        }
        effectiveNorm = Math.Clamp(effectiveNorm, 0.02f, 0.98f);

        var (entryX, entryY) = _topology.ComputeLocalEntryPoint(effectiveEdge, effectiveNorm, returningPeer);

        // Safe return margin: Place cursor 30 pixels INSIDE the Windows screen so it doesn't immediately bounce back
        int safeEntryX = entryX;
        int safeEntryY = entryY;
        switch (effectiveEdge)
        {
            case ScreenEdge.Left:
                safeEntryX = entryX + 30;
                break;
            case ScreenEdge.Right:
                safeEntryX = entryX - 30;
                break;
            case ScreenEdge.Top:
                safeEntryY = entryY + 30;
                break;
            case ScreenEdge.Bottom:
                safeEntryY = entryY - 30;
                break;
        }

        _lastCursorX = safeEntryX;
        _lastCursorY = safeEntryY;
        _suppressedEntryX = safeEntryX;
        _suppressedEntryY = safeEntryY;
        _suppressAnchorJitterUntil = DateTimeOffset.UtcNow.AddMilliseconds(400);
        _lastInjectedCursorTime = Environment.TickCount64;

        SetCursorPos(safeEntryX, safeEntryY);

        ActiveTargetChanged?.Invoke(null, effectiveEdge, effectiveNorm);
    }

    private void OnRemoteEdgeHandOffReceived(EdgeHandOffPacket packet, System.Net.IPEndPoint sender)
    {
        if (ActiveRemotePeer != null)
        {
            ReturnControlToLocal(packet.TargetEntranceEdge, packet.NormalizedPosition, notifyPeer: false);
            return;
        }

        if (DateTimeOffset.UtcNow - _lastReturnToLocalTime < TimeSpan.FromMilliseconds(150))
            return;

        // Receiving secondary mode: remote peer is handing cursor control over to this machine
        string senderIp = sender.Address.IsIPv4MappedToIPv6
            ? sender.Address.MapToIPv4().ToString()
            : sender.Address.ToString();

        var peer = _network.DiscoveredPeers.FirstOrDefault(p => p.IpAddress == senderIp)
                   ?? _network.DiscoveredPeers.FirstOrDefault(p => p.IsMutuallyPaired);

        _controllingRemotePeer = peer;
        _incomingEntryEdge = packet.TargetEntranceEdge;
        _incomingPushAccum = 0;

        ScreenEdge entranceEdge = packet.TargetEntranceEdge;
        if (entranceEdge == ScreenEdge.None && peer?.AssignedEdgeOnLocal is { } edge && edge != ScreenEdge.None)
        {
            entranceEdge = edge;
        }

        if (entranceEdge != ScreenEdge.None)
        {
            var (entryX, entryY) = _topology.ComputeLocalEntryPoint(entranceEdge, packet.NormalizedPosition, peer);
            _lastCursorX = entryX;
            _lastCursorY = entryY;
            _lastInjectedCursorTime = Environment.TickCount64;
            SetCursorPos(entryX, entryY);
        }
    }

    private void OnRemoteEdgeRelayRequested(string relayTargetDeviceId, ScreenEdge returnEdge, float normPos)
    {
        var targetPeer = _network.DiscoveredPeers.FirstOrDefault(p =>
            string.Equals(p.DeviceId, relayTargetDeviceId, StringComparison.OrdinalIgnoreCase) && p.IsMutuallyPaired);

        if (targetPeer != null)
        {
            SwitchControlToPeer(targetPeer, ScreenEdge.None, returnEdge, normPos);
        }
    }

    private void OnRemoteMouseMoveReceived(MouseMovePacket packet)
    {
        if (ActiveRemotePeer != null)
            return;

        if (!GetCursorPos(out POINT pt))
            return;

        int newX = pt.X + packet.DeltaX;
        int newY = pt.Y + packet.DeltaY;

        // Check if pushing against edges when being controlled remotely
        if (_controllingRemotePeer != null)
        {
            var mon = _topology.FindMonitorContainingPoint(pt.X, pt.Y) ?? _topology.FindMonitorNearestPoint(pt.X, pt.Y);

            ScreenEdge pushingEdge = ScreenEdge.None;
            double pushAmount = 0;

            if (pt.X <= mon.VirtualX + 2 && packet.DeltaX < 0)
            {
                pushingEdge = ScreenEdge.Left;
                pushAmount = -packet.DeltaX;
            }
            else if (pt.X >= mon.Right - 3 && packet.DeltaX > 0)
            {
                pushingEdge = ScreenEdge.Right;
                pushAmount = packet.DeltaX;
            }
            else if (pt.Y <= mon.VirtualY + 2 && packet.DeltaY < 0)
            {
                pushingEdge = ScreenEdge.Top;
                pushAmount = -packet.DeltaY;
            }
            else if (pt.Y >= mon.Bottom - 3 && packet.DeltaY > 0)
            {
                pushingEdge = ScreenEdge.Bottom;
                pushAmount = packet.DeltaY;
            }

            if (pushingEdge != ScreenEdge.None)
            {
                _incomingPushAccum += pushAmount;

                float norm = pushingEdge switch
                {
                    ScreenEdge.Left or ScreenEdge.Right => Math.Clamp((float)(pt.Y - mon.VirtualY) / Math.Max(1, mon.Height), 0f, 1f),
                    ScreenEdge.Top or ScreenEdge.Bottom => Math.Clamp((float)(pt.X - mon.VirtualX) / Math.Max(1, mon.Width), 0f, 1f),
                    _ => 0.5f
                };

                if (_incomingPushAccum >= 24)
                {
                    // Case A: Pushing against the entrance edge from the Host -> return to Host!
                    if (pushingEdge == _incomingEntryEdge)
                    {
                        var targetPeer = _controllingRemotePeer;
                        var entranceOnHost = SpatialTopologyEngine.GetOppositeEdge(_incomingEntryEdge);

                        _controllingRemotePeer = null;
                        _incomingEntryEdge = ScreenEdge.None;
                        _incomingPushAccum = 0;

                        _network.SendEdgeReturnToPeer(targetPeer, entranceOnHost, norm);
                        return;
                    }
                    else
                    {
                        // Case B: Pushing against another edge that has an adjacent peer in the colony mesh!
                        var adjPeer = _topology.FindAdjacentPeerOnEdge(_network.LocalDeviceId, pushingEdge, norm);
                        if (adjPeer != null)
                        {
                            var host = _controllingRemotePeer;
                            var entranceOnAdj = SpatialTopologyEngine.GetOppositeEdge(pushingEdge);

                            _controllingRemotePeer = null;
                            _incomingEntryEdge = ScreenEdge.None;
                            _incomingPushAccum = 0;

                            _network.SendEdgeRelayToHost(host, adjPeer.DeviceId, entranceOnAdj, norm);
                            return;
                        }
                    }
                }
            }
            else if (Math.Abs(packet.DeltaX) > 2 || Math.Abs(packet.DeltaY) > 2)
            {
                _incomingPushAccum = 0;
            }
        }

        var (clampedX, clampedY) = _topology.ClampToVirtualDesktop(newX, newY);
        _lastCursorX = clampedX;
        _lastCursorY = clampedY;
        _lastInjectedCursorTime = Environment.TickCount64;
        SetCursorPos(clampedX, clampedY);
    }

    private void OnRemoteMouseButtonReceived(MouseButtonPacket packet)
    {
        if (ActiveRemotePeer != null)
            return;

        uint flag = 0;
        uint data = 0;

        switch (packet.Button, packet.IsPressed)
        {
            case (MouseButtonCode.Left, true):
                flag = 0x0002u; // MOUSEEVENTF_LEFTDOWN
                break;
            case (MouseButtonCode.Left, false):
                flag = 0x0004u; // MOUSEEVENTF_LEFTUP
                break;
            case (MouseButtonCode.Right, true):
                flag = 0x0008u; // MOUSEEVENTF_RIGHTDOWN
                break;
            case (MouseButtonCode.Right, false):
                flag = 0x0010u; // MOUSEEVENTF_RIGHTUP
                break;
            case (MouseButtonCode.Middle, true):
                flag = 0x0020u; // MOUSEEVENTF_MIDDLEDOWN
                break;
            case (MouseButtonCode.Middle, false):
                flag = 0x0040u; // MOUSEEVENTF_MIDDLEUP
                break;
            case (MouseButtonCode.XButton1, true):
                flag = 0x0080u; // MOUSEEVENTF_XDOWN
                data = 1;
                break;
            case (MouseButtonCode.XButton1, false):
                flag = 0x0100u; // MOUSEEVENTF_XUP
                data = 1;
                break;
            case (MouseButtonCode.XButton2, true):
                flag = 0x0080u; // MOUSEEVENTF_XDOWN
                data = 2;
                break;
            case (MouseButtonCode.XButton2, false):
                flag = 0x0100u; // MOUSEEVENTF_XUP
                data = 2;
                break;
        }

        if (flag != 0)
            mouse_event(flag, 0, 0, data, UIntPtr.Zero);
    }

    private void OnRemoteMouseScrollReceived(MouseScrollPacket packet)
    {
        if (ActiveRemotePeer != null)
            return;

        if (packet.ScrollY != 0)
            mouse_event(0x0800u, 0, 0, unchecked((uint)packet.ScrollY), UIntPtr.Zero); // MOUSEEVENTF_WHEEL
        if (packet.ScrollX != 0)
            mouse_event(0x1000u, 0, 0, unchecked((uint)packet.ScrollX), UIntPtr.Zero); // MOUSEEVENTF_HWHEEL
    }

    private void OnRemoteKeyEventReceived(KeyEventPacket packet)
    {
        if (ActiveRemotePeer != null)
            return;

        uint dwFlags = packet.IsPressed ? 0u : KEYEVENTF_KEYUP;
        byte vk = (byte)packet.VirtualKey;
        byte scan = (byte)packet.ScanCode;

        if (vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0x6F or 0x90 or 0xA3 or 0xA5)
        {
            dwFlags |= 0x0001; // KEYEVENTF_EXTENDEDKEY
        }

        keybd_event(vk, scan, dwFlags, UIntPtr.Zero);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            // Ignore self-injected events
            if ((info.flags & LLMHF_INJECTED) == 0)
            {
                // If this is a WM_MOUSEMOVE caused by our SetCursorPos injection within 120ms, ignore it!
                if (Environment.TickCount64 - _lastInjectedCursorTime < 120 &&
                    Math.Abs(info.pt.X - _lastCursorX) <= 2 &&
                    Math.Abs(info.pt.Y - _lastCursorY) <= 2)
                {
                    return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
                }

                if (_controllingRemotePeer != null)
                {
                    _controllingRemotePeer = null;
                    _incomingPushAccum = 0;
                }

                int msg = wParam.ToInt32();
                var remote = ActiveRemotePeer;

                if (remote == null)
                {
                    // If we just returned from remote control, Windows message queue might still contain
                    // stale hardware mouse move events that were generated while the cursor was pinned at the screen center (_anchorX, _anchorY).
                    // Suppress those stale anchor-centered mouse events so they don't snap the cursor back to screen center!
                    if (DateTimeOffset.UtcNow < _suppressAnchorJitterUntil)
                    {
                        int dxAnchor = info.pt.X - _anchorX;
                        int dyAnchor = info.pt.Y - _anchorY;
                        int distAnchorSq = dxAnchor * dxAnchor + dyAnchor * dyAnchor;
                        if (distAnchorSq < 160 * 160)
                        {
                            SetCursorPos(_suppressedEntryX, _suppressedEntryY);
                            _lastCursorX = _suppressedEntryX;
                            _lastCursorY = _suppressedEntryY;
                            return (IntPtr)1;
                        }
                    }

                    // Local Windows mode: check if cursor hits an active outer edge toward Android/Linux
                    if (_controllingRemotePeer == null && msg == WM_MOUSEMOVE)
                    {
                        int dx = info.pt.X - _lastCursorX;
                        int dy = info.pt.Y - _lastCursorY;

                        var (clampedX, clampedY) = _topology.ClampToVirtualDesktop(info.pt.X, info.pt.Y);

                        _lastCursorX = clampedX;
                        _lastCursorY = clampedY;

                        var transition = _topology.EvaluateCursorStep(clampedX, clampedY, dx, dy);
                        if (transition.ShouldTransition && transition.TargetPeer != null &&
                            (DateTimeOffset.UtcNow - _lastReturnToLocalTime > TimeSpan.FromMilliseconds(400)))
                        {
                            SwitchControlToPeer(
                                transition.TargetPeer,
                                transition.LocalExitEdge,
                                transition.TargetEntranceEdge,
                                transition.NormalizedPosition);
                            return (IntPtr)1;
                        }
                    }
                }
                else
                {
                    // Remote Control Mode (Controlling Android or Nobara Linux!)
                    switch (msg)
                    {
                        case WM_MOUSEMOVE:
                        {
                            // Keep local Windows cursor anchored at screen center (_anchorX, _anchorY)
                            SetCursorPos(_anchorX, _anchorY);

                            if (!_rawInputActive)
                            {
                                // Fallback when RawInput is not available (e.g. headless tests)
                                if (info.pt.X == _anchorX && info.pt.Y == _anchorY)
                                {
                                    _lastRemoteAnchorX = _anchorX;
                                    _lastRemoteAnchorY = _anchorY;
                                    return (IntPtr)1;
                                }

                                int dx = info.pt.X - _anchorX;
                                int dy = info.pt.Y - _anchorY;
                                if (dx != 0 || dy != 0)
                                {
                                    remote.RemoteCursorX = Math.Clamp(remote.RemoteCursorX + dx, 0, remote.ScreenWidth);
                                    remote.RemoteCursorY = Math.Clamp(remote.RemoteCursorY + dy, 0, remote.ScreenHeight);

                                    _network.SendMouseMove(
                                        remote,
                                        (short)Math.Clamp(dx, short.MinValue, short.MaxValue),
                                        (short)Math.Clamp(dy, short.MinValue, short.MaxValue));
                                }
                            }

                            // Suppress local cursor movement so it remains anchored at (_anchorX, _anchorY)
                            return (IntPtr)1;
                        }

                        case WM_LBUTTONDOWN:
                            _network.SendMouseButton(remote, MouseButtonCode.Left, true);
                            return (IntPtr)1;
                        case WM_LBUTTONUP:
                            _network.SendMouseButton(remote, MouseButtonCode.Left, false);
                            return (IntPtr)1;

                        case WM_RBUTTONDOWN:
                            _network.SendMouseButton(remote, MouseButtonCode.Right, true);
                            return (IntPtr)1;
                        case WM_RBUTTONUP:
                            _network.SendMouseButton(remote, MouseButtonCode.Right, false);
                            return (IntPtr)1;

                        case WM_MBUTTONDOWN:
                            _network.SendMouseButton(remote, MouseButtonCode.Middle, true);
                            return (IntPtr)1;
                        case WM_MBUTTONUP:
                            _network.SendMouseButton(remote, MouseButtonCode.Middle, false);
                            return (IntPtr)1;

                        case WM_XBUTTONDOWN:
                            _network.SendMouseButton(remote, MouseButtonCode.XButton1, true);
                            return (IntPtr)1;
                        case WM_XBUTTONUP:
                            _network.SendMouseButton(remote, MouseButtonCode.XButton1, false);
                            return (IntPtr)1;

                        case WM_MOUSEWHEEL:
                        {
                            short wheelDelta = unchecked((short)((info.mouseData >> 16) & 0xFFFF));
                            _network.SendMouseScroll(remote, 0, wheelDelta);
                            return (IntPtr)1;
                        }

                        case WM_MOUSEHWHEEL:
                        {
                            short hWheelDelta = unchecked((short)((info.mouseData >> 16) & 0xFFFF));
                            _network.SendMouseScroll(remote, hWheelDelta, 0);
                            return (IntPtr)1;
                        }
                    }
                }
            }
        }

        return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((info.flags & LLKHF_INJECTED) == 0)
            {
                int msg = wParam.ToInt32();
                bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                int vk = (int)info.vkCode;

                UpdateModifierState(vk, isDown, isUp);

                // Check Global Safety / Lock Hotkeys:
                // 1) Scroll Lock OR Ctrl+Alt+L -> Return to Windows if remote, or toggle screen lock
                // 2) Ctrl+Alt+Shift+Esc -> Emergency return to Windows
                if (isDown)
                {
                    bool isScrollLock = vk == VK_SCROLL;
                    bool isCtrlAltL = _ctrlDown && _altDown && vk == VK_L;
                    bool isEmergencyEsc = _ctrlDown && _altDown && _shiftDown && vk == VK_ESCAPE;

                    if (isEmergencyEsc || ((isScrollLock || isCtrlAltL) && ActiveRemotePeer != null))
                    {
                        ReturnControlToLocal(ScreenEdge.None, 0.5f, notifyPeer: true);
                        return (IntPtr)1;
                    }

                    if (isScrollLock || isCtrlAltL)
                    {
                        _topology.IsScreenLocked = !_topology.IsScreenLocked;
                        ScreenLockToggled?.Invoke(_topology.IsScreenLocked);
                        if (isCtrlAltL)
                            return (IntPtr)1;
                    }
                }

                var remote = ActiveRemotePeer;
                if (remote != null && (isDown || isUp))
                {
                    var mods = GetCurrentModifiers();
                    char ch = isDown ? TranslateVkToUnicode((uint)vk, info.scanCode) : '\0';

                    _network.SendKeyEvent(
                        remote,
                        (ushort)vk,
                        (ushort)info.scanCode,
                        isDown,
                        mods,
                        ch);

                    // When controlling a remote device, intercept and suppress ALL keystrokes and modifiers
                    // (including Win, Alt, Ctrl) from reaching the Windows host OS so multi-finger gestures
                    // and shortcuts route exclusively to the focused target device.
                    return (IntPtr)1;
                }
            }
        }

        return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
    }

    private void UpdateModifierState(int vk, bool isDown, bool isUp)
    {
        if (!isDown && !isUp)
            return;

        bool state = isDown;
        switch (vk)
        {
            case 0xA0: // VK_LSHIFT
            case 0xA1: // VK_RSHIFT
            case VK_SHIFT:
                _shiftDown = state;
                break;
            case 0xA2: // VK_LCONTROL
            case 0xA3: // VK_RCONTROL
            case VK_CONTROL:
                _ctrlDown = state;
                break;
            case 0xA4: // VK_LMENU
            case 0xA5: // VK_RMENU
            case VK_MENU:
                _altDown = state;
                break;
            case VK_LWIN:
            case VK_RWIN:
                _winDown = state;
                break;
        }
    }

    private static bool IsModifierVk(int vk) =>
        vk is VK_SHIFT or 0xA0 or 0xA1
            or VK_CONTROL or 0xA2 or 0xA3
            or VK_MENU or 0xA4 or 0xA5
            or VK_LWIN or VK_RWIN;

    private KeyModifiers GetCurrentModifiers()
    {
        var mods = KeyModifiers.None;
        if (_shiftDown) mods |= KeyModifiers.Shift;
        if (_ctrlDown) mods |= KeyModifiers.Ctrl;
        if (_altDown) mods |= KeyModifiers.Alt;
        if (_winDown) mods |= KeyModifiers.Meta;
        return mods;
    }

    private char TranslateVkToUnicode(uint vkCode, uint scanCode)
    {
        if (_ctrlDown || _altDown || _winDown)
            return '\0';

        byte[] keyState = new byte[256];
        if (_shiftDown)
            keyState[VK_SHIFT] = 0x80;

        var sb = new StringBuilder(4);
        int res = ToUnicode(vkCode, scanCode, keyState, sb, sb.Capacity, 0);
        return res == 1 && sb.Length > 0 ? sb[0] : '\0';
    }

    private void OnPeerDisconnected(PeerDeviceNode peer)
    {
        if (ActiveRemotePeer == peer || (peer != null && ActiveRemotePeer?.DeviceId == peer.DeviceId))
        {
            ReturnControlToLocal(ScreenEdge.None, 0.5f, notifyPeer: false);
        }

        if (_controllingRemotePeer == peer || (peer != null && _controllingRemotePeer?.DeviceId == peer.DeviceId))
        {
            _controllingRemotePeer = null;
            _incomingPushAccum = 0;
        }
    }

    public void Dispose()
    {
        StopHooks();
        if (_attachedHwnd != IntPtr.Zero && _hwndSourceHook != null)
        {
            try
            {
                var source = HwndSource.FromHwnd(_attachedHwnd);
                source?.RemoveHook(_hwndSourceHook);
            }
            catch { }
            _hwndSourceHook = null;
            _attachedHwnd = IntPtr.Zero;
        }

        _network.RemoteEdgeHandOffReceived -= OnRemoteEdgeHandOffReceived;
        _network.RemoteMouseMoveReceived -= OnRemoteMouseMoveReceived;
        _network.RemoteMouseButtonReceived -= OnRemoteMouseButtonReceived;
        _network.RemoteMouseScrollReceived -= OnRemoteMouseScrollReceived;
        _network.RemoteKeyEventReceived -= OnRemoteKeyEventReceived;
        _network.PeerDisconnected -= OnPeerDisconnected;
    }

    // =========================================================================
    // Win32 Native Interop
    // =========================================================================

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices,
        uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr hRawInput,
        uint uiCommand,
        IntPtr pData,
        ref uint pcbSize,
        uint cbSizeHeader);

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern int ToUnicode(
        uint wVirtKey,
        uint wScanCode,
        byte[] lpKeyState,
        [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszBuff,
        int cchBuff,
        uint wFlags);

    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private void ReleaseHeldModifiers()
    {
        if (_shiftDown)
        {
            keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            _shiftDown = false;
        }
        if (_ctrlDown)
        {
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            _ctrlDown = false;
        }
        if (_altDown)
        {
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            _altDown = false;
        }
        if (_winDown)
        {
            keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_RWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            _winDown = false;
        }
    }
}
