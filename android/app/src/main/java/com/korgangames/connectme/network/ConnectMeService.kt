package com.korgangames.connectme.network

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.Environment
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import androidx.core.app.NotificationCompat
import com.korgangames.connectme.MainActivity
import com.korgangames.connectme.protocol.ProtocolConstants
import com.korgangames.connectme.protocol.TcpFrameCodec
import com.korgangames.connectme.protocol.WirePacketCodec
import com.korgangames.connectme.service.CursorAccessibilityService
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.io.InputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.ServerSocket
import java.net.Socket
import java.nio.charset.StandardCharsets
import java.security.SecureRandom
import java.util.Collections
import java.util.HashSet
import java.util.UUID
import java.util.concurrent.CopyOnWriteArrayList

import com.korgangames.connectme.protocol.ColonyMember

data class DiscoveredPcPeer(
    val deviceId: String,
    var deviceName: String,
    var platform: String,
    var ipAddress: String,
    var udpInputPort: Int,
    var tcpControlPort: Int,
    var myEnteredPinVerifiedByRemote: Boolean = false,
    var remoteEnteredMyPinVerified: Boolean = false,
    var colonyId: String? = null,
    var colonyName: String? = null,
    var colonyMembers: MutableList<ColonyMember> = mutableListOf(),
    var lastSeenMs: Long = System.currentTimeMillis()
) {
    val isMutuallyPaired: Boolean
        get() = myEnteredPinVerifiedByRemote && remoteEnteredMyPinVerified
}

data class AndroidShelfItem(
    val fileName: String,
    val filePath: String,
    val fileSizeBytes: Long,
    val senderName: String,
    val isOutgoing: Boolean
)

class ConnectMeService : Service() {

    companion object {
        const val ACTION_RETURN_TO_PC = "com.korgangames.connectme.RETURN_TO_PC"
        private const val CHANNEL_ID = "connect_me_service_channel"
        private const val NOTIFICATION_ID = 4285

        @Volatile
        var instance: ConnectMeService? = null
            private set

        val discoveredPeers = CopyOnWriteArrayList<DiscoveredPcPeer>()
        val shelfItems = CopyOnWriteArrayList<AndroidShelfItem>()
        val telemetryLogs = CopyOnWriteArrayList<String>()

        var onStateUpdated: (() -> Unit)? = null

        @Volatile
        var activePcAddress: InetAddress? = null

        @Volatile
        var activePcUdpPort: Int = ProtocolConstants.FAST_INPUT_UDP_PORT

        @Volatile
        var activePcTcpPort: Int = ProtocolConstants.DATA_CONTROL_TCP_PORT

        fun log(msg: String) {
            val line = "${java.text.SimpleDateFormat("yyyy-MM-dd HH:mm:ss", java.util.Locale.US).format(java.util.Date())} $msg"
            telemetryLogs.add(0, line)
            while (telemetryLogs.size > 200) {
                telemetryLogs.removeAt(telemetryLogs.size - 1)
            }
            try {
                instance?.let { ctx ->
                    val logFile = java.io.File(ctx.filesDir, "connectme_android.log")
                    if (logFile.length() > 3 * 1024 * 1024) {
                        val oldFile = java.io.File(ctx.filesDir, "connectme_android.old.log")
                        logFile.renameTo(oldFile)
                    }
                    logFile.appendText("$line\n")
                }
            } catch (_: Exception) {}
            android.util.Log.i("ConnectMe", msg)
            onStateUpdated?.invoke()
        }

        fun getFullLogText(context: Context): String {
            val sb = StringBuilder()
            try {
                val logFile = java.io.File(context.filesDir, "connectme_android.log")
                if (logFile.exists()) {
                    sb.append(logFile.readText())
                }
            } catch (_: Exception) {}
            if (sb.isEmpty()) {
                val reversed = telemetryLogs.toList().reversed()
                sb.append(reversed.joinToString("\n"))
            }
            return sb.toString()
        }

        fun clearLogs(context: Context) {
            telemetryLogs.clear()
            try {
                val logFile = java.io.File(context.filesDir, "connectme_android.log")
                if (logFile.exists()) {
                    logFile.writeText("[Connect Me Android Günlüğü Sıfırlandı]\n")
                }
            } catch (_: Exception) {}
            onStateUpdated?.invoke()
        }
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val mainHandler = Handler(Looper.getMainLooper())

    fun log(msg: String) = Companion.log(msg)

    private var discoverySocket: DatagramSocket? = null
    private var inputUdpSocket: DatagramSocket? = null
    private var tcpServerSocket: ServerSocket? = null

    val localDeviceId: String by lazy {
        val prefs = getSharedPreferences("connectme_prefs", Context.MODE_PRIVATE)
        var id = prefs.getString("persistent_local_device_id", null)
        if (id.isNullOrEmpty()) {
            val model = Build.MODEL.replace("[^a-zA-Z0-9]".toRegex(), "").take(8).lowercase(java.util.Locale.US)
            id = "android-$model-${UUID.randomUUID().toString().take(6)}"
            prefs.edit().putString("persistent_local_device_id", id).apply()
        }
        id
    }
    val localDeviceName: String = "${Build.MANUFACTURER} ${Build.MODEL}"

    @Volatile
    var localPairingPin: String = generateSixDigitPin()
        private set

    val suppressedAutoConnectDeviceIds: MutableSet<String> = Collections.synchronizedSet(HashSet())

    private var serviceClipboardManager: ClipboardManager? = null
    private var serviceClipboardListener: ClipboardManager.OnPrimaryClipChangedListener? = null

    @Volatile
    var lastReceivedClipboardHash: String? = null

    @Volatile
    var lastSentClipboardHash: String? = null

    override fun onCreate() {
        super.onCreate()
        instance = this
        startForegroundWithNotification()
        startNetworkLoops()

        mainHandler.post {
            try {
                serviceClipboardManager = getSystemService(Context.CLIPBOARD_SERVICE) as? ClipboardManager
                serviceClipboardListener = ClipboardManager.OnPrimaryClipChangedListener {
                    handleServiceClipboardChanged()
                }
                serviceClipboardManager?.addPrimaryClipChangedListener(serviceClipboardListener)
            } catch (_: Exception) {}
        }

        log("[Servis] Connect Me Android başlatıldı (Yerel 6 Haneli PIN: $localPairingPin).")
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_RETURN_TO_PC) {
            val cursorSvc = CursorAccessibilityService.instance
            cursorSvc?.deactivateCursor()
            sendEdgeHandOffBackToPeer(ProtocolConstants.EDGE_LEFT, 0.5f)
            log("[Kenar Dönüşü] Bildirim üzerinden bilgisayar ekranına dönüş tetiklendi.")
            return START_STICKY
        }
        return START_STICKY
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        serviceClipboardListener?.let {
            try { serviceClipboardManager?.removePrimaryClipChangedListener(it) } catch (_: Exception) {}
        }
        scope.cancel()
        try { discoverySocket?.close() } catch (_: Exception) {}
        try { inputUdpSocket?.close() } catch (_: Exception) {}
        try { tcpServerSocket?.close() } catch (_: Exception) {}
        if (instance === this) {
            instance = null
        }
        super.onDestroy()
    }

    fun regeneratePin(): String {
        localPairingPin = generateSixDigitPin()
        log("[Güvenlik] Yeni 6 haneli PIN üretildi: $localPairingPin")
        return localPairingPin
    }

    private fun generateSixDigitPin(): String {
        val num = 100000 + SecureRandom().nextInt(900000)
        return num.toString()
    }

    private fun startForegroundWithNotification() {
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val chan = NotificationChannel(
                CHANNEL_ID,
                "Connect Me Arka Plan Bağlantısı",
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

        val returnIntent = Intent(this, ConnectMeService::class.java).apply {
            action = ACTION_RETURN_TO_PC
        }
        val returnPendingIntent = PendingIntent.getService(
            this,
            1,
            returnIntent,
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )

        val notification: Notification = NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("Connect Me Aktif (PIN: $localPairingPin)")
            .setContentText("Çoklu cihaz kenar geçişi, çift taraflı PIN ve Drop Shelf hazır")
            .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
            .setContentIntent(pendingIntent)
            .addAction(android.R.drawable.ic_menu_revert, "⬅️ PC'ye Dön", returnPendingIntent)
            .setOngoing(true)
            .build()

        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                startForeground(
                    NOTIFICATION_ID,
                    notification,
                    ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE
                )
            } else {
                startForeground(NOTIFICATION_ID, notification)
            }
        } catch (e: Exception) {
            android.util.Log.e("ConnectMe", "startForeground failed: ${e.message}", e)
        }
    }

    private fun startNetworkLoops() {
        // 1. UDP Discovery Loop (42849)
        scope.launch {
            try {
                val sock = DatagramSocket(null).apply {
                    reuseAddress = true
                    broadcast = true
                    bind(InetSocketAddress(ProtocolConstants.DISCOVERY_UDP_PORT))
                }
                discoverySocket = sock
                launch { discoveryBroadcastLoop(sock) }
                launch { discoveryReceiveLoop(sock) }
            } catch (e: Exception) {
                log("[Uyarı] Discovery UDP başlatılamadı: ${e.message}")
            }
        }

        // 2. UDP Fast-Path Input Receiver (42850)
        scope.launch {
            try {
                val sock = DatagramSocket(null).apply {
                    reuseAddress = true
                    receiveBufferSize = 256 * 1024
                    bind(InetSocketAddress(ProtocolConstants.FAST_INPUT_UDP_PORT))
                }
                inputUdpSocket = sock
                log("[Fast-Path] UDP 42850 girdi kanalı hazır.")
                inputReceiveLoop(sock)
            } catch (e: Exception) {
                log("[Hata] UDP Girdi portu bağlanamadı: ${e.message}")
            }
        }

        // 3. TCP Mutual PIN, Clipboard & Drop Shelf Server (42851)
        scope.launch {
            try {
                val server = ServerSocket().apply {
                    reuseAddress = true
                    bind(InetSocketAddress(ProtocolConstants.DATA_CONTROL_TCP_PORT))
                }
                tcpServerSocket = server
                log("[Veri & PIN Kanalı] TCP 42851 sunucusu hazır.")
                while (isActive) {
                    val client = server.accept()
                    launch { handleTcpClient(client) }
                }
            } catch (e: Exception) {
                if (isActive) log("[Uyarı] TCP sunucu: ${e.message}")
            }
        }
    }

    private suspend fun discoveryBroadcastLoop(sock: DatagramSocket) {
        while (scope.isActive) {
            sendDiscoveryBeacon(sock, InetAddress.getByName("255.255.255.255"))
            delay(3000)
        }
    }

    fun triggerManualConnectAndVerifyPin(ipAddress: String, enteredRemotePin: String) {
        scope.launch {
            try {
                val cleanIp = ipAddress.trim()
                val cleanPin = enteredRemotePin.trim().replace(" ", "").replace("-", "")
                val addr = InetAddress.getByName(cleanIp)
                activePcAddress = addr

                var peer = discoveredPeers.find { it.ipAddress == cleanIp }
                if (peer == null) {
                    peer = DiscoveredPcPeer(
                        deviceId = "pc-$cleanIp",
                        deviceName = "Cihaz ($cleanIp)",
                        platform = "windows",
                        ipAddress = cleanIp,
                        udpInputPort = ProtocolConstants.FAST_INPUT_UDP_PORT,
                        tcpControlPort = ProtocolConstants.DATA_CONTROL_TCP_PORT
                    )
                    discoveredPeers.add(0, peer)
                }

                discoverySocket?.let { sendDiscoveryBeacon(it, addr) }

                if (cleanPin.length == 6) {
                    submitRemotePinToPeerInternal(peer, cleanPin)
                } else {
                    log("[Keşif] $cleanIp eklendi. Eşleşmek için karşı cihazın 6 haneli PIN kodunu girin.")
                }
            } catch (e: Exception) {
                log("[Hata] Bağlantı hatası: ${e.message}")
            }
        }
    }

    fun submitRemotePinToPeer(peer: DiscoveredPcPeer, enteredRemotePin: String) {
        scope.launch {
            submitRemotePinToPeerInternal(peer, enteredRemotePin.trim().replace(" ", "").replace("-", ""))
        }
    }

    private fun submitRemotePinToPeerInternal(peer: DiscoveredPcPeer, cleanPin: String) {
        if (cleanPin.length != 6) {
            log("[PIN Uyarı] Lütfen karşı cihazdaki 6 haneli kodu eksiksiz girin.")
            return
        }

        suppressedAutoConnectDeviceIds.remove(peer.deviceId)

        try {
            val cursorSvc = CursorAccessibilityService.instance
            Socket().use { socket ->
                socket.connect(InetSocketAddress(peer.ipAddress, peer.tcpControlPort), 6000)
                val reqHeader = JSONObject().apply {
                    put("type", "PAIR_REQUEST")
                    put("senderId", localDeviceId)
                    put("senderName", localDeviceName)
                    put("senderPlatform", "android")
                    put("senderUdpPort", ProtocolConstants.FAST_INPUT_UDP_PORT)
                    put("senderTcpPort", ProtocolConstants.DATA_CONTROL_TCP_PORT)
                    put("senderScreenWidth", cursorSvc?.screenWidth ?: 1080)
                    put("senderScreenHeight", cursorSvc?.screenHeight ?: 2400)
                    put("targetPin", cleanPin)
                }
                TcpFrameCodec.writeFrame(socket.getOutputStream(), reqHeader)

                val resp = TcpFrameCodec.readHeader(socket.getInputStream())
                if (resp != null && resp.first.optString("type") == "PAIR_VERIFY_ACK") {
                    peer.myEnteredPinVerifiedByRemote = true
                    if (resp.first.optBoolean("isMutualComplete", false)) {
                        peer.remoteEnteredMyPinVerified = true
                    }
                    val respColId = resp.first.optString("colonyId", "").takeIf { it.isNotEmpty() }
                    if (respColId != null) peer.colonyId = respColId
                    val respColMembers = resp.first.optJSONArray("colonyMembers")
                    if (respColMembers != null) {
                        val membersList = mutableListOf<ColonyMember>()
                        for (i in 0 until respColMembers.length()) {
                            val mo = respColMembers.getJSONObject(i)
                            membersList.add(
                                ColonyMember(
                                    deviceId = mo.optString("deviceId"),
                                    deviceName = mo.optString("deviceName"),
                                    platform = mo.optString("platform", "windows"),
                                    ipAddress = mo.optString("ipAddress"),
                                    tcpPort = mo.optInt("tcpControlPort", ProtocolConstants.DATA_CONTROL_TCP_PORT),
                                    udpPort = mo.optInt("udpInputPort", ProtocolConstants.FAST_INPUT_UDP_PORT)
                                )
                            )
                        }
                        if (membersList.isNotEmpty()) peer.colonyMembers = membersList
                    }
                    try {
                        activePcAddress = InetAddress.getByName(peer.ipAddress)
                    } catch (_: Exception) {}
                    activePcUdpPort = peer.udpInputPort
                    activePcTcpPort = peer.tcpControlPort

                    if (peer.isMutuallyPaired) {
                        introduceNewMemberToColony(peer)
                        log("[Çift Taraflı Eşleşme] ✅ '${peer.deviceName}' ile karşılıklı 6 haneli PIN doğrulaması tamamlandı!")
                    } else {
                        log("[PIN Doğrulama] ✅ '${peer.deviceName}' kodu doğrulandı! Şimdi karşı cihazda da sizin kodunuzu ($localPairingPin) girin.")
                    }
                    onStateUpdated?.invoke()
                } else {
                    log("[PIN Red] ❌ '${peer.deviceName}' girdiğiniz 6 haneli kodu ($cleanPin) reddetti.")
                }
            }
        } catch (e: Exception) {
            log("[PIN Hata] '${peer.deviceName}' (${peer.ipAddress}) ulaşılamadı: ${e.message}")
        }
    }

    fun disconnectPeer(peer: DiscoveredPcPeer) {
        suppressedAutoConnectDeviceIds.add(peer.deviceId)
        peer.myEnteredPinVerifiedByRemote = false
        peer.remoteEnteredMyPinVerified = false
        if (activePcAddress?.hostAddress == peer.ipAddress) {
            activePcAddress = null
        }
        CursorAccessibilityService.instance?.deactivateCursor()

        scope.launch {
            try {
                Socket().use { sock ->
                    sock.connect(InetSocketAddress(peer.ipAddress, peer.tcpControlPort), 2500)
                    val header = JSONObject().apply {
                        put("type", "DISCONNECT")
                        put("senderId", localDeviceId)
                        put("senderName", localDeviceName)
                        put("senderPlatform", "android")
                    }
                    TcpFrameCodec.writeFrame(sock.getOutputStream(), header)
                }
            } catch (e: Exception) {}
        }

        log("[Bağlantı] 🔌 '${peer.deviceName}' ile olan bağlantı sonlandırıldı.")
        onStateUpdated?.invoke()
    }

    fun acceptInboundPairing(peer: DiscoveredPcPeer, rememberDevice: Boolean = true) {
        peer.myEnteredPinVerifiedByRemote = true
        peer.remoteEnteredMyPinVerified = true
        val token = if (rememberDevice) UUID.randomUUID().toString() else null
        if (token != null) {
            saveTrustTokenForDevice(peer.deviceId, token)
        }

        scope.launch {
            try {
                Socket().use { sock ->
                    sock.connect(InetSocketAddress(peer.ipAddress, peer.tcpControlPort), 4000)
                    val header = JSONObject().apply {
                        put("type", "PAIR_ACCEPT")
                        put("senderId", localDeviceId)
                        put("senderName", localDeviceName)
                        put("senderPlatform", "android")
                        put("senderUdpPort", ProtocolConstants.FAST_INPUT_UDP_PORT)
                        put("senderTcpPort", ProtocolConstants.DATA_CONTROL_TCP_PORT)
                        val colId = getCurrentColonyId()
                        if (colId != null) {
                            put("colonyId", colId)
                            val arr = org.json.JSONArray()
                            getColonyMembers().forEach { m ->
                                arr.put(JSONObject().apply {
                                    put("deviceId", m.deviceId)
                                    put("deviceName", m.deviceName)
                                    put("platform", m.platform)
                                    put("ipAddress", m.ipAddress)
                                    put("tcpControlPort", m.tcpPort)
                                    put("udpInputPort", m.udpPort)
                                })
                            }
                            put("colonyMembers", arr)
                        }
                        if (rememberDevice && token != null) {
                            put("requestTrust", true)
                            put("trustToken", token)
                        }
                        put("isMutualComplete", true)
                    }
                    TcpFrameCodec.writeFrame(sock.getOutputStream(), header)
                }
            } catch (e: Exception) {
                log("[Bağlantı Uyarı] PAIR_ACCEPT iletimi: ${e.message}")
            }

            introduceNewMemberToColony(peer)
            log("[Çift Taraflı Eşleşme] ✅ '${peer.deviceName}' bağlantı isteği onaylandı ve karşılıklı eşleşme sağlandı!")
            onStateUpdated?.invoke()
        }
    }

    fun connectToColonyViaPeer(colonyPeer: DiscoveredPcPeer, pin: String) {
        scope.launch {
            val cleanPin = pin.trim().replace(" ", "").replace("-", "")
            submitRemotePinToPeerInternal(colonyPeer, cleanPin)
            if (colonyPeer.myEnteredPinVerifiedByRemote && colonyPeer.colonyMembers.isNotEmpty()) {
                for (m in colonyPeer.colonyMembers) {
                    if (m.deviceId == localDeviceId || m.deviceId == colonyPeer.deviceId) continue
                    var p = discoveredPeers.find { it.deviceId == m.deviceId || it.ipAddress == m.ipAddress }
                    if (p == null) {
                        p = DiscoveredPcPeer(
                            deviceId = m.deviceId,
                            deviceName = m.deviceName,
                            platform = m.platform,
                            ipAddress = m.ipAddress,
                            udpInputPort = m.udpPort,
                            tcpControlPort = m.tcpPort,
                            myEnteredPinVerifiedByRemote = true,
                            remoteEnteredMyPinVerified = true,
                            colonyId = colonyPeer.colonyId
                        )
                        discoveredPeers.add(0, p)
                    } else {
                        p.deviceName = m.deviceName
                        p.platform = m.platform
                        p.ipAddress = m.ipAddress
                        p.udpInputPort = m.udpPort
                        p.tcpControlPort = m.tcpPort
                        p.myEnteredPinVerifiedByRemote = true
                        p.remoteEnteredMyPinVerified = true
                        p.colonyId = colonyPeer.colonyId
                    }
                }
                log("[Koloni] 🪐 '${colonyPeer.deviceName}' üzerinden tüm koloniye (${colonyPeer.colonyMembers.size} cihaz) tek PIN ile bağlanıldı!")
                onStateUpdated?.invoke()
            }
        }
    }

    fun getCurrentColonyId(): String? {
        val paired = discoveredPeers.filter { it.isMutuallyPaired }
        if (paired.isEmpty()) return null
        val allIds = (paired.map { it.deviceId } + localDeviceId).sorted()
        val combined = allIds.joinToString("|")
        return try {
            val md = java.security.MessageDigest.getInstance("SHA-256")
            val digest = md.digest(combined.toByteArray(StandardCharsets.UTF_8))
            val hex = digest.joinToString("") { "%02x".format(it) }
            "colony-${hex.take(12)}"
        } catch (_: Exception) {
            "colony-${combined.hashCode().toUInt().toString(16)}"
        }
    }

    fun getColonyMembers(): List<ColonyMember> {
        val list = mutableListOf(
            ColonyMember(
                deviceId = localDeviceId,
                deviceName = localDeviceName,
                platform = "android",
                ipAddress = getLocalIpv4Address(),
                tcpPort = ProtocolConstants.DATA_CONTROL_TCP_PORT,
                udpPort = ProtocolConstants.FAST_INPUT_UDP_PORT
            )
        )
        for (p in discoveredPeers.filter { it.isMutuallyPaired }) {
            list.add(
                ColonyMember(
                    deviceId = p.deviceId,
                    deviceName = p.deviceName,
                    platform = p.platform,
                    ipAddress = p.ipAddress,
                    tcpPort = p.tcpControlPort,
                    udpPort = p.udpInputPort
                )
            )
        }
        return list
    }

    fun introduceNewMemberToColony(newMember: DiscoveredPcPeer) {
        scope.launch {
            val otherPaired = discoveredPeers.filter { it.isMutuallyPaired && it.deviceId != newMember.deviceId }
            if (otherPaired.isEmpty()) return@launch

            val colId = getCurrentColonyId()
            val allMembers = getColonyMembers()
            val membersArray = org.json.JSONArray().apply {
                allMembers.forEach { m ->
                    put(JSONObject().apply {
                        put("deviceId", m.deviceId)
                        put("deviceName", m.deviceName)
                        put("platform", m.platform)
                        put("ipAddress", m.ipAddress)
                        put("tcpControlPort", m.tcpPort)
                        put("udpInputPort", m.udpPort)
                    })
                }
            }
            val newMemberObj = JSONObject().apply {
                put("deviceId", newMember.deviceId)
                put("deviceName", newMember.deviceName)
                put("platform", newMember.platform)
                put("ipAddress", newMember.ipAddress)
                put("tcpControlPort", newMember.tcpControlPort)
                put("udpInputPort", newMember.udpInputPort)
            }

            for (existingPeer in otherPaired) {
                try {
                    Socket().use { sock ->
                        sock.connect(InetSocketAddress(existingPeer.ipAddress, existingPeer.tcpControlPort), 3000)
                        val header = JSONObject().apply {
                            put("type", "COLONY_INTRODUCE")
                            put("senderId", localDeviceId)
                            put("senderName", localDeviceName)
                            if (colId != null) put("colonyId", colId)
                            put("colonyMembers", membersArray)
                            put("newMember", newMemberObj)
                        }
                        TcpFrameCodec.writeFrame(sock.getOutputStream(), header)
                    }
                } catch (_: Exception) {}
            }

            try {
                Socket().use { sock ->
                    sock.connect(InetSocketAddress(newMember.ipAddress, newMember.tcpControlPort), 3000)
                    val header = JSONObject().apply {
                        put("type", "COLONY_MEMBERS_SYNC")
                        put("senderId", localDeviceId)
                        put("senderName", localDeviceName)
                        if (colId != null) put("colonyId", colId)
                        put("colonyMembers", membersArray)
                    }
                    TcpFrameCodec.writeFrame(sock.getOutputStream(), header)
                }
            } catch (_: Exception) {}

            log("[Koloni] 🪐 '${newMember.deviceName}' tüm koloni üyelerine (${otherPaired.size} cihaz) başarıyla tanıtıldı!")
        }
    }

    private fun sendDiscoveryBeacon(sock: DatagramSocket, targetAddr: InetAddress) {
        try {
            val cursorSvc = CursorAccessibilityService.instance
            val colId = getCurrentColonyId()
            val colMembers = if (colId != null) getColonyMembers() else emptyList()
            val json = JSONObject().apply {
                put("type", "DISCOVER_BEACON")
                put("deviceId", localDeviceId)
                put("deviceName", localDeviceName)
                put("platform", "android")
                put("udpInputPort", ProtocolConstants.FAST_INPUT_UDP_PORT)
                put("tcpControlPort", ProtocolConstants.DATA_CONTROL_TCP_PORT)
                put("screenWidth", cursorSvc?.screenWidth ?: 1080)
                put("screenHeight", cursorSvc?.screenHeight ?: 2400)
                if (colId != null) {
                    put("colonyId", colId)
                    val arr = org.json.JSONArray()
                    colMembers.forEach { m ->
                        arr.put(JSONObject().apply {
                            put("deviceId", m.deviceId)
                            put("deviceName", m.deviceName)
                            put("platform", m.platform)
                            put("ipAddress", m.ipAddress)
                            put("tcpControlPort", m.tcpPort)
                            put("udpInputPort", m.udpPort)
                        })
                    }
                    put("colonyMembers", arr)
                }
                put("timestamp", System.currentTimeMillis())
            }
            val bytes = json.toString().toByteArray(StandardCharsets.UTF_8)
            val pkt = DatagramPacket(bytes, bytes.size, targetAddr, ProtocolConstants.DISCOVERY_UDP_PORT)
            sock.send(pkt)
        } catch (_: Exception) {
        }
    }

    private suspend fun discoveryReceiveLoop(sock: DatagramSocket) {
        val buf = ByteArray(4096)
        while (scope.isActive) {
            try {
                val pkt = DatagramPacket(buf, buf.size)
                sock.receive(pkt)
                val str = String(pkt.data, 0, pkt.length, StandardCharsets.UTF_8)
                val json = JSONObject(str)
                val deviceId = json.optString("deviceId", "")
                if (deviceId.isEmpty() || deviceId == localDeviceId) continue

                val senderIp = pkt.address.hostAddress ?: continue
                val deviceName = json.optString("deviceName", "PC")
                val platform = json.optString("platform", "windows")
                val udpPort = json.optInt("udpInputPort", ProtocolConstants.FAST_INPUT_UDP_PORT)
                val tcpPort = json.optInt("tcpControlPort", ProtocolConstants.DATA_CONTROL_TCP_PORT)

                val colId = json.optString("colonyId", "").takeIf { it.isNotEmpty() }
                val colName = json.optString("colonyName", "").takeIf { it.isNotEmpty() }
                val membersArray = json.optJSONArray("colonyMembers")
                val membersList = mutableListOf<ColonyMember>()
                if (membersArray != null) {
                    for (i in 0 until membersArray.length()) {
                        val mo = membersArray.getJSONObject(i)
                        membersList.add(
                            ColonyMember(
                                deviceId = mo.optString("deviceId"),
                                deviceName = mo.optString("deviceName"),
                                platform = mo.optString("platform", "windows"),
                                ipAddress = mo.optString("ipAddress"),
                                tcpPort = mo.optInt("tcpControlPort", ProtocolConstants.DATA_CONTROL_TCP_PORT),
                                udpPort = mo.optInt("udpInputPort", ProtocolConstants.FAST_INPUT_UDP_PORT)
                            )
                        )
                    }
                }

                val existing = discoveredPeers.find { it.deviceId == deviceId || it.ipAddress == senderIp }
                val savedToken = getTrustTokenForDevice(deviceId)
                if (existing != null) {
                    existing.deviceName = deviceName
                    existing.platform = platform
                    existing.ipAddress = senderIp
                    existing.udpInputPort = udpPort
                    existing.tcpControlPort = tcpPort
                    if (colId != null) existing.colonyId = colId
                    if (colName != null) existing.colonyName = colName
                    if (membersList.isNotEmpty()) existing.colonyMembers = membersList
                    existing.lastSeenMs = System.currentTimeMillis()
                    if (savedToken != null && !existing.isMutuallyPaired && !suppressedAutoConnectDeviceIds.contains(deviceId)) {
                        triggerTrustedReconnect(existing, savedToken)
                    }
                } else {
                    val peer = DiscoveredPcPeer(deviceId, deviceName, platform, senderIp, udpPort, tcpPort).apply {
                        this.colonyId = colId
                        this.colonyName = colName
                        if (membersList.isNotEmpty()) this.colonyMembers = membersList
                    }
                    discoveredPeers.add(0, peer)
                    val trustMsg = if (savedToken != null) " [⭐ Güvenilir - Otomatik Bağlanıyor...]" else " — Çift taraflı 6 haneli PIN ile eşleşebilirsiniz."
                    log("[Keşif] Cihaz bulundu: $deviceName ($senderIp)$trustMsg")
                    sendDiscoveryBeacon(sock, pkt.address)
                    if (savedToken != null && !suppressedAutoConnectDeviceIds.contains(deviceId)) {
                        triggerTrustedReconnect(peer, savedToken)
                    }
                }
                onStateUpdated?.invoke()
            } catch (_: Exception) {
            }
        }
    }

    private suspend fun inputReceiveLoop(sock: DatagramSocket) {
        val buf = ByteArray(64)
        while (scope.isActive) {
            try {
                val pkt = DatagramPacket(buf, buf.size)
                sock.receive(pkt)
                val len = pkt.length
                if (!WirePacketCodec.isValidHeader(buf, len)) continue

                val senderIp = pkt.address.hostAddress ?: continue
                val senderPeer = discoveredPeers.find { it.ipAddress == senderIp }
                if (senderPeer != null && !senderPeer.isMutuallyPaired && !senderPeer.remoteEnteredMyPinVerified) {
                    // Reject input from unverified peers
                    continue
                }

                activePcAddress = pkt.address
                activePcUdpPort = pkt.port

                val cursorSvc = CursorAccessibilityService.instance
                when (WirePacketCodec.getPacketType(buf)) {
                    ProtocolConstants.PACKET_EDGE_HANDOFF -> {
                        WirePacketCodec.decodeEdgeHandOff(buf, len)?.let { ho ->
                            cursorSvc?.onEdgeHandOffEnter(ho.targetEntranceEdge, ho.normalizedPosition)
                            log("[Kenar Geçişi] İmleç Android ekranına geçti (%${(ho.normalizedPosition * 100).toInt()}).")
                        }
                    }

                    ProtocolConstants.PACKET_MOUSE_MOVE -> {
                        WirePacketCodec.decodeMouseMove(buf, len)?.let { mm ->
                            cursorSvc?.onRemoteMouseMove(mm)
                        }
                    }

                    ProtocolConstants.PACKET_MOUSE_BUTTON -> {
                        WirePacketCodec.decodeMouseButton(buf, len)?.let { mb ->
                            cursorSvc?.onRemoteMouseButton(mb)
                        }
                    }

                    ProtocolConstants.PACKET_MOUSE_SCROLL -> {
                        WirePacketCodec.decodeMouseScroll(buf, len)?.let { ms ->
                            cursorSvc?.onRemoteMouseScroll(ms)
                        }
                    }

                    ProtocolConstants.PACKET_KEY_EVENT -> {
                        WirePacketCodec.decodeKeyEvent(buf, len)?.let { ke ->
                            cursorSvc?.onRemoteKeyEvent(ke)
                        }
                    }

                    ProtocolConstants.PACKET_HEARTBEAT_PING -> {
                        WirePacketCodec.decodeHeartbeatTimestamp(buf, len)?.let { ts ->
                            val pong = WirePacketCodec.encodeHeartbeat(ProtocolConstants.PACKET_HEARTBEAT_PONG, ts)
                            sock.send(DatagramPacket(pong, pong.size, pkt.address, pkt.port))
                        }
                    }
                }
            } catch (_: Exception) {
            }
        }
    }

    fun sendEdgeHandOffBackToPeer(windowsEntranceEdge: Byte, normalizedPos: Float) {
        scope.launch(Dispatchers.IO) {
            val pairedPeer = discoveredPeers.firstOrNull { it.isMutuallyPaired }
                ?: discoveredPeers.firstOrNull { it.remoteEnteredMyPinVerified || it.myEnteredPinVerifiedByRemote }
            val targetAddr = activePcAddress
                ?: (if (pairedPeer != null) {
                    try { InetAddress.getByName(pairedPeer.ipAddress) } catch (_: Exception) { null }
                } else null)

            val senderPeer = (if (targetAddr != null) discoveredPeers.find { it.ipAddress == targetAddr.hostAddress } else null)
                ?: pairedPeer

            val targetUdpPort = senderPeer?.udpInputPort ?: ProtocolConstants.FAST_INPUT_UDP_PORT
            val targetTcpPort = senderPeer?.tcpControlPort ?: ProtocolConstants.DATA_CONTROL_TCP_PORT

            // 1. Fast UDP Datagram (3x burst for redundancy)
            if (targetAddr != null) {
                try {
                    val sock = inputUdpSocket
                    if (sock != null && !sock.isClosed) {
                        val bytes = WirePacketCodec.encodeEdgeHandOff(windowsEntranceEdge, false, normalizedPos)
                        val dp = DatagramPacket(bytes, bytes.size, targetAddr, targetUdpPort)
                        sock.send(dp)
                        sock.send(dp)
                        sock.send(dp)
                    }
                } catch (e: Exception) {
                    log("[UDP Uyarı] Kenar dönüş UDP: ${e.message}")
                }
            }

            // 2. Guaranteed TCP Fallback Handoff (ensures Wi-Fi drops never trap cursor on phone)
            val tcpTargetIp = senderPeer?.ipAddress ?: targetAddr?.hostAddress
            if (tcpTargetIp != null) {
                try {
                    Socket().use { socket ->
                        socket.connect(InetSocketAddress(tcpTargetIp, targetTcpPort), 2500)
                        val header = JSONObject().apply {
                            put("type", "EDGE_RETURN")
                            put("senderId", localDeviceId)
                            put("senderName", localDeviceName)
                            put("returnEdge", windowsEntranceEdge.toInt())
                            put("normalizedPosition", normalizedPos.toDouble())
                        }
                        TcpFrameCodec.writeFrame(socket.getOutputStream(), header)
                    }
                } catch (e: Exception) {
                    log("[TCP Uyarı] Kenar dönüş TCP: ${e.message}")
                }
            }

            log("[Kenar Geçişi] ⬅️ İmleç Android'den bilgisayara döndü (Kenar: $windowsEntranceEdge, %${(normalizedPos * 100).toInt()}).")
        }
    }

    private fun handleServiceClipboardChanged() {
        try {
            val cm = serviceClipboardManager ?: return
            val clip = cm.primaryClip ?: return
            if (clip.itemCount == 0) return
            val text = clip.getItemAt(0)?.coerceToText(this)?.toString() ?: return
            if (text.isBlank()) return

            val hash = hashText(text)
            if (hash == lastReceivedClipboardHash || hash == lastSentClipboardHash) {
                return
            }

            lastSentClipboardHash = hash
            sendClipboardTextToPc(text)
        } catch (_: Exception) {
        }
    }

    fun hashText(str: String): String {
        return try {
            val md = java.security.MessageDigest.getInstance("SHA-256")
            val digest = md.digest(str.toByteArray(StandardCharsets.UTF_8))
            digest.joinToString("") { "%02x".format(it) }
        } catch (_: Exception) {
            str.hashCode().toString()
        }
    }

    fun sendClipboardTextToPc(text: String) {
        if (text.isBlank()) return
        scope.launch {
            val pairedTargets = discoveredPeers.filter { it.isMutuallyPaired || it.remoteEnteredMyPinVerified || it.myEnteredPinVerifiedByRemote }
            if (pairedTargets.isEmpty()) {
                log("[Uyarı] Pano göndermek için önce en az bir cihazla 6 haneli PIN onayını tamamlayın.")
                return@launch
            }

            for (peer in pairedTargets) {
                try {
                    Socket().use { socket ->
                        socket.connect(InetSocketAddress(peer.ipAddress, peer.tcpControlPort), 5000)
                        val header = JSONObject().apply {
                            put("type", "CLIPBOARD_TEXT")
                            put("senderId", localDeviceId)
                            put("senderName", localDeviceName)
                            put("text", text)
                        }
                        TcpFrameCodec.writeFrame(socket.getOutputStream(), header)
                    }
                } catch (_: Exception) {
                }
            }
            log("[Evrensel Pano] Metin (${text.length} krk) tüm onaylı cihazlara gönderildi.")
        }
    }

    fun sendStreamToPcShelf(fileName: String, inputStream: InputStream, fileSize: Long) {
        scope.launch {
            val targetPeer = discoveredPeers.firstOrNull { it.isMutuallyPaired }
                ?: discoveredPeers.firstOrNull { it.remoteEnteredMyPinVerified || it.myEnteredPinVerifiedByRemote }
                ?: discoveredPeers.firstOrNull()
            if (targetPeer == null || (!targetPeer.isMutuallyPaired && !targetPeer.remoteEnteredMyPinVerified && !targetPeer.myEnteredPinVerifiedByRemote)) {
                log("[Uyarı] Dosya göndermek için önce cihazla 6 haneli PIN onayını tamamlayın.")
                return@launch
            }

            try {
                Socket().use { socket ->
                    socket.connect(InetSocketAddress(targetPeer.ipAddress, targetPeer.tcpControlPort), 8000)
                    val header = JSONObject().apply {
                        put("type", "SHELF_FILE")
                        put("senderId", localDeviceId)
                        put("senderName", localDeviceName)
                        put("fileName", fileName)
                        put("mimeType", "application/octet-stream")
                    }
                    inputStream.use { stream ->
                        TcpFrameCodec.writeFrame(socket.getOutputStream(), header, stream, fileSize)
                    }
                }
                shelfItems.add(
                    0,
                    AndroidShelfItem(
                        fileName = fileName,
                        filePath = "",
                        fileSizeBytes = fileSize,
                        senderName = "$localDeviceName -> ${targetPeer.deviceName}",
                        isOutgoing = true
                    )
                )
                log("[Drop Shelf] '$fileName' -> '${targetPeer.deviceName}' Ortak Cebine gönderildi!")
            } catch (e: Exception) {
                log("[Drop Shelf Hata] '$fileName' gönderilemedi: ${e.message}")
            }
        }
    }

    private fun handleTcpClient(client: Socket) {
        client.use { sock ->
            val input = sock.getInputStream()
            val output = sock.getOutputStream()
            val (header, binLen) = TcpFrameCodec.readHeader(input) ?: return
            val type = header.optString("type", "")
            val senderId = header.optString("senderId", "")
            val senderName = header.optString("senderName", "Cihaz")
            val remoteIp = sock.inetAddress.hostAddress ?: "0.0.0.0"

            when (type) {
                "PAIR_REQUEST" -> {
                    val submittedPin = header.optString("targetPin", "").trim()
                    val udpPort = header.optInt("senderUdpPort", ProtocolConstants.FAST_INPUT_UDP_PORT)
                    val tcpPort = header.optInt("senderTcpPort", ProtocolConstants.DATA_CONTROL_TCP_PORT)
                    val platform = header.optString("senderPlatform", "windows")
                    val reqTrust = header.optBoolean("requestTrust", false)
                    val trustToken = header.optString("trustToken", "")

                    val peer = discoveredPeers.find { it.deviceId == senderId || it.ipAddress == remoteIp }
                        ?: DiscoveredPcPeer(
                            deviceId = if (senderId.isNotEmpty()) senderId else "peer-$remoteIp",
                            deviceName = senderName,
                            platform = platform,
                            ipAddress = remoteIp,
                            udpInputPort = udpPort,
                            tcpControlPort = tcpPort
                        ).also { discoveredPeers.add(0, it) }

                    peer.deviceName = senderName
                    peer.ipAddress = remoteIp
                    peer.udpInputPort = udpPort
                    peer.tcpControlPort = tcpPort

                    if (submittedPin == localPairingPin) {
                        peer.remoteEnteredMyPinVerified = true
                        activePcAddress = sock.inetAddress
                        activePcUdpPort = udpPort
                        activePcTcpPort = tcpPort

                        val initReturnEdge = header.optInt("returnEdge", ProtocolConstants.EDGE_NONE.toInt()).toByte()
                        if (initReturnEdge != ProtocolConstants.EDGE_NONE) {
                            CursorAccessibilityService.instance?.activeEntranceEdge = initReturnEdge
                        }

                        if (reqTrust && trustToken.isNotEmpty()) {
                            saveTrustTokenForDevice(peer.deviceId, trustToken)
                            log("[Güvenlik] ⭐ '$senderName' güvenilir cihaz olarak hatırlandı.")
                        }

                        val ack = JSONObject().apply {
                            put("type", "PAIR_VERIFY_ACK")
                            put("senderId", localDeviceId)
                            put("senderName", localDeviceName)
                            val colId = getCurrentColonyId()
                            if (colId != null) {
                                put("colonyId", colId)
                                val arr = org.json.JSONArray()
                                getColonyMembers().forEach { m ->
                                    arr.put(JSONObject().apply {
                                        put("deviceId", m.deviceId)
                                        put("deviceName", m.deviceName)
                                        put("platform", m.platform)
                                        put("ipAddress", m.ipAddress)
                                        put("tcpControlPort", m.tcpPort)
                                        put("udpInputPort", m.udpPort)
                                    })
                                }
                                put("colonyMembers", arr)
                            }
                            if (reqTrust && trustToken.isNotEmpty()) {
                                put("trustToken", trustToken)
                                put("requestTrust", true)
                            }
                            put("isMutualComplete", peer.isMutuallyPaired)
                        }
                        TcpFrameCodec.writeFrame(output, ack)

                        val trustTag = if (reqTrust) " (⭐ Cihaz hatırlandı)" else ""
                        if (peer.isMutuallyPaired) {
                            introduceNewMemberToColony(peer)
                            log("[Çift Taraflı Eşleşme] ✅ '$senderName' ile karşılıklı 6 haneli PIN doğrulaması tamamlandı!$trustTag")
                        } else {
                            log("[PIN İsteği] 🔔 '$senderName' sizin kodunuzu ($localPairingPin) doğruladı!$trustTag Bağlantıyı tamamlamak için siz de onun 6 haneli kodunu girin veya 'Kabul Et'e basın.")
                        }
                    } else {
                        val rej = JSONObject().apply {
                            put("type", "PAIR_REJECT")
                            put("senderId", localDeviceId)
                            put("senderName", localDeviceName)
                        }
                        TcpFrameCodec.writeFrame(output, rej)
                        log("[Güvenlik] ⚠️ '$senderName' hatalı 6 haneli kod denedi (Girilen: $submittedPin, Beklenen: $localPairingPin).")
                    }
                    onStateUpdated?.invoke()
                }

                "PAIR_ACCEPT" -> {
                    val udpPort = header.optInt("senderUdpPort", ProtocolConstants.FAST_INPUT_UDP_PORT)
                    val tcpPort = header.optInt("senderTcpPort", ProtocolConstants.DATA_CONTROL_TCP_PORT)
                    val platform = header.optString("senderPlatform", "windows")
                    val reqTrust = header.optBoolean("requestTrust", false)
                    val trustToken = header.optString("trustToken", "")

                    val peer = discoveredPeers.find { it.deviceId == senderId || it.ipAddress == remoteIp }
                        ?: DiscoveredPcPeer(
                            deviceId = if (senderId.isNotEmpty()) senderId else "peer-$remoteIp",
                            deviceName = senderName,
                            platform = platform,
                            ipAddress = remoteIp,
                            udpInputPort = udpPort,
                            tcpControlPort = tcpPort
                        ).also { discoveredPeers.add(0, it) }

                    peer.deviceName = senderName
                    peer.ipAddress = remoteIp
                    peer.udpInputPort = udpPort
                    peer.tcpControlPort = tcpPort
                    peer.myEnteredPinVerifiedByRemote = true
                    peer.remoteEnteredMyPinVerified = true
                    activePcAddress = sock.inetAddress
                    activePcUdpPort = udpPort
                    activePcTcpPort = tcpPort

                    val colId = header.optString("colonyId", "").takeIf { it.isNotEmpty() }
                    if (colId != null) peer.colonyId = colId
                    val membersArray = header.optJSONArray("colonyMembers")
                    if (membersArray != null) {
                        val membersList = mutableListOf<ColonyMember>()
                        for (i in 0 until membersArray.length()) {
                            val mo = membersArray.getJSONObject(i)
                            membersList.add(ColonyMember(
                                deviceId = mo.optString("deviceId"),
                                deviceName = mo.optString("deviceName"),
                                platform = mo.optString("platform", "windows"),
                                ipAddress = mo.optString("ipAddress"),
                                tcpPort = mo.optInt("tcpControlPort", ProtocolConstants.DATA_CONTROL_TCP_PORT),
                                udpPort = mo.optInt("udpInputPort", ProtocolConstants.FAST_INPUT_UDP_PORT)
                            ))
                        }
                        if (membersList.isNotEmpty()) peer.colonyMembers = membersList
                    }

                    if (reqTrust && trustToken.isNotEmpty()) {
                        saveTrustTokenForDevice(peer.deviceId, trustToken)
                    }

                    val ack = JSONObject().apply {
                        put("type", "PAIR_ACCEPT_ACK")
                        put("senderId", localDeviceId)
                        put("senderName", localDeviceName)
                        val cId = getCurrentColonyId()
                        if (cId != null) {
                            put("colonyId", cId)
                            val arr = org.json.JSONArray()
                            getColonyMembers().forEach { m ->
                                arr.put(JSONObject().apply {
                                    put("deviceId", m.deviceId)
                                    put("deviceName", m.deviceName)
                                    put("platform", m.platform)
                                    put("ipAddress", m.ipAddress)
                                    put("tcpControlPort", m.tcpPort)
                                    put("udpInputPort", m.udpPort)
                                })
                            }
                            put("colonyMembers", arr)
                        }
                        put("isMutualComplete", true)
                    }
                    TcpFrameCodec.writeFrame(output, ack)

                    log("[Çift Taraflı Eşleşme] ✅ '$senderName' ($remoteIp) doğrudan kabul ile eşleşti!")
                    introduceNewMemberToColony(peer)
                    onStateUpdated?.invoke()
                }

                "COLONY_INTRODUCE" -> {
                    val nm = header.optJSONObject("newMember")
                    if (nm != null) {
                        val nmId = nm.optString("deviceId", "")
                        if (nmId.isNotEmpty() && nmId != localDeviceId) {
                            val nmName = nm.optString("deviceName", "Cihaz")
                            val nmPlatform = nm.optString("platform", "windows")
                            val nmIp = nm.optString("ipAddress", remoteIp)
                            val nmUdp = nm.optInt("udpInputPort", ProtocolConstants.FAST_INPUT_UDP_PORT)
                            val nmTcp = nm.optInt("tcpControlPort", ProtocolConstants.DATA_CONTROL_TCP_PORT)
                            val colId = header.optString("colonyId", "").takeIf { it.isNotEmpty() }

                            var peer = discoveredPeers.find { it.deviceId == nmId || it.ipAddress == nmIp }
                            if (peer == null) {
                                peer = DiscoveredPcPeer(
                                    deviceId = nmId,
                                    deviceName = nmName,
                                    platform = nmPlatform,
                                    ipAddress = nmIp,
                                    udpInputPort = nmUdp,
                                    tcpControlPort = nmTcp,
                                    myEnteredPinVerifiedByRemote = true,
                                    remoteEnteredMyPinVerified = true,
                                    colonyId = colId
                                )
                                discoveredPeers.add(0, peer)
                            } else {
                                peer.deviceName = nmName
                                peer.platform = nmPlatform
                                peer.ipAddress = nmIp
                                peer.udpInputPort = nmUdp
                                peer.tcpControlPort = nmTcp
                                peer.myEnteredPinVerifiedByRemote = true
                                peer.remoteEnteredMyPinVerified = true
                                peer.colonyId = colId
                            }
                            log("[Koloni Eşleşmesi] 🪐 '$senderName' yeni koloni üyesi '$nmName' ($nmIp) tanıttı ve otomatik bağlandı!")
                            onStateUpdated?.invoke()
                        }
                    }
                }

                "COLONY_MEMBERS_SYNC" -> {
                    val colId = header.optString("colonyId", "").takeIf { it.isNotEmpty() }
                    val membersArray = header.optJSONArray("colonyMembers")
                    if (membersArray != null) {
                        for (i in 0 until membersArray.length()) {
                            val mo = membersArray.getJSONObject(i)
                            val mId = mo.optString("deviceId", "")
                            if (mId.isEmpty() || mId == localDeviceId) continue
                            val mName = mo.optString("deviceName", "Cihaz")
                            val mPlatform = mo.optString("platform", "windows")
                            val mIp = mo.optString("ipAddress", "")
                            val mUdp = mo.optInt("udpInputPort", ProtocolConstants.FAST_INPUT_UDP_PORT)
                            val mTcp = mo.optInt("tcpControlPort", ProtocolConstants.DATA_CONTROL_TCP_PORT)

                            var peer = discoveredPeers.find { it.deviceId == mId || (mIp.isNotEmpty() && it.ipAddress == mIp) }
                            if (peer == null) {
                                peer = DiscoveredPcPeer(
                                    deviceId = mId,
                                    deviceName = mName,
                                    platform = mPlatform,
                                    ipAddress = mIp,
                                    udpInputPort = mUdp,
                                    tcpControlPort = mTcp,
                                    myEnteredPinVerifiedByRemote = true,
                                    remoteEnteredMyPinVerified = true,
                                    colonyId = colId
                                )
                                discoveredPeers.add(0, peer)
                            } else {
                                peer.deviceName = mName
                                peer.platform = mPlatform
                                if (mIp.isNotEmpty()) peer.ipAddress = mIp
                                peer.udpInputPort = mUdp
                                peer.tcpControlPort = mTcp
                                peer.myEnteredPinVerifiedByRemote = true
                                peer.remoteEnteredMyPinVerified = true
                                peer.colonyId = colId
                            }
                        }
                        log("[Koloni Eşleşmesi] 🪐 '$senderName' üzerinden ${membersArray.length()} cihazlık koloni ağına dahil olundu!")
                        onStateUpdated?.invoke()
                    }
                }

                "EDGE_HANDOFF" -> {
                    val edge = header.optInt("returnEdge", ProtocolConstants.EDGE_NONE.toInt()).toByte()
                    val normPos = header.optDouble("normalizedPosition", 0.5).toFloat()
                    CursorAccessibilityService.instance?.onEdgeHandOffEnter(edge, normPos)
                    log("[Kenar Geçişi TCP] İmleç Android ekranına TCP üzerinden girdi (Kenar: $edge, %${(normPos * 100).toInt()}).")
                }

                "TRUSTED_RECONNECT" -> {
                    val udpPort = header.optInt("senderUdpPort", ProtocolConstants.FAST_INPUT_UDP_PORT)
                    val tcpPort = header.optInt("senderTcpPort", ProtocolConstants.DATA_CONTROL_TCP_PORT)
                    val platform = header.optString("senderPlatform", "windows")
                    val token = header.optString("trustToken", "")

                    val peer = discoveredPeers.find { it.deviceId == senderId || it.ipAddress == remoteIp }
                        ?: DiscoveredPcPeer(
                            deviceId = if (senderId.isNotEmpty()) senderId else "peer-$remoteIp",
                            deviceName = senderName,
                            platform = platform,
                            ipAddress = remoteIp,
                            udpInputPort = udpPort,
                            tcpControlPort = tcpPort
                        ).also { discoveredPeers.add(0, it) }

                    val savedToken = getTrustTokenForDevice(peer.deviceId)
                    if (savedToken != null && savedToken == token) {
                        peer.myEnteredPinVerifiedByRemote = true
                        peer.remoteEnteredMyPinVerified = true
                        activePcAddress = sock.inetAddress
                        activePcUdpPort = udpPort
                        activePcTcpPort = tcpPort

                        val ack = JSONObject().apply {
                            put("type", "TRUSTED_RECONNECT_ACK")
                            put("senderId", localDeviceId)
                            put("senderName", localDeviceName)
                            put("trustToken", token)
                            put("isMutualComplete", true)
                        }
                        TcpFrameCodec.writeFrame(output, ack)
                        log("[Otomatik Bağlantı] ⭐ Güvenilir cihaz '$senderName' ($remoteIp) PIN'siz otomatik bağlandı!")
                        val initReturnEdge = header.optInt("returnEdge", ProtocolConstants.EDGE_NONE.toInt()).toByte()
                        if (initReturnEdge != ProtocolConstants.EDGE_NONE) {
                            CursorAccessibilityService.instance?.activeEntranceEdge = initReturnEdge
                        }
                    } else {
                        val rej = JSONObject().apply {
                            put("type", "PAIR_REJECT")
                            put("senderId", localDeviceId)
                            put("senderName", localDeviceName)
                        }
                        TcpFrameCodec.writeFrame(output, rej)
                        log("[Güvenlik] ⚠️ '$senderName' güvenilirlik belirteci doğrulanamadı.")
                    }
                    onStateUpdated?.invoke()
                }

                "DISCONNECT" -> {
                    val peer = discoveredPeers.find { it.deviceId == senderId || it.ipAddress == remoteIp }
                    if (peer != null) {
                        suppressedAutoConnectDeviceIds.add(peer.deviceId)
                        peer.myEnteredPinVerifiedByRemote = false
                        peer.remoteEnteredMyPinVerified = false
                    } else if (senderId.isNotEmpty()) {
                        suppressedAutoConnectDeviceIds.add(senderId)
                    }
                    if (activePcAddress?.hostAddress == remoteIp) {
                        activePcAddress = null
                    }
                    CursorAccessibilityService.instance?.deactivateCursor()
                    log("[Bağlantı] 🔌 '$senderName' ($remoteIp) bağlantıyı sonlandırdı.")
                    onStateUpdated?.invoke()
                }

                "EDGE_CONFIG" -> {
                    val returnEdge = header.optInt("returnEdge", ProtocolConstants.EDGE_LEFT.toInt()).toByte()
                    if (returnEdge != ProtocolConstants.EDGE_NONE) {
                        CursorAccessibilityService.instance?.activeEntranceEdge = returnEdge
                        val edgeName = when (returnEdge) {
                            ProtocolConstants.EDGE_LEFT -> "Sol Kenar"
                            ProtocolConstants.EDGE_RIGHT -> "Sağ Kenar"
                            ProtocolConstants.EDGE_TOP -> "Üst Kenar"
                            ProtocolConstants.EDGE_BOTTOM -> "Alt Kenar"
                            else -> "Kenar $returnEdge"
                        }
                        log("[Ekran Konfigürasyonu] Ortak geçiş kenarı ayarlandı: $edgeName.")
                    }
                    val ack = JSONObject().apply {
                        put("type", "EDGE_CONFIG_ACK")
                        put("senderId", localDeviceId)
                    }
                    TcpFrameCodec.writeFrame(output, ack)
                    onStateUpdated?.invoke()
                }

                "CLIPBOARD_TEXT" -> {
                    val text = header.optString("text", "")
                    if (text.isNotEmpty()) {
                        val hash = hashText(text)
                        lastReceivedClipboardHash = hash
                        CursorAccessibilityService.instance?.notifyRemoteClipboardReceived(hash)
                        mainHandler.post {
                            try {
                                val cm = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                                cm.setPrimaryClip(ClipData.newPlainText("ConnectMe", text))
                            } catch (_: Exception) {}
                        }
                        log("[Evrensel Pano] $senderName cihazından metin kopyalandı (${text.length} krk).")
                    }
                    Unit
                }

                "SHELF_FILE" -> {
                    val rawName = header.optString("fileName", "shared_item.bin")
                    val safeName = File(rawName).name
                    val dir = File(getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS), "ConnectMe-Shelf")
                    if (!dir.exists()) dir.mkdirs()
                    val outFile = File(dir, safeName)

                    FileOutputStream(outFile).use { fos ->
                        val buf = ByteArray(64 * 1024)
                        var remaining = binLen
                        while (remaining > 0) {
                            val toRead = minOf(buf.size.toLong(), remaining).toInt()
                            val r = input.read(buf, 0, toRead)
                            if (r <= 0) break
                            fos.write(buf, 0, r)
                            remaining -= r
                        }
                    }

                    shelfItems.add(
                        0,
                        AndroidShelfItem(
                            fileName = safeName,
                            filePath = outFile.absolutePath,
                            fileSizeBytes = binLen,
                            senderName = senderName,
                            isOutgoing = false
                        )
                    )
                    log("[Drop Shelf] $senderName -> '$safeName' Android ortak cebine indi!")
                }

                else -> {
                    // Diğer veya tanımlanmamış paketler
                }
            }
            Unit
        }
    }

    fun getTrustTokenForDevice(deviceId: String): String? {
        val prefs = getSharedPreferences("connect_me_trust", Context.MODE_PRIVATE)
        return prefs.getString("token_$deviceId", null)
    }

    fun saveTrustTokenForDevice(deviceId: String, token: String) {
        val prefs = getSharedPreferences("connect_me_trust", Context.MODE_PRIVATE)
        prefs.edit().putString("token_$deviceId", token).apply()
    }

    fun revokeTrustForDevice(deviceId: String) {
        val prefs = getSharedPreferences("connect_me_trust", Context.MODE_PRIVATE)
        prefs.edit().remove("token_$deviceId").apply()
        val peer = discoveredPeers.find { it.deviceId == deviceId }
        if (peer != null) {
            disconnectPeer(peer)
        } else {
            suppressedAutoConnectDeviceIds.add(deviceId)
        }
        log("[Güvenlik] 🗑️ Cihaz ($deviceId) güvenilenler listesinden kaldırıldı.")
        onStateUpdated?.invoke()
    }

    fun triggerTrustedReconnect(peer: DiscoveredPcPeer, token: String) {
        if (peer.isMutuallyPaired) return
        if (suppressedAutoConnectDeviceIds.contains(peer.deviceId)) return
        scope.launch {
            try {
                Socket().use { socket ->
                    socket.connect(InetSocketAddress(peer.ipAddress, peer.tcpControlPort), 4000)
                    val header = JSONObject().apply {
                        put("type", "TRUSTED_RECONNECT")
                        put("senderId", localDeviceId)
                        put("senderName", localDeviceName)
                        put("senderPlatform", "android")
                        put("senderUdpPort", ProtocolConstants.FAST_INPUT_UDP_PORT)
                        put("senderTcpPort", ProtocolConstants.DATA_CONTROL_TCP_PORT)
                        put("trustToken", token)
                    }
                    TcpFrameCodec.writeFrame(socket.getOutputStream(), header)
                    val (resp, _) = TcpFrameCodec.readHeader(socket.getInputStream()) ?: return@launch
                    if (resp.optString("type") == "TRUSTED_RECONNECT_ACK" && resp.optBoolean("isMutualComplete", false)) {
                        peer.myEnteredPinVerifiedByRemote = true
                        peer.remoteEnteredMyPinVerified = true
                        activePcAddress = InetAddress.getByName(peer.ipAddress)
                        activePcUdpPort = peer.udpInputPort
                        activePcTcpPort = peer.tcpControlPort
                        log("[Otomatik Bağlantı] ⭐ '${peer.deviceName}' ile güvenli otomatik bağlantı sağlandı!")
                        onStateUpdated?.invoke()
                    }
                }
            } catch (_: Exception) {}
        }
    }

    fun getLocalIpv4Address(): String {
        try {
            val interfaces = NetworkInterface.getNetworkInterfaces()
            while (interfaces.hasMoreElements()) {
                val ni = interfaces.nextElement()
                if (!ni.isUp || ni.isLoopback) continue
                val addrs = ni.inetAddresses
                while (addrs.hasMoreElements()) {
                    val addr = addrs.nextElement()
                    if (addr is Inet4Address && !addr.isLoopbackAddress) {
                        return addr.hostAddress ?: "0.0.0.0"
                    }
                }
            }
        } catch (_: Exception) {
        }
        return "0.0.0.0"
    }
}
