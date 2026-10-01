package com.korgangames.connectme.audio

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.media.projection.MediaProjection
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import com.korgangames.connectme.MainActivity

/**
 * Dedicated Foreground Service for MediaProjection Audio Streaming.
 * In Android 14+, mediaProjection FGS type can only run when an active
 * MediaProjection token exists and must not be bound to the main application launch service.
 */
class AudioStreamService : Service() {

    companion object {
        const val ACTION_START_AUDIO = "com.korgangames.connectme.START_AUDIO"
        const val ACTION_STOP_AUDIO = "com.korgangames.connectme.STOP_AUDIO"
        private const val CHANNEL_ID = "connect_me_audio_channel"
        private const val NOTIFICATION_ID = 4286

        @Volatile
        var activeProjection: MediaProjection? = null
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP_AUDIO -> {
                AudioStreamEngine.instance.stop()
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
                    stopForeground(STOP_FOREGROUND_REMOVE)
                } else {
                    @Suppress("DEPRECATION")
                    stopForeground(true)
                }
                stopSelf()
                return START_NOT_STICKY
            }
            ACTION_START_AUDIO -> {
                val hostIp = intent.getStringExtra("hostIp")
                if (hostIp != null) {
                    startForegroundAudio()
                    AudioStreamEngine.instance.start(hostIp, activeProjection)
                }
            }
        }
        return START_NOT_STICKY
    }

    private fun startForegroundAudio() {
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
            .setContentText("Telefon sesleri ultra-düşük gecikmeyle bilgisayar kulaklığından çalınıyor.")
            .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
            .setContentIntent(pendingIntent)
            .setOngoing(true)
            .build()

        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
                    startForeground(
                        NOTIFICATION_ID,
                        notification,
                        ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION
                    )
                } else {
                    startForeground(NOTIFICATION_ID, notification)
                }
            } else {
                startForeground(NOTIFICATION_ID, notification)
            }
        } catch (e: Exception) {
            android.util.Log.e("AudioStreamService", "startForeground failed: ${e.message}", e)
        }
    }

    override fun onDestroy() {
        AudioStreamEngine.instance.stop()
        activeProjection = null
        super.onDestroy()
    }
}
