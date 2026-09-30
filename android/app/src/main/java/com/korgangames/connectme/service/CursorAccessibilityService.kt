package com.korgangames.connectme.service

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.content.Context
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
        private set

    // Which edge of the Android screen faces the Windows PC (default: Left edge faces Right edge of Windows)
    @Volatile
    var activeEntranceEdge: Byte = ProtocolConstants.EDGE_LEFT

    private var isLeftButtonDown = false
    private var downX = 0f
    private var downY = 0f
    private var downTimestamp = 0L
    private var returnEdgePushAccum = 0f

    override fun onServiceConnected() {
        super.onServiceConnected()
        instance = this
        windowManager = getSystemService(Context.WINDOW_SERVICE) as WindowManager
        refreshScreenMetrics()
        ensureOverlayCreated()
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        // Focus tracking handled dynamically via findFocus(FOCUS_INPUT)
    }

    override fun onInterrupt() {}

    override fun onDestroy() {
        removeOverlay()
        if (instance === this) {
            instance = null
        }
        super.onDestroy()
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
            if (isOverlayAttached || !Settings.canDrawOverlays(this)) return@post
            val wm = windowManager ?: return@post

            val view = CursorPointerView(this)
            val params = WindowManager.LayoutParams(
                64,
                64,
                WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
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
            } catch (_: Exception) {
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

        // Check if pushing back across the entrance edge to return to Windows
        var pushedOutward = 0f
        var returnNormPos = 0.5f
        var oppositeWindowsEdge: Byte = ProtocolConstants.EDGE_NONE

        when (activeEntranceEdge) {
            ProtocolConstants.EDGE_LEFT -> if (nextX < 0 && packet.deltaX < 0) {
                pushedOutward = -packet.deltaX.toFloat()
                returnNormPos = (cursorY / screenHeight).coerceIn(0f, 1f)
                oppositeWindowsEdge = ProtocolConstants.EDGE_RIGHT
            }
            ProtocolConstants.EDGE_RIGHT -> if (nextX > screenWidth && packet.deltaX > 0) {
                pushedOutward = packet.deltaX.toFloat()
                returnNormPos = (cursorY / screenHeight).coerceIn(0f, 1f)
                oppositeWindowsEdge = ProtocolConstants.EDGE_LEFT
            }
            ProtocolConstants.EDGE_TOP -> if (nextY < 0 && packet.deltaY < 0) {
                pushedOutward = -packet.deltaY.toFloat()
                returnNormPos = (cursorX / screenWidth).coerceIn(0f, 1f)
                oppositeWindowsEdge = ProtocolConstants.EDGE_BOTTOM
            }
            ProtocolConstants.EDGE_BOTTOM -> if (nextY > screenHeight && packet.deltaY > 0) {
                pushedOutward = packet.deltaY.toFloat()
                returnNormPos = (cursorX / screenWidth).coerceIn(0f, 1f)
                oppositeWindowsEdge = ProtocolConstants.EDGE_TOP
            }
        }

        if (pushedOutward > 0f && !isLeftButtonDown) {
            returnEdgePushAccum += pushedOutward
            if (returnEdgePushAccum >= 22f) {
                // Return control back to Windows!
                returnEdgePushAccum = 0f
                isCursorActiveOnAndroid = false
                mainHandler.post {
                    cursorView?.visibility = View.GONE
                }
                ConnectMeService.instance?.sendEdgeHandOffBackToPeer(oppositeWindowsEdge, returnNormPos)
                return
            }
        } else {
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
        if (sy == 0) return

        val startX = cursorX.coerceIn(40f, (screenWidth - 40).toFloat())
        val startY = cursorY.coerceIn(120f, (screenHeight - 120).toFloat())
        // Wheel up (positive) scrolls content up -> finger swipes down
        val swipeDeltaY = (sy * 1.4f).coerceIn(-360f, 360f)
        val endY = (startY + swipeDeltaY).coerceIn(40f, (screenHeight - 40).toFloat())

        mainHandler.post {
            val path = Path().apply {
                moveTo(startX, startY)
                lineTo(startX, endY)
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
            val isCtrl = (packet.modifiers and 0x02) != 0

            // Escape -> Android Back
            if (packet.virtualKey == 0x1B) {
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
