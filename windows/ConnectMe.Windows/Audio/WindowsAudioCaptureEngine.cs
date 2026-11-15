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
        IntPtr pEndpointVolume = IntPtr.Zero;
        IntPtr evVtbl = IntPtr.Zero;
        IntPtr pFormat = IntPtr.Zero;
        bool mutedByUs = false;
        Guid eventContext = new Guid("C0FFEE00-ABCD-4321-8765-123456789ABC");

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

            // Activate IAudioEndpointVolume so we can mute local speakers while streaming to remote sink
            Guid iidEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
            if (activate(pDevice, ref iidEndpointVolume, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out pEndpointVolume) == 0 &&
                pEndpointVolume != IntPtr.Zero)
            {
                evVtbl = Marshal.ReadIntPtr(pEndpointVolume);
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

            bool isFloat = DetectWaveFormatProperties(pFormat, out uint channels, out uint sampleRate, out ushort bitsPerSample);

            IntPtr initPtr = Marshal.ReadIntPtr(acVtbl, 3 * IntPtr.Size);
            var init = Marshal.GetDelegateForFunctionPointer<InitializeDelegate>(initPtr);

            // WASAPI shared loopback buffer: 100ms standard with progressive fallbacks
            long hnsBuffer = 1000000; // 100ms
            int initHr = init(pAudioClient, AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, hnsBuffer, 0, pFormat, IntPtr.Zero);
            if (initHr != 0)
            {
                hnsBuffer = 2000000; // 200ms fallback
                initHr = init(pAudioClient, AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, hnsBuffer, 0, pFormat, IntPtr.Zero);
            }
            if (initHr != 0)
            {
                hnsBuffer = 5000000; // 500ms fallback
                initHr = init(pAudioClient, AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, hnsBuffer, 0, pFormat, IntPtr.Zero);
            }
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

            IntPtr ccVtbl = Marshal.ReadIntPtr(pCaptureClient);
            var getBuffer = Marshal.GetDelegateForFunctionPointer<GetBufferDelegate>(Marshal.ReadIntPtr(ccVtbl, 3 * IntPtr.Size));
            var releaseBuffer = Marshal.GetDelegateForFunctionPointer<ReleaseBufferDelegate>(Marshal.ReadIntPtr(ccVtbl, 4 * IntPtr.Size));

            IntPtr startPtr = Marshal.ReadIntPtr(acVtbl, 10 * IntPtr.Size);
            var start = Marshal.GetDelegateForFunctionPointer<StartDelegate>(startPtr);
            start(pAudioClient);

            if (!IPAddress.TryParse(_targetIp, out var parsedIp) ||
                IPAddress.IsLoopback(parsedIp) ||
                ConnectMe.Core.Network.ConnectMeNetworkNode.GetLocalIPv4Addresses().Any(a => a.Equals(parsedIp)))
            {
                CaptureStatusChanged?.Invoke("Hata: Hedef IP geçersiz veya bu bilgisayarın kendi IP'si seçildi.");
                return;
            }

            IPEndPoint remoteEp = new IPEndPoint(parsedIp, _targetPort);
            timeBeginPeriod(1);

            SetMuteDelegate? setMute = null;
            GetMuteDelegate? getMute = null;
            if (pEndpointVolume != IntPtr.Zero && evVtbl != IntPtr.Zero)
            {
                setMute = Marshal.GetDelegateForFunctionPointer<SetMuteDelegate>(Marshal.ReadIntPtr(evVtbl, 14 * IntPtr.Size));
                getMute = Marshal.GetDelegateForFunctionPointer<GetMuteDelegate>(Marshal.ReadIntPtr(evVtbl, 15 * IntPtr.Size));
            }

            if (MuteLocalWhileStreaming && setMute != null && getMute != null)
            {
                if (getMute(pEndpointVolume, out int alreadyMuted) == 0 && alreadyMuted == 0)
                {
                    if (setMute(pEndpointVolume, 1, ref eventContext) == 0)
                    {
                        mutedByUs = true;
                    }
                }
            }

            while (_isRunning && !_disposed)
            {
                // Dynamically sync local speaker mute if user toggles the checkbox while streaming
                if (setMute != null && getMute != null)
                {
                    if (MuteLocalWhileStreaming && !mutedByUs)
                    {
                        if (getMute(pEndpointVolume, out int curMuted) == 0 && curMuted == 0)
                        {
                            if (setMute(pEndpointVolume, 1, ref eventContext) == 0)
                                mutedByUs = true;
                        }
                    }
                    else if (!MuteLocalWhileStreaming && mutedByUs)
                    {
                        setMute(pEndpointVolume, 0, ref eventContext);
                        mutedByUs = false;
                    }
                }

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
                        byte[] pcmChunk = ConvertToPcm16Stereo(pData, numFramesToRead, channels, sampleRate, bitsPerSample, isFloat, isSilent: false);

                        if (pcmChunk.Length > 0 && _udpClient != null)
                        {
                            // Split into MTU-safe chunks of up to 960 bytes (~5ms @ 48kHz Stereo, strictly 4-byte aligned)
                            int offset = 0;
                            while (offset < pcmChunk.Length && _isRunning)
                            {
                                int chunkLen = Math.Min(960, pcmChunk.Length - offset);
                                chunkLen -= (chunkLen % 4);
                                if (chunkLen <= 0) break;

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
            if (mutedByUs && pEndpointVolume != IntPtr.Zero && evVtbl != IntPtr.Zero)
            {
                try
                {
                    var setMute = Marshal.GetDelegateForFunctionPointer<SetMuteDelegate>(Marshal.ReadIntPtr(evVtbl, 14 * IntPtr.Size));
                    setMute(pEndpointVolume, 0, ref eventContext);
                }
                catch { }
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
            if (pEndpointVolume != IntPtr.Zero) Marshal.Release(pEndpointVolume);
            if (pDevice != IntPtr.Zero) Marshal.Release(pDevice);
            try { timeEndPeriod(1); } catch { }
        }
    }

    public static bool DetectWaveFormatProperties(
        IntPtr pFormat,
        out uint channels,
        out uint sampleRate,
        out ushort bitsPerSample)
    {
        var wfe = Marshal.PtrToStructure<WAVEFORMATEX>(pFormat);
        channels = wfe.nChannels;
        sampleRate = wfe.nSamplesPerSec;
        bitsPerSample = wfe.wBitsPerSample;

        if (wfe.wFormatTag == WAVE_FORMAT_IEEE_FLOAT)
        {
            return true;
        }

        if (wfe.wFormatTag == WAVE_FORMAT_EXTENSIBLE && wfe.cbSize >= 22)
        {
            var wfext = Marshal.PtrToStructure<WAVEFORMATEXTENSIBLE>(pFormat);
            if (wfext.SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)
            {
                return true;
            }
            if (wfext.SubFormat == KSDATAFORMAT_SUBTYPE_PCM)
            {
                return false;
            }
        }

        return wfe.wBitsPerSample == 32;
    }

    public static unsafe byte[] ConvertToPcm16Stereo(
        IntPtr pData,
        uint numFrames,
        uint channels,
        uint sampleRate,
        ushort bitsPerSample,
        bool isFloat,
        bool isSilent)
    {
        if (numFrames == 0 || channels == 0) return Array.Empty<byte>();

        // We produce 48000 Hz Stereo (2 channels) 16-bit PCM:
        int targetFrames = (int)numFrames;
        if (sampleRate != 48000 && sampleRate > 0)
        {
            targetFrames = (int)Math.Round((numFrames * 48000.0) / sampleRate);
        }
        if (targetFrames <= 0) return Array.Empty<byte>();

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
                        if (!float.IsFinite(sample)) sample = 0f;
                        pOutShort[i] = (short)Math.Clamp((int)MathF.Round(sample * 32767f), short.MinValue, short.MaxValue);
                    }
                }
                else if (bitsPerSample == 16)
                {
                    short* pIn = (short*)pData;
                    for (int i = 0; i < numFrames * 2; i++)
                    {
                        pOutShort[i] = pIn[i];
                    }
                }
                else if (bitsPerSample == 32)
                {
                    int* pIn = (int*)pData;
                    for (int i = 0; i < numFrames * 2; i++)
                    {
                        pOutShort[i] = (short)(pIn[i] >> 16);
                    }
                }
                else if (bitsPerSample == 24)
                {
                    byte* pIn = (byte*)pData;
                    for (int i = 0; i < numFrames * 2; i++)
                    {
                        int val = pIn[i * 3] | (pIn[i * 3 + 1] << 8) | (pIn[i * 3 + 2] << 16);
                        if ((val & 0x800000) != 0) val |= unchecked((int)0xFF000000);
                        pOutShort[i] = (short)(val >> 8);
                    }
                }
            }
            else
            {
                // Resample with linear interpolation and downmix / upmix channels to stereo 48kHz
                double ratio = (double)sampleRate / 48000.0;
                for (int f = 0; f < targetFrames; f++)
                {
                    double srcPos = f * ratio;
                    int idx0 = (int)srcPos;
                    if (idx0 >= numFrames) idx0 = (int)numFrames - 1;
                    int idx1 = Math.Min(idx0 + 1, (int)numFrames - 1);
                    float frac = (float)(srcPos - idx0);

                    ReadStereoSampleFrame(pData, idx0, channels, bitsPerSample, isFloat, out float l0, out float r0);
                    ReadStereoSampleFrame(pData, idx1, channels, bitsPerSample, isFloat, out float l1, out float r1);

                    float left = l0 + (l1 - l0) * frac;
                    float right = r0 + (r1 - r0) * frac;

                    pOutShort[f * 2] = (short)Math.Clamp((int)MathF.Round(left * 32767f), short.MinValue, short.MaxValue);
                    pOutShort[f * 2 + 1] = (short)Math.Clamp((int)MathF.Round(right * 32767f), short.MinValue, short.MaxValue);
                }
            }
        }

        return pcmBytes;
    }

    private static unsafe void ReadStereoSampleFrame(
        IntPtr pData,
        int frameIdx,
        uint channels,
        ushort bitsPerSample,
        bool isFloat,
        out float left,
        out float right)
    {
        left = 0f;
        right = 0f;
        int baseIdx = (int)(frameIdx * channels);

        if (isFloat)
        {
            float* pIn = (float*)pData;
            left = pIn[baseIdx];
            right = channels == 1 ? left : pIn[baseIdx + 1];
            if (!float.IsFinite(left)) left = 0f;
            if (!float.IsFinite(right)) right = 0f;
        }
        else if (bitsPerSample == 16)
        {
            short* pIn = (short*)pData;
            left = pIn[baseIdx] / 32768f;
            right = channels == 1 ? left : pIn[baseIdx + 1] / 32768f;
        }
        else if (bitsPerSample == 32)
        {
            int* pIn = (int*)pData;
            left = pIn[baseIdx] / 2147483648f;
            right = channels == 1 ? left : pIn[baseIdx + 1] / 2147483648f;
        }
        else if (bitsPerSample == 24)
        {
            byte* pIn = (byte*)pData;
            int lVal = pIn[baseIdx * 3] | (pIn[baseIdx * 3 + 1] << 8) | (pIn[baseIdx * 3 + 2] << 16);
            if ((lVal & 0x800000) != 0) lVal |= unchecked((int)0xFF000000);
            left = lVal / 8388608f;

            if (channels == 1)
            {
                right = left;
            }
            else
            {
                int rIdx = baseIdx + 1;
                int rVal = pIn[rIdx * 3] | (pIn[rIdx * 3 + 1] << 8) | (pIn[rIdx * 3 + 2] << 16);
                if ((rVal & 0x800000) != 0) rVal |= unchecked((int)0xFF000000);
                right = rVal / 8388608f;
            }
        }
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

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WAVEFORMATEXTENSIBLE
    {
        public WAVEFORMATEX Format;
        public ushort wValidBitsPerSample;
        public uint dwChannelMask;
        public Guid SubFormat;
    }

    public static readonly Guid KSDATAFORMAT_SUBTYPE_PCM = new Guid("00000001-0000-0010-8000-00aa00389b71");
    public static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new Guid("00000003-0000-0010-8000-00aa00389b71");

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

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetMuteDelegate(IntPtr thisPtr, int bMute, ref Guid pguidEventContext);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetMuteDelegate(IntPtr thisPtr, out int pbMute);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint timeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint timeEndPeriod(uint uMilliseconds);
}
