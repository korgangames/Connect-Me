# Connect Me 🌐🖱️⌨️

**Connect Me**, **Windows**, **Linux (Nobara / Wayland)** ve **Android** cihazlarınızı ekran yansıtma (screen mirroring) olmadan tek bir fiziksel klavye ve fare ile kontrol etmenizi, ortak pano (Universal Clipboard) kullanmanızı ve cihazlar arasında sürükle-bırak / manyetik cep (Drop Shelf) ile kolayca eşya paylaşmanızı sağlayan hibrit (**Wi-Fi + Bluetooth**) bir ekosistem uygulamasıdır.

> 📄 **Mimari ve Tasarım Dokümanı:** Detaylı sistem mimarisi, işletim sistemi çözümleri, ağ protokolü ve yol haritası için [**App Design Document (`docs/ADD.md`)**](docs/ADD.md) dosyasına göz atın.

## ✨ Temel Özellikler

- **Kenardan Kenara Doğal Geçiş (Seamless Edge Switching):** İmleç ekranın kenarına ulaştığında harici bir monitöre geçer gibi diğer cihaza (PC veya Android) anında geçer; ekran kopyalama veya ek sanal monitör oluşturma gerektirmez.
- **Hibrit Wi-Fi + Bluetooth Mimarisi:**
  - **Wi-Fi (QUIC / UDP / mDNS):** <2ms ultra düşük gecikmeli girdi akışı, yüksek hızlı dosya transferi ve zengin pano senkronizasyonu.
  - **Bluetooth (BLE + Classic HID):** Yakınlık keşfi ve Android cihazlarda root/ADB gerektirmeden yerel işletim sistemi fare imleci (Native OS Cursor) + klavye kontrolü.
- **Evrensel Pano (Universal Clipboard):** Metin, HTML, ekran görüntüleri ve dosyaları cihazlar arasında kopyalayıp yapıştırın.
- **Sürtünmesiz Eşya Paylaşımı (Frictionless Item Sharing):**
  - **Edge Drag & Drop:** Dosyaları doğrudan ekran kenarından diğer cihaza sürükleyip bırakın.
  - **Manyetik Cep (Drop Shelf):** Ekran kenarında açılan ortak cebe dosya, görsel veya link bırakarak tüm bağlı cihazlardan anında erişin.

## 🛠️ Hedeflenen Teknoloji Yığını

- **Core & Networking:** Rust (`tokio`, `quinn` [QUIC], `rustls`, `mdns-sd`, `btleplug`)
- **Windows:** `windows-rs` (Win32 Low-Level Hooks, Raw Input, `SendInput`, OLE Drag & Drop)
- **Linux (Nobara):** `evdev` + `/dev/uinput` & Wayland `InputCapture` / `RemoteDesktop` Portals (`ashpd`, `libei`), `bluer` (BlueZ HID)
- **Android:** Kotlin (Jetpack Compose) + Rust Core (`UniFFI` / JNI), `BluetoothHidDevice`, `Shizuku`, `AccessibilityService`

---
*Geliştirici: **Korgan Games***
