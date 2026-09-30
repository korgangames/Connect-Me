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
import java.util.UUID
import java.util.concurrent.CopyOnWriteArrayList

data class DiscoveredPcPeer(
    val deviceId: String,
    val deviceName: String,
    val platform: String,
    val ipAddress: String,
    val udpInputPort: Int,
    val tcpControlPort: Int,
    var lastSeenMs: Long = System.currentTimeMillis()
)

data class AndroidShelfItem(
    val fileName: String,
    val filePath: String,
    val fileSizeBytes: Long,
    val senderName: String,
    val isOutgoing: Boolean
)

class ConnectMeService : Service() {

    companion object {
        private const val CHANNEL_ID = "connect_me_service_channel"
        private const val NOTIFICATION_ID = 4285

        @Volatile
        var instance: ConnectMeService? = null
            private set

        val discoveredPeers = CopyOnWriteArrayList<DiscoveredPcPeer>()
        val shelfItems = CopyOnWriteArrayList<AndroidShelfItem>()
        val telemetryLogs = CopyOnWriteArrayList<String>()

        var onStateUpdated: (() -> Unit)? = null

        fun log(msg: String) {
            val line = "${java.text.SimpleDateFormat("HH:mm:ss", java.util.Locale.US).format(java.util.Date())} $msg"
            telemetryLogs.add(0, line)
            while (telemetryLogs.size > 100) {
                telemetryLogs.removeAt(telemetryLogs.size - 1)
            }
            onStateUpdated?.invoke()
        }
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val mainHandler = Handler(Looper.getMainLooper())

    private var discoverySocket: DatagramSocket? = null
    private var inputUdpSocket: DatagramSocket? = null
    private var tcpServerSocket: ServerSocket? = null

    val localDeviceId: String = "android-" + UUID.randomUUID().toString().take(8)
    val localDeviceName: String = "${Build.MANUFACTURER} ${Build.MODEL}"

    @Volatile
    var activePcAddress: InetAddress? = null

    @Volatile
    var activePcUdpPort: Int = ProtocolConstants.FAST_INPUT_UDP_PORT

    @Volatile
    var activePcTcpPort: Int = ProtocolConstants.DATA_CONTROL_TCP_PORT

    override fun onCreate() {
        super.onCreate()
        instance = this
        startForegroundWithNotification()
        startNetworkLoops()
        log("[Servis] Connect Me Android Ağ ve Girdi Motoru başlatıldı.")
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        return START_STICKY
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        scope.cancel()
        try { discoverySocket?.close() } catch (_: Exception) {}
        try { inputUdpSocket?.close() } catch (_: Exception) {}
        try { tcpServerSocket?.close() } catch (_: Exception) {}
        if (instance === this) {
            instance = null
        }
        super.onDestroy()
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

        val notification: Notification = NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("Connect Me Aktif")
            .setContentText("Windows/Linux kenar geçişi, ortak pano ve Drop Shelf hazır")
            .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
            .setContentIntent(pendingIntent)
            .setOngoing(true)
            .build()

        startForeground(NOTIFICATION_ID, notification)
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

        // 3. TCP Clipboard & Drop Shelf Server (42851)
        scope.launch {
            try {
                val server = ServerSocket().apply {
                    reuseAddress = true
                    bind(InetSocketAddress(ProtocolConstants.DATA_CONTROL_TCP_PORT))
                }
                tcpServerSocket = server
                log("[Veri Kanalı] TCP 42851 Pano & Drop Shelf sunucusu hazır.")
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

    fun triggerManualConnectToPc(ipAddress: String) {
        scope.launch {
            try {
                val addr = InetAddress.getByName(ipAddress.trim())
                activePcAddress = addr
                activePcUdpPort = ProtocolConstants.FAST_INPUT_UDP_PORT
                activePcTcpPort = ProtocolConstants.DATA_CONTROL_TCP_PORT

                val existing = discoveredPeers.find { it.ipAddress == ipAddress }
                if (existing == null) {
                    discoveredPeers.add(
                        0,
                        DiscoveredPcPeer(
                            deviceId = "pc-$ipAddress",
                            deviceName = "Windows PC ($ipAddress)",
                            platform = "windows",
                            ipAddress = ipAddress,
                            udpInputPort = ProtocolConstants.FAST_INPUT_UDP_PORT,
                            tcpControlPort = ProtocolConstants.DATA_CONTROL_TCP_PORT
                        )
                    )
                }

                discoverySocket?.let { sendDiscoveryBeacon(it, addr) }
                log("[Bağlantı] Windows PC ($ipAddress) hedeflendi ve keşif sinyali gönderildi.")
            } catch (e: Exception) {
                log("[Hata] Geçersiz PC IP adresi: ${e.message}")
            }
        }
    }

    private fun sendDiscoveryBeacon(sock: DatagramSocket, targetAddr: InetAddress) {
        try {
            val cursorSvc = CursorAccessibilityService.instance
            val json = JSONObject().apply {
                put("type", "DISCOVER_BEACON")
                put("deviceId", localDeviceId)
                put("deviceName", localDeviceName)
                put("platform", "android")
                put("udpInputPort", ProtocolConstants.FAST_INPUT_UDP_PORT)
                put("tcpControlPort", ProtocolConstants.DATA_CONTROL_TCP_PORT)
                put("screenWidth", cursorSvc?.screenWidth ?: 1080)
                put("screenHeight", cursorSvc?.screenHeight ?: 2400)
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

                activePcAddress = pkt.address
                activePcUdpPort = udpPort
                activePcTcpPort = tcpPort

                val existingIdx = discoveredPeers.indexOfFirst { it.deviceId == deviceId || it.ipAddress == senderIp }
                val peer = DiscoveredPcPeer(deviceId, deviceName, platform, senderIp, udpPort, tcpPort)
                if (existingIdx >= 0) {
                    discoveredPeers[existingIdx] = peer
                } else {
                    discoveredPeers.add(0, peer)
                    log("[Keşif] Bilgisayar bulundu: $deviceName ($senderIp)")
                    sendDiscoveryBeacon(sock, pkt.address)
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

                activePcAddress = pkt.address
                activePcUdpPort = pkt.port

                val cursorSvc = CursorAccessibilityService.instance
                when (WirePacketCodec.getPacketType(buf)) {
                    ProtocolConstants.PACKET_EDGE_HANDOFF -> {
                        WirePacketCodec.decodeEdgeHandOff(buf, len)?.let { ho ->
                            cursorSvc?.onEdgeHandOffEnter(ho.targetEntranceEdge, ho.normalizedPosition)
                            log("[Kenar Geçişi] İmleç Windows'tan Android ekranına geçti (%${(ho.normalizedPosition * 100).toInt()}).")
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

    /**
     * Sends an EDGE_HANDOFF packet back to the active Windows PC when the cursor
     * leaves the Android screen edge.
     */
    fun sendEdgeHandOffBackToPeer(windowsEntranceEdge: Byte, normalizedPos: Float) {
        scope.launch {
            try {
                val targetAddr = activePcAddress ?: return@launch
                val sock = inputUdpSocket ?: return@launch
                val bytes = WirePacketCodec.encodeEdgeHandOff(windowsEntranceEdge, false, normalizedPos)
                val dp = DatagramPacket(bytes, bytes.size, targetAddr, activePcUdpPort)
                sock.send(dp)
                sock.send(dp)
                log("[Kenar Geçişi] İmleç Android'den Windows ekranına geri döndü.")
            } catch (_: Exception) {
            }
        }
    }

    fun sendClipboardTextToPc(text: String) {
        if (text.isBlank()) return
        scope.launch {
            val targetAddr = activePcAddress ?: discoveredPeers.firstOrNull()?.let { InetAddress.getByName(it.ipAddress) }
            if (targetAddr == null) {
                log("[Uyarı] Pano göndermek için bağlı bilgisayar bulunamadı.")
                return@launch
            }

            try {
                Socket().use { socket ->
                    socket.connect(InetSocketAddress(targetAddr, activePcTcpPort), 5000)
                    val header = JSONObject().apply {
                        put("type", "CLIPBOARD_TEXT")
                        put("senderId", localDeviceId)
                        put("senderName", localDeviceName)
                        put("text", text)
                    }
                    TcpFrameCodec.writeFrame(socket.getOutputStream(), header)
                    log("[Evrensel Pano] Metin (${text.length} krk) Windows panosuna gönderildi.")
                }
            } catch (e: Exception) {
                log("[Pano Hata] Gönderilemedi: ${e.message}")
            }
        }
    }

    fun sendStreamToPcShelf(fileName: String, inputStream: InputStream, fileSize: Long) {
        scope.launch {
            val targetAddr = activePcAddress ?: discoveredPeers.firstOrNull()?.let { InetAddress.getByName(it.ipAddress) }
            if (targetAddr == null) {
                log("[Uyarı] Dosya göndermek için bağlı bilgisayar bulunamadı.")
                return@launch
            }

            try {
                Socket().use { socket ->
                    socket.connect(InetSocketAddress(targetAddr, activePcTcpPort), 8000)
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
                        senderName = "$localDeviceName -> PC",
                        isOutgoing = true
                    )
                )
                log("[Drop Shelf] '$fileName' Windows Ortak Cebine gönderildi!")
            } catch (e: Exception) {
                log("[Drop Shelf Hata] '$fileName' gönderilemedi: ${e.message}")
            }
        }
    }

    private fun handleTcpClient(client: Socket) {
        client.use { sock ->
            val input = sock.getInputStream()
            val (header, binLen) = TcpFrameCodec.readHeader(input) ?: return
            val type = header.optString("type", "")
            val senderName = header.optString("senderName", "Windows PC")

            when (type) {
                "CLIPBOARD_TEXT" -> {
                    val text = header.optString("text", "")
                    if (text.isNotEmpty()) {
                        mainHandler.post {
                            val cm = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                            cm.setPrimaryClip(ClipData.newPlainText("ConnectMe", text))
                        }
                        log("[Evrensel Pano] $senderName cihazından metin kopyalandı (${text.length} krk).")
                    }
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
            }
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
