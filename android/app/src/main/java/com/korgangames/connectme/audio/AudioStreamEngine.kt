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

        // Enforce Android 10+ and active MediaProjection for system audio capture.
        // Never fall back to room microphone!
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q || mediaProjection == null) {
            Log.w(TAG, "Dahili sistem sesi yakalama için Android 10+ ve MediaProjection izni zorunludur.")
            stop()
            onStatusChanged?.invoke(false, "Dahili ses yakalama için ekran/ses izni verilmedi.")
            return false
        }

        targetHostIp = hostIp
        isRunning.set(true)

        val channelConfig = AudioFormat.CHANNEL_IN_STEREO
        val audioEncoding = AudioFormat.ENCODING_PCM_16BIT
        val minBufferSize = AudioRecord.getMinBufferSize(SAMPLE_RATE, channelConfig, audioEncoding)
        val bufferSize = minBufferSize.coerceAtLeast(3840)

        val format = AudioFormat.Builder()
            .setEncoding(audioEncoding)
            .setSampleRate(SAMPLE_RATE)
            .setChannelMask(channelConfig)
            .build()

        try {
            val captureConfig = AudioPlaybackCaptureConfiguration.Builder(mediaProjection)
                .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
                .addMatchingUsage(AudioAttributes.USAGE_GAME)
                .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
                .build()

            val record = AudioRecord.Builder()
                .setAudioPlaybackCaptureConfig(captureConfig)
                .setAudioFormat(format)
                .setBufferSizeInBytes(bufferSize)
                .build()

            if (record.state != AudioRecord.STATE_INITIALIZED) {
                Log.e(TAG, "AudioPlaybackCapture AudioRecord initialization failed!")
                try { record.release() } catch (_: Throwable) {}
                stop()
                onStatusChanged?.invoke(false, "Ses yakalama motoru başlatılamadı.")
                return false
            }

            audioRecord = record

            try {
                audioRecord?.startRecording()
                // Drain any stale hardware buffer so the very first packet sent is live
                val drainBuf = ByteArray(960)
                var drainCount = 5
                while (drainCount-- > 0 && Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                    val r = audioRecord?.read(drainBuf, 0, 960, AudioRecord.READ_NON_BLOCKING) ?: -1
                    if (r <= 0) break
                }
            } catch (t: Throwable) {
                Log.e(TAG, "AudioRecord startRecording failed: ${t.message}", t)
                stop()
                onStatusChanged?.invoke(false, "Ses kaydı başlatılamadı: ${t.message}")
                return false
            }

            udpSocket = DatagramSocket()

            isStreaming = true
            onStatusChanged?.invoke(true, "🟢 Sesi Hedefe Aktarılıyor ($SAMPLE_RATE Hz Stereo)")

            streamThread = Thread({
                streamAudioLoop(hostIp)
            }, "ConnectMe-AudioStreamThread").apply {
                priority = Thread.MAX_PRIORITY
                start()
            }

            return true
        } catch (ex: Throwable) {
            Log.e(TAG, "Failed to start audio streaming: ${ex.message}", ex)
            stop()
            onStatusChanged?.invoke(false, "Hata: ${ex.message}")
            return false
        }
    }

    private fun isBufferSilent(buffer: ByteArray, length: Int): Boolean {
        var i = 0
        while (i + 1 < length) {
            val sample = (buffer[i].toInt() and 0xFF) or (buffer[i + 1].toInt() shl 8)
            val sampleShort = sample.toShort().toInt()
            if (sampleShort > 40 || sampleShort < -40) {
                return false
            }
            i += 2
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

        // 240 samples * 2 channels * 2 bytes = 5ms @ 48kHz (total UDP packet: 14 + 960 = 974 bytes < 1500 MTU)
        // 100% immune to Wi-Fi IP fragmentation, drops, and kernel reassembly delays!
        val pcmChunkSize = 960
        val pcmBuffer = ByteArray(pcmChunkSize)
        val packetBuffer = ByteArray(14 + pcmChunkSize)
        var sequence: Short = 0
        var silentStreak = 0
        val maxSilentStreakHangover = 200 // 200 * 5ms = 1.0 second hangover before pausing

        while (isRunning.get()) {
            val record = audioRecord ?: break
            val bytesRead = try {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                    record.read(pcmBuffer, 0, pcmChunkSize, AudioRecord.READ_BLOCKING)
                } else {
                    record.read(pcmBuffer, 0, pcmChunkSize)
                }
            } catch (t: Throwable) {
                -1
            }

            if (bytesRead > 0) {
                // Ensure strict 4-byte frame alignment (16-bit Stereo: 2 ch * 2 bytes = 4 bytes per frame)
                val validBytes = bytesRead - (bytesRead % 4)
                if (validBytes <= 0) continue

                // Silence suppression with 1-second hangover:
                // Prevents clipping between spoken words or musical pauses, while pausing network transmission
                // after 1 second of continuous silence.
                if (isBufferSilent(pcmBuffer, validBytes)) {
                    silentStreak++
                    if (silentStreak > maxSilentStreakHangover) {
                        continue
                    }
                } else {
                    silentStreak = 0
                }

                val bb = ByteBuffer.wrap(packetBuffer).order(ByteOrder.LITTLE_ENDIAN)
                bb.put(ProtocolConstants.MAGIC_0)
                bb.put(ProtocolConstants.MAGIC_1)
                bb.put(ProtocolConstants.PROTOCOL_VERSION)
                bb.put(0x08.toByte()) // PacketType.AudioChunk
                bb.put(CHANNELS.toByte())
                bb.putInt(SAMPLE_RATE)
                bb.put(BITS_PER_SAMPLE.toByte())
                bb.putShort(sequence++)
                bb.putShort(validBytes.toShort())
                bb.put(pcmBuffer, 0, validBytes)

                val packetLength = 14 + validBytes
                try {
                    val datagram = DatagramPacket(packetBuffer, packetLength, targetAddr, AUDIO_UDP_PORT)
                    udpSocket?.send(datagram)
                } catch (ex: Throwable) {
                    if (!isRunning.get()) break
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
