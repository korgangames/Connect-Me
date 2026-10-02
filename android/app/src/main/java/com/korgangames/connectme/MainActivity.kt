package com.korgangames.connectme

import android.Manifest
import android.app.Activity
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.media.projection.MediaProjectionManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.OpenableColumns
import android.provider.Settings
import android.text.InputType
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import com.korgangames.connectme.audio.AudioStreamEngine
import com.korgangames.connectme.audio.AudioStreamService
import com.korgangames.connectme.network.ConnectMeService
import com.korgangames.connectme.protocol.ProtocolConstants
import com.korgangames.connectme.service.CursorAccessibilityService
import com.korgangames.connectme.updater.AppUpdateManager

class MainActivity : AppCompatActivity() {

    private lateinit var statusIpText: TextView
    private lateinit var localPinBadgeText: TextView
    private lateinit var overlayStatusText: TextView
    private lateinit var accessibilityStatusText: TextView
    private lateinit var peersStatusText: TextView
    private lateinit var pcIpInput: EditText
    private lateinit var remotePinInput: EditText
    private lateinit var shelfListText: TextView
    private lateinit var logViewText: TextView
    private lateinit var audioStatusText: TextView
    private lateinit var audioToggleBtn: Button

    // In-App Auto Updater UI elements
    private lateinit var updateCard: LinearLayout
    private lateinit var updateTitleText: TextView
    private lateinit var updateDetailText: TextView
    private lateinit var updateProgressBar: ProgressBar
    private lateinit var updateActionBtn: Button
    private lateinit var checkUpdateBtn: Button

    private val mediaProjectionLauncher = registerForActivityResult(
        ActivityResultContracts.StartActivityForResult()
    ) { result ->
        if (result.resultCode == Activity.RESULT_OK && result.data != null) {
            val mediaProjectionManager = getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
            val projection = mediaProjectionManager.getMediaProjection(result.resultCode, result.data!!)
            val pairedPc = ConnectMeService.discoveredPeers.firstOrNull { it.isMutuallyPaired }
            if (pairedPc != null) {
                AudioStreamService.activeProjection = projection
                val intent = Intent(this, AudioStreamService::class.java).apply {
                    action = AudioStreamService.ACTION_START_AUDIO
                    putExtra("hostIp", pairedPc.ipAddress)
                }
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                    startForegroundService(intent)
                } else {
                    startService(intent)
                }
                updateAudioUi()
                Toast.makeText(this, "🎧 Sesler bilgisayardaki kulaklığınıza aktarılıyor!", Toast.LENGTH_SHORT).show()
            }
        } else {
            val pairedPc = ConnectMeService.discoveredPeers.firstOrNull { it.isMutuallyPaired }
            if (pairedPc != null) {
                AudioStreamEngine.instance.start(pairedPc.ipAddress, null)
                updateAudioUi()
                Toast.makeText(this, "Mikrofon ses aktarımı başlatıldı", Toast.LENGTH_SHORT).show()
            }
        }
    }

    private val pickFileLauncher = registerForActivityResult(ActivityResultContracts.GetContent()) { uri: Uri? ->
        if (uri != null) {
            sendPickedUriToPc(uri)
        }
    }

    private val requestPermissionsLauncher = registerForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) { _ ->
        refreshUiState()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        title = "Connect Me v1.6.5"

        try {
            val svcIntent = Intent(this, ConnectMeService::class.java)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                startForegroundService(svcIntent)
            } else {
                startService(svcIntent)
            }
        } catch (e: Exception) {
            android.util.Log.e("ConnectMe", "Failed to start ConnectMeService: ${e.message}", e)
        }

        try {
            checkAndRequestRuntimePermissions()
        } catch (e: Exception) {
            android.util.Log.e("ConnectMe", "Failed to check permissions: ${e.message}", e)
        }

        buildProgrammaticDarkUi()

        try {
            checkAppUpdate(isManual = false)
        } catch (e: Exception) {
            android.util.Log.e("ConnectMe", "Failed to check updates: ${e.message}", e)
        }
    }

    override fun onResume() {
        super.onResume()

        // Kullanıcı "Bilinmeyen kaynaklardan yükleme" iznini açıp geri döndüyse bekleyen APK kurulumunu tamamla
        val pending = AppUpdateManager.pendingApkToInstall
        if (pending != null && pending.exists()) {
            if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O || packageManager.canRequestPackageInstalls()) {
                AppUpdateManager.installApk(this, pending)
            }
        }

        ConnectMeService.onStateUpdated = {
            runOnUiThread { refreshUiState() }
        }
        AudioStreamEngine.instance.onStatusChanged = { _, _ ->
            runOnUiThread { updateAudioUi() }
        }
        refreshUiState()
        updateAudioUi()
    }

    override fun onPause() {
        ConnectMeService.onStateUpdated = null
        AudioStreamEngine.instance.onStatusChanged = null
        super.onPause()
    }

    private fun refreshUiState() {
        val svc = ConnectMeService.instance
        val localIp = svc?.getLocalIpv4Address() ?: "Bağlanıyor..."
        val pin = svc?.localPairingPin ?: "------"
        statusIpText.text = "📱 Android IP: $localIp  |  v1.6.5 (v1-6-5)  |  UDP: 42850  |  TCP: 42851  |  Ses: 42852"
        localPinBadgeText.text = "🔐 BU CİHAZIN 6 HANELİ KODU: $pin"

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
            "🔍 Ağdaki bilgisayarlar ve cihazlar aranıyor..."
        } else {
            peers.joinToString("\n\n") { p ->
                val isTrusted = svc?.getTrustTokenForDevice(p.deviceId) != null
                val trustTag = if (isTrusted) "⭐ " else ""
                val state = when {
                    p.isMutuallyPaired -> "${trustTag}🟢 ÇİFT TARAFLI ONAYLI (Aktif)"
                    p.myEnteredPinVerifiedByRemote -> "${trustTag}🟡 KARŞI ONAY BEKLİYOR (PC'de $pin kodunu girin)"
                    p.remoteEnteredMyPinVerified -> "${trustTag}🟠 SİZİN ONAYINIZ BEKLENİYOR (PC'nin kodunu aşağıya girin)"
                    else -> if (isTrusted) "⭐ ⚪ GÜVENİLİR (Otomatik Bağlanıyor...)" else "⚪ EŞLEŞMEDİ (6 Haneli PIN Gerekli)"
                }
                "🖥️ ${p.deviceName} (${p.ipAddress})\n   Durum: $state"
            }
        }

        val items = ConnectMeService.shelfItems
        shelfListText.text = if (items.isEmpty()) {
            "Henüz ortak cepte eşya yok. Bilgisayardan sürükleyip bırakabilir veya aşağıdan dosya seçebilirsiniz."
        } else {
            items.take(8).joinToString("\n") { item ->
                val prefix = if (item.isOutgoing) "📤 Gönderildi" else "📥 Alındı"
                "$prefix: ${item.fileName} (${item.fileSizeBytes / 1024} KB)"
            }
        }

        logViewText.text = ConnectMeService.telemetryLogs.take(15).joinToString("\n")
    }

    private fun sendPickedUriToPc(uri: Uri) {
        val svc = ConnectMeService.instance ?: return
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
            Toast.makeText(this, "'$fileName' Ortak Cebe gönderiliyor...", Toast.LENGTH_SHORT).show()
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
            setPadding(42, 52, 42, 52)
        }

        root.addView(TextView(this).apply {
            text = "🌐 Connect Me v1.6.5"
            textSize = 24f
            setTypeface(typeface, Typeface.BOLD)
            setTextColor(Color.parseColor("#38BDF8"))
        })

        statusIpText = TextView(this).apply {
            text = "📱 Android IP: Yükleniyor..."
            textSize = 12.5f
            setTextColor(Color.parseColor("#9CA3AF"))
            setPadding(0, 8, 0, 8)
        }
        root.addView(statusIpText)

        val updateRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setPadding(0, 0, 0, 14)
        }
        checkUpdateBtn = Button(this).apply {
            text = "🔄 Güncellemeleri Denetle"
            textSize = 11f
            setTextColor(Color.parseColor("#38BDF8"))
            val bg = GradientDrawable().apply {
                setColor(Color.parseColor("#1E293B"))
                cornerRadius = 14f
                setStroke(1, Color.parseColor("#38BDF8"))
            }
            background = bg
            setPadding(24, 8, 24, 8)
            setOnClickListener { checkAppUpdate(isManual = true) }
        }
        updateRow.addView(checkUpdateBtn)
        root.addView(updateRow)

        // In-App Auto Updater Card (Varsayılan olarak gizli, güncelleme bulununca belirir)
        updateCard = createCardLayout().apply {
            visibility = View.GONE
            val strokeBg = GradientDrawable().apply {
                setColor(Color.parseColor("#0F172A"))
                cornerRadius = 24f
                setStroke(3, Color.parseColor("#22C55E"))
            }
            background = strokeBg
        }

        updateTitleText = TextView(this).apply {
            text = "🎉 Yeni Sürüm Mevcut!"
            textSize = 16.5f
            setTypeface(typeface, Typeface.BOLD)
            setTextColor(Color.parseColor("#4ADE80"))
        }
        updateCard.addView(updateTitleText)

        updateDetailText = TextView(this).apply {
            text = "Yenilikler yükleniyor..."
            textSize = 12f
            setTextColor(Color.parseColor("#E2E8F0"))
            setPadding(0, 6, 0, 8)
        }
        updateCard.addView(updateDetailText)

        updateProgressBar = ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal).apply {
            isIndeterminate = false
            max = 100
            progress = 0
            visibility = View.GONE
            val lp = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            )
            lp.setMargins(0, 4, 0, 8)
            layoutParams = lp
        }
        updateCard.addView(updateProgressBar)

        updateActionBtn = createStyledButton("⬇️ Tek Tıkla İndir ve Kur", "#16A34A") {
            // dinamik atanır
        }
        updateCard.addView(updateActionBtn)
        root.addView(updateCard)

        // Local 6-Digit PIN Card
        val pinCard = createCardLayout()
        localPinBadgeText = TextView(this).apply {
            text = "🔐 BU CİHAZIN 6 HANELİ KODU: ------"
            textSize = 18f
            setTypeface(typeface, Typeface.BOLD)
            setTextColor(Color.parseColor("#38BDF8"))
        }
        pinCard.addView(localPinBadgeText)
        pinCard.addView(TextView(this).apply {
            text = "Bağlanmak istediğiniz bilgisayarda veya diğer cihazda bu 6 haneli kodu girin."
            textSize = 12f
            setTextColor(Color.parseColor("#9CA3AF"))
            setPadding(0, 6, 0, 0)
        })
        root.addView(pinCard)

        // Permissions Card
        val permCard = createCardLayout()
        permCard.addView(createSectionTitle("🛡️ 1. Kurulum ve İmleç İzinleri"))

        overlayStatusText = TextView(this).apply {
            textSize = 13.5f
            setPadding(0, 12, 0, 12)
            setOnClickListener { showOverlayGuideDialog() }
        }
        permCard.addView(overlayStatusText)

        accessibilityStatusText = TextView(this).apply {
            textSize = 13.5f
            setPadding(0, 8, 0, 12)
            setOnClickListener { showAccessibilityGuideDialog() }
        }
        permCard.addView(accessibilityStatusText)

        val guideBtn = createStyledButton("📖 Samsung & Android İzin Rehberi (Adım Adım)", "#1E3A8A") {
            showAccessibilityGuideDialog()
        }
        permCard.addView(guideBtn)
        root.addView(permCard)

        // Mutual 6-Digit PIN & PC Connection Card
        val pcCard = createCardLayout()
        pcCard.addView(createSectionTitle("🔗 2. Çoklu Cihaz & Çift Taraflı 6 Haneli Kod Onayı"))

        peersStatusText = TextView(this).apply {
            textSize = 13f
            setTextColor(Color.parseColor("#E5E7EB"))
            setPadding(0, 12, 0, 16)
        }
        pcCard.addView(peersStatusText)

        remotePinInput = EditText(this).apply {
            hint = "Karşı Cihazın 6 Haneli Kodu (Örn: 482910)"
            setHintTextColor(Color.parseColor("#6B7280"))
            setTextColor(Color.parseColor("#38BDF8"))
            inputType = InputType.TYPE_CLASS_NUMBER
            setBackgroundColor(Color.parseColor("#0D1117"))
            setPadding(28, 22, 28, 22)
        }
        pcCard.addView(remotePinInput)

        val verifyPinBtn = createStyledButton("✅ Karşı Cihazın 6 Haneli Kodunu Doğrula", "#059669") {
            val enteredPin = remotePinInput.text.toString().trim()
            val firstPeer = ConnectMeService.discoveredPeers.firstOrNull()
            if (firstPeer != null) {
                ConnectMeService.instance?.submitRemotePinToPeer(firstPeer, enteredPin)
            } else {
                val ip = pcIpInput.text.toString().trim()
                if (ip.isNotEmpty()) {
                    ConnectMeService.instance?.triggerManualConnectAndVerifyPin(ip, enteredPin)
                } else {
                    Toast.makeText(this, "Önce cihazın keşfedilmesini bekleyin veya IP girin", Toast.LENGTH_SHORT).show()
                }
            }
        }
        pcCard.addView(verifyPinBtn)

        pcIpInput = EditText(this).apply {
            hint = "Opsiyonel: Manuel Cihaz IP Adresi (Örn: 192.168.1.35)"
            setHintTextColor(Color.parseColor("#6B7280"))
            setTextColor(Color.WHITE)
            inputType = InputType.TYPE_CLASS_PHONE
            setBackgroundColor(Color.parseColor("#0D1117"))
            setPadding(28, 22, 28, 22)
            val lp = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            )
            lp.setMargins(0, 20, 0, 0)
            layoutParams = lp
        }
        pcCard.addView(pcIpInput)

        val connectBtn = createStyledButton("➕ IP ile Cihaz Ekle ve Kodu Gönder", "#2563EB") {
            val ip = pcIpInput.text.toString().trim()
            val pin = remotePinInput.text.toString().trim()
            if (ip.isNotEmpty()) {
                ConnectMeService.instance?.triggerManualConnectAndVerifyPin(ip, pin)
                refreshUiState()
            }
        }
        pcCard.addView(connectBtn)
        root.addView(pcCard)

        // 🎧 Merkezi Kulaklık & Ses Yönlendirme (Audio Hub) Card
        val audioCard = createCardLayout()
        audioCard.addView(createSectionTitle("🎧 3. Merkezi Kulaklık & Ses Köprüsü (Audio Hub)"))

        val audioInfoText = TextView(this).apply {
            text = "Telefonunuzun tüm seslerini (YouTube, Spotify, oyunlar, bildirimler) bilgisayara ve ona bağlı kulaklığınıza aktarır."
            textSize = 12f
            setTextColor(Color.parseColor("#94A3B8"))
            setPadding(0, 6, 0, 10)
        }
        audioCard.addView(audioInfoText)

        audioStatusText = TextView(this).apply {
            text = if (AudioStreamEngine.instance.isStreaming) "🟢 Sesi Bilgisayara Aktarılıyor (48kHz Stereo)" else "⚪ Ses aktarımı kapalı"
            textSize = 12.5f
            setTypeface(typeface, Typeface.BOLD)
            setTextColor(if (AudioStreamEngine.instance.isStreaming) Color.parseColor("#4ADE80") else Color.parseColor("#9CA3AF"))
            setPadding(0, 4, 0, 10)
        }
        audioCard.addView(audioStatusText)

        audioToggleBtn = createStyledButton(if (AudioStreamEngine.instance.isStreaming) "⏹️ Bilgisayara Ses Aktarımını Durdur" else "🎧 Sesi Bilgisayara / Kulaklığa Aktar", if (AudioStreamEngine.instance.isStreaming) "#DC2626" else "#059669") {
            toggleAudioStreaming()
        }
        audioCard.addView(audioToggleBtn)
        root.addView(audioCard)

        // Universal Clipboard & Drop Shelf Card
        val shelfCard = createCardLayout()
        shelfCard.addView(createSectionTitle("🧲 4. Ortak Cep (Drop Shelf) & Evrensel Pano"))

        val sendFileBtn = createStyledButton("📤 Dosya / Fotoğraf Seç ve Gönder", "#0284C7") {
            pickFileLauncher.launch("*/*")
        }
        shelfCard.addView(sendFileBtn)

        val autoClipBanner = TextView(this).apply {
            text = "⚡ Evrensel Pano: Kesintisiz & Otomatik Aktif\nHer iki cihazda kopyalanan metinler hiçbir butona basmadan anında karşı tarafa aktarılır."
            textSize = 12f
            setTextColor(Color.parseColor("#34D399"))
            setPadding(0, 10, 0, 10)
        }
        shelfCard.addView(autoClipBanner)

        val sendClipBtn = createStyledButton("📋 Panoyu Manuel Tekrar Gönder (İsteğe Bağlı)", "#065F46") {
            val cm = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
            val txt = cm.primaryClip?.getItemAt(0)?.coerceToText(this)?.toString() ?: ""
            if (txt.isNotEmpty()) {
                ConnectMeService.instance?.sendClipboardTextToPc(txt)
                Toast.makeText(this, "Pano onaylı cihazlara gönderildi!", Toast.LENGTH_SHORT).show()
            } else {
                Toast.makeText(this, "Panoda metin bulunamadı", Toast.LENGTH_SHORT).show()
            }
        }
        shelfCard.addView(sendClipBtn)

        val returnPcBtn = createStyledButton("⬅️ Fare / Klavye Kontrolünü Bilgisayara Geri Ver", "#374151") {
            CursorAccessibilityService.instance?.deactivateCursor()
            ConnectMeService.instance?.sendEdgeHandOffBackToPeer(ProtocolConstants.EDGE_LEFT, 0.5f)
            Toast.makeText(this, "İmleç kontrolü bilgisayara devredildi", Toast.LENGTH_SHORT).show()
        }
        shelfCard.addView(returnPcBtn)

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

        val exportBtn = createStyledButton("📤 Logları Dışa Aktar / Paylaş", "#2563EB") {
            exportAndShareLogs()
        }
        logCard.addView(exportBtn)

        val logBtnRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            val rlp = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)
            rlp.setMargins(0, 12, 0, 0)
            layoutParams = rlp
        }
        val copyLogsBtn = Button(this).apply {
            text = "📋 Panoya Kopyala"
            isAllCaps = false
            setTextColor(Color.WHITE)
            background = GradientDrawable().apply {
                setColor(Color.parseColor("#1F2937"))
                cornerRadius = 18f
                setStroke(1, Color.parseColor("#374151"))
            }
            val lp = LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f)
            lp.setMargins(0, 0, 8, 0)
            layoutParams = lp
            setOnClickListener { copyLogsToClipboard() }
        }
        val clearLogsBtn = Button(this).apply {
            text = "🧹 Temizle"
            isAllCaps = false
            setTextColor(Color.parseColor("#F87171"))
            background = GradientDrawable().apply {
                setColor(Color.parseColor("#1F2937"))
                cornerRadius = 18f
                setStroke(1, Color.parseColor("#374151"))
            }
            val lp = LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f)
            lp.setMargins(8, 0, 0, 0)
            layoutParams = lp
            setOnClickListener {
                ConnectMeService.clearLogs(this@MainActivity)
                refreshUiState()
                Toast.makeText(this@MainActivity, "🧹 Log geçmişi temizlendi", Toast.LENGTH_SHORT).show()
            }
        }
        logBtnRow.addView(copyLogsBtn)
        logBtnRow.addView(clearLogsBtn)
        logCard.addView(logBtnRow)

        logViewText = TextView(this).apply {
            textSize = 11.5f
            typeface = Typeface.MONOSPACE
            setTextColor(Color.parseColor("#A7F3D0"))
            setPadding(0, 14, 0, 6)
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
            lp.setMargins(0, 0, 0, 28)
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
            lp.setMargins(0, 16, 0, 0)
            layoutParams = lp
            setOnClickListener { onClick() }
        }
    }

    private fun checkAndRequestRuntimePermissions() {
        val permissions = mutableListOf<String>()
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            permissions.add(Manifest.permission.RECORD_AUDIO)
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                permissions.add(Manifest.permission.POST_NOTIFICATIONS)
            }
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            if (checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED) {
                permissions.add(Manifest.permission.BLUETOOTH_CONNECT)
            }
            if (checkSelfPermission(Manifest.permission.BLUETOOTH_SCAN) != PackageManager.PERMISSION_GRANTED) {
                permissions.add(Manifest.permission.BLUETOOTH_SCAN)
            }
        }
        if (permissions.isNotEmpty()) {
            requestPermissionsLauncher.launch(permissions.toTypedArray())
        }
    }

    private fun showAccessibilityGuideDialog() {
        AlertDialog.Builder(this)
            .setTitle("📱 Samsung / Android Erişilebilirlik İzni")
            .setMessage(
                "Fare tıklamaları ve klavye girişlerinin telefonunuza iletilmesi için bu izin şarttır.\n\n" +
                "📌 Samsung (One UI) Kurulum Adımları:\n" +
                "1. Aşağıdaki '1. Erişilebilirlik Sayfasına Git' butonuna dokunun.\n" +
                "2. Açılan menünün EN ALTINA kaydırın ve 'Yüklü uygulamalar' (veya Yüklü Servisler) seçeneğine girin.\n" +
                "3. 'Connect Me — Fare & Klavye Köprüsü' servisini seçip Açık (Aktif) konuma getirin.\n\n" +
                "⚠️ Ayar Kilitli / Gri İse ('Kısıtlanmış Ayar' Uyarısı):\n" +
                "Google güvenlik politikası gereği dışarıdan (APK) yüklenen uygulamalar kilitlenebilir. Kilidi açmak için:\n" +
                "-> Aşağıdaki '2. Uygulama Bilgisi (3 Nokta ⋮)' butonuna dokunun, sağ üstteki üç noktaya (⋮) basıp 'Kısıtlanmış ayarlara izin ver' seçeneğini onaylayın."
            )
            .setPositiveButton("1. Erişilebilirlik Sayfasına Git") { _, _ ->
                try {
                    startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
                } catch (e: Exception) {
                    Toast.makeText(this, "Ayar sayfası açılamadı: ${e.message}", Toast.LENGTH_SHORT).show()
                }
            }
            .setNeutralButton("2. Uygulama Bilgisi (3 Nokta ⋮)") { _, _ ->
                try {
                    val intent = Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
                        data = Uri.parse("package:$packageName")
                    }
                    startActivity(intent)
                } catch (e: Exception) {
                    Toast.makeText(this, "Uygulama bilgisi açılamadı: ${e.message}", Toast.LENGTH_SHORT).show()
                }
            }
            .setNegativeButton("Kapat", null)
            .show()
    }

    private fun showOverlayGuideDialog() {
        AlertDialog.Builder(this)
            .setTitle("🖱️ İmleç Görünürlüğü (Overlay) İzni")
            .setMessage(
                "Bilgisayar farenizi ekran kenarından telefonunuza geçirdiğinizde 120Hz imlecin ekran üzerinde görünmesi için bu izin gereklidir.\n\n" +
                "Açılan listede Connect Me'yi bulup anahtarı açık konuma getirin."
            )
            .setPositiveButton("İzin Sayfasına Git") { _, _ ->
                try {
                    startActivity(
                        Intent(
                            Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                            Uri.parse("package:$packageName")
                        )
                    )
                } catch (e: Exception) {
                    Toast.makeText(this, "Ayar sayfası açılamadı: ${e.message}", Toast.LENGTH_SHORT).show()
                }
            }
            .setNegativeButton("Kapat", null)
            .show()
    }

    private fun copyLogsToClipboard() {
        val logContent = ConnectMeService.getFullLogText(this)
        if (logContent.isBlank()) {
            Toast.makeText(this, "Kopyalanacak log kaydı bulunamadı", Toast.LENGTH_SHORT).show()
            return
        }
        val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        val clip = ClipData.newPlainText("ConnectMe Android Logs", logContent)
        clipboard.setPrimaryClip(clip)
        Toast.makeText(this, "📋 Tüm loglar panoya kopyalandı!", Toast.LENGTH_SHORT).show()
    }

    private fun exportAndShareLogs() {
        val logContent = ConnectMeService.getFullLogText(this)
        val report = buildString {
            appendLine("=== Connect Me Android Tanılama Günlüğü ===")
            appendLine("Tarih: ${java.text.SimpleDateFormat("yyyy-MM-dd HH:mm:ss", java.util.Locale.US).format(java.util.Date())}")
            appendLine("Uygulama Sürümü: v1.6.5")
            appendLine("Cihaz Modeli: ${Build.MANUFACTURER} ${Build.MODEL} (${Build.DEVICE})")
            appendLine("Android Sürümü: ${Build.VERSION.RELEASE} (API ${Build.VERSION.SDK_INT})")
            appendLine("Erişilebilirlik Hizmeti: ${if (CursorAccessibilityService.instance != null) "AÇIK ✅" else "KAPALI ❌"}")
            appendLine("Overlay (Üste Çizme) İzni: ${if (Settings.canDrawOverlays(this@MainActivity)) "AÇIK ✅" else "KAPALI ❌"}")
            appendLine("Ekran Çözünürlüğü: ${resources.displayMetrics.widthPixels}x${resources.displayMetrics.heightPixels}")
            appendLine("Yerel PIN: ${ConnectMeService.instance?.localPairingPin ?: "Yok"}")
            appendLine("Keşfedilen Cihaz Sayısı: ${ConnectMeService.discoveredPeers.size}")
            appendLine("===========================================")
            appendLine()
            if (logContent.isBlank()) {
                appendLine("[Henüz log kaydı yok]")
            } else {
                appendLine(logContent)
            }
        }

        val sendIntent = Intent(Intent.ACTION_SEND).apply {
            type = "text/plain"
            putExtra(Intent.EXTRA_SUBJECT, "ConnectMe-Android-Logs-v1.6.5.txt")
            putExtra(Intent.EXTRA_TEXT, report)
        }
        startActivity(Intent.createChooser(sendIntent, "Connect Me Loglarını Dışa Aktar / Paylaş"))
    }

    private fun checkAppUpdate(isManual: Boolean) {
        if (isManual) {
            Toast.makeText(this, "Güncellemeler denetleniyor...", Toast.LENGTH_SHORT).show()
        }
        AppUpdateManager.checkForUpdates(this, "1.6.5") { updateInfo, errorMsg ->
            if (updateInfo != null) {
                updateCard.visibility = View.VISIBLE
                updateTitleText.text = "🎉 Yeni Sürüm Mevcut: ${updateInfo.versionName}"
                updateDetailText.text = "${updateInfo.releaseTitle}\n\n${updateInfo.releaseNotes.take(350)}..."
                val sizeMb = if (updateInfo.fileSize > 0) " (${updateInfo.fileSize / 1024 / 1024} MB)" else ""
                updateActionBtn.text = "⬇️ Tek Tıkla İndir ve Kur$sizeMb"
                updateActionBtn.isEnabled = true
                updateActionBtn.setOnClickListener {
                    startDownloadAndInstall(updateInfo)
                }
                if (isManual) {
                    Toast.makeText(this, "Yeni sürüm bulundu: ${updateInfo.versionName}", Toast.LENGTH_SHORT).show()
                }
            } else if (errorMsg != null) {
                updateCard.visibility = View.GONE
                android.util.Log.w("ConnectMe", errorMsg)
                ConnectMeService.instance?.log("[Güncelleme] $errorMsg")
                if (isManual) {
                    Toast.makeText(this, "Güncelleme denetlenemedi:\n$errorMsg", Toast.LENGTH_LONG).show()
                }
            } else {
                updateCard.visibility = View.GONE
                if (isManual) {
                    Toast.makeText(this, "Connect Me güncel (v1.6.5)!", Toast.LENGTH_SHORT).show()
                }
            }
        }
    }

    private fun startDownloadAndInstall(updateInfo: AppUpdateManager.UpdateInfo) {
        updateActionBtn.isEnabled = false
        updateProgressBar.visibility = View.VISIBLE
        updateProgressBar.progress = 0
        updateActionBtn.text = "İndiriliyor..."

        AppUpdateManager.downloadApk(
            context = this,
            updateInfo = updateInfo,
            onProgress = { percent, downloadedBytes, totalBytes ->
                updateProgressBar.progress = percent
                val downMb = downloadedBytes / 1024 / 1024
                val totMb = totalBytes / 1024 / 1024
                updateActionBtn.text = "%$percent İndiriliyor ($downMb MB / $totMb MB)..."
            },
            onComplete = { apkFile ->
                updateActionBtn.isEnabled = true
                updateActionBtn.text = "📦 Güncellemeyi Yükle (APK)"
                updateActionBtn.setOnClickListener {
                    AppUpdateManager.installApk(this, apkFile)
                }
                updateProgressBar.progress = 100

                // Kullanıcıya imza ve kurulum rehberi sunarak sistem yükleyicisini çağır
                android.app.AlertDialog.Builder(this)
                    .setTitle("Güncelleme Hazır: ${updateInfo.versionName}")
                    .setMessage("Connect Me ${updateInfo.versionName} APK dosyası başarıyla indirildi.\n\n⚠️ ÖNEMLİ BİLGİ: Eğer telefonunuzda daha önceki sürümler (v1.6.4 veya altı) yüklüyse, eski sürümün anahtar imzası farklı olduğu için Android 'Uygulama yüklenemedi' uyarısı verebilir. Bu durumda mevcut eski Connect Me uygulamasını telefonunuzdan kaldırıp bu yeni APK'yı kurmanız gerekmektedir.\n\nYükleme ekranı açılsın mı?")
                    .setPositiveButton("Yükleyiciyi Aç") { _, _ ->
                        AppUpdateManager.installApk(this, apkFile)
                    }
                    .setNeutralButton("Tarayıcıda Aç") { _, _ ->
                        try {
                            startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(updateInfo.downloadUrl)))
                        } catch (e: Exception) {}
                    }
                    .setNegativeButton("Kapat", null)
                    .show()
            },
            onError = { error ->
                updateActionBtn.isEnabled = true
                updateActionBtn.text = "Tekrar Dene"
                updateProgressBar.visibility = View.GONE
                Toast.makeText(this, "İndirme Hatası: $error", Toast.LENGTH_LONG).show()
            }
        )
    }

    private fun updateAudioUi() {
        if (!::audioStatusText.isInitialized || !::audioToggleBtn.isInitialized) return
        val isStreaming = AudioStreamEngine.instance.isStreaming
        audioStatusText.text = if (isStreaming) {
            "🟢 Sesi Bilgisayara Aktarılıyor (48kHz 16-bit Stereo, <15ms)\nBilgisayara takılı kulaklıktan dinleniyor."
        } else {
            "⚪ Ses aktarımı kapalı. Başlatıldığında tüm telefon sesleri bilgisayar kulaklığından gelir."
        }
        audioStatusText.setTextColor(if (isStreaming) Color.parseColor("#4ADE80") else Color.parseColor("#9CA3AF"))
        audioToggleBtn.text = if (isStreaming) "⏹️ Bilgisayara Ses Aktarımını Durdur" else "🎧 Sesi Bilgisayara / Kulaklığa Aktar"
        (audioToggleBtn.background as? GradientDrawable)?.setColor(
            Color.parseColor(if (isStreaming) "#DC2626" else "#059669")
        )
    }

    private fun toggleAudioStreaming() {
        val engine = AudioStreamEngine.instance
        if (engine.isStreaming) {
            val intent = Intent(this, AudioStreamService::class.java).apply {
                action = AudioStreamService.ACTION_STOP_AUDIO
            }
            startService(intent)
            engine.stop()
            updateAudioUi()
            Toast.makeText(this, "Ses aktarımı durduruldu", Toast.LENGTH_SHORT).show()
        } else {
            val pairedPc = ConnectMeService.discoveredPeers.firstOrNull { it.isMutuallyPaired }
            if (pairedPc == null) {
                Toast.makeText(this, "Önce bilgisayarla 6 haneli PIN eşleşmesini tamamlayın!", Toast.LENGTH_LONG).show()
                return
            }

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                val mediaProjectionManager = getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
                mediaProjectionLauncher.launch(mediaProjectionManager.createScreenCaptureIntent())
            } else {
                val intent = Intent(this, AudioStreamService::class.java).apply {
                    action = AudioStreamService.ACTION_START_AUDIO
                    putExtra("hostIp", pairedPc.ipAddress)
                }
                startService(intent)
                updateAudioUi()
            }
        }
    }
}
