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
        int width = Math.Max(800, GetSystemMetrics(0));  // SM_CXSCREEN
        int height = Math.Max(600, GetSystemMetrics(1)); // SM_CYSCREEN
        _topology.UpdateLocalScreenBounds(0, 0, width, height);
        _network.LocalScreenWidth = width;
        _network.LocalScreenHeight = height;
    }

    /// <summary>
    /// Manually or automatically switches input focus to the specified peer.
    /// </summary>
    public void SwitchControlToPeer(PeerDeviceNode peer, ScreenEdge localExitEdge, ScreenEdge targetEntranceEdge, float normalizedPosition)
    {
        if (GetCursorPos(out POINT pt))
        {
            _anchorX = Math.Clamp(pt.X, _topology.LocalLeft + 2, _topology.LocalLeft + _topology.LocalWidth - 3);
            _anchorY = Math.Clamp(pt.Y, _topology.LocalTop + 2, _topology.LocalTop + _topology.LocalHeight - 3);
            SetCursorPos(_anchorX, _anchorY);
        }

        ActiveRemotePeer = peer;
        _network.SendEdgeHandOff(peer, targetEntranceEdge, normalizedPosition);
        ActiveTargetChanged?.Invoke(peer, localExitEdge, normalizedPosition);
    }

    /// <summary>
    /// Returns control back to the local Windows screen at the specified entrance edge and normalized coordinate.
    /// </summary>
    public void ReturnControlToLocal(ScreenEdge localEntranceEdge, float normalizedPosition)
    {
        if (ActiveRemotePeer == null)
            return;

        ActiveRemotePeer = null;
        if (localEntranceEdge != ScreenEdge.None)
        {
            var (entryX, entryY) = _topology.ComputeLocalEntryPoint(localEntranceEdge, normalizedPosition);
            _lastCursorX = entryX;
            _lastCursorY = entryY;
            SetCursorPos(entryX, entryY);
        }

        ActiveTargetChanged?.Invoke(null, localEntranceEdge, normalizedPosition);
    }

    private void OnRemoteEdgeHandOffReceived(EdgeHandOffPacket packet, System.Net.IPEndPoint sender)
    {
        ReturnControlToLocal(packet.TargetEntranceEdge, packet.NormalizedPosition);
    }

    private void OnRemoteMouseMoveReceived(MouseMovePacket packet)
    {
        if (ActiveRemotePeer != null)
            return;

        if (GetCursorPos(out POINT pt))
        {
            SetCursorPos(pt.X + packet.DeltaX, pt.Y + packet.DeltaY);
        }
    }

    private void OnRemoteMouseButtonReceived(MouseButtonPacket packet)
    {
        if (ActiveRemotePeer != null)
            return;

        uint flag = (packet.Button, packet.IsPressed) switch
        {
            (MouseButtonCode.Left, true) => 0x0002u,   // MOUSEEVENTF_LEFTDOWN
            (MouseButtonCode.Left, false) => 0x0004u,  // MOUSEEVENTF_LEFTUP
            (MouseButtonCode.Right, true) => 0x0008u,  // MOUSEEVENTF_RIGHTDOWN
            (MouseButtonCode.Right, false) => 0x0010u, // MOUSEEVENTF_RIGHTUP
            (MouseButtonCode.Middle, true) => 0x0020u, // MOUSEEVENTF_MIDDLEDOWN
            (MouseButtonCode.Middle, false) => 0x0040u,// MOUSEEVENTF_MIDDLEUP
            _ => 0u
        };

        if (flag != 0)
            mouse_event(flag, 0, 0, 0, UIntPtr.Zero);
    }

    private void OnRemoteMouseScrollReceived(MouseScrollPacket packet)
    {
        if (ActiveRemotePeer != null)
            return;

        if (packet.ScrollY != 0)
            mouse_event(0x0800u, 0, 0, unchecked((uint)packet.ScrollY), UIntPtr.Zero); // MOUSEEVENTF_WHEEL
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            // Ignore self-injected events
            if ((info.flags & LLMHF_INJECTED) == 0)
            {
                int msg = wParam.ToInt32();
                var remote = ActiveRemotePeer;

                if (remote == null)
                {
                    // Local Windows mode: check if cursor hits an active edge toward Android/Linux
                    if (msg == WM_MOUSEMOVE)
                    {
                        int dx = info.pt.X - _lastCursorX;
                        int dy = info.pt.Y - _lastCursorY;

                        int clampedX = Math.Clamp(info.pt.X, _topology.LocalLeft, _topology.LocalLeft + _topology.LocalWidth - 1);
                        int clampedY = Math.Clamp(info.pt.Y, _topology.LocalTop, _topology.LocalTop + _topology.LocalHeight - 1);

                        _lastCursorX = clampedX;
                        _lastCursorY = clampedY;

                        var transition = _topology.EvaluateCursorStep(clampedX, clampedY, dx, dy);
                        if (transition.ShouldTransition && transition.TargetPeer != null)
                        {
                            _anchorX = clampedX;
                            _anchorY = clampedY;
                            ActiveRemotePeer = transition.TargetPeer;
                            _network.SendEdgeHandOff(
                                transition.TargetPeer,
                                transition.TargetEntranceEdge,
                                transition.NormalizedPosition);
                            ActiveTargetChanged?.Invoke(
                                transition.TargetPeer,
                                transition.LocalExitEdge,
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
                                _network.SendMouseMove(
                                    remote,
                                    (short)Math.Clamp(dx, short.MinValue, short.MaxValue),
                                    (short)Math.Clamp(dy, short.MinValue, short.MaxValue));
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
                        ReturnControlToLocal(ScreenEdge.None, 0.5f);
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

                    // Allow local modifier key state updates for Ctrl/Alt/Shift so Windows doesn't get stuck modifiers,
                    // but suppress all regular keys from typing into local Windows apps.
                    if (!IsModifierVk(vk))
                    {
                        return (IntPtr)1;
                    }
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

    public void Dispose()
    {
        StopHooks();
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
}
