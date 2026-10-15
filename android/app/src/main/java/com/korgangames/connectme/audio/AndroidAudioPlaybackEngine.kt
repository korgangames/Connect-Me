package com.korgangames.connectme.audio

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioTrack
import android.os.Build
import android.util.Log
import com.korgangames.connectme.protocol.ProtocolConstants
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Ultra-low-latency real-time PCM audio playback engine for Android.
 * Receives raw 16-bit PCM stereo audio chunks sent from Windows or Linux over UDP port 42852,
 * and plays them directly through Android's speaker or connected Bluetooth/wired headphones.
 */
class AndroidAudioPlaybackEngine private constructor() {

    companion object {
        private const val TAG = "AndroidAudioPlayback"
        val instance: AndroidAudioPlaybackEngine by lazy { AndroidAudioPlaybackEngine() }
    }

    private val isRunning = AtomicBoolean(false)
    private var receiveThread: Thread? = null
    private var udpSocket: DatagramSocket? = null
    private var audioTrack: AudioTrack? = null

    @Volatile
    var isPlaying: Boolean = false
        private set

    @Synchronized
    fun start(): Boolean {
        if (isRunning.get()) return true
        isRunning.set(true)

        try {
            val sampleRate = 48000
            val channelConfig = AudioFormat.CHANNEL_OUT_STEREO
            val audioEncoding = AudioFormat.ENCODING_PCM_16BIT
            val minBufSize = AudioTrack.getMinBufferSize(sampleRate, channelConfig, audioEncoding)
            val bufferSize = (minBufSize * 2).coerceAtLeast(7680)

            val track = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                AudioTrack.Builder()
                    .setAudioAttributes(
                        AudioAttributes.Builder()
                            .setUsage(AudioAttributes.USAGE_MEDIA)
                            .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                            .build()
                    )
                    .setAudioFormat(
                        AudioFormat.Builder()
                            .setEncoding(audioEncoding)
                            .setSampleRate(sampleRate)
                            .setChannelMask(channelConfig)
                            .build()
                    )
                    .setBufferSizeInBytes(bufferSize)
                    .setTransferMode(AudioTrack.MODE_STREAM)
                    .build()
            } else {
                @Suppress("DEPRECATION")
                AudioTrack(
                    AudioManager.STREAM_MUSIC,
                    sampleRate,
                    channelConfig,
                    audioEncoding,
                    bufferSize,
                    AudioTrack.MODE_STREAM
                )
            }

            try {
                track.play()
            } catch (t: Throwable) {
                Log.w(TAG, "AudioTrack play deferred: ${t.message}")
            }
            audioTrack = track

            val socket = DatagramSocket(null).apply {
                reuseAddress = true
                receiveBufferSize = 512 * 1024
                bind(InetSocketAddress(ProtocolConstants.AUDIO_STREAM_UDP_PORT))
            }
            udpSocket = socket

            isPlaying = true
            receiveThread = Thread({
                receiveAndPlayLoop(socket, track)
            }, "ConnectMe-AudioPlaybackThread").apply {
                priority = Thread.MAX_PRIORITY
                start()
            }
            Log.i(TAG, "Android Audio Playback Engine started on UDP port ${ProtocolConstants.AUDIO_STREAM_UDP_PORT}")
            return true
        } catch (t: Throwable) {
            Log.e(TAG, "Failed to start audio playback: ${t.message}", t)
            stop()
            return false
        }
    }

    private fun receiveAndPlayLoop(socket: DatagramSocket, track: AudioTrack) {
        val buffer = ByteArray(4096)
        val packet = DatagramPacket(buffer, buffer.size)

        var cachedMyIp: String? = null
        var lastIpCheck = 0L

        while (isRunning.get()) {
            try {
                // In Java DatagramPacket, reset packet length before each receive to prevent truncation
                packet.length = buffer.size
                socket.receive(packet)

                // Anti-Loopback / Anti-Feedback Protection
                val remoteAddr = packet.address
                if (remoteAddr != null) {
                    if (remoteAddr.isLoopbackAddress || remoteAddr.isAnyLocalAddress) {
                        continue
                    }
                    val now = System.currentTimeMillis()
                    if (now - lastIpCheck > 3000) {
                        cachedMyIp = com.korgangames.connectme.network.ConnectMeService.instance?.getLocalIpv4Address()
                        lastIpCheck = now
                    }
                    val senderIp = remoteAddr.hostAddress
                    if (cachedMyIp != null && cachedMyIp != "0.0.0.0" && senderIp == cachedMyIp) {
                        continue
                    }
                }

                val len = packet.length
                if (len >= 14 && buffer[0] == 'C'.code.toByte() && buffer[1] == 'M'.code.toByte() && buffer[3] == 0x08.toByte()) {
                    val pcmLen = ByteBuffer.wrap(buffer, 12, 2).order(ByteOrder.LITTLE_ENDIAN).short.toInt() and 0xFFFF
                    if (pcmLen > 0 && 14 + pcmLen <= len) {
                        if (track.playState != AudioTrack.PLAYSTATE_PLAYING) {
                            try { track.play() } catch (_: Throwable) {}
                        }
                        track.write(buffer, 14, pcmLen)
                    }
                }
            } catch (t: Throwable) {
                if (!isRunning.get()) break
            }
        }
    }

    @Synchronized
    fun stop() {
        isRunning.set(false)
        isPlaying = false

        try {
            udpSocket?.close()
        } catch (_: Throwable) {}
        udpSocket = null

        try {
            receiveThread?.interrupt()
            receiveThread?.join(300)
        } catch (_: Throwable) {}
        receiveThread = null

        try {
            audioTrack?.pause()
            audioTrack?.flush()
            audioTrack?.stop()
        } catch (_: Throwable) {}
        try {
            audioTrack?.release()
        } catch (_: Throwable) {}
        audioTrack = null
    }
}
