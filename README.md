# Connect Me 🌐🖱️⌨️

**Connect Me**, **Windows**, **Android** ve **Linux (Nobara — KDE Plasma Wayland)** cihazlarınızı ekran yansıtma (screen mirroring) olmadan tek bir fiziksel klavye ve fare ile kontrol etmenizi, ortak pano (Universal Clipboard) kullanmanızı ve cihazlar arasında sürükle-bırak / manyetik cep (Drop Shelf) ile kolayca eşya paylaşmanızı sağlayan hibrit (**Wi-Fi + Bluetooth**) bir ekosistem uygulamasıdır.

> 📄 **Mimari ve Tasarım Dokümanı:** Detaylı sistem mimarisi, işletim sistemi çözümleri, ağ protokolü ve yol haritası için [**App Design Document (`docs/ADD.md`)**](docs/ADD.md) dosyasına göz atın.

## 🗺️ Geliştirme Yol Haritası Özeti

1. **Faz 1 (Aktif Hedef): Windows ↔ Android Çekirdek Bağlantı & Kenar Geçişli Kontrol**
   - Wi-Fi (UDP `<1.5ms` Girdi Akışı + Otomatik Keşif) & Bluetooth (BLE Keşif / HID).
   - Fare ekran kenarına değdiğinde Windows'tan Android'e (ve Android'den Windows'a) pürüzsüz geçiş.
   - Android üzerinde 120Hz donanım hızlandırmalı Overlay İmleç + Accessibility/Klavye enjeksiyonu (ekran klavyesi açılmadan PC klavyesiyle yazma).
2. **Faz 2: Windows ↔ Android Evrensel Pano & Manyetik Cep (Drop Shelf)**
   - Çift yönlü metin/görsel pano senkronizasyonu ve sürükle-bırak dosya paylaşımı.
3. **Faz 3: Nobara Linux (KDE Plasma Wayland) Entegrasyonu**
   - KWin Wayland Portalları (`InputCapture` & `RemoteDesktop` / `libei`) + `/dev/uinput` hibrit girdi motoru ve KDE `Klipper` pano entegrasyonu.
4. **Faz 4: Üçlü Ekosistem Tam Senkronizasyon & Cilalama**

---
*Geliştirici: **Korgan Games***
