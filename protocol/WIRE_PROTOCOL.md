# Connect Me — Wire Protocol v1.3 Specification (Çoklu Monitör Genişletmesi)

Bu doküman **Windows**, **Android** ve **Linux (Nobara KDE Plasma)** istemcilerinin birbirleriyle haberleşmek için kullandığı bayt seviyesindeki (byte-level) ortak protokolü, **Çift Taraflı 6 Haneli PIN Eşleşme (Mutual Dual-PIN Handshake)** akışını ve **Çoklu Monitör Sanal Masaüstü (Multi-Monitor Virtual Desktop)** topoloji senkronizasyonunu tanımlar. Tüm çok baytlı tam sayılar **Little-Endian (LE)** formatındadır.

---

## 1. Port Tahsisi

| Kanal | Protokol | Port | Amaç |
| :--- | :--- | :--- | :--- |
| **Discovery (Keşif)** | UDP Broadcast / Unicast + BLE | `42849` | Yerel ağda ve Bluetooth menzilinde otomatik çoklu cihaz & monitör listesi keşfi |
| **Fast-Path Input (Girdi)** | UDP Binary | `42850` | `<1.5ms` gecikmeli fare, klavye, kenar geçişi (`EdgeHandOff`) ve heartbeat |
| **Data & Control (Veri)** | TCP Framed Stream | `42851` | Çift taraflı 6 haneli PIN doğrulaması, Çoklu Monitör Senkronizasyonu (`TOPOLOGY_SYNC`), Evrensel Pano ve Drop Shelf dosya aktarımı |

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
     "senderMonitors": [
       { "monitorId": "DISPLAY1", "name": "Monitör 1 (DP-1)", "virtualX": 0, "virtualY": 0, "width": 2560, "height": 1440, "scaleFactor": 1.25, "isPrimary": true },
       { "monitorId": "DISPLAY2", "name": "Monitör 2 (HDMI-1)", "virtualX": 2560, "virtualY": 0, "width": 1920, "height": 1080, "scaleFactor": 1.0, "isPrimary": false }
     ],
     "targetPin": "739104"
   }
   ```
2. **Karşı Doğrulama ve `PAIR_VERIFY_ACK` / Çift Taraflı Onay:**
   * `Cihaz B`, gelen `targetPin` değerini kendi `LocalPin` değeriyle karşılaştırır:
     * Eşleşmezse `PAIR_REJECT` döner.
     * Eşleşirse `Cihaz B`, `Cihaz A`'yı `RemoteVerifiedMyPin = true` olarak işaretler ve `PAIR_VERIFY_ACK` döner (`senderMonitors` kendi monitör listesini içerir).
   * Bağlantının **Tam Onaylı (`MutuallyPaired`)** olması için `Cihaz B` kullanıcısı da `Cihaz A`'nın 6 haneli kodunu (`482910`) kendi ekranında girer ve `PAIR_REQUEST` gönderir.
   * Her iki cihazda da `(MyEnteredPinAccepted && RemoteVerifiedMyPin)` koşulu sağlandığı anda durum **`MutuallyPaired`** olur ve iki cihaz birbirinin **2D Ekran Konfigürasyonu Kanvasına** otomatik olarak eklenir!

---

## 3. Çoklu Monitör Sanal Masaüstü Protokolü (Multi-Monitor Architecture)

Windows (`EnumDisplayMonitors`) veya Linux Nobara KDE Plasma (`kscreen-doctor -j` / `wlr-randr` / `xrandr`) üzerinde birden fazla fiziksel ekran bağlandığında sistem şu kurallara göre çalışır:

1. **`PhysicalMonitorDescriptor` Veri Yapısı:**
   * `monitorId`: Fiziksel konektör / port adı (`DP-1`, `HDMI-A-1`, `DISPLAY1`).
   * `name`: Kullanıcı dostu etiket.
   * `virtualX`, `virtualY`: İşletim sisteminin sanal masaüstü koordinatları (Sol/üst monitörler için negatif koordinatları destekler, örn: `-1080`).
   * `width`, `height`: Fiziksel piksel çözünürlüğü.
   * `scaleFactor`: Per-Monitor DPI ölçeği (örn. `1.25` = %125).
   * `isPrimary`: Birincil ekran bayrağı.
2. **İç Birleşim Çizgisi Koruması (Internal Seam Rule):**
   * Bilgisayarın kendi fiziksel monitörleri arasındaki ortak dikey/yatay kenarlarda (ör. `Monitör 1`'den `Monitör 2`'ye geçiş) imleç serbestçe hareket eder; işletim sistemi imleci yerel olarak taşır, Connect Me araya girmez (`ShouldTransition = false`).
   * İmleç yalnızca bir monitörün **dışa açık kenarına (Exposed Outer Edge)** çarptığında ve kenar direnci aşıldığında diğer cihaza (`Android`, diğer PC) geçer!
3. **`TOPOLOGY_SYNC` Çerçevesi (TCP 42851):**
   * Bilgisayara yeni bir monitör takıldığında, çıkarıldığında veya düzeni değiştirildiğinde eşleşmiş tüm cihazlara güncel monitör listesi gönderilir.

---

## 4. UDP Fast-Path Girdi Paketleri (Port `42850`)

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
