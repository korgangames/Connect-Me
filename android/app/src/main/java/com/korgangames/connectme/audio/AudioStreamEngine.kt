package com.korgangames.connectme.audio

import android.annotation.SuppressLint
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioPlaybackCaptureConfiguration
import android.media.AudioRecord
import android.media.MediaRecorder
import android.media.projection.MediaProjection
import android.os.Build
import android.util.Log
import com.korgangames.connectme.protocol.ProtocolConstants
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Low-latency real-time PCM audio streaming engine for Android.
 * Captures internal system audio (YouTube, Spotify, games, notifications) using
 * AudioPlaybackCaptureConfiguration (Android 10+ / MediaProjection) or high-quality mic fallback,
 * and streams raw 16-bit PCM chunks over UDP port 42852 to the central computer's headphones.
 */
class AudioStreamEngine private constructor() {

    companion object {
        private const val TAG = "ConnectMeAudio"
        const val SAMPLE_RATE = 48000
        const val CHANNELS = 2
        const val BITS_PER_SAMPLE = 16
        const val AUDIO_UDP_PORT = ProtocolConstants.AUDIO_STREAM_UDP_PORT

        val instance: AudioStreamEngine by lazy { AudioStreamEngine() }
    }

    private val isRunning = AtomicBoolean(false)
    private var streamThread: Thread? = null
    private var audioRecord: AudioRecord? = null
    private var udpSocket: DatagramSocket? = null

    @Volatile
    var isStreaming: Boolean = false
        private set

    @Volatile
    var targetHostIp: String? = null

    var onStatusChanged: ((Boolean, String) -> Unit)? = null

    @SuppressLint("MissingPermission")
    fun start(hostIp: String, mediaProjection: MediaProjection? = null): Boolean {
        if (isRunning.get()) {
            if (targetHostIp == hostIp) return true
            stop()
        }

        // Anti-Loopback Protection: Never stream audio to self
        val myIp = com.korgangames.connectme.network.ConnectMeService.instance?.getLocalIpv4Address()
        if (hostIp == "127.0.0.1" || (myIp != null && myIp != "0.0.0.0" && hostIp == myIp)) {
            Log.w(TAG, "Refusing to stream audio to local/self IP ($hostIp)")
            stop()
            onStatusChanged?.invoke(false, "Kendi cihazınıza ses yönlendirilemez")
            return false
        }

        targetHostIp = hostIp
        isRunning.set(true)

        val channelConfig = AudioFormat.CHANNEL_IN_STEREO
        val audioEncoding = AudioFormat.ENCODING_PCM_16BIT
        val minBufferSize = AudioRecord.getMinBufferSize(SAMPLE_RATE, channelConfig, audioEncoding)
        val bufferSize = (minBufferSize * 2).coerceAtLeast(7680)

        val format = AudioFormat.Builder()
            .setEncoding(audioEncoding)
            .setSampleRate(SAMPLE_RATE)
            .setChannelMask(channelConfig)
            .build()

        var record: AudioRecord? = null
        var captureMode = "Bilinmiyor"

        // 1. AudioPlaybackCapture (clean internal system audio) via MediaProjection (Android 10+)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q && mediaProjection != null) {
            try {
                val captureConfig = AudioPlaybackCaptureConfiguration.Builder(mediaProjection)
                    .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
                    .addMatchingUsage(AudioAttributes.USAGE_GAME)
                    .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
                    .build()

                val rec = AudioRecord.Builder()
                    .setAudioPlaybackCaptureConfig(captureConfig)
                    .setAudioFormat(format)
                    .setBufferSizeInBytes(bufferSize)
                    .build()

                if (rec.state == AudioRecord.STATE_INITIALIZED) {
                    record = rec
                    captureMode = "Dahili Sistem Sesi"
                    Log.i(TAG, "AudioPlaybackCapture ($captureMode) başarıyla hazırlandı.")
                } else {
                    try { rec.release() } catch (_: Throwable) {}
                }
            } catch (t: Throwable) {
                Log.w(TAG, "AudioPlaybackCapture başlatılamadı: ${t.message}")
            }
        }

        // Never fall back to ambient microphone for system audio streaming (prevents room noise & static hiss)
        if (record == null || record.state != AudioRecord.STATE_INITIALIZED) {
            Log.w(TAG, "Sistem sesi yakalama başlatılamadı (MediaProjection gereklidir).")
            stop()
            onStatusChanged?.invoke(false, "Sistem sesini aktarmak için MediaProjection ekran/ses izni gereklidir.")
            return false
        }

        audioRecord = record

        try {
            audioRecord?.startRecording()
        } catch (t: Throwable) {
            Log.e(TAG, "AudioRecord startRecording failed: ${t.message}", t)
            stop()
            onStatusChanged?.invoke(false, "Ses kaydı başlatılamadı: ${t.message}")
            return false
        }

        udpSocket = DatagramSocket()

        isStreaming = true
        onStatusChanged?.invoke(true, "🟢 Ses Aktarılıyor: $captureMode ($SAMPLE_RATE Hz Stereo)")

        streamThread = Thread({
            streamAudioLoop(hostIp)
        }, "ConnectMe-AudioStreamThread").apply {
            priority = Thread.MAX_PRIORITY
            start()
        }

        return true
    }

    private fun streamAudioLoop(hostIp: String) {
        val targetAddr = try {
            InetAddress.getByName(hostIp)
        } catch (ex: Throwable) {
            Log.e(TAG, "Invalid host IP $hostIp: ${ex.message}")
            stop()
            return
        }

        // Native 10ms hardware read buffer (480 samples * 2 ch * 2 bytes = 1920 bytes @ 48kHz Stereo)
        val pcmReadSize = 1920
        val pcmBuffer = ByteArray(pcmReadSize)
        val packetBuffer = ByteArray(14 + 960)
        val bb = ByteBuffer.wrap(packetBuffer).order(ByteOrder.LITTLE_ENDIAN)
        packetBuffer[0] = ProtocolConstants.MAGIC_0
        packetBuffer[1] = ProtocolConstants.MAGIC_1
        packetBuffer[2] = ProtocolConstants.PROTOCOL_VERSION
        packetBuffer[3] = 0x08.toByte() // PacketType.AudioChunk
        packetBuffer[4] = CHANNELS.toByte()
        bb.putInt(5, SAMPLE_RATE)
        packetBuffer[9] = BITS_PER_SAMPLE.toByte()

        val datagram = DatagramPacket(packetBuffer, packetBuffer.size, targetAddr, AUDIO_UDP_PORT)
        var sequence = 0

        while (isRunning.get()) {
            val record = audioRecord ?: break
            val bytesRead = try {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                    record.read(pcmBuffer, 0, pcmReadSize, AudioRecord.READ_BLOCKING)
                } else {
                    record.read(pcmBuffer, 0, pcmReadSize)
                }
            } catch (t: Throwable) {
                -1
            }

            if (bytesRead > 0) {
                // Ensure strict 4-byte frame alignment (16-bit Stereo: 2 ch * 2 bytes = 4 bytes per frame)
                val validBytes = bytesRead - (bytesRead % 4)
                if (validBytes <= 0) continue

                // Skip transmitting pure digital silence to save battery and Wi-Fi airtime
                var hasAudio = false
                for (i in 0 until validBytes step 2) {
                    if (pcmBuffer[i] != 0.toByte() || pcmBuffer[i + 1] != 0.toByte()) {
                        hasAudio = true
                        break
                    }
                }
                if (!hasAudio) continue

                // Send in MTU-safe chunks of at most 960 bytes (5ms each) to eliminate Wi-Fi IP fragmentation
                var offset = 0
                while (offset < validBytes && isRunning.get()) {
                    val chunkLen = Math.min(960, validBytes - offset)

                    bb.putShort(10, (sequence++ and 0xFFFF).toShort())
                    bb.putShort(12, chunkLen.toShort())
                    System.arraycopy(pcmBuffer, offset, packetBuffer, 14, chunkLen)

                    datagram.length = 14 + chunkLen
                    try {
                        udpSocket?.send(datagram)
                    } catch (ex: Throwable) {
                        if (!isRunning.get()) break
                    }

                    offset += chunkLen
                }
            } else {
                try {
                    Thread.sleep(5)
                } catch (ie: InterruptedException) {
                    break
                }
            }
        }
    }

    fun stop() {
        isRunning.set(false)
        isStreaming = false
        targetHostIp = null

        try {
            udpSocket?.close()
        } catch (_: Throwable) {}
        udpSocket = null

        try {
            streamThread?.interrupt()
            streamThread?.join(300)
        } catch (_: Throwable) {}
        streamThread = null

        try {
            audioRecord?.stop()
        } catch (_: Throwable) {}
        try {
            audioRecord?.release()
        } catch (_: Throwable) {}
        audioRecord = null

        onStatusChanged?.invoke(false, "⚪ Ses aktarımı durduruldu")
    }
}
