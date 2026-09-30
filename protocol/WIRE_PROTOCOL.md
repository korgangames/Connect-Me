# Connect Me — Wire Protocol v1.2 Specification

Bu doküman **Windows**, **Android** ve **Linux (Nobara KDE Plasma)** istemcilerinin birbirleriyle haberleşmek için kullandığı bayt seviyesindeki (byte-level) ortak protokolü ve **Çift Taraflı 6 Haneli PIN Eşleşme (Mutual Dual-PIN Handshake)** akışını tanımlar. Tüm çok baytlı tam sayılar **Little-Endian (LE)** formatındadır.

---

## 1. Port Tahsisi

| Kanal | Protokol | Port | Amaç |
| :--- | :--- | :--- | :--- |
| **Discovery (Keşif)** | UDP Broadcast / Unicast + BLE | `42849` | Yerel ağda ve Bluetooth menzilinde otomatik çoklu cihaz keşfi |
| **Fast-Path Input (Girdi)** | UDP Binary | `42850` | `<1.5ms` gecikmeli fare, klavye, kenar geçişi (`EdgeHandOff`) ve heartbeat |
| **Data & Control (Veri)** | TCP Framed Stream | `42851` | Çift taraflı 6 haneli PIN doğrulaması, Evrensel Pano ve Drop Shelf dosya aktarımı |

---

## 2. Çift Taraflı 6 Haneli PIN Eşleşme Protokolü (Mutual Dual-PIN Handshake)

Her cihazda uygulama açıldığında kendine ait **6 haneli bir PIN kodu** (`LocalPin`, örn. `482910`) oluşturulur. İki cihazın (`Cihaz A` ve `Cihaz B`) birbirine bağlanıp kenar geçişi, pano ve dosya paylaşımı yapabilmesi için **her iki tarafın da karşı cihazın 6 haneli kodunu doğrulaması** gerekir:

1. **`PAIR_REQUEST` (TCP 42851):**
   * Bir cihazda (Örn: `Cihaz A`), karşı cihazın (`Cihaz B`) ekranında görünen 6 haneli PIN kodu (`targetPin`) girilerek gönderilir:
   ```json
   {
     "type": "PAIR_REQUEST",
     "senderId": "win-pc-01",
     "senderName": "Egemen-PC",
     "senderPlatform": "windows",
     "senderUdpPort": 42850,
     "senderTcpPort": 42851,
     "senderScreenWidth": 2560,
     "senderScreenHeight": 1440,
     "targetPin": "739104"
   }
   ```
2. **Karşı Doğrulama ve `PAIR_RESPONSE` / Çift Taraflı Onay:**
   * `Cihaz B`, gelen `targetPin` değerini kendi `LocalPin` değeriyle karşılaştırır:
     * Eşleşmezse `PAIR_REJECT` döner.
     * Eşleşirse `Cihaz B`, `Cihaz A`'yı `RemoteVerifiedMyPin = true` olarak işaretler ve `PAIR_VERIFY_ACK` döner (`Cihaz A`'da `MyEnteredPinAccepted = true` olur).
   * Bağlantının **Tam Onaylı (`MutuallyPaired`)** olması için `Cihaz B` kullanıcısı da `Cihaz A`'nın 6 haneli kodunu (`482910`) kendi ekranında girer ve `PAIR_REQUEST` gönderir!
   * Her iki cihazda da `(MyEnteredPinAccepted && RemoteVerifiedMyPin)` koşulu sağlandığı anda durum **`MutuallyPaired`** olur ve iki cihaz birbirinin **2D Ekran Konfigürasyonu Kanvasına** otomatik olarak eklenir!

---

## 3. UDP Fast-Path Girdi Paketleri (Port `42850`)

Her UDP girdi paketi **4 baytlık sabit başlık (Header)** ile başlar:

```text
+--------+--------+---------+------------+-------------------------+
| Byte 0 | Byte 1 | Byte 2  |   Byte 3   |       Bytes 4..N        |
+--------+--------+---------+------------+-------------------------+
|  0x43  |  0x4D  |  0x01   | PacketType | Packet-Specific Payload |
|  ('C') |  ('M') | (Ver 1) |    (u8)    |     (Little-Endian)     |
+--------+--------+---------+------------+-------------------------+
```

### Paket Tipleri (`PacketType`)
* **`0x01` — `MOUSE_MOVE` (10 Bayt):** `Sequence (u16)`, `DeltaX (i16)`, `DeltaY (i16)`
* **`0x02` — `MOUSE_BUTTON` (6 Bayt):** `Button (u8)`, `Action (u8)`
* **`0x03` — `MOUSE_SCROLL` (8 Bayt):** `ScrollX (i16)`, `ScrollY (i16)`
* **`0x04` — `KEY_EVENT` (12 Bayt):** `VirtualKey (u16)`, `ScanCode (u16)`, `Action (u8)`, `Modifiers (u8)`, `UnicodeChar (u16)`
* **`0x05` — `EDGE_HANDOFF` (10 Bayt):** `TargetEntranceEdge (u8)`, `Flags (u8)`, `NormalizedPosition (f32)`
* **`0x06` / `0x07` — `HEARTBEAT_PING` / `PONG` (12 Bayt):** `TimestampMs (i64)`
