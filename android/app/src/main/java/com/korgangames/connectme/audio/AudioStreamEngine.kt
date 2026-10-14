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

        try {
            var record: AudioRecord? = null

            // In Android 10+ (API 29+), try AudioPlaybackCaptureConfiguration
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q && mediaProjection != null) {
                try {
                    val captureConfig = AudioPlaybackCaptureConfiguration.Builder(mediaProjection)
                        .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
                        .addMatchingUsage(AudioAttributes.USAGE_GAME)
                        .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
                        .build()

                    record = AudioRecord.Builder()
                        .setAudioPlaybackCaptureConfig(captureConfig)
                        .setAudioFormat(format)
                        .setBufferSizeInBytes(bufferSize)
                        .build()

                    Log.i(TAG, "Internal system audio playback capture initialized successfully")
                } catch (e: Throwable) {
                    Log.w(TAG, "AudioPlaybackCapture failed (${e.message}), falling back to MIC source: ${e.message}")
                    record = null
                }
            }

            // Fallback to high-quality audio source if mediaProjection wasn't used or failed
            if (record == null || record.state != AudioRecord.STATE_INITIALIZED) {
                try {
                    record?.release()
                } catch (_: Throwable) {}
                try {
                    record = AudioRecord(
                        MediaRecorder.AudioSource.MIC,
                        SAMPLE_RATE,
                        channelConfig,
                        audioEncoding,
                        bufferSize
                    )
                    Log.i(TAG, "Initialized microphone audio source fallback")
                } catch (t: Throwable) {
                    Log.e(TAG, "Microphone fallback failed: ${t.message}", t)
                    record = null
                }
            }

            audioRecord = record

            if (audioRecord == null || audioRecord?.state != AudioRecord.STATE_INITIALIZED) {
                Log.e(TAG, "AudioRecord initialization failed!")
                stop()
                onStatusChanged?.invoke(false, "Ses yakalama motoru başlatılamadı")
                return false
            }

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

    private fun streamAudioLoop(hostIp: String) {
        val targetAddr = try {
            InetAddress.getByName(hostIp)
        } catch (ex: Throwable) {
            Log.e(TAG, "Invalid host IP $hostIp: ${ex.message}")
            stop()
            return
        }

        // 20ms audio frame @ 48kHz Stereo 16-bit:
        // 48000 samples/sec * 0.02s = 960 samples/channel * 2 channels * 2 bytes = 3840 bytes
        // Using 1920 bytes (10ms stereo) for lower latency
        val pcmChunkSize = 1920
        val pcmBuffer = ByteArray(pcmChunkSize)
        val packetBuffer = ByteArray(14 + pcmChunkSize)
        var sequence: Short = 0

        while (isRunning.get()) {
            val record = audioRecord ?: break
            val bytesRead = try {
                record.read(pcmBuffer, 0, pcmChunkSize)
            } catch (t: Throwable) {
                -1
            }
            if (bytesRead > 0) {
                val bb = ByteBuffer.wrap(packetBuffer).order(ByteOrder.LITTLE_ENDIAN)
                bb.put(ProtocolConstants.MAGIC_0)
                bb.put(ProtocolConstants.MAGIC_1)
                bb.put(ProtocolConstants.PROTOCOL_VERSION)
                bb.put(0x08.toByte()) // PacketType.AudioChunk
                bb.put(CHANNELS.toByte())
                bb.putInt(SAMPLE_RATE)
                bb.put(BITS_PER_SAMPLE.toByte())
                bb.putShort(sequence++)
                bb.putShort(bytesRead.toShort())
                bb.put(pcmBuffer, 0, bytesRead)

                val packetLength = 14 + bytesRead
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

        try {
            audioRecord?.stop()
        } catch (_: Throwable) {}

        try {
            audioRecord?.release()
        } catch (_: Throwable) {}
        audioRecord = null

        try {
            udpSocket?.close()
        } catch (_: Throwable) {}
        udpSocket = null

        try {
            streamThread?.interrupt()
            streamThread?.join(200)
        } catch (_: Throwable) {}
        streamThread = null

        onStatusChanged?.invoke(false, "⚪ Ses aktarımı durduruldu")
    }
}
