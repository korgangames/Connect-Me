using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using ConnectMe.Core.Network;
using ConnectMe.Core.Protocol;

namespace ConnectMe.Windows.Audio;

/// <summary>
/// Ultra-low-latency Win32 native waveOut audio playback engine.
/// Receives raw PCM 16-bit audio packets streamed over UDP from Android, Linux, or other PCs,
/// and plays them directly through the central machine's default audio endpoint (e.g. connected headphones).
/// Requires 0 external dependencies (uses standard winmm.dll).
/// </summary>
public sealed class WindowsAudioPlaybackEngine : IDisposable
{
    private const int WAVE_MAPPER = -1;
    private const int MMSYSERR_NOERROR = 0;
    private const int WAVE_FORMAT_PCM = 1;
    private const int CALLBACK_NULL = 0x00000000;
    private const int WHDR_DONE = 0x00000001;

    private readonly ConnectMeNetworkNode _network;
    private readonly object _lock = new();

    private IntPtr _hWaveOut = IntPtr.Zero;
    private uint _currentSampleRate;
    private ushort _currentChannels;
    private ushort _currentBitsPerSample;

    private float _volume = 1.0f; // 0.0f to 1.0f
    private bool _isMuted;
    private bool _isEnabled = true;
    private bool _disposed;

    private string _activeSourceDescription = "Bekleniyor...";
    private long _totalAudioBytesReceived;
    private DateTimeOffset _lastAudioPacketTime = DateTimeOffset.MinValue;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            _isEnabled = value;
            if (!_isEnabled)
            {
                CloseWaveOut();
            }
        }
    }

    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1f);
    }

    public bool IsMuted
    {
        get => _isMuted;
        set => _isMuted = value;
    }

    public bool IsReceivingAudio =>
        _isEnabled && (DateTimeOffset.UtcNow - _lastAudioPacketTime).TotalSeconds < 2.0;

    public string ActiveSourceDescription => _activeSourceDescription;
    public long TotalAudioBytesReceived => _totalAudioBytesReceived;

    public event Action<string>? AudioStatusChanged;

    public WindowsAudioPlaybackEngine(ConnectMeNetworkNode network)
    {
        _network = network;
        _network.RemoteAudioChunkReceived += OnRemoteAudioChunkReceived;
    }

    private void OnRemoteAudioChunkReceived(AudioChunkPacket packet, IPEndPoint sender)
    {
        if (!_isEnabled || _disposed)
            return;

        if (packet.PcmData == null || packet.PcmData.Length == 0)
            return;

        _lastAudioPacketTime = DateTimeOffset.UtcNow;
        _totalAudioBytesReceived += packet.PcmData.Length;

        string senderIp = sender.Address.ToString();
        var peer = _network.MutuallyPairedPeers.FirstOrDefault(p => p.IpAddress == senderIp);
        string peerName = peer?.DeviceName ?? senderIp;
        _activeSourceDescription = $"{peerName} ({packet.SampleRate} Hz {(packet.Channels == 2 ? "Stereo" : "Mono")})";

        lock (_lock)
        {
            if (_disposed || !_isEnabled)
                return;

            EnsureWaveOutOpen(packet.SampleRate, packet.Channels, packet.BitsPerSample);

            if (_hWaveOut == IntPtr.Zero)
                return;

            if (_isMuted || _volume <= 0.001f)
                return;

            byte[] pcm = packet.PcmData;

            // Apply software volume scaling if volume is reduced
            if (Math.Abs(_volume - 1.0f) > 0.02f)
            {
                pcm = (byte[])pcm.Clone();
                ApplyVolume(pcm, _volume);
            }

            PlayPcmBuffer(pcm);
        }

        AudioStatusChanged?.Invoke(_activeSourceDescription);
    }

    private static void ApplyVolume(byte[] pcm, float vol)
    {
        Span<short> samples = MemoryMarshal.Cast<byte, short>(pcm.AsSpan());
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)Math.Clamp((int)(samples[i] * vol), short.MinValue, short.MaxValue);
        }
    }

    private void EnsureWaveOutOpen(uint sampleRate, byte channels, byte bitsPerSample)
    {
        if (sampleRate == 0) sampleRate = 48000;
        if (channels == 0) channels = 2;
        if (bitsPerSample == 0) bitsPerSample = 16;

        if (_hWaveOut != IntPtr.Zero &&
            _currentSampleRate == sampleRate &&
            _currentChannels == channels &&
            _currentBitsPerSample == bitsPerSample)
        {
            return;
        }

        CloseWaveOut();

        var format = new WAVEFORMATEX
        {
            wFormatTag = WAVE_FORMAT_PCM,
            nChannels = channels,
            nSamplesPerSec = sampleRate,
            wBitsPerSample = bitsPerSample,
            nBlockAlign = (ushort)(channels * (bitsPerSample / 8)),
            cbSize = 0
        };
        format.nAvgBytesPerSec = format.nSamplesPerSec * format.nBlockAlign;

        int res = waveOutOpen(out _hWaveOut, unchecked((uint)WAVE_MAPPER), ref format, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
        if (res == MMSYSERR_NOERROR)
        {
            _currentSampleRate = sampleRate;
            _currentChannels = channels;
            _currentBitsPerSample = bitsPerSample;
        }
        else
        {
            _hWaveOut = IntPtr.Zero;
        }
    }

    private void PlayPcmBuffer(byte[] pcm)
    {
        if (_hWaveOut == IntPtr.Zero || pcm.Length == 0)
            return;

        // Allocate unmanaged memory for the PCM buffer and WAVEHDR
        IntPtr pData = Marshal.AllocHGlobal(pcm.Length);
        Marshal.Copy(pcm, 0, pData, pcm.Length);

        var hdr = new WAVEHDR
        {
            lpData = pData,
            dwBufferLength = (uint)pcm.Length,
            dwFlags = 0
        };

        IntPtr pHdr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEHDR>());
        Marshal.StructureToPtr(hdr, pHdr, false);

        int prepRes = waveOutPrepareHeader(_hWaveOut, pHdr, (uint)Marshal.SizeOf<WAVEHDR>());
        if (prepRes == MMSYSERR_NOERROR)
        {
            int writeRes = waveOutWrite(_hWaveOut, pHdr, (uint)Marshal.SizeOf<WAVEHDR>());
            if (writeRes == MMSYSERR_NOERROR)
            {
                // Queue cleanup task after estimated playback duration
                int durationMs = (int)((pcm.Length / (double)(_currentSampleRate * _currentChannels * 2)) * 1000.0) + 100;
                Task.Delay(Math.Max(50, durationMs)).ContinueWith(_ =>
                {
                    try
                    {
                        if (_hWaveOut != IntPtr.Zero)
                        {
                            waveOutUnprepareHeader(_hWaveOut, pHdr, (uint)Marshal.SizeOf<WAVEHDR>());
                        }
                    }
                    catch { }
                    finally
                    {
                        try { Marshal.FreeHGlobal(pData); } catch { }
                        try { Marshal.FreeHGlobal(pHdr); } catch { }
                    }
                });
                return;
            }
        }

        // Cleanup on failure
        try { Marshal.FreeHGlobal(pData); } catch { }
        try { Marshal.FreeHGlobal(pHdr); } catch { }
    }

    private void CloseWaveOut()
    {
        if (_hWaveOut != IntPtr.Zero)
        {
            try
            {
                waveOutReset(_hWaveOut);
                waveOutClose(_hWaveOut);
            }
            catch { }
            _hWaveOut = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _network.RemoteAudioChunkReceived -= OnRemoteAudioChunkReceived;
        lock (_lock)
        {
            CloseWaveOut();
        }
    }

    // =========================================================================
    // Win32 Native Interop (winmm.dll)
    // =========================================================================

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
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(
        out IntPtr hWaveOut,
        uint uDeviceID,
        ref WAVEFORMATEX lpFormat,
        IntPtr dwCallback,
        IntPtr dwInstance,
        uint fdwOpen);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, uint uSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr hWaveOut, IntPtr lpWaveOutHdr, uint uSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, uint uSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr hWaveOut);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr hWaveOut);
}
