package com.korgangames.connectme.service

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.content.ClipboardManager
import android.content.Context
import java.security.MessageDigest
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Path
import android.graphics.PixelFormat
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.util.DisplayMetrics
import android.view.Gravity
import android.view.View
import android.view.WindowManager
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo
import com.korgangames.connectme.network.ConnectMeService
import com.korgangames.connectme.protocol.KeyEventPacket
import com.korgangames.connectme.protocol.MouseButtonPacket
import com.korgangames.connectme.protocol.MouseMovePacket
import com.korgangames.connectme.protocol.MouseScrollPacket
import com.korgangames.connectme.protocol.ProtocolConstants
import kotlin.math.hypot
import kotlin.math.roundToInt

/**
 * 120Hz Hardware-Accelerated Overlay Cursor, Gesture Injector, and
 * Physical Keyboard Bridge for Connect Me on Android.
 */
class CursorAccessibilityService : AccessibilityService() {

    companion object {
        @Volatile
        var instance: CursorAccessibilityService? = null
            private set
    }

    private val mainHandler = Handler(Looper.getMainLooper())
    private var windowManager: WindowManager? = null
    private var cursorView: CursorPointerView? = null
    private var overlayParams: WindowManager.LayoutParams? = null
    private var isOverlayAttached = false

    var screenWidth = 1080
        private set
    var screenHeight = 2400
        private set

    @Volatile
    var cursorX = 540f
        private set

    @Volatile
    var cursorY = 1200f
        private set

    @Volatile
    var isCursorActiveOnAndroid = false

    fun deactivateCursor() {
        isCursorActiveOnAndroid = false
        mainHandler.post {
            cursorView?.visibility = View.GONE
        }
    }

    // Which edge of the Android screen faces the Windows PC (default: Left edge faces Right edge of Windows)
    @Volatile
    var activeEntranceEdge: Byte = ProtocolConstants.EDGE_LEFT

    private var isLeftButtonDown = false
    private var downX = 0f
    private var downY = 0f
    private var downTimestamp = 0L
    private var returnEdgePushAccum = 0f

    private var clipboardManager: ClipboardManager? = null
    private var clipboardListener: ClipboardManager.OnPrimaryClipChangedListener? = null

    @Volatile
    private var lastReceivedClipboardHash: String? = null

    @Volatile
    private var lastSentClipboardHash: String? = null

    override fun onServiceConnected() {
        super.onServiceConnected()
        instance = this
        windowManager = getSystemService(Context.WINDOW_SERVICE) as WindowManager
        refreshScreenMetrics()
        ensureOverlayCreated()

        // Seamless universal clipboard listener
        clipboardManager = getSystemService(Context.CLIPBOARD_SERVICE) as? ClipboardManager
        clipboardListener = ClipboardManager.OnPrimaryClipChangedListener {
            handleLocalClipboardChanged()
        }
        clipboardManager?.addPrimaryClipChangedListener(clipboardListener)
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        // Focus tracking handled dynamically via findFocus(FOCUS_INPUT)
    }

    override fun onInterrupt() {}

    override fun onDestroy() {
        clipboardListener?.let { clipboardManager?.removePrimaryClipChangedListener(it) }
        removeOverlay()
        if (instance === this) {
            instance = null
        }
        super.onDestroy()
    }

    fun notifyRemoteClipboardReceived(hash: String) {
        lastReceivedClipboardHash = hash
    }

    private fun handleLocalClipboardChanged() {
        try {
            val cm = clipboardManager ?: return
            val clip = cm.primaryClip ?: return
            if (clip.itemCount == 0) return
            val text = clip.getItemAt(0)?.coerceToText(this)?.toString() ?: return
            if (text.isBlank()) return

            val hash = hashText(text)
            if (hash == lastReceivedClipboardHash || hash == lastSentClipboardHash) {
                return
            }

            lastSentClipboardHash = hash
            ConnectMeService.instance?.sendClipboardTextToPc(text)
            ConnectMeService.instance?.log("[Evrensel Pano] Android'den kopyalanan metin (${text.length} krk) bilgisayara otomatik aktarıldı.")
        } catch (_: Exception) {
        }
    }

    private fun hashText(str: String): String {
        return try {
            val md = MessageDigest.getInstance("SHA-256")
            val digest = md.digest(str.toByteArray(Charsets.UTF_8))
            digest.joinToString("") { "%02x".format(it) }
        } catch (_: Exception) {
            str.hashCode().toString()
        }
    }

    fun refreshScreenMetrics() {
        val wm = windowManager ?: return
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            val bounds = wm.maximumWindowMetrics.bounds
            screenWidth = bounds.width().coerceAtLeast(480)
            screenHeight = bounds.height().coerceAtLeast(800)
        } else {
            val dm = DisplayMetrics()
            @Suppress("DEPRECATION")
            wm.defaultDisplay.getRealMetrics(dm)
            screenWidth = dm.widthPixels.coerceAtLeast(480)
            screenHeight = dm.heightPixels.coerceAtLeast(800)
        }
    }

    private fun ensureOverlayCreated() {
        mainHandler.post {
            if (isOverlayAttached) return@post
            val wm = windowManager ?: return@post

            val overlayType = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                // TYPE_ACCESSIBILITY_OVERLAY is natively permitted for AccessibilityService
                // and does NOT require SYSTEM_ALERT_WINDOW permission!
                WindowManager.LayoutParams.TYPE_ACCESSIBILITY_OVERLAY
            } else {
                @Suppress("DEPRECATION")
                WindowManager.LayoutParams.TYPE_SYSTEM_ALERT
            }

            val view = CursorPointerView(this)
            val params = WindowManager.LayoutParams(
                64,
                64,
                overlayType,
                WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                        WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE or
                        WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or
                        WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS or
                        WindowManager.LayoutParams.FLAG_HARDWARE_ACCELERATED,
                PixelFormat.TRANSLUCENT
            ).apply {
                gravity = Gravity.TOP or Gravity.START
                x = cursorX.roundToInt()
                y = cursorY.roundToInt()
            }

            try {
                wm.addView(view, params)
                cursorView = view
                overlayParams = params
                isOverlayAttached = true
                view.visibility = if (isCursorActiveOnAndroid) View.VISIBLE else View.GONE
            } catch (e: Exception) {
                // Fallback to TYPE_APPLICATION_OVERLAY if OEM ROM rejects TYPE_ACCESSIBILITY_OVERLAY
                if (Settings.canDrawOverlays(this)) {
                    try {
                        params.type = WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY
                        wm.addView(view, params)
                        cursorView = view
                        overlayParams = params
                        isOverlayAttached = true
                        view.visibility = if (isCursorActiveOnAndroid) View.VISIBLE else View.GONE
                    } catch (_: Exception) {
                    }
                }
            }
        }
    }

    private fun removeOverlay() {
        mainHandler.post {
            if (isOverlayAttached && cursorView != null) {
                try {
                    windowManager?.removeView(cursorView)
                } catch (_: Exception) {
                }
            }
            isOverlayAttached = false
            cursorView = null
        }
    }

    /**
     * Called when the cursor crosses from Windows onto this Android device.
     */
    fun onEdgeHandOffEnter(entranceEdge: Byte, normalizedPosition: Float) {
        refreshScreenMetrics()
        activeEntranceEdge = if (entranceEdge != ProtocolConstants.EDGE_NONE) {
            entranceEdge
        } else {
            ProtocolConstants.EDGE_LEFT
        }

        val t = normalizedPosition.coerceIn(0.03f, 0.97f)
        when (activeEntranceEdge) {
            ProtocolConstants.EDGE_LEFT -> {
                cursorX = 10f
                cursorY = t * screenHeight
            }
            ProtocolConstants.EDGE_RIGHT -> {
                cursorX = (screenWidth - 10).toFloat()
                cursorY = t * screenHeight
            }
            ProtocolConstants.EDGE_TOP -> {
                cursorX = t * screenWidth
                cursorY = 10f
            }
            ProtocolConstants.EDGE_BOTTOM -> {
                cursorX = t * screenWidth
                cursorY = (screenHeight - 10).toFloat()
            }
        }

        returnEdgePushAccum = 0f
        isCursorActiveOnAndroid = true

        mainHandler.post {
            ensureOverlayCreated()
            cursorView?.visibility = View.VISIBLE
            cursorView?.triggerPulse()
            updateCursorLayoutPosition()
        }
    }

    /**
     * Processes high-frequency relative mouse movement from Windows.
     */
    fun onRemoteMouseMove(packet: MouseMovePacket) {
        if (!isCursorActiveOnAndroid) {
            isCursorActiveOnAndroid = true
        }

        val nextX = cursorX + packet.deltaX
        val nextY = cursorY + packet.deltaY

        // Detect if cursor is hitting any outer boundary and pushing outward towards PC
        var pushingBorder: Byte = ProtocolConstants.EDGE_NONE
        var outwardDelta = 0f
        var returnNormPos = 0.5f
        var oppositeWindowsEdge: Byte = ProtocolConstants.EDGE_NONE

        if (packet.deltaX < 0 && (cursorX <= 6f || nextX < 0f)) {
            pushingBorder = ProtocolConstants.EDGE_LEFT
            outwardDelta = -packet.deltaX.toFloat()
            returnNormPos = (cursorY / screenHeight).coerceIn(0f, 1f)
            oppositeWindowsEdge = ProtocolConstants.EDGE_RIGHT
        } else if (packet.deltaX > 0 && (cursorX >= (screenWidth - 6f) || nextX > screenWidth)) {
            pushingBorder = ProtocolConstants.EDGE_RIGHT
            outwardDelta = packet.deltaX.toFloat()
            returnNormPos = (cursorY / screenHeight).coerceIn(0f, 1f)
            oppositeWindowsEdge = ProtocolConstants.EDGE_LEFT
        } else if (packet.deltaY < 0 && (cursorY <= 6f || nextY < 0f)) {
            pushingBorder = ProtocolConstants.EDGE_TOP
            outwardDelta = -packet.deltaY.toFloat()
            returnNormPos = (cursorX / screenWidth).coerceIn(0f, 1f)
            oppositeWindowsEdge = ProtocolConstants.EDGE_BOTTOM
        } else if (packet.deltaY > 0 && (cursorY >= (screenHeight - 6f) || nextY > screenHeight)) {
            pushingBorder = ProtocolConstants.EDGE_BOTTOM
            outwardDelta = packet.deltaY.toFloat()
            returnNormPos = (cursorX / screenWidth).coerceIn(0f, 1f)
            oppositeWindowsEdge = ProtocolConstants.EDGE_TOP
        }

        if (pushingBorder != ProtocolConstants.EDGE_NONE && outwardDelta > 0f && !isLeftButtonDown) {
            // Buttery-smooth return: only 3px needed when pushing against the edge facing the PC!
            // If pushing against an unexpected edge, allow return with 14px so cursor is NEVER trapped.
            val requiredResistance = if (pushingBorder == activeEntranceEdge) 3f else 14f
            returnEdgePushAccum += outwardDelta
            if (returnEdgePushAccum >= requiredResistance) {
                returnEdgePushAccum = 0f
                isCursorActiveOnAndroid = false
                mainHandler.post {
                    cursorView?.visibility = View.GONE
                }
                ConnectMeService.instance?.sendEdgeHandOffBackToPeer(oppositeWindowsEdge, returnNormPos)
                return
            }
        } else if (cursorX > 25f && cursorX < (screenWidth - 25f) && cursorY > 25f && cursorY < (screenHeight - 25f)) {
            // Only reset resistance when user visibly moves away from the edge back towards screen center
            returnEdgePushAccum = 0f
        }

        cursorX = nextX.coerceIn(0f, (screenWidth - 1).toFloat())
        cursorY = nextY.coerceIn(0f, (screenHeight - 1).toFloat())

        mainHandler.post {
            if (!isOverlayAttached) ensureOverlayCreated()
            cursorView?.visibility = View.VISIBLE
            updateCursorLayoutPosition()
        }
    }

    private fun updateCursorLayoutPosition() {
        val wm = windowManager ?: return
        val view = cursorView ?: return
        val params = overlayParams ?: return
        params.x = cursorX.roundToInt()
        params.y = cursorY.roundToInt()
        try {
            wm.updateViewLayout(view, params)
        } catch (_: Exception) {
        }
    }

    fun onRemoteMouseButton(packet: MouseButtonPacket) {
        when (packet.button) {
            ProtocolConstants.BUTTON_LEFT -> {
                if (packet.isPressed) {
                    isLeftButtonDown = true
                    downX = cursorX
                    downY = cursorY
                    downTimestamp = System.currentTimeMillis()
                    mainHandler.post { cursorView?.setClicking(true) }
                } else if (isLeftButtonDown) {
                    isLeftButtonDown = false
                    val upX = cursorX
                    val upY = cursorY
                    val duration = (System.currentTimeMillis() - downTimestamp).coerceIn(25L, 600L)
                    mainHandler.post {
                        cursorView?.setClicking(false)
                        dispatchTouchOrDrag(downX, downY, upX, upY, duration)
                    }
                }
            }

            ProtocolConstants.BUTTON_RIGHT -> {
                if (packet.isPressed) {
                    mainHandler.post { performGlobalAction(GLOBAL_ACTION_BACK) }
                }
            }

            ProtocolConstants.BUTTON_MIDDLE -> {
                if (packet.isPressed) {
                    mainHandler.post { performGlobalAction(GLOBAL_ACTION_HOME) }
                }
            }
        }
    }

    fun onRemoteMouseScroll(packet: MouseScrollPacket) {
        val sy = packet.scrollY.toInt()
        val sx = packet.scrollX.toInt()
        if (sy == 0 && sx == 0) return

        val startX = cursorX.coerceIn(40f, (screenWidth - 40).toFloat())
        val startY = cursorY.coerceIn(120f, (screenHeight - 120).toFloat())
        // Wheel up (positive) scrolls content up -> finger swipes down
        val swipeDeltaX = (sx * 1.4f).coerceIn(-360f, 360f)
        val swipeDeltaY = (sy * 1.4f).coerceIn(-360f, 360f)
        val endX = (startX + swipeDeltaX).coerceIn(40f, (screenWidth - 40).toFloat())
        val endY = (startY + swipeDeltaY).coerceIn(40f, (screenHeight - 40).toFloat())

        mainHandler.post {
            val path = Path().apply {
                moveTo(startX, startY)
                lineTo(endX, endY)
            }
            val stroke = GestureDescription.StrokeDescription(path, 0, 90)
            val gesture = GestureDescription.Builder().addStroke(stroke).build()
            dispatchGesture(gesture, null, null)
        }
    }

    private fun dispatchTouchOrDrag(x1: Float, y1: Float, x2: Float, y2: Float, durationMs: Long) {
        val dist = hypot(x2 - x1, y2 - y1)
        val path = Path().apply {
            moveTo(x1, y1)
            if (dist > 10f) {
                lineTo(x2, y2)
            }
        }
        val strokeDuration = if (dist > 10f) durationMs.coerceAtLeast(100L) else 40L
        val stroke = GestureDescription.StrokeDescription(path, 0, strokeDuration)
        val gesture = GestureDescription.Builder().addStroke(stroke).build()
        dispatchGesture(gesture, null, null)
    }

    /**
     * Direct physical keyboard bridge: inserts characters (including Turkish Unicode)
     * and handles Backspace, Enter, Escape, and Clipboard shortcuts directly on focused input fields.
     */
    fun onRemoteKeyEvent(packet: KeyEventPacket) {
        if (!packet.isPressed) return

        mainHandler.post {
            val isShift = (packet.modifiers and 0x01) != 0
            val isCtrl = (packet.modifiers and 0x02) != 0
            val isAlt = (packet.modifiers and 0x04) != 0
            val isMeta = (packet.modifiers and 0x08) != 0 // Win key

            // 1. Windows Touchpad & Navigation Gestures Routed to Android:
            
            // 3-Finger Swipe Up OR Alt+Tab / Win+Tab -> Recent Apps (Overview / Task View)
            if (((isAlt || isMeta) && packet.virtualKey == 0x09) || (isMeta && packet.virtualKey == 0x09)) {
                performGlobalAction(GLOBAL_ACTION_RECENTS)
                return@post
            }

            // 3-Finger Swipe Down OR Win+D / Win+H -> Go to Android Home Screen
            if (isMeta && (packet.virtualKey == 0x44 || packet.virtualKey == 0x48)) { // D or H
                performGlobalAction(GLOBAL_ACTION_HOME)
                return@post
            }

            // 4-Finger Swipe Left / Right (Ctrl+Win+Left / Ctrl+Win+Right) -> Virtual Desktop Switch (Home page swipe on Android)
            if (isMeta && isCtrl && (packet.virtualKey == 0x25 || packet.virtualKey == 0x27)) {
                val cy = screenHeight * 0.5f
                if (packet.virtualKey == 0x25) { // Left arrow
                    dispatchTouchOrDrag(screenWidth * 0.15f, cy, screenWidth * 0.85f, cy, 200L)
                } else { // Right arrow
                    dispatchTouchOrDrag(screenWidth * 0.85f, cy, screenWidth * 0.15f, cy, 200L)
                }
                return@post
            }

            // Win+A or Win+N -> Quick Settings / Notifications
            if (isMeta && (packet.virtualKey == 0x41 || packet.virtualKey == 0x4E)) {
                performGlobalAction(GLOBAL_ACTION_NOTIFICATIONS)
                return@post
            }

            // Win key alone (VK_LWIN / VK_RWIN) -> Android Home Screen
            if (packet.virtualKey == 0x5B || packet.virtualKey == 0x5C) {
                performGlobalAction(GLOBAL_ACTION_HOME)
                return@post
            }

            // Escape or Browser Back -> Android Back
            if (packet.virtualKey == 0x1B || packet.virtualKey == 0xA6) {
                performGlobalAction(GLOBAL_ACTION_BACK)
                return@post
            }

            val focusedNode = findFocus(AccessibilityNodeInfo.FOCUS_INPUT) ?: return@post
            try {
                if (isCtrl) {
                    when (packet.virtualKey) {
                        0x43 -> { // Ctrl+C
                            focusedNode.performAction(AccessibilityNodeInfo.ACTION_COPY)
                            return@post
                        }
                        0x56 -> { // Ctrl+V
                            focusedNode.performAction(AccessibilityNodeInfo.ACTION_PASTE)
                            return@post
                        }
                        0x58 -> { // Ctrl+X
                            focusedNode.performAction(AccessibilityNodeInfo.ACTION_CUT)
                            return@post
                        }
                        0x41 -> { // Ctrl+A
                            val current = focusedNode.text?.toString() ?: ""
                            val args = Bundle().apply {
                                putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_START_INT, 0)
                                putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_END_INT, current.length)
                            }
                            focusedNode.performAction(AccessibilityNodeInfo.ACTION_SET_SELECTION, args)
                            return@post
                        }
                    }
                }

                val currentText = focusedNode.text?.toString() ?: ""
                when (packet.virtualKey) {
                    0x08 -> { // Backspace
                        if (currentText.isNotEmpty()) {
                            setNodeText(focusedNode, currentText.dropLast(1))
                        }
                    }
                    0x0D -> { // Enter
                        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                            val imeEntered = focusedNode.performAction(
                                AccessibilityNodeInfo.AccessibilityAction.ACTION_IME_ENTER.id
                            )
                            if (!imeEntered) {
                                setNodeText(focusedNode, currentText + "\n")
                            }
                        } else {
                            setNodeText(focusedNode, currentText + "\n")
                        }
                    }
                    else -> {
                        val ch = packet.unicodeChar
                        if (ch.code >= 32) {
                            setNodeText(focusedNode, currentText + ch)
                        }
                    }
                }
            } finally {
                @Suppress("DEPRECATION")
                focusedNode.recycle()
            }
        }
    }

    private fun setNodeText(node: AccessibilityNodeInfo, newText: String) {
        val args = Bundle().apply {
            putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, newText)
        }
        node.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, args)
    }

    /**
     * Custom vector pointer view with click feedback and edge entry glow.
     */
    private class CursorPointerView(context: Context) : View(context) {
        private val fillPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.FILL
            color = Color.WHITE
        }
        private val strokePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.STROKE
            strokeWidth = 3.2f
            color = Color.parseColor("#0F172A")
        }
        private val glowPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.STROKE
            strokeWidth = 4f
            color = Color.parseColor("#38BDF8")
        }
        private val arrowPath = Path().apply {
            moveTo(2f, 2f)
            lineTo(2f, 38f)
            lineTo(12f, 29f)
            lineTo(19f, 44f)
            lineTo(25f, 41f)
            lineTo(18f, 26f)
            lineTo(30f, 26f)
            close()
        }

        private var isClicking = false
        private var pulseRadius = 0f

        fun setClicking(clicking: Boolean) {
            isClicking = clicking
            fillPaint.color = if (clicking) Color.parseColor("#38BDF8") else Color.WHITE
            invalidate()
        }

        fun triggerPulse() {
            pulseRadius = 26f
            invalidate()
        }

        override fun onDraw(canvas: Canvas) {
            super.onDraw(canvas)
            if (pulseRadius > 0f) {
                canvas.drawCircle(14f, 14f, pulseRadius, glowPaint)
                pulseRadius = (pulseRadius - 4f).coerceAtLeast(0f)
                if (pulseRadius > 0f) postInvalidateDelayed(16)
            }
            canvas.drawPath(arrowPath, fillPaint)
            canvas.drawPath(arrowPath, strokePaint)
        }
    }
}
