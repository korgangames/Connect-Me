package com.korgangames.connectme.protocol

import org.json.JSONObject
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.StandardCharsets

object ProtocolConstants {
    const val MAGIC_0: Byte = 0x43 // 'C'
    const val MAGIC_1: Byte = 0x4D // 'M'
    const val VERSION: Byte = 0x01
    const val PROTOCOL_VERSION: Byte = 0x01

    const val DISCOVERY_UDP_PORT = 42849
    const val FAST_INPUT_UDP_PORT = 42850
    const val DATA_CONTROL_TCP_PORT = 42851
    const val AUDIO_STREAM_UDP_PORT = 42852

    const val PACKET_MOUSE_MOVE: Byte = 0x01
    const val PACKET_MOUSE_BUTTON: Byte = 0x02
    const val PACKET_MOUSE_SCROLL: Byte = 0x03
    const val PACKET_KEY_EVENT: Byte = 0x04
    const val PACKET_EDGE_HANDOFF: Byte = 0x05
    const val PACKET_HEARTBEAT_PING: Byte = 0x06
    const val PACKET_HEARTBEAT_PONG: Byte = 0x07
    const val PACKET_AUDIO_CHUNK: Byte = 0x08

    const val EDGE_NONE: Byte = 0
    const val EDGE_LEFT: Byte = 1
    const val EDGE_RIGHT: Byte = 2
    const val EDGE_TOP: Byte = 3
    const val EDGE_BOTTOM: Byte = 4

    const val BUTTON_LEFT: Byte = 1
    const val BUTTON_RIGHT: Byte = 2
    const val BUTTON_MIDDLE: Byte = 3
}

data class MouseMovePacket(val sequence: Int, val deltaX: Short, val deltaY: Short)
data class MouseButtonPacket(val button: Byte, val isPressed: Boolean)
data class MouseScrollPacket(val scrollX: Short, val scrollY: Short)
data class KeyEventPacket(
    val virtualKey: Int,
    val scanCode: Int,
    val isPressed: Boolean,
    val modifiers: Int,
    val unicodeChar: Char
)
data class EdgeHandOffPacket(
    val targetEntranceEdge: Byte,
    val isDraggingShelfItem: Boolean,
    val normalizedPosition: Float
)

object WirePacketCodec {

    fun isValidHeader(buf: ByteArray, length: Int): Boolean {
        return length >= 4 &&
                buf[0] == ProtocolConstants.MAGIC_0 &&
                buf[1] == ProtocolConstants.MAGIC_1 &&
                buf[2] == ProtocolConstants.VERSION
    }

    fun getPacketType(buf: ByteArray): Byte = buf[3]

    fun decodeMouseMove(buf: ByteArray, length: Int): MouseMovePacket? {
        if (length < 10 || !isValidHeader(buf, length)) return null
        val bb = ByteBuffer.wrap(buf, 4, 6).order(ByteOrder.LITTLE_ENDIAN)
        val seq = bb.short.toInt() and 0xFFFF
        val dx = bb.short
        val dy = bb.short
        return MouseMovePacket(seq, dx, dy)
    }

    fun decodeMouseButton(buf: ByteArray, length: Int): MouseButtonPacket? {
        if (length < 6 || !isValidHeader(buf, length)) return null
        return MouseButtonPacket(buf[4], buf[5].toInt() != 0)
    }

    fun decodeMouseScroll(buf: ByteArray, length: Int): MouseScrollPacket? {
        if (length < 8 || !isValidHeader(buf, length)) return null
        val bb = ByteBuffer.wrap(buf, 4, 4).order(ByteOrder.LITTLE_ENDIAN)
        return MouseScrollPacket(bb.short, bb.short)
    }

    fun decodeKeyEvent(buf: ByteArray, length: Int): KeyEventPacket? {
        if (length < 12 || !isValidHeader(buf, length)) return null
        val bb = ByteBuffer.wrap(buf, 4, 8).order(ByteOrder.LITTLE_ENDIAN)
        val vk = bb.short.toInt() and 0xFFFF
        val sc = bb.short.toInt() and 0xFFFF
        val pressed = bb.get().toInt() != 0
        val mods = bb.get().toInt() and 0xFF
        val ch = (bb.short.toInt() and 0xFFFF).toChar()
        return KeyEventPacket(vk, sc, pressed, mods, ch)
    }

    fun decodeEdgeHandOff(buf: ByteArray, length: Int): EdgeHandOffPacket? {
        if (length < 10 || !isValidHeader(buf, length)) return null
        val edge = buf[4]
        val dragging = buf[5].toInt() != 0
        val pos = ByteBuffer.wrap(buf, 6, 4).order(ByteOrder.LITTLE_ENDIAN).float
        return EdgeHandOffPacket(edge, dragging, pos.coerceIn(0f, 1f))
    }

    fun encodeEdgeHandOff(targetEntranceEdge: Byte, isDragging: Boolean, normalizedPos: Float): ByteArray {
        val bb = ByteBuffer.allocate(10).order(ByteOrder.LITTLE_ENDIAN)
        bb.put(ProtocolConstants.MAGIC_0)
        bb.put(ProtocolConstants.MAGIC_1)
        bb.put(ProtocolConstants.VERSION)
        bb.put(ProtocolConstants.PACKET_EDGE_HANDOFF)
        bb.put(targetEntranceEdge)
        bb.put(if (isDragging) 1.toByte() else 0.toByte())
        bb.putFloat(normalizedPos.coerceIn(0f, 1f))
        return bb.array()
    }

    fun encodeHeartbeat(type: Byte, timestampMs: Long): ByteArray {
        val bb = ByteBuffer.allocate(12).order(ByteOrder.LITTLE_ENDIAN)
        bb.put(ProtocolConstants.MAGIC_0)
        bb.put(ProtocolConstants.MAGIC_1)
        bb.put(ProtocolConstants.VERSION)
        bb.put(type)
        bb.putLong(timestampMs)
        return bb.array()
    }

    fun decodeHeartbeatTimestamp(buf: ByteArray, length: Int): Long? {
        if (length < 12 || !isValidHeader(buf, length)) return null
        return ByteBuffer.wrap(buf, 4, 8).order(ByteOrder.LITTLE_ENDIAN).long
    }
}

object TcpFrameCodec {

    fun writeFrame(
        output: OutputStream,
        headerJson: JSONObject,
        payloadStream: InputStream? = null,
        payloadLength: Long = 0L
    ) {
        val jsonBytes = headerJson.toString().toByteArray(StandardCharsets.UTF_8)
        val prefix = ByteBuffer.allocate(12).order(ByteOrder.LITTLE_ENDIAN)
        prefix.putInt(jsonBytes.size)
        prefix.putLong(payloadLength)

        output.write(prefix.array())
        output.write(jsonBytes)

        if (payloadStream != null && payloadLength > 0) {
            val buffer = ByteArray(64 * 1024)
            var remaining = payloadLength
            while (remaining > 0) {
                val toRead = minOf(buffer.size.toLong(), remaining).toInt()
                val read = payloadStream.read(buffer, 0, toRead)
                if (read <= 0) break
                output.write(buffer, 0, read)
                remaining -= read
            }
        }
        output.flush()
    }

    fun readHeader(input: InputStream): Pair<JSONObject, Long>? {
        val prefix = ByteArray(12)
        if (!readExact(input, prefix, 12)) return null

        val bb = ByteBuffer.wrap(prefix).order(ByteOrder.LITTLE_ENDIAN)
        val jsonLen = bb.int
        val binLen = bb.long
        if (jsonLen <= 0 || jsonLen > 10 * 1024 * 1024) return null

        val jsonBytes = ByteArray(jsonLen)
        if (!readExact(input, jsonBytes, jsonLen)) return null

        val jsonStr = String(jsonBytes, StandardCharsets.UTF_8)
        return Pair(JSONObject(jsonStr), binLen)
    }

    fun readExact(input: InputStream, buffer: ByteArray, length: Int): Boolean {
        var offset = 0
        while (offset < length) {
            val read = input.read(buffer, offset, length - offset)
            if (read <= 0) return false
            offset += read
        }
        return true
    }
}
