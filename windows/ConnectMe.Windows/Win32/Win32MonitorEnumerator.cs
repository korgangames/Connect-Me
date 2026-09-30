using System.Runtime.InteropServices;
using ConnectMe.Core.Protocol;

namespace ConnectMe.Windows.Win32;

/// <summary>
/// Low-level Win32 monitor enumerator that discovers all active physical monitors
/// on Windows, their exact virtual screen coordinates (including negative offsets for left/top screens),
/// primary monitor status, device names, and per-monitor DPI scale factors.
/// </summary>
public static class Win32MonitorEnumerator
{
    private const int MONITORINFOF_PRIMARY = 0x00000001;
    private const int CCHDEVICENAME = 32;
    private const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("shcore.dll", SetLastError = true)]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>
    /// Enumerates all currently active physical monitors on the Windows desktop.
    /// Falls back to GetSystemMetrics virtual screen if enumeration fails.
    /// </summary>
    public static List<PhysicalMonitorDescriptor> EnumerateLocalMonitors()
    {
        var monitors = new List<PhysicalMonitorDescriptor>();
        int index = 0;

        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) =>
            {
                var mi = new MONITORINFOEX();
                mi.cbSize = Marshal.SizeOf(mi);

                if (GetMonitorInfo(hMon, ref mi))
                {
                    index++;
                    bool isPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;
                    string devName = string.IsNullOrWhiteSpace(mi.szDevice)
                        ? $"DISPLAY{index}"
                        : mi.szDevice.Replace(@"\\.\", "");

                    int vx = mi.rcMonitor.Left;
                    int vy = mi.rcMonitor.Top;
                    int w = Math.Max(320, mi.rcMonitor.Right - mi.rcMonitor.Left);
                    int h = Math.Max(240, mi.rcMonitor.Bottom - mi.rcMonitor.Top);

                    double scale = 1.0;
                    try
                    {
                        if (GetDpiForMonitor(hMon, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
                        {
                            scale = Math.Round(dpiX / 96.0, 2);
                        }
                    }
                    catch
                    {
                        // Shcore not available or old OS
                    }

                    string friendly = isPrimary
                        ? $"Monitör {index} ({devName} — Birincil)"
                        : $"Monitör {index} ({devName})";

                    monitors.Add(new PhysicalMonitorDescriptor
                    {
                        MonitorId = devName,
                        Name = friendly,
                        VirtualX = vx,
                        VirtualY = vy,
                        Width = w,
                        Height = h,
                        ScaleFactor = scale,
                        IsPrimary = isPrimary
                    });
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // Fallback handled below
        }

        if (monitors.Count == 0)
        {
            int w = GetSystemMetrics(0);  // SM_CXSCREEN
            int h = GetSystemMetrics(1);  // SM_CYSCREEN
            monitors.Add(new PhysicalMonitorDescriptor
            {
                MonitorId = "DISPLAY1",
                Name = "Birincil Ekran",
                VirtualX = 0,
                VirtualY = 0,
                Width = Math.Max(800, w),
                Height = Math.Max(600, h),
                ScaleFactor = 1.0,
                IsPrimary = true
            });
        }
        else
        {
            // Ensure exactly one monitor is designated as primary
            if (!monitors.Any(m => m.IsPrimary))
            {
                monitors[0].IsPrimary = true;
            }
        }

        // Sort so primary monitor is first, then left-to-right
        return monitors
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.VirtualX)
            .ToList();
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
