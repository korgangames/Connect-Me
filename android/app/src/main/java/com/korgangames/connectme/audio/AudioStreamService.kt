package com.korgangames.connectme.audio

import android.app.Activity
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.util.Log
import androidx.core.app.NotificationCompat
import com.korgangames.connectme.MainActivity

/**
 * Dedicated Foreground Service for MediaProjection Audio Streaming.
 * In Android 14+, mediaProjection FGS type can only run when an active
 * MediaProjection token exists and must not be bound to the main application launch service.
 */
class AudioStreamService : Service() {

    companion object {
        private const val TAG = "AudioStreamService"
        const val ACTION_START_AUDIO = "com.korgangames.connectme.START_AUDIO"
        const val ACTION_STOP_AUDIO = "com.korgangames.connectme.STOP_AUDIO"
        private const val CHANNEL_ID = "connect_me_audio_channel"
        private const val NOTIFICATION_ID = 4286

        @Volatile
        var activeProjection: MediaProjection? = null
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val action = intent?.action ?: return START_NOT_STICKY

        when (action) {
            ACTION_STOP_AUDIO -> {
                stopAudioStreaming()
                return START_NOT_STICKY
            }
            ACTION_START_AUDIO -> {
                val hostIp = intent.getStringExtra("hostIp")
                val resultCode = intent.getIntExtra("resultCode", Activity.RESULT_CANCELED)
                val resultData: Intent? = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                    intent.getParcelableExtra("resultData", Intent::class.java)
                } else {
                    @Suppress("DEPRECATION")
                    intent.getParcelableExtra("resultData")
                }

                if (hostIp.isNullOrBlank()) {
                    stopAudioStreaming()
                    return START_NOT_STICKY
                }

                // 1. In Android 14+ (API 34+), startForeground with FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION
                // MUST be called before calling MediaProjectionManager.getMediaProjection().
                startForegroundAudio()

                var projection = activeProjection
                if (resultCode == Activity.RESULT_OK && resultData != null) {
                    try {
                        val mpm = getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
                        projection = mpm.getMediaProjection(resultCode, resultData)
                        activeProjection = projection

                        projection?.registerCallback(object : MediaProjection.Callback() {
                            override fun onStop() {
                                Log.i(TAG, "MediaProjection stopped by system")
                                stopAudioStreaming()
                            }
                        }, Handler(Looper.getMainLooper()))
                    } catch (t: Throwable) {
                        Log.e(TAG, "Failed to initialize MediaProjection: ${t.message}", t)
                        projection = null
                    }
                }

                if (projection == null) {
                    Log.w(TAG, "Cannot start AudioStreamService without active MediaProjection")
                    stopAudioStreaming()
                    return START_NOT_STICKY
                }

                val started = AudioStreamEngine.instance.start(hostIp, projection)
                if (!started) {
                    stopAudioStreaming()
                }
            }
        }
        return START_NOT_STICKY
    }

    private fun startForegroundAudio() {
        try {
            val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                val chan = NotificationChannel(
                    CHANNEL_ID,
                    "Connect Me Ses Aktarımı",
                    NotificationManager.IMPORTANCE_LOW
                )
                nm.createNotificationChannel(chan)
            }

            val pendingIntent = PendingIntent.getActivity(
                this,
                0,
                Intent(this, MainActivity::class.java),
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
            )

            val notification: Notification = NotificationCompat.Builder(this, CHANNEL_ID)
                .setContentTitle("Connect Me — Ses Aktarımı Aktif 🎧")
                .setContentText("Telefon sesleri ultra-düşük gecikmeyle hedef cihaza aktarılıyor.")
                .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
                .setContentIntent(pendingIntent)
                .setOngoing(true)
                .build()

        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
                try {
                    startForeground(
                        NOTIFICATION_ID,
                        notification,
                        ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION
                    )
                } catch (se: SecurityException) {
                    Log.w(TAG, "startForeground with MEDIA_PROJECTION failed, falling back to basic foreground: ${se.message}")
                    startForeground(NOTIFICATION_ID, notification)
                }
            } else if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                try {
                    startForeground(
                        NOTIFICATION_ID,
                        notification,
                        ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION
                    )
                } catch (se: SecurityException) {
                    startForeground(NOTIFICATION_ID, notification)
                }
            } else {
                startForeground(NOTIFICATION_ID, notification)
            }
        } catch (t: Throwable) {
            Log.e(TAG, "startForegroundAudio error: ${t.message}", t)
        }
    }

    private fun stopAudioStreaming() {
        try {
            AudioStreamEngine.instance.stop()
        } catch (_: Throwable) {}

        try {
            activeProjection?.stop()
        } catch (_: Throwable) {}
        activeProjection = null

        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
                stopForeground(STOP_FOREGROUND_REMOVE)
            } else {
                @Suppress("DEPRECATION")
                stopForeground(true)
            }
        } catch (_: Throwable) {}

        stopSelf()
    }

    override fun onDestroy() {
        stopAudioStreaming()
        super.onDestroy()
    }
}
