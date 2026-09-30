package com.korgangames.connectme

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.provider.OpenableColumns
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import com.korgangames.connectme.network.ConnectMeService

/**
 * Handles Android system "Share -> Connect Me: Ortak Cebe Gönder" intents
 * for instant file, photo, and text/URL sharing to Windows & Linux.
 */
class ShareReceiverActivity : AppCompatActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        if (intent?.action == Intent.ACTION_SEND) {
            val sharedText = intent.getStringExtra(Intent.EXTRA_TEXT)
            val sharedStreamUri = if (android.os.Build.VERSION.SDK_INT >= 33) {
                intent.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java)
            } else {
                @Suppress("DEPRECATION")
                intent.getParcelableExtra(Intent.EXTRA_STREAM) as? Uri
            }

            val svc = ConnectMeService.instance
            if (svc != null) {
                if (sharedStreamUri != null) {
                    sendUriToPcShelf(svc, sharedStreamUri)
                } else if (!sharedText.isNullOrBlank()) {
                    svc.sendClipboardTextToPc(sharedText)
                    Toast.makeText(this, "Connect Me: Metin/Link Windows panosuna gönderildi!", Toast.LENGTH_SHORT).show()
                }
            } else {
                Toast.makeText(this, "Önce Connect Me uygulamasını bir kez açın.", Toast.LENGTH_LONG).show()
            }
        }

        finish()
    }

    private fun sendUriToPcShelf(svc: ConnectMeService, uri: Uri) {
        var fileName = "android_paylasim"
        var fileSize = 0L
        contentResolver.query(uri, null, null, null, null)?.use { cursor ->
            val nameIdx = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
            val sizeIdx = cursor.getColumnIndex(OpenableColumns.SIZE)
            if (cursor.moveToFirst()) {
                if (nameIdx >= 0) fileName = cursor.getString(nameIdx) ?: fileName
                if (sizeIdx >= 0) fileSize = cursor.getLong(sizeIdx)
            }
        }

        val stream = contentResolver.openInputStream(uri) ?: return
        if (fileSize <= 0L) {
            val bytes = stream.readBytes()
            svc.sendStreamToPcShelf(fileName, bytes.inputStream(), bytes.size.toLong())
        } else {
            svc.sendStreamToPcShelf(fileName, stream, fileSize)
        }
        Toast.makeText(this, "Connect Me: '$fileName' Windows Ortak Cebine gönderildi!", Toast.LENGTH_SHORT).show()
    }
}
