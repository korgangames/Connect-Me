using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ConnectMe.Core.Network;

namespace ConnectMe.Windows.Win32;

/// <summary>
/// Monitors Windows Clipboard changes via Win32 AddClipboardFormatListener
/// and synchronizes text & images bidirectionally with Android and Linux peers.
/// </summary>
public sealed class WindowsClipboardService : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    private readonly ConnectMeNetworkNode _network;
    private HwndSource? _hwndSource;
    private bool _suppressNextLocalEvent;

    public bool IsAutoSyncEnabled { get; set; } = true;

    public WindowsClipboardService(ConnectMeNetworkNode network)
    {
        _network = network;
        _network.RemoteClipboardTextReceived += OnRemoteClipboardTextReceived;
        _network.RemoteClipboardImageReceived += OnRemoteClipboardImageReceived;
    }

    public void AttachToWindow(Window window)
    {
        var helper = new WindowInteropHelper(window);
        if (helper.Handle == IntPtr.Zero)
            return;

        _hwndSource = HwndSource.FromHwnd(helper.Handle);
        _hwndSource?.AddHook(WndProc);
        AddClipboardFormatListener(helper.Handle);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && IsAutoSyncEnabled)
        {
            if (_suppressNextLocalEvent)
            {
                _suppressNextLocalEvent = false;
                return IntPtr.Zero;
            }

            _ = CaptureAndBroadcastLocalClipboardAsync();
        }

        return IntPtr.Zero;
    }

    private async Task CaptureAndBroadcastLocalClipboardAsync()
    {
        try
        {
            if (System.Windows.Clipboard.ContainsText())
            {
                string text = System.Windows.Clipboard.GetText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    await _network.BroadcastClipboardTextAsync(text).ConfigureAwait(false);
                }
            }
            else if (System.Windows.Clipboard.ContainsImage())
            {
                var img = System.Windows.Clipboard.GetImage();
                if (img != null)
                {
                    byte[] pngBytes = EncodeBitmapSourceToPng(img);
                    if (pngBytes.Length > 0)
                    {
                        await _network.BroadcastClipboardImageAsync(pngBytes).ConfigureAwait(false);
                    }
                }
            }
        }
        catch
        {
            // Clipboard may be temporarily locked by another process
        }
    }

    private void OnRemoteClipboardTextReceived(string text, string senderName)
    {
        if (!IsAutoSyncEnabled)
            return;

        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                _suppressNextLocalEvent = true;
                System.Windows.Clipboard.SetText(text);
            }
            catch
            {
                _suppressNextLocalEvent = false;
            }
        });
    }

    private void OnRemoteClipboardImageReceived(byte[] pngBytes, string senderName)
    {
        if (!IsAutoSyncEnabled)
            return;

        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                using var ms = new MemoryStream(pngBytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze();

                _suppressNextLocalEvent = true;
                System.Windows.Clipboard.SetImage(bitmap);
            }
            catch
            {
                _suppressNextLocalEvent = false;
            }
        });
    }

    private static byte[] EncodeBitmapSourceToPng(BitmapSource source)
    {
        using var ms = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(ms);
        return ms.ToArray();
    }

    public void Dispose()
    {
        if (_hwndSource != null && _hwndSource.Handle != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(_hwndSource.Handle);
            _hwndSource.RemoveHook(WndProc);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
