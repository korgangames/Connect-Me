# Connect Me v1.3.0 — Bağımsız Çalıştırılabilir Sürümler (Windows, Android & Linux)

Connect Me'nin tüm işletim sistemleri için son kullanıcıya hazır, hiçbir konsol veya komut satırı gerektirmeyen bağımsız paketleri yayınlandı!

### 🌟 Yenilikler ve Öne Çıkanlar:
* **🪟 Windows (.NET Gömülü Standalone GUI):**
  - .NET 10 çalışma zamanı doğrudan uygulamanın içine gömüldü (`--self-contained true`).
  - Siyah konsol / cmd penceresi kaldırıldı (`WinExe`), çift tıklamayla doğrudan modern koyu temalı ekran konfigürasyon arayüzü açılır.
* **📱 Android (120Hz KVM & Pano):**
  - Doğrudan indirilebilir ve kurulabilir `ConnectMe.apk`.
  - Android ekranında pürüzsüz 120Hz sanal imleç, dokunma/kaydırma ve fiziksel klavye köprüsü.
* **🐧 Linux (Nobara / KDE Plasma Wayland & X11):**
  - Bağımsız grafik kontrol paneli (`connectme_linux_gui.py`).
  - Masaüstü ve başlat menüsü entegrasyonu (`ConnectMe.desktop` & `install-desktop.sh`). Terminal açmadan çift tıklamayla çalışır.
* **⭐ Bu Cihaza Güven ve Hatırla:**
  - PIN doğrulamasının ardından cihazları güvenilir olarak kaydetme ve program açıkken şifresiz otomatik bağlanma (`TRUSTED_RECONNECT`).

### 📦 İndirilebilir Paketler:
1. **Windows:** `ConnectMe-Windows-x64.zip` (Çıkartın ve `ConnectMe.exe`'ye çift tıklayın)
2. **Android:** `ConnectMe.apk` (Doğrudan cihaza kurun)
3. **Linux:** `ConnectMe-Linux-x64.tar.gz` (Arşivi açın, `start-connectme.sh` ile başlatın veya `install-desktop.sh` ile menüye ekleyin)
