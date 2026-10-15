using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using ConnectMe.Core.Protocol;

namespace ConnectMe.Windows.Audio;

/// <summary>
/// Ultra-low-latency Windows Audio Capture Engine using WASAPI Loopback Capture.
/// Captures the machine's live desktop audio output and streams it as 48kHz Stereo 16-bit PCM packets
/// over UDP to a remote active audio sink (Linux laptop, Android tablet, or another PC).
/// Uses 100% native Win32/WASAPI COM interop with 0 external dependencies.
/// </summary>
public sealed class WindowsAudioCaptureEngine : IDisposable
{
    private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const int AUDCLNT_SHAREMODE_SHARED = 0;
    private const int WAVE_FORMAT_PCM = 1;
    private const int WAVE_FORMAT_IEEE_FLOAT = 3;
    private const int WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

    private readonly object _lock = new();
    private Thread? _captureThread;
    private volatile bool _isRunning;
    private volatile bool _disposed;

    private UdpClient? _udpClient;
    private string? _targetIp;
    private int _targetPort = ProtocolConstants.AudioStreamUdpPort;

    private ushort _sequence;
    private long _totalAudioBytesSent;

    public bool IsCapturing => _isRunning;
    public long TotalAudioBytesSent => _totalAudioBytesSent;
    public string? TargetIp => _targetIp;
    public bool MuteLocalWhileStreaming { get; set; } = true;

    public event Action<string>? CaptureStatusChanged;

    public void Start(string targetIp, int targetPort = ProtocolConstants.AudioStreamUdpPort)
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (_isRunning)
            {
                if (_targetIp == targetIp && _targetPort == targetPort)
                    return;
                Stop();
            }

            _targetIp = targetIp;
            _targetPort = targetPort;
            _totalAudioBytesSent = 0;
            _sequence = 0;
            _isRunning = true;

            try
            {
                _udpClient = new UdpClient();
            }
            catch (Exception ex)
            {
                CaptureStatusChanged?.Invoke($"Hata: {ex.Message}");
                _isRunning = false;
                return;
            }

            _captureThread = new Thread(CaptureLoopWorker)
            {
                Name = "ConnectMe-WindowsWasapiCapture",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _captureThread.Start();

            CaptureStatusChanged?.Invoke($"Yakalama Aktif -> {targetIp}:{targetPort}");
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_isRunning) return;
            _isRunning = false;

            try
            {
                _udpClient?.Close();
                _udpClient = null;
            }
            catch { }

            CaptureStatusChanged?.Invoke("Durduruldu");
        }

        try
        {
            _captureThread?.Join(500);
        }
        catch { }
        _captureThread = null;
    }

    private unsafe void CaptureLoopWorker()
    {
        IntPtr pDevice = IntPtr.Zero;
        IntPtr pAudioClient = IntPtr.Zero;
        IntPtr acVtbl = IntPtr.Zero;
        IntPtr pCaptureClient = IntPtr.Zero;
        IntPtr pFormat = IntPtr.Zero;
        IntPtr pEndpointVol = IntPtr.Zero;
        bool originalMuteState = false;
        bool hasMuted = false;
        Guid emptyGuid = Guid.Empty;

        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out pDevice);
            if (hr != 0 || pDevice == IntPtr.Zero)
            {
                CaptureStatusChanged?.Invoke("Varsayılan ses çıkış cihazı bulunamadı.");
                return;
            }

            IntPtr vtbl = Marshal.ReadIntPtr(pDevice);
            IntPtr activatePtr = Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size);
            var activate = Marshal.GetDelegateForFunctionPointer<ActivateDelegate>(activatePtr);

            Guid iidAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
            int actHr = activate(pDevice, ref iidAudioClient, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out pAudioClient);
            if (actHr != 0 || pAudioClient == IntPtr.Zero)
            {
                CaptureStatusChanged?.Invoke("IAudioClient başlatılamadı.");
                return;
            }

            acVtbl = Marshal.ReadIntPtr(pAudioClient);
            IntPtr getMixFormatPtr = Marshal.ReadIntPtr(acVtbl, 8 * IntPtr.Size);
            var getMixFormat = Marshal.GetDelegateForFunctionPointer<GetMixFormatDelegate>(getMixFormatPtr);
            int gmfHr = getMixFormat(pAudioClient, out pFormat);
            if (gmfHr != 0 || pFormat == IntPtr.Zero)
            {
                CaptureStatusChanged?.Invoke("Ses formatı alınamadı.");
                return;
            }

            var wfe = Marshal.PtrToStructure<WAVEFORMATEX>(pFormat);
            int subFormatTag = 0;
            if (wfe.wFormatTag == WAVE_FORMAT_EXTENSIBLE && wfe.cbSize >= 22)
            {
                subFormatTag = Marshal.ReadInt32(pFormat, 24);
            }
            bool isFloat = (wfe.wFormatTag == WAVE_FORMAT_IEEE_FLOAT) ||
                           (subFormatTag == 3) ||
                           (wfe.wBitsPerSample == 32);

            uint channels = wfe.nChannels;
            uint sampleRate = wfe.nSamplesPerSec;

            IntPtr initPtr = Marshal.ReadIntPtr(acVtbl, 3 * IntPtr.Size);
            var init = Marshal.GetDelegateForFunctionPointer<InitializeDelegate>(initPtr);
            // Ultra-low latency: 20ms driver buffer (200,000 in 100-ns units)
            int initHr = init(pAudioClient, AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, 200000 /* 20ms */, 0, pFormat, IntPtr.Zero);
            if (initHr != 0)
            {
                CaptureStatusChanged?.Invoke($"WASAPI loopback başlatılamadı (0x{initHr:X}).");
                return;
            }

            IntPtr getServicePtr = Marshal.ReadIntPtr(acVtbl, 14 * IntPtr.Size);
            var getService = Marshal.GetDelegateForFunctionPointer<GetServiceDelegate>(getServicePtr);
            Guid iidCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
            int gsHr = getService(pAudioClient, ref iidCaptureClient, out pCaptureClient);
            if (gsHr != 0 || pCaptureClient == IntPtr.Zero)
            {
                CaptureStatusChanged?.Invoke("IAudioCaptureClient servisi alınamadı.");
                return;
            }

            // Optionally mute the local physical speakers during streaming (only remote device plays audio)
            if (MuteLocalWhileStreaming)
            {
                try
                {
                    Guid iidEndpointVol = new Guid("5BC64442-78A7-4082-8F6F-7983A4F6F48F");
                    int volHr = activate(pDevice, ref iidEndpointVol, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out pEndpointVol);
                    if (volHr == 0 && pEndpointVol != IntPtr.Zero)
                    {
                        var epVol = (IAudioEndpointVolume)Marshal.GetObjectForIUnknown(pEndpointVol);
                        epVol.GetMute(out originalMuteState);
                        if (!originalMuteState)
                        {
                            epVol.SetMute(true, ref emptyGuid);
                            hasMuted = true;
                        }
                    }
                }
                catch { }
            }

            IntPtr ccVtbl = Marshal.ReadIntPtr(pCaptureClient);
            var getBuffer = Marshal.GetDelegateForFunctionPointer<GetBufferDelegate>(Marshal.ReadIntPtr(ccVtbl, 3 * IntPtr.Size));
            var releaseBuffer = Marshal.GetDelegateForFunctionPointer<ReleaseBufferDelegate>(Marshal.ReadIntPtr(ccVtbl, 4 * IntPtr.Size));

            IntPtr startPtr = Marshal.ReadIntPtr(acVtbl, 10 * IntPtr.Size);
            var start = Marshal.GetDelegateForFunctionPointer<StartDelegate>(startPtr);
            start(pAudioClient);

            if (!IPAddress.TryParse(_targetIp, out var parsedIp) || IPAddress.IsLoopback(parsedIp))
            {
                CaptureStatusChanged?.Invoke("Hata: Hedef IP geçersiz veya yerel döngü (loopback) seçildi.");
                return;
            }

            IPEndPoint remoteEp = new IPEndPoint(parsedIp, _targetPort);

            while (_isRunning && !_disposed)
            {
                bool hadData = false;

                // Drain all available audio packets in the WASAPI buffer to eliminate lag
                while (_isRunning && !_disposed)
                {
                    int gbHr = getBuffer(pCaptureClient, out IntPtr pData, out uint numFramesToRead, out uint flags, out _, out _);
                    if (gbHr != 0 || numFramesToRead == 0 || pData == IntPtr.Zero)
                        break;

                    hadData = true;
                    bool isSilent = (flags & 0x01 /* AUDCLNT_BUFFERFLAGS_SILENT */) != 0;

                    if (!isSilent)
                    {
                        byte[] pcmChunk = ConvertToPcm16Stereo(pData, numFramesToRead, channels, sampleRate, isFloat, isSilent: false);

                        if (pcmChunk.Length > 0 && _udpClient != null)
                        {
                            // Split into MTU-safe chunks of up to 960 bytes (~5ms @ 48kHz Stereo)
                            int offset = 0;
                            while (offset < pcmChunk.Length && _isRunning)
                            {
                                int chunkLen = Math.Min(960, pcmChunk.Length - offset);
                                byte[] subChunk = new byte[chunkLen];
                                Buffer.BlockCopy(pcmChunk, offset, subChunk, 0, chunkLen);

                                var packet = new AudioChunkPacket(
                                    Channels: 2,
                                    SampleRate: 48000,
                                    BitsPerSample: 16,
                                    Sequence: _sequence++,
                                    PcmData: subChunk);

                                byte[] wireBytes = WirePacketCodec.EncodeAudioChunk(packet);
                                try
                                {
                                    _udpClient.Send(wireBytes, wireBytes.Length, remoteEp);
                                    _totalAudioBytesSent += chunkLen;
                                }
                                catch { }

                                offset += chunkLen;
                            }
                        }
                    }

                    releaseBuffer(pCaptureClient, numFramesToRead);
                }

                if (!hadData)
                {
                    Thread.Sleep(2);
                }
                else
                {
                    Thread.Sleep(1);
                }
            }
        }
        catch (Exception ex)
        {
            CaptureStatusChanged?.Invoke($"Yakalama hatası: {ex.Message}");
        }
        finally
        {
            if (hasMuted && pEndpointVol != IntPtr.Zero)
            {
                try
                {
                    var epVol = (IAudioEndpointVolume)Marshal.GetObjectForIUnknown(pEndpointVol);
                    epVol.SetMute(false, ref emptyGuid);
                }
                catch { }
            }
            if (pEndpointVol != IntPtr.Zero)
            {
                try { Marshal.Release(pEndpointVol); } catch { }
            }

            if (pAudioClient != IntPtr.Zero && acVtbl != IntPtr.Zero)
            {
                try
                {
                    IntPtr stopPtr = Marshal.ReadIntPtr(acVtbl, 11 * IntPtr.Size);
                    var stop = Marshal.GetDelegateForFunctionPointer<StopDelegate>(stopPtr);
                    stop(pAudioClient);
                }
                catch { }
            }

            if (pCaptureClient != IntPtr.Zero) Marshal.Release(pCaptureClient);
            if (pFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(pFormat);
            if (pAudioClient != IntPtr.Zero) Marshal.Release(pAudioClient);
            if (pDevice != IntPtr.Zero) Marshal.Release(pDevice);
        }
    }

    private static unsafe byte[] ConvertToPcm16Stereo(
        IntPtr pData,
        uint numFrames,
        uint channels,
        uint sampleRate,
        bool isFloat,
        bool isSilent)
    {
        if (numFrames == 0) return Array.Empty<byte>();

        // We produce 48000 Hz Stereo (2 channels) 16-bit PCM:
        // numFrames at sampleRate -> targetFrames at 48000 Hz
        int targetFrames = (int)numFrames;
        if (sampleRate != 48000 && sampleRate > 0)
        {
            targetFrames = (int)((numFrames * 48000L) / sampleRate);
        }

        byte[] pcmBytes = new byte[targetFrames * 4]; // 2 channels * 2 bytes = 4 bytes per frame
        if (isSilent)
        {
            return pcmBytes;
        }

        fixed (byte* pOut = pcmBytes)
        {
            short* pOutShort = (short*)pOut;

            if (sampleRate == 48000 && channels == 2)
            {
                if (isFloat)
                {
                    float* pIn = (float*)pData;
                    for (int i = 0; i < numFrames * 2; i++)
                    {
                        float sample = pIn[i];
                        pOutShort[i] = (short)Math.Clamp((int)(sample * 32767f), short.MinValue, short.MaxValue);
                    }
                }
                else
                {
                    short* pIn = (short*)pData;
                    for (int i = 0; i < numFrames * 2; i++)
                    {
                        pOutShort[i] = pIn[i];
                    }
                }
            }
            else
            {
                // Resample or downmix / upmix channels to stereo 48kHz
                double ratio = (double)sampleRate / 48000.0;
                for (int f = 0; f < targetFrames; f++)
                {
                    int srcFrame = (int)(f * ratio);
                    if (srcFrame >= numFrames) srcFrame = (int)numFrames - 1;

                    float left = 0f, right = 0f;
                    if (isFloat)
                    {
                        float* pIn = (float*)pData;
                        if (channels == 1)
                        {
                            left = right = pIn[srcFrame];
                        }
                        else
                        {
                            left = pIn[srcFrame * channels];
                            right = pIn[srcFrame * channels + 1];
                        }
                    }
                    else
                    {
                        short* pIn = (short*)pData;
                        if (channels == 1)
                        {
                            left = right = pIn[srcFrame] / 32768f;
                        }
                        else
                        {
                            left = pIn[srcFrame * channels] / 32768f;
                            right = pIn[srcFrame * channels + 1] / 32768f;
                        }
                    }

                    pOutShort[f * 2] = (short)Math.Clamp((int)(left * 32767f), short.MinValue, short.MaxValue);
                    pOutShort[f * 2 + 1] = (short)Math.Clamp((int)(right * 32767f), short.MinValue, short.MaxValue);
                }
            }
        }

        return pcmBytes;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    // =========================================================================
    // Win32 / WASAPI COM Interop
    // =========================================================================

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr ppDevices);
        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr ppDevice);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEXTENSIBLE
    {
        public WAVEFORMATEX Format;
        public ushort wValidBitsPerSample;
        public uint dwChannelMask;
        public Guid SubFormat;
    }

    private static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new Guid("00000003-0000-0010-8000-00aa00389b71");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ActivateDelegate(IntPtr thisPtr, ref Guid iid, int dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetMixFormatDelegate(IntPtr thisPtr, out IntPtr ppFormat);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int InitializeDelegate(IntPtr thisPtr, int shareMode, uint streamFlags, long hnsBuffer, long hnsPeriod, IntPtr pFormat, IntPtr sessionGuid);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetServiceDelegate(IntPtr thisPtr, ref Guid riid, out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int StartDelegate(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int StopDelegate(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBufferDelegate(IntPtr thisPtr, out IntPtr ppData, out uint pNumFramesToRead, out uint pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseBufferDelegate(IntPtr thisPtr, uint numFramesRead);

    [Guid("5BC64442-78A7-4082-8F6F-7983A4F6F48F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr pNotify);
        int UnregisterControlChangeNotify(IntPtr pNotify);
        int GetChannelCount(out uint pnChannelCount);
        int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        int GetMasterVolumeLevel(out float pfLevelDB);
        int GetMasterVolumeLevelScalar(out float pfLevel);
        int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
        int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
        int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
        int VolumeStepUp(ref Guid pguidEventContext);
        int VolumeStepDown(ref Guid pguidEventContext);
        int QueryHardwareSupport(out uint pdwHardwareSupportMask);
        int GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
    }
}
