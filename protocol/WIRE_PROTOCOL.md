# Connect Me — Wire Protocol v1 Specification

Bu doküman **Windows**, **Android** ve **Linux (Nobara KDE Plasma)** istemcilerinin birbirleriyle haberleşmek için kullandığı bayt seviyesindeki (byte-level) ortak protokolü tanımlar. Tüm çok baytlı tam sayılar **Little-Endian (LE)** formatındadır.

---

## 1. Port Tahsisi

| Kanal | Protokol | Port | Amaç |
| :--- | :--- | :--- | :--- |
| **Discovery (Keşif)** | UDP Broadcast / Unicast + BLE | `42849` | Yerel ağda ve Bluetooth menzilinde otomatik cihaz keşfi |
| **Fast-Path Input (Girdi)** | UDP Binary | `42850` | `<1.5ms` gecikmeli fare, klavye, kenar geçişi (`EdgeHandOff`) ve heartbeat |
| **Data & Control (Veri)** | TCP Framed Stream | `42851` | Eşleşme (PIN/QR), Evrensel Pano (Metin/Görsel) ve Drop Shelf dosya aktarımı |

---

## 2. UDP Fast-Path Girdi Paketleri (Port `42850`)

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

#### `0x01` — `MOUSE_MOVE` (Toplam: 10 Bayt)
Aktif kaynak cihazdan hedef cihaza göreceli (delta) fare hareketini iletir.
* `Bytes 4..5` (`u16` LE): `Sequence` (Sıra numarası)
* `Bytes 6..7` (`i16` LE): `DeltaX` (Yatay piksel değişimi)
* `Bytes 8..9` (`i16` LE): `DeltaY` (Dikey piksel değişimi)

#### `0x02` — `MOUSE_BUTTON` (Toplam: 6 Bayt)
Fare tuşu basılma ve bırakılma olaylarını iletir.
* `Byte 4` (`u8`): `Button`
  * `1` = Sol Tık (Android: Dokunma / Sürükleme)
  * `2` = Sağ Tık (Android: `GLOBAL_ACTION_BACK` - Geri)
  * `3` = Orta Tık (Android: `GLOBAL_ACTION_HOME` - Ana Ekran)
  * `4` = Yan Tuş 1 (Geri)
  * `5` = Yan Tuş 2 (İleri)
* `Byte 5` (`u8`): `Action` (`1` = Basıldı / Down, `0` = Bırakıldı / Up)

#### `0x03` — `MOUSE_SCROLL` (Toplam: 8 Bayt)
Fare tekerleği (dikey ve yatay kaydırma) hareketlerini iletir.
* `Bytes 4..5` (`i16` LE): `ScrollX`
* `Bytes 6..7` (`i16` LE): `ScrollY` (Pozitif = Yukarı, Negatif = Aşağı)

#### `0x04` — `KEY_EVENT` (Toplam: 12 Bayt)
Klavye tuş vuruşlarını ve işletim sistemi tarafından çözümlenmiş Unicode karakterini (Türkçe Q/F karakterler dahil) iletir.
* `Bytes 4..5` (`u16` LE): `VirtualKey` (Windows VK kodu)
* `Bytes 6..7` (`u16` LE): `ScanCode` (Donanım tarama kodu)
* `Byte 8` (`u8`): `Action` (`1` = KeyDown, `0` = KeyUp)
* `Byte 9` (`u8`): `Modifiers` Bitmask (`0x01 = Shift`, `0x02 = Ctrl`, `0x04 = Alt`, `0x08 = Win/Meta`)
* `Bytes 10..11` (`u16` LE): `UnicodeChar` (UTF-16 karakter kodu; basılabilir karakter değilse `0`)

#### `0x05` — `EDGE_HANDOFF` (Toplam: 10 Bayt)
İmleç bir cihazın ekran kenarından diğer cihaza geçtiğinde kontrolü devretmek için gönderilir.
* `Byte 4` (`u8`): `TargetEntranceEdge` (Hedef cihazın hangi kenarından giriş yapıldığı: `1 = Sol`, `2 = Sağ`, `3 = Üst`, `4 = Alt`)
* `Byte 5` (`u8`): `Flags` (`0 = Normal Geçiş`, `1 = Dosya/Öğe Sürükleyerek Geçiş`)
* `Bytes 6..9` (`f32` LE): `NormalizedPosition` (`0.0` ile `1.0` arası; kenar boyunca orantısal giriş noktası)

#### `0x06` — `HEARTBEAT_PING` & `0x07` — `HEARTBEAT_PONG` (Toplam: 12 Bayt)
Bağlantı canlılığını ve gecikmeyi (`RTT ms`) ölçer.
* `Bytes 4..11` (`i64` LE): `TimestampMs` (Gönderici zaman damgası)

---

## 3. TCP Kontrol, Evrensel Pano ve Dosya Kanalı (Port `42851`)

Her TCP çerçevesi (Frame) **12 baytlık uzunluk başlığı** ile başlar:
* `Bytes 0..3` (`i32` LE): `JsonHeaderLength` (UTF-8 JSON başlığının bayt uzunluğu)
* `Bytes 4..11` (`i64` LE): `BinaryPayloadLength` (JSON başlığından hemen sonra gelen ikili dosya/görsel verisinin bayt uzunluğu; yoksa `0`)
* `Bytes 12..(12 + JsonHeaderLength)`: UTF-8 JSON Kontrol Mesajı
* `Bytes (12 + JsonHeaderLength)..`: İkili (Binary) Veri Akışı (Dosya veya PNG görsel)
