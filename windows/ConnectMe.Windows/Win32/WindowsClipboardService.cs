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
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(60).ConfigureAwait(false);
            }

            List<string>? validFiles = null;
            byte[]? pngBytes = null;
            string? plainText = null;
            bool clipboardReadSuccess = false;

            try
            {
                if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
                {
                    await dispatcher.InvokeAsync(() =>
                    {
                        // 1. Check Copied Files FIRST (Ctrl+C in Windows Explorer)
                        if (System.Windows.Clipboard.ContainsFileDropList())
                        {
                            var fileList = System.Windows.Clipboard.GetFileDropList();
                            if (fileList != null && fileList.Count > 0)
                            {
                                validFiles = new List<string>();
                                foreach (string? f in fileList)
                                {
                                    if (!string.IsNullOrWhiteSpace(f) && File.Exists(f))
                                    {
                                        var fi = new FileInfo(f);
                                        if (fi.Length <= 250L * 1024 * 1024)
                                        {
                                            validFiles.Add(fi.FullName);
                                        }
                                    }
                                }
                            }
                            clipboardReadSuccess = true;
                        }
                        // 2. Check Screenshots / Copied Images
                        else if (System.Windows.Clipboard.ContainsImage())
                        {
                            var img = System.Windows.Clipboard.GetImage();
                            if (img != null)
                            {
                                pngBytes = EncodeBitmapSourceToPng(img);
                            }
                            clipboardReadSuccess = true;
                        }
                        // 3. Check Plain Text
                        else if (System.Windows.Clipboard.ContainsText())
                        {
                            string text = System.Windows.Clipboard.GetText();
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                plainText = text;
                            }
                            clipboardReadSuccess = true;
                        }
                        else
                        {
                            clipboardReadSuccess = true;
                        }
                    });
                }
            }
            catch
            {
                // Clipboard may be temporarily locked by Windows Explorer during Ctrl+C; retry
                continue;
            }

            if (!clipboardReadSuccess)
                continue;

            try
            {
                if (validFiles is { Count: > 0 })
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
                else if (pngBytes is { Length: > 0 })
                {
                    await _network.BroadcastClipboardImageAsync(pngBytes).ConfigureAwait(false);
                }
                else if (!string.IsNullOrWhiteSpace(plainText))
                {
                    await _network.BroadcastClipboardTextAsync(plainText).ConfigureAwait(false);
                }
            }
            catch
            {
            }

            break;
        }
    }

    public async Task<int> TransferClipboardFilesToPeerAsync(PeerDeviceNode? targetPeer = null, bool includeExplorerSelection = true)
    {
        try
        {
            List<string>? filesToTransfer = null;
            if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
            {
                await dispatcher.InvokeAsync(() =>
                {
                    // If user is dragging a file from Windows Explorer without pressing Ctrl+C,
                    // check the active Explorer window's selection first!
                    if (includeExplorerSelection)
                    {
                        var explorerFiles = GetSelectedFilesFromExplorer(onlyForegroundExplorer: true);
                        if (explorerFiles.Count > 0)
                        {
                            filesToTransfer = explorerFiles;
                            return;
                        }
                    }

                    try
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
                    }
                    catch
                    {
                    }

                    if ((filesToTransfer == null || filesToTransfer.Count == 0) && includeExplorerSelection)
                    {
                        var fallbackExplorerFiles = GetSelectedFilesFromExplorer(onlyForegroundExplorer: false);
                        if (fallbackExplorerFiles.Count > 0)
                        {
                            filesToTransfer = fallbackExplorerFiles;
                        }
                    }
                });
            }

            if (filesToTransfer != null && filesToTransfer.Count > 0)
            {
                var peers = targetPeer != null
                    ? new List<PeerDeviceNode> { targetPeer }
                    : _network.MutuallyPairedPeers.ToList();

                int sentCount = 0;
                foreach (string path in filesToTransfer)
                {
                    foreach (var peer in peers)
                    {
                        if (await _network.SendFileToPeerShelfAsync(peer, path).ConfigureAwait(false) != null)
                        {
                            sentCount++;
                        }
                    }
                }
                return sentCount;
            }
        }
        catch
        {
        }
        return 0;
    }

    public static List<string> GetSelectedFilesFromExplorer(bool onlyForegroundExplorer = false)
    {
        var results = new List<string>();
        try
        {
            IntPtr fgHwnd = GetForegroundWindow();
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null)
                return results;

            object? shell = Activator.CreateInstance(shellType);
            if (shell == null)
                return results;

            object? windows = shellType.InvokeMember("Windows", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
            if (windows == null)
                return results;

            int count = Convert.ToInt32(windows.GetType().InvokeMember("Count", System.Reflection.BindingFlags.GetProperty, null, windows, null));
            List<string>? fallbackSelection = null;

            for (int i = 0; i < count; i++)
            {
                try
                {
                    object? win = windows.GetType().InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, windows, new object[] { i });
                    if (win == null)
                        continue;

                    long winHwnd = 0;
                    try
                    {
                        object? hwndObj = win.GetType().InvokeMember("HWND", System.Reflection.BindingFlags.GetProperty, null, win, null);
                        if (hwndObj != null)
                            winHwnd = Convert.ToInt64(hwndObj);
                    }
                    catch
                    {
                    }

                    if (onlyForegroundExplorer && (fgHwnd == IntPtr.Zero || winHwnd != fgHwnd.ToInt64()))
                        continue;

                    object? doc = win.GetType().InvokeMember("Document", System.Reflection.BindingFlags.GetProperty, null, win, null);
                    if (doc == null)
                        continue;

                    object? selectedItems = doc.GetType().InvokeMember("SelectedItems", System.Reflection.BindingFlags.InvokeMethod, null, doc, null);
                    if (selectedItems == null)
                        continue;

                    int selCount = Convert.ToInt32(selectedItems.GetType().InvokeMember("Count", System.Reflection.BindingFlags.GetProperty, null, selectedItems, null));
                    if (selCount <= 0)
                        continue;

                    var currentList = new List<string>();
                    for (int j = 0; j < selCount; j++)
                    {
                        object? item = selectedItems.GetType().InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, selectedItems, new object[] { j });
                        if (item == null)
                            continue;

                        string? path = item.GetType().InvokeMember("Path", System.Reflection.BindingFlags.GetProperty, null, item, null) as string;
                        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                        {
                            currentList.Add(path);
                        }
                    }

                    if (currentList.Count > 0)
                    {
                        if (fgHwnd != IntPtr.Zero && winHwnd == fgHwnd.ToInt64())
                        {
                            return currentList;
                        }
                        fallbackSelection ??= currentList;
                    }
                }
                catch
                {
                }
            }

            if (!onlyForegroundExplorer && fallbackSelection != null)
                return fallbackSelection;
        }
        catch
        {
        }
        return results;
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
