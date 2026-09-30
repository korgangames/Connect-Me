package com.korgangames.connectme

import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.OpenableColumns
import android.provider.Settings
import android.text.InputType
import android.view.Gravity
import android.view.ViewGroup
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import com.korgangames.connectme.network.ConnectMeService
import com.korgangames.connectme.service.CursorAccessibilityService

class MainActivity : AppCompatActivity() {

    private lateinit var statusIpText: TextView
    private lateinit var overlayStatusText: TextView
    private lateinit var accessibilityStatusText: TextView
    private lateinit var peersStatusText: TextView
    private lateinit var pcIpInput: EditText
    private lateinit var shelfListText: TextView
    private lateinit var logViewText: TextView

    private val pickFileLauncher = registerForActivityResult(ActivityResultContracts.GetContent()) { uri: Uri? ->
        if (uri != null) {
            sendPickedUriToPc(uri)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Start Foreground Network Service
        val svcIntent = Intent(this, ConnectMeService::class.java)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            startForegroundService(svcIntent)
        } else {
            startService(svcIntent)
        }

        buildProgrammaticDarkUi()
    }

    override fun onResume() {
        super.onResume()
        ConnectMeService.onStateUpdated = {
            runOnUiThread { refreshUiState() }
        }
        refreshUiState()
    }

    override fun onPause() {
        ConnectMeService.onStateUpdated = null
        super.onPause()
    }

    private fun refreshUiState() {
        val svc = ConnectMeService.instance
        val localIp = svc?.getLocalIpv4Address() ?: "Bağlanıyor..."
        statusIpText.text = "📱 Android IP: $localIp  |  UDP Girdi: 42850  |  TCP Veri: 42851"

        val hasOverlay = Settings.canDrawOverlays(this)
        overlayStatusText.text = if (hasOverlay) {
            "✅ 1. İzin: 120Hz Overlay Fare İmleci AKTİF"
        } else {
            "⚠️ 1. İzin Gerekli: Diğer Uygulamaların Üzerinde Göster (Tıkla)"
        }
        overlayStatusText.setTextColor(if (hasOverlay) Color.parseColor("#4ADE80") else Color.parseColor("#FBBF24"))

        val hasAccessibility = CursorAccessibilityService.instance != null
        accessibilityStatusText.text = if (hasAccessibility) {
            "✅ 2. İzin: Erişilebilirlik Tıklama & Klavye Köprüsü AKTİF"
        } else {
            "⚠️ 2. İzin Gerekli: Erişilebilirlik Servisini Aç (Tıkla)"
        }
        accessibilityStatusText.setTextColor(if (hasAccessibility) Color.parseColor("#4ADE80") else Color.parseColor("#FBBF24"))

        val peers = ConnectMeService.discoveredPeers
        peersStatusText.text = if (peers.isEmpty()) {
            "🔍 Aynı Wi-Fi ağındaki Windows bilgisayar aranıyor..."
        } else {
            peers.joinToString("\n") { "🪟 Bağlı PC: ${it.deviceName} (${it.ipAddress}:${it.udpInputPort})" }
        }

        val items = ConnectMeService.shelfItems
        shelfListText.text = if (items.isEmpty()) {
            "Henüz ortak cepte eşya yok. Windows'tan sürükleyip bırakabilir veya aşağıdan dosya seçebilirsiniz."
        } else {
            items.take(8).joinToString("\n") { item ->
                val prefix = if (item.isOutgoing) "📤 Gönderildi" else "📥 Alındı"
                "$prefix: ${item.fileName} (${item.fileSizeBytes / 1024} KB)"
            }
        }

        logViewText.text = ConnectMeService.telemetryLogs.take(15).joinToString("\n")
    }

    private fun sendPickedUriToPc(uri: Uri) {
        val svc = ConnectMeService.instance
        if (svc == null) {
            Toast.makeText(this, "Servis henüz hazır değil", Toast.LENGTH_SHORT).show()
            return
        }

        var fileName = "paylasilan_dosya"
        var fileSize = 0L
        contentResolver.query(uri, null, null, null, null)?.use { cursor ->
            val nameIdx = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
            val sizeIdx = cursor.getColumnIndex(OpenableColumns.SIZE)
            if (cursor.moveToFirst()) {
                if (nameIdx >= 0) fileName = cursor.getString(nameIdx) ?: fileName
                if (sizeIdx >= 0) fileSize = cursor.getLong(sizeIdx)
            }
        }

        val stream = contentResolver.openInputStream(uri)
        if (stream != null) {
            if (fileSize <= 0L) {
                val bytes = stream.readBytes()
                svc.sendStreamToPcShelf(fileName, bytes.inputStream(), bytes.size.toLong())
            } else {
                svc.sendStreamToPcShelf(fileName, stream, fileSize)
            }
            Toast.makeText(this, "'$fileName' Windows Ortak Cebine gönderiliyor...", Toast.LENGTH_SHORT).show()
        }
    }

    private fun buildProgrammaticDarkUi() {
        val scroll = ScrollView(this).apply {
            setBackgroundColor(Color.parseColor("#0D1117"))
            layoutParams = ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT
            )
        }

        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(42, 56, 42, 56)
        }

        // Header
        root.addView(TextView(this).apply {
            text = "🌐 Connect Me"
            textSize = 24f
            setTypeface(typeface, Typeface.BOLD)
            setTextColor(Color.parseColor("#38BDF8"))
        })

        statusIpText = TextView(this).apply {
            text = "📱 Android IP: Yükleniyor..."
            textSize = 13f
            setTextColor(Color.parseColor("#9CA3AF"))
            setPadding(0, 8, 0, 28)
        }
        root.addView(statusIpText)

        // Permissions Card
        val permCard = createCardLayout()
        permCard.addView(createSectionTitle("🛡️ 1. Kurulum ve İmleç İzinleri"))

        overlayStatusText = TextView(this).apply {
            textSize = 13.5f
            setPadding(0, 12, 0, 12)
            setOnClickListener {
                val intent = Intent(
                    Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                    Uri.parse("package:$packageName")
                )
                startActivity(intent)
            }
        }
        permCard.addView(overlayStatusText)

        accessibilityStatusText = TextView(this).apply {
            textSize = 13.5f
            setPadding(0, 8, 0, 12)
            setOnClickListener {
                startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
            }
        }
        permCard.addView(accessibilityStatusText)
        root.addView(permCard)

        // PC Connection Card
        val pcCard = createCardLayout()
        pcCard.addView(createSectionTitle("🪟 2. Windows / Linux Bilgisayar Bağlantısı"))

        peersStatusText = TextView(this).apply {
            textSize = 13f
            setTextColor(Color.parseColor("#E5E7EB"))
            setPadding(0, 10, 0, 16)
        }
        pcCard.addView(peersStatusText)

        pcIpInput = EditText(this).apply {
            hint = "Windows PC IP Adresi (Örn: 192.168.1.35)"
            setHintTextColor(Color.parseColor("#6B7280"))
            setTextColor(Color.WHITE)
            inputType = InputType.TYPE_CLASS_PHONE
            setBackgroundColor(Color.parseColor("#0D1117"))
            setPadding(28, 22, 28, 22)
        }
        pcCard.addView(pcIpInput)

        val connectBtn = createStyledButton("➕ Windows PC IP'sine Bağlan", "#2563EB") {
            val ip = pcIpInput.text.toString().trim()
            if (ip.isNotEmpty()) {
                ConnectMeService.instance?.triggerManualConnectToPc(ip)
                refreshUiState()
            }
        }
        pcCard.addView(connectBtn)
        root.addView(pcCard)

        // Universal Clipboard & Drop Shelf Card
        val shelfCard = createCardLayout()
        shelfCard.addView(createSectionTitle("🧲 3. Manyetik Ortak Cep (Drop Shelf) & Pano"))

        val sendFileBtn = createStyledButton("📤 Dosya / Fotoğraf Seç ve Windows'a Gönder", "#0284C7") {
            pickFileLauncher.launch("*/*")
        }
        shelfCard.addView(sendFileBtn)

        val sendClipBtn = createStyledButton("📋 Android Panosunu Şimdi Windows'a Gönder", "#059669") {
            val cm = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
            val txt = cm.primaryClip?.getItemAt(0)?.coerceToText(this)?.toString() ?: ""
            if (txt.isNotEmpty()) {
                ConnectMeService.instance?.sendClipboardTextToPc(txt)
                Toast.makeText(this, "Pano Windows'a gönderildi!", Toast.LENGTH_SHORT).show()
            } else {
                Toast.makeText(this, "Panoda metin bulunamadı", Toast.LENGTH_SHORT).show()
            }
        }
        shelfCard.addView(sendClipBtn)

        shelfListText = TextView(this).apply {
            textSize = 12.5f
            setTextColor(Color.parseColor("#D1D5DB"))
            setPadding(0, 18, 0, 6)
        }
        shelfCard.addView(shelfListText)
        root.addView(shelfCard)

        // Live Telemetry Card
        val logCard = createCardLayout()
        logCard.addView(createSectionTitle("📡 Canlı Protokol Günlüğü"))
        logViewText = TextView(this).apply {
            textSize = 11.5f
            typeface = Typeface.MONOSPACE
            setTextColor(Color.parseColor("#A7F3D0"))
            setPadding(0, 10, 0, 6)
        }
        logCard.addView(logViewText)
        root.addView(logCard)

        scroll.addView(root)
        setContentView(scroll)
    }

    private fun createCardLayout(): LinearLayout {
        val bg = GradientDrawable().apply {
            setColor(Color.parseColor("#161B22"))
            cornerRadius = 24f
            setStroke(2, Color.parseColor("#30363D"))
        }
        return LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            background = bg
            setPadding(36, 32, 36, 32)
            val lp = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            )
            lp.setMargins(0, 0, 0, 32)
            layoutParams = lp
        }
    }

    private fun createSectionTitle(title: String): TextView {
        return TextView(this).apply {
            text = title
            textSize = 16f
            setTypeface(typeface, Typeface.BOLD)
            setTextColor(Color.parseColor("#F0F6FC"))
        }
    }

    private fun createStyledButton(title: String, hexColor: String, onClick: () -> Unit): Button {
        val bg = GradientDrawable().apply {
            setColor(Color.parseColor(hexColor))
            cornerRadius = 18f
        }
        return Button(this).apply {
            text = title
            isAllCaps = false
            setTextColor(Color.WHITE)
            background = bg
            gravity = Gravity.CENTER
            val lp = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            )
            lp.setMargins(0, 18, 0, 0)
            layoutParams = lp
            setOnClickListener { onClick() }
        }
    }
}
