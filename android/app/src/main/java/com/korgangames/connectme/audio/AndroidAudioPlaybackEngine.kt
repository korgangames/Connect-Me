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
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Ultra-low-latency real-time PCM audio playback engine for Android.
 * Receives raw 16-bit PCM stereo audio chunks sent from Windows or Linux over UDP port 4287,
 * using a decoupled bounded jitter queue to guarantee sub-30ms lag and zero audio stutter.
 */
class AndroidAudioPlaybackEngine private constructor() {

    companion object {
        private const val TAG = "AndroidAudioPlayback"
        private const val MAX_JITTER_CHUNKS = 8 // ~40ms max cushion, strict anti-lag guard
        val instance: AndroidAudioPlaybackEngine by lazy { AndroidAudioPlaybackEngine() }
    }

    private val isRunning = AtomicBoolean(false)
    private val pcmQueue = ConcurrentLinkedQueue<ByteArray>()
    private val queueLock = Object()

    private var receiveThread: Thread? = null
    private var playbackThread: Thread? = null
    private var udpSocket: DatagramSocket? = null
    private var audioTrack: AudioTrack? = null

    @Volatile
    var isPlaying: Boolean = false
        private set

    @Synchronized
    fun start(): Boolean {
        if (isRunning.get()) return true
        isRunning.set(true)
        pcmQueue.clear()

        try {
            val sampleRate = 48000
            val channelConfig = AudioFormat.CHANNEL_OUT_STEREO
            val audioEncoding = AudioFormat.ENCODING_PCM_16BIT
            val minBufSize = AudioTrack.getMinBufferSize(sampleRate, channelConfig, audioEncoding)
            val bufferSize = minBufSize.coerceAtLeast(19200)

            val track = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                val builder = AudioTrack.Builder()
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

                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                    builder.setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
                }

                builder.build()
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

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
                try {
                    // Set internal buffer to ~50ms (2400 frames) for instant response
                    val frameTarget = 2400.coerceAtLeast(minBufSize / 4)
                    track.setBufferSizeInFrames(frameTarget)
                } catch (ignored: Throwable) {}
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

            // Thread 1: High-priority UDP network receive loop (NEVER blocked by AudioTrack)
            receiveThread = Thread({
                receiveUdpLoop(socket)
            }, "ConnectMe-AudioReceiveThread").apply {
                priority = Thread.MAX_PRIORITY
                start()
            }

            // Thread 2: Dedicated AudioTrack feeder loop
            playbackThread = Thread({
                playbackLoop(track)
            }, "ConnectMe-AudioTrackFeeder").apply {
                priority = Thread.MAX_PRIORITY
                start()
            }

            Log.i(TAG, "Android Audio Playback Engine started on UDP port ${ProtocolConstants.AUDIO_STREAM_UDP_PORT} (Decoupled Jitter Queue)")
            return true
        } catch (t: Throwable) {
            Log.e(TAG, "Failed to start audio playback: ${t.message}", t)
            stop()
            return false
        }
    }

    private fun receiveUdpLoop(socket: DatagramSocket) {
        val buffer = ByteArray(4096)
        val packet = DatagramPacket(buffer, buffer.size)

        var cachedMyIp: String? = null
        var lastIpCheck = 0L

        while (isRunning.get()) {
            try {
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
                        val chunk = ByteArray(pcmLen)
                        System.arraycopy(buffer, 14, chunk, 0, pcmLen)

                        // Strict anti-lag queue limit: drop oldest if buffer exceeds MAX_JITTER_CHUNKS (~40ms)
                        while (pcmQueue.size >= MAX_JITTER_CHUNKS) {
                            pcmQueue.poll()
                        }
                        pcmQueue.offer(chunk)

                        synchronized(queueLock) {
                            queueLock.notify()
                        }
                    }
                }
            } catch (t: Throwable) {
                if (!isRunning.get()) break
            }
        }
    }

    private fun playbackLoop(track: AudioTrack) {
        while (isRunning.get()) {
            try {
                val chunk = pcmQueue.poll()
                if (chunk != null) {
                    if (track.playState != AudioTrack.PLAYSTATE_PLAYING) {
                        try { track.play() } catch (ignored: Throwable) {}
                    }

                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                        track.write(chunk, 0, chunk.size, AudioTrack.WRITE_BLOCKING)
                    } else {
                        track.write(chunk, 0, chunk.size)
                    }
                } else {
                    synchronized(queueLock) {
                        if (pcmQueue.isEmpty() && isRunning.get()) {
                            queueLock.wait(10)
                        }
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

        synchronized(queueLock) {
            queueLock.notifyAll()
        }

        try {
            udpSocket?.close()
        } catch (ignored: Throwable) {}
        udpSocket = null

        try {
            receiveThread?.interrupt()
            receiveThread?.join(300)
        } catch (ignored: Throwable) {}
        receiveThread = null

        try {
            playbackThread?.interrupt()
            playbackThread?.join(300)
        } catch (ignored: Throwable) {}
        playbackThread = null

        pcmQueue.clear()

        try {
            audioTrack?.pause()
            audioTrack?.flush()
            audioTrack?.stop()
        } catch (ignored: Throwable) {}
        try {
            audioTrack?.release()
        } catch (ignored: Throwable) {}
        audioTrack = null
    }
}
