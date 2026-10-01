package com.korgangames.connectme.updater

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import androidx.core.content.FileProvider
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.net.HttpURLConnection
import java.net.URL
import kotlin.concurrent.thread

/**
 * Android Uygulama İçi Otomatik Güncelleme Yöneticisi (In-Program Auto Updater)
 * GitHub Releases API'sini sorgular, yeni sürüm APK'sını doğrudan uygulama içinde
 * arka planda indirir ve tek tıkla sistem paket yükleyicisini (Package Installer) tetikler.
 */
object AppUpdateManager {

    private const val GITHUB_LATEST_RELEASE_URL =
        "https://api.github.com/repos/korgangames/Connect-Me/releases/latest"

    private val mainHandler = Handler(Looper.getMainLooper())
    var pendingApkToInstall: File? = null

    data class UpdateInfo(
        val versionName: String,
        val releaseTitle: String,
        val releaseNotes: String,
        val downloadUrl: String,
        val apkFileName: String,
        val fileSize: Long
    )

    /**
     * GitHub Releases API üzerinden en son yayınlanan sürümü denetler.
     */
    fun checkForUpdates(
        context: Context,
        currentVersionName: String,
        onResult: (UpdateInfo?) -> Unit
    ) {
        thread(isDaemon = true, name = "ConnectMe-UpdateChecker") {
            try {
                val url = URL(GITHUB_LATEST_RELEASE_URL)
                val conn = (url.openConnection() as HttpURLConnection).apply {
                    requestMethod = "GET"
                    connectTimeout = 8000
                    readTimeout = 8000
                    setRequestProperty("User-Agent", "ConnectMe-Android")
                    setRequestProperty("Accept", "application/vnd.github.v3+json")
                }

                if (conn.responseCode != HttpURLConnection.HTTP_OK) {
                    mainHandler.post { onResult(null) }
                    return@thread
                }

                val jsonStr = conn.inputStream.bufferedReader().use { it.readText() }
                conn.disconnect()

                val json = JSONObject(jsonStr)
                val remoteTag = json.optString("tag_name", "").trim()
                val releaseTitle = json.optString("name", remoteTag)
                val releaseNotes = json.optString("body", "")

                if (remoteTag.isEmpty() || !isNewerVersion(remoteTag, currentVersionName)) {
                    mainHandler.post { onResult(null) }
                    return@thread
                }

                val assets = json.optJSONArray("assets")
                var downloadUrl = ""
                var apkFileName = "ConnectMe-$remoteTag.apk"
                var fileSize = 0L

                if (assets != null) {
                    for (i in 0 until assets.length()) {
                        val asset = assets.getJSONObject(i)
                        val name = asset.optString("name", "")
                        if (name.endsWith(".apk", ignoreCase = true)) {
                            downloadUrl = asset.optString("browser_download_url", "")
                            apkFileName = name
                            fileSize = asset.optLong("size", 0L)
                            break
                        }
                    }
                }

                if (downloadUrl.isEmpty()) {
                    mainHandler.post { onResult(null) }
                    return@thread
                }

                val updateInfo = UpdateInfo(
                    versionName = remoteTag,
                    releaseTitle = releaseTitle,
                    releaseNotes = releaseNotes,
                    downloadUrl = downloadUrl,
                    apkFileName = apkFileName,
                    fileSize = fileSize
                )

                mainHandler.post { onResult(updateInfo) }
            } catch (e: Exception) {
                e.printStackTrace()
                mainHandler.post { onResult(null) }
            }
        }
    }

    /**
     * APK dosyasını arka planda indirir ve ilerleme durumunu UI'a bildirir.
     */
    fun downloadApk(
        context: Context,
        updateInfo: UpdateInfo,
        onProgress: (percent: Int, downloadedBytes: Long, totalBytes: Long) -> Unit,
        onComplete: (File) -> Unit,
        onError: (String) -> Unit
    ) {
        thread(isDaemon = true, name = "ConnectMe-ApkDownloader") {
            try {
                var currentUrl = updateInfo.downloadUrl
                var conn: HttpURLConnection
                var redirectCount = 0

                // GitHub Releases redirect (302) to AWS S3 takip döngüsü
                while (true) {
                    conn = (URL(currentUrl).openConnection() as HttpURLConnection).apply {
                        instanceFollowRedirects = false
                        connectTimeout = 15000
                        readTimeout = 30000
                        setRequestProperty("User-Agent", "ConnectMe-Android")
                    }

                    val code = conn.responseCode
                    if (code in listOf(301, 302, 303, 307, 308)) {
                        val newLocation = conn.getHeaderField("Location")
                        conn.disconnect()
                        if (newLocation != null && redirectCount < 7) {
                            currentUrl = newLocation
                            redirectCount++
                            continue
                        }
                    }
                    break
                }

                if (conn.responseCode !in 200..299) {
                    mainHandler.post { onError("Sunucu yanıt vermedi (HTTP ${conn.responseCode})") }
                    return@thread
                }

                val totalLen = if (conn.contentLengthLong > 0) conn.contentLengthLong else updateInfo.fileSize
                val targetDir = context.getExternalCacheDir() ?: context.cacheDir
                val targetFile = File(targetDir, updateInfo.apkFileName)

                if (targetFile.exists()) {
                    targetFile.delete()
                }

                val input = conn.inputStream
                val output = FileOutputStream(targetFile)
                val buffer = ByteArray(8192)
                var downloaded = 0L
                var lastPercent = -1

                input.use { inStream ->
                    output.use { outStream ->
                        var read: Int
                        while (inStream.read(buffer).also { read = it } != -1) {
                            outStream.write(buffer, 0, read)
                            downloaded += read
                            if (totalLen > 0) {
                                val percent = ((downloaded * 100) / totalLen).toInt()
                                if (percent != lastPercent) {
                                    lastPercent = percent
                                    mainHandler.post { onProgress(percent, downloaded, totalLen) }
                                }
                            }
                        }
                    }
                }
                conn.disconnect()

                if (targetFile.length() < 1024) {
                    mainHandler.post { onError("İndirilen dosya geçersiz veya bozuk.") }
                    return@thread
                }

                mainHandler.post {
                    onProgress(100, targetFile.length(), targetFile.length())
                    onComplete(targetFile)
                }
            } catch (e: Exception) {
                e.printStackTrace()
                mainHandler.post { onError(e.message ?: "İndirme sırasında bağlantı hatası oluştu.") }
            }
        }
    }

    /**
     * İndirilen APK dosyasını doğrudan sistem Package Installer ile çalıştırır.
     */
    fun installApk(activity: Activity, apkFile: File) {
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                if (!activity.packageManager.canRequestPackageInstalls()) {
                    pendingApkToInstall = apkFile
                    val intent = Intent(
                        Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                        Uri.parse("package:${activity.packageName}")
                    )
                    activity.startActivity(intent)
                    return
                }
            }

            pendingApkToInstall = null
            val apkUri = FileProvider.getUriForFile(
                activity,
                "${activity.packageName}.fileprovider",
                apkFile
            )

            val installIntent = Intent(Intent.ACTION_VIEW).apply {
                setDataAndType(apkUri, "application/vnd.android.package-archive")
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            }
            activity.startActivity(installIntent)
        } catch (e: Exception) {
            e.printStackTrace()
        }
    }

    /**
     * Semantik sürüm karşılaştırması (Örn: "v1.6.0" > "1.5.0").
     */
    fun isNewerVersion(remoteTag: String, currentVersion: String): Boolean {
        try {
            val rParts = remoteTag.removePrefix("v").split('.').mapNotNull { it.toIntOrNull() }
            val cParts = currentVersion.removePrefix("v").split('.').mapNotNull { it.toIntOrNull() }

            val maxLen = maxOf(rParts.size, cParts.size)
            for (i in 0 until maxLen) {
                val r = rParts.getOrElse(i) { 0 }
                val c = cParts.getOrElse(i) { 0 }
                if (r > c) return true
                if (r < c) return false
            }
            return false
        } catch (e: Exception) {
            return false
        }
    }
}
