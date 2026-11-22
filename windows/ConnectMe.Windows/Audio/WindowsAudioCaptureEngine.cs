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

            // Activate IAudioEndpointVolume so we can mute local speakers while streaming to remote sink
            Guid iidEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
            if (activate(pDevice, ref iidEndpointVolume, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out pEndpointVolume) == 0 &&
                pEndpointVolume != IntPtr.Zero)
            {
                evVtbl = Marshal.ReadIntPtr(pEndpointVolume);
            }

            uint channels = 2;
            uint sampleRate = 48000;
            ushort bitsPerSample = 16;
            bool isFloat = false;
            bool initialized = false;
            string captureModeLabel = "Process Loopback";

            // Prefer Windows 10 Build 20348+ / Win11 Process Loopback (EXCLUDE current ConnectMe PID):
            // 1) Eliminates echo/feedback loops mathematically at OS level
            // 2) Captures 48kHz Stereo 16-bit PCM cleanly even when physical endpoint is MUTED!
            if (TryActivateProcessLoopbackClient(out pAudioClient, out pFormat, out channels, out sampleRate, out bitsPerSample, out isFloat))
            {
                acVtbl = Marshal.ReadIntPtr(pAudioClient);
                var initProc = Marshal.GetDelegateForFunctionPointer<InitializeDelegate>(Marshal.ReadIntPtr(acVtbl, 3 * IntPtr.Size));
                long hnsBuf = 1000000; // 100ms
                int initHr = initProc(pAudioClient, AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, hnsBuf, 0, pFormat, IntPtr.Zero);
                if (initHr == 0)
                {
                    initialized = true;
                }
                else
                {
                    Marshal.Release(pAudioClient);
                    pAudioClient = IntPtr.Zero;
                    if (pFormat != IntPtr.Zero) { Marshal.FreeCoTaskMem(pFormat); pFormat = IntPtr.Zero; }
                }
            }

            // Fallback to Classic Endpoint Loopback (pre-20H1 Windows 10)
            if (!initialized)
            {
                captureModeLabel = "WASAPI Loopback";
                Guid iidAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
                int actHr = activate(pDevice, ref iidAudioClient, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out pAudioClient);
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

                isFloat = DetectWaveFormatProperties(pFormat, out channels, out sampleRate, out bitsPerSample);

                IntPtr initPtr = Marshal.ReadIntPtr(acVtbl, 3 * IntPtr.Size);
                var init = Marshal.GetDelegateForFunctionPointer<InitializeDelegate>(initPtr);

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

            // Rebind UDP socket explicitly to the LAN NIC reaching targetIp if multi-homed
            try
            {
                var bestLocalIp = ConnectMe.Core.Network.ConnectMeNetworkNode.FindBestLocalIpForTarget(parsedIp);
                if (bestLocalIp != null && _udpClient != null)
                {
                    _udpClient.Close();
                    _udpClient = new UdpClient(new IPEndPoint(bestLocalIp, 0));
                    _udpClient.Client.SendBufferSize = 512 * 1024;
                }
            }
            catch { }

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

            CaptureStatusChanged?.Invoke($"Yakalama Aktif ({captureModeLabel}) -> {_targetIp}:{_targetPort}");

            // Pre-allocate zero-GC wire packet buffer: 14B header + max 960B payload
            byte[] wireBuf = new byte[14 + 960];
            wireBuf[0] = ProtocolConstants.MagicByte0;
            wireBuf[1] = ProtocolConstants.MagicByte1;
            wireBuf[2] = ProtocolConstants.ProtocolVersion;
            wireBuf[3] = (byte)PacketType.AudioChunk;
            wireBuf[4] = 2; // Stereo
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(wireBuf.AsSpan(5, 4), 48000);
            wireBuf[9] = 16; // 16-bit PCM

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
                        byte[]? directPcm = null;
                        byte* pcmPtr;
                        int totalPcmBytes;

                        if (sampleRate == 48000 && channels == 2 && bitsPerSample == 16 && !isFloat)
                        {
                            // Zero-copy fast path (Process Loopback outputs native 48kHz Stereo S16LE!)
                            pcmPtr = (byte*)pData;
                            totalPcmBytes = (int)(numFramesToRead * 4);
                        }
                        else
                        {
                            directPcm = ConvertToPcm16Stereo(pData, numFramesToRead, channels, sampleRate, bitsPerSample, isFloat, isSilent: false);
                            totalPcmBytes = directPcm.Length;
                            pcmPtr = null;
                        }

                        if (totalPcmBytes > 0 && _udpClient != null)
                        {
                            int offset = 0;
                            while (offset < totalPcmBytes && _isRunning)
                            {
                                int chunkLen = Math.Min(960, totalPcmBytes - offset);
                                chunkLen -= (chunkLen % 4);
                                if (chunkLen <= 0) break;

                                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(wireBuf.AsSpan(10, 2), _sequence++);
                                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(wireBuf.AsSpan(12, 2), (ushort)chunkLen);

                                if (directPcm != null)
                                {
                                    Buffer.BlockCopy(directPcm, offset, wireBuf, 14, chunkLen);
                                }
                                else
                                {
                                    Marshal.Copy((IntPtr)(pcmPtr + offset), wireBuf, 14, chunkLen);
                                }

                                try
                                {
                                    _udpClient.Send(wireBuf, 14 + chunkLen, remoteEp);
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

    private static bool TryActivateProcessLoopbackClient(
        out IntPtr pAudioClient,
        out IntPtr pFormat,
        out uint channels,
        out uint sampleRate,
        out ushort bitsPerSample,
        out bool isFloat)
    {
        pAudioClient = IntPtr.Zero;
        pFormat = IntPtr.Zero;
        channels = 2;
        sampleRate = 48000;
        bitsPerSample = 16;
        isFloat = false;

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348) || IntPtr.Size != 8)
            return false;

        IntPtr pActParams = IntPtr.Zero;
        IntPtr pProp = IntPtr.Zero;
        IntPtr pOp = IntPtr.Zero;
        try
        {
            var actParams = new AUDIOCLIENT_ACTIVATION_PARAMS
            {
                ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
                ProcessLoopbackParams = new AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
                {
                    TargetProcessId = (uint)Environment.ProcessId,
                    ProcessLoopbackMode = 1 // PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE
                }
            };
            pActParams = Marshal.AllocCoTaskMem(Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>());
            Marshal.StructureToPtr(actParams, pActParams, false);

            var prop = new PROPVARIANT_BLOB
            {
                vt = 65, // VT_BLOB
                cbSize = (uint)Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>(),
                pBlobData = pActParams
            };
            pProp = Marshal.AllocCoTaskMem(Marshal.SizeOf<PROPVARIANT_BLOB>());
            Marshal.StructureToPtr(prop, pProp, false);

            using var handler = new ProcessLoopbackCompletionHandler();
            Guid iidAC = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
            int hr = ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iidAC, pProp, handler, out pOp);
            if (hr != 0)
                return false;

            if (!handler.Done.WaitOne(2000) || handler.Operation == IntPtr.Zero)
                return false;

            IntPtr opVtbl = Marshal.ReadIntPtr(handler.Operation);
            var getRes = Marshal.GetDelegateForFunctionPointer<GetActivateResultDelegate>(Marshal.ReadIntPtr(opVtbl, 3 * IntPtr.Size));
            int resHr = getRes(handler.Operation, out int actHr, out pAudioClient);
            if (resHr != 0 || actHr != 0 || pAudioClient == IntPtr.Zero)
            {
                if (pAudioClient != IntPtr.Zero) { Marshal.Release(pAudioClient); pAudioClient = IntPtr.Zero; }
                return false;
            }

            var wfx = new WAVEFORMATEX
            {
                wFormatTag = WAVE_FORMAT_PCM,
                nChannels = 2,
                nSamplesPerSec = 48000,
                wBitsPerSample = 16,
                nBlockAlign = 4,
                nAvgBytesPerSec = 48000 * 4,
                cbSize = 0
            };
            pFormat = Marshal.AllocCoTaskMem(Marshal.SizeOf<WAVEFORMATEX>());
            Marshal.StructureToPtr(wfx, pFormat, false);
            return true;
        }
        catch
        {
            if (pAudioClient != IntPtr.Zero) { Marshal.Release(pAudioClient); pAudioClient = IntPtr.Zero; }
            if (pFormat != IntPtr.Zero) { Marshal.FreeCoTaskMem(pFormat); pFormat = IntPtr.Zero; }
            return false;
        }
        finally
        {
            if (pOp != IntPtr.Zero) Marshal.Release(pOp);
            if (pProp != IntPtr.Zero) Marshal.FreeCoTaskMem(pProp);
            if (pActParams != IntPtr.Zero) Marshal.FreeCoTaskMem(pActParams);
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

    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig]
        int ActivateCompleted(IntPtr activateOperation);
    }

    [ComVisible(true)]
    private sealed class ProcessLoopbackCompletionHandler : IActivateAudioInterfaceCompletionHandler, IDisposable
    {
        public readonly ManualResetEvent Done = new(false);
        public IntPtr Operation;

        public int ActivateCompleted(IntPtr activateOperation)
        {
            Operation = activateOperation;
            if (Operation != IntPtr.Zero) Marshal.AddRef(Operation);
            Done.Set();
            return 0;
        }

        public void Dispose()
        {
            if (Operation != IntPtr.Zero)
            {
                Marshal.Release(Operation);
                Operation = IntPtr.Zero;
            }
            Done.Dispose();
        }
    }

    [DllImport("mmdevapi.dll", CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(
        string deviceInterfacePath,
        [In] ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IntPtr activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
    {
        public uint TargetProcessId;
        public uint ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public uint ActivationType;
        public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PROPVARIANT_BLOB
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint cbSize;
        [FieldOffset(16)] public IntPtr pBlobData;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetActivateResultDelegate(IntPtr thisPtr, out int activateResult, out IntPtr activatedInterface);

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
