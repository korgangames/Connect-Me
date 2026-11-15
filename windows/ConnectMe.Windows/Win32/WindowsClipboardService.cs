using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ConnectMe.Core.Network;
using ConnectMe.Core.Topology;

namespace ConnectMe.Windows.Win32;

/// <summary>
/// Monitors Windows Clipboard changes via Win32 AddClipboardFormatListener
/// and synchronizes text, screenshots/images, and copied files bidirectionally with Android and Linux peers.
/// </summary>
public sealed class WindowsClipboardService : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    private readonly ConnectMeNetworkNode _network;
    private HwndSource? _hwndSource;
    private bool _suppressNextLocalEvent;
    private string _lastBroadcastedFileSignature = string.Empty;

    public bool IsAutoSyncEnabled { get; set; } = true;

    public WindowsClipboardService(ConnectMeNetworkNode network)
    {
        _network = network;
        _network.RemoteClipboardTextReceived += OnRemoteClipboardTextReceived;
        _network.RemoteClipboardImageReceived += OnRemoteClipboardImageReceived;
        _network.ShelfItemReceived += OnRemoteShelfFileReceived;
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
            if (System.Windows.Clipboard.ContainsImage())
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
            else if (System.Windows.Clipboard.ContainsFileDropList())
            {
                var fileList = System.Windows.Clipboard.GetFileDropList();
                if (fileList != null && fileList.Count > 0)
                {
                    var validFiles = new List<string>();
                    foreach (string? f in fileList)
                    {
                        if (!string.IsNullOrWhiteSpace(f) && File.Exists(f))
                        {
                            var fi = new FileInfo(f);
                            if (fi.Length <= 100L * 1024 * 1024)
                            {
                                validFiles.Add(fi.FullName);
                            }
                        }
                    }

                    if (validFiles.Count > 0)
                    {
                        string sig = string.Join("|", validFiles.Select(p =>
                        {
                            var fi = new FileInfo(p);
                            return $"{fi.FullName}:{fi.Length}:{fi.LastWriteTimeUtc.Ticks}";
                        }));

                        if (sig != _lastBroadcastedFileSignature)
                        {
                            _lastBroadcastedFileSignature = sig;
                            var peers = _network.MutuallyPairedPeers;
                            foreach (string filePath in validFiles)
                            {
                                foreach (var peer in peers)
                                {
                                    await _network.SendFileToPeerShelfAsync(peer, filePath).ConfigureAwait(false);
                                }
                            }
                        }
                    }
                }
            }
            else if (System.Windows.Clipboard.ContainsText())
            {
                string text = System.Windows.Clipboard.GetText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    await _network.BroadcastClipboardTextAsync(text).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // Clipboard may be temporarily locked by another process
        }
    }

    public async Task TransferClipboardFilesToPeerAsync(PeerDeviceNode peer)
    {
        try
        {
            List<string>? filesToTransfer = null;
            if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
            {
                await dispatcher.InvokeAsync(() =>
                {
                    if (System.Windows.Clipboard.ContainsFileDropList())
                    {
                        var dropList = System.Windows.Clipboard.GetFileDropList();
                        if (dropList != null && dropList.Count > 0)
                        {
                            filesToTransfer = new List<string>();
                            foreach (string? f in dropList)
                            {
                                if (!string.IsNullOrWhiteSpace(f) && File.Exists(f))
                                {
                                    filesToTransfer.Add(f);
                                }
                            }
                        }
                    }
                });
            }

            if (filesToTransfer != null && filesToTransfer.Count > 0)
            {
                foreach (string path in filesToTransfer)
                {
                    await _network.SendFileToPeerShelfAsync(peer, path).ConfigureAwait(false);
                }
            }
        }
        catch
        {
        }
    }

    private void OnRemoteShelfFileReceived(ShelfItemEntry item)
    {
        if (!IsAutoSyncEnabled || item.IsOutgoing || string.IsNullOrWhiteSpace(item.LocalFilePath) || !File.Exists(item.LocalFilePath))
            return;

        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var fi = new FileInfo(item.LocalFilePath);
                _lastBroadcastedFileSignature = $"{fi.FullName}:{fi.Length}:{fi.LastWriteTimeUtc.Ticks}";
                _suppressNextLocalEvent = true;
                var collection = new StringCollection { fi.FullName };
                System.Windows.Clipboard.SetFileDropList(collection);
            }
            catch
            {
                _suppressNextLocalEvent = false;
            }
        });
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

    public static byte[] EncodeBitmapSourceToPng(BitmapSource source)
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
