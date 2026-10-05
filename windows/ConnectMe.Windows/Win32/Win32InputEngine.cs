using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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

    private bool _shiftDown;
    private bool _ctrlDown;
    private bool _altDown;
    private bool _winDown;

    private PeerDeviceNode? _controllingRemotePeer;
    private ScreenEdge _incomingEntryEdge = ScreenEdge.None;
    private double _incomingPushAccum;
    private DateTimeOffset _lastReturnToLocalTime;

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
        _network.PeerDisconnected += OnPeerDisconnected;
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
        SetCursorPos(_anchorX, _anchorY);

        // Estimate remote cursor coordinates on entry
        peer.RemoteCursorX = targetEntranceEdge switch
        {
            ScreenEdge.Left => 15,
            ScreenEdge.Right => Math.Max(0, peer.ScreenWidth - 15),
            ScreenEdge.Top => (int)(peer.ScreenWidth * Math.Clamp(normalizedPosition, 0.05f, 0.95f)),
            ScreenEdge.Bottom => (int)(peer.ScreenWidth * Math.Clamp(normalizedPosition, 0.05f, 0.95f)),
            _ => peer.ScreenWidth / 2
        };
        peer.RemoteCursorY = targetEntranceEdge switch
        {
            ScreenEdge.Top => 15,
            ScreenEdge.Bottom => Math.Max(0, peer.ScreenHeight - 15),
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

        if (localEntranceEdge != ScreenEdge.None)
        {
            var (entryX, entryY) = _topology.ComputeLocalEntryPoint(localEntranceEdge, normalizedPosition, returningPeer);
            _lastCursorX = entryX;
            _lastCursorY = entryY;
            SetCursorPos(entryX, entryY);
        }
        else
        {
            _lastCursorX = _anchorX;
            _lastCursorY = _anchorY;
            SetCursorPos(_anchorX, _anchorY);
        }

        ActiveTargetChanged?.Invoke(null, localEntranceEdge, normalizedPosition);
    }

    private void OnRemoteEdgeHandOffReceived(EdgeHandOffPacket packet, System.Net.IPEndPoint sender)
    {
        if (ActiveRemotePeer != null)
        {
            ReturnControlToLocal(packet.TargetEntranceEdge, packet.NormalizedPosition, notifyPeer: false);
            return;
        }

        if (DateTimeOffset.UtcNow - _lastReturnToLocalTime < TimeSpan.FromMilliseconds(500))
            return;

        // Receiving secondary mode: remote peer is handing cursor control over to this machine
        string senderIp = sender.Address.ToString();
        var peer = _network.DiscoveredPeers.FirstOrDefault(p => p.IpAddress == senderIp)
                   ?? _network.DiscoveredPeers.FirstOrDefault(p => p.IsMutuallyPaired);

        _controllingRemotePeer = peer;
        _incomingEntryEdge = packet.TargetEntranceEdge;
        _incomingPushAccum = 0;

        if (packet.TargetEntranceEdge != ScreenEdge.None)
        {
            var (entryX, entryY) = _topology.ComputeLocalEntryPoint(packet.TargetEntranceEdge, packet.NormalizedPosition, peer);
            _lastCursorX = entryX;
            _lastCursorY = entryY;
            SetCursorPos(entryX, entryY);
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

        // Check if pushing against the entrance edge to return control to the remote host
        if (_controllingRemotePeer != null && _incomingEntryEdge != ScreenEdge.None)
        {
            var mon = _topology.FindMonitorContainingPoint(pt.X, pt.Y) ?? _topology.FindMonitorNearestPoint(pt.X, pt.Y);
            bool pushHit = false;

            switch (_incomingEntryEdge)
            {
                case ScreenEdge.Left:
                    if (pt.X <= mon.VirtualX + 2 && packet.DeltaX < 0)
                    {
                        _incomingPushAccum += -packet.DeltaX;
                        pushHit = true;
                    }
                    else if (packet.DeltaX > 2)
                    {
                        _incomingPushAccum = 0;
                    }
                    break;

                case ScreenEdge.Right:
                    if (pt.X >= mon.Right - 3 && packet.DeltaX > 0)
                    {
                        _incomingPushAccum += packet.DeltaX;
                        pushHit = true;
                    }
                    else if (packet.DeltaX < -2)
                    {
                        _incomingPushAccum = 0;
                    }
                    break;

                case ScreenEdge.Top:
                    if (pt.Y <= mon.VirtualY + 2 && packet.DeltaY < 0)
                    {
                        _incomingPushAccum += -packet.DeltaY;
                        pushHit = true;
                    }
                    else if (packet.DeltaY > 2)
                    {
                        _incomingPushAccum = 0;
                    }
                    break;

                case ScreenEdge.Bottom:
                    if (pt.Y >= mon.Bottom - 3 && packet.DeltaY > 0)
                    {
                        _incomingPushAccum += packet.DeltaY;
                        pushHit = true;
                    }
                    else if (packet.DeltaY < -2)
                    {
                        _incomingPushAccum = 0;
                    }
                    break;
            }

            if (pushHit && _incomingPushAccum >= 8)
            {
                var targetPeer = _controllingRemotePeer;
                var entranceOnHost = SpatialTopologyEngine.GetOppositeEdge(_incomingEntryEdge);
                float norm = _incomingEntryEdge switch
                {
                    ScreenEdge.Left or ScreenEdge.Right => Math.Clamp((float)(pt.Y - mon.VirtualY) / Math.Max(1, mon.Height), 0f, 1f),
                    ScreenEdge.Top or ScreenEdge.Bottom => Math.Clamp((float)(pt.X - mon.VirtualX) / Math.Max(1, mon.Width), 0f, 1f),
                    _ => 0.5f
                };

                _controllingRemotePeer = null;
                _incomingEntryEdge = ScreenEdge.None;
                _incomingPushAccum = 0;

                _network.SendEdgeReturnToPeer(targetPeer, entranceOnHost, norm);
                return;
            }
        }

        var (clampedX, clampedY) = _topology.ClampToVirtualDesktop(newX, newY);
        _lastCursorX = clampedX;
        _lastCursorY = clampedY;
        SetCursorPos(clampedX, clampedY);
        mouse_event(0x0001u, packet.DeltaX, packet.DeltaY, 0, (UIntPtr)0xFFFFFF);
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
                if (_controllingRemotePeer != null)
                {
                    _controllingRemotePeer = null;
                    _incomingPushAccum = 0;
                }

                int msg = wParam.ToInt32();
                var remote = ActiveRemotePeer;

                if (remote == null)
                {
                    // Local Windows mode: check if cursor hits an active outer edge toward Android/Linux
                    if (msg == WM_MOUSEMOVE)
                    {
                        int dx = info.pt.X - _lastCursorX;
                        int dy = info.pt.Y - _lastCursorY;

                        var (clampedX, clampedY) = _topology.ClampToVirtualDesktop(info.pt.X, info.pt.Y);

                        _lastCursorX = clampedX;
                        _lastCursorY = clampedY;

                        var transition = _topology.EvaluateCursorStep(clampedX, clampedY, dx, dy);
                        if (transition.ShouldTransition && transition.TargetPeer != null)
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
                                SetCursorPos(_anchorX, _anchorY);
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
