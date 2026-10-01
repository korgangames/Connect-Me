# Connect Me v1.5.0 (v1-5-0) — Merkezi Ses Yönlendirme (Audio Hub) & Kulaklık Köprüsü

Connect Me'nin tüm bağlı cihazların (Android telefon/tablet ve Linux bilgisayarlar) sistem, müzik ve medya seslerini merkez Windows bilgisayara ve ona bağlı kulaklığa sıfır-ekstra bağımlılıkla aktaran **Merkezi Ses Yönlendirme (Central Audio Hub)** özelliğini içeren yeni kararlı sürümü yayınlandı!

---

### 🌟 v1.5.0 Yenilikleri ve Geliştirmeleri:

* **🎧 Merkezi Ses Yönlendirme (Central Audio Hub):**
  - **Kulaklık Odaklı Mimari:** Kullanıcının kulaklığı merkez bilgisayara (Windows) bağlıyken; Android telefondan video izlerken, müzik dinlerken veya Linux cihazında çalışırken tüm sesler aynı kulaklıktan pürüzsüz ve senkronize olarak duyulur.
  - **UDP Port 42852 Yüksek Hızlı Akış:** Ses iletimi için özel tahsis edilen 42852 UDP portu üzerinden ultra-düşük gecikmeli (<15ms) 48.000 Hz 16-bit Stereo PCM (S16LE) akışı sağlanır.

* **🪟 Windows Native Win32 WaveOut Ses Motoru (Zero Dependency):**
  - **Sıfır Dış Kütüphane:** Hiçbir harici ses kütüphanesine ihtiyaç duymadan doğrudan Windows `winmm.dll` WaveOut API'leri üzerinden çalışır.
  - **Dinamik Ses Karıştırma & Örnekleme:** Gelen ses paketlerini otomatik olarak algılar; frekans ve kanal değişimlerine dinamik olarak uyum sağlar.
  - **Yazılımsal Ses Seviyesi & Mute Kontrolü:** Arayüzdeki kaydırıcı (0% - 150%) ve sessize alma (Mute) düğmesi ile gelen sesler kulaklık çıkışına zarar vermeden ölçeklenir.
  - **Bellek Yönetimi & Havuzlama:** Ses kesintilerini (buffer underrun) önlemek için unmanaged bellek havuzu ve dairesel tampon mimarisi kullanılmıştır.

* **📱 Android Sistem ve Medya Sesi Yakalama (AudioPlaybackCapture API):**
  - **Dahili Sistem Sesi Akışı (Android 10+):** `AudioPlaybackCaptureConfiguration` ve `MediaProjection` izinleri sayesinde YouTube, Spotify, oyunlar ve sistem bildirimlerinin sesi mikrofona ihtiyaç duymadan doğrudan dijital olarak yakalanıp aktarılır.
  - **Akıllı Mikrofon Geri Uyumluluğu:** Sistem sesi izni verilmeyen eski cihazlarda otomatik olarak düşük gecikmeli PCM mikrofon yakalamaya geçer.
  - **Tek Dokunuşla Başlatma:** Android arayüzüne eklenen "🎧 Kulaklığa Aktarımı Başlat" kartı ile tek dokunuşla akış kontrolü sağlanır.

* **🐧 Linux (Nobara / KDE Plasma) PipeWire & PulseAudio Entegrasyonu:**
  - **Yerel PipeWire / PulseAudio Akışı:** Linux üzerinde `pw-record` (PipeWire) veya `parec` (PulseAudio) kullanılarak varsayılan ses çıkışı yakalanır ve UDP 42852 portundan Windows ses merkezine aktarılır.
  - **Linux GUI Ses Sekmesi:** Arayüze eklenen "🎧 Ses Yönlendirme" sekmesi üzerinden tek tıkla ses akışı başlatılıp durdurulabilir.

* **🖥️ Windows Arayüz Geliştirmeleri:**
  - **Üst Çubuk Ses Rozeti:** Üst durum çubuğunda anlık ses akışı durumu, veri aktarımı ve kaynak cihaz bilgisi (`🎧 Aktif: 192.168.1.106`) yeşil canlı rozetle gösterilir.
  - **Özel Ses Merkezi Sekmesi:** Ses seviyesi ayarı, Mute anahtarı ve tanılama sayaçlarını içeren yeni geniş çalışma alanı sekmesi eklendi.

---

### 📦 İndirilebilir Paketler (Yalnızca Sürümlü Dosya Adları):
1. **Windows:** `ConnectMe-Windows-x64v1-5-0.zip` — Çıkartın ve `ConnectMe.exe`'ye çift tıklayın (.NET gerektirmez, standalone).
2. **Android:** `ConnectMeV1-5-0.apk` — Doğrudan Android cihazınıza kurun.
3. **Linux:** `ConnectMe-Linux-x64v1-5-0.tar.gz` — Nobara / KDE Plasma için bağımsız paket (`start-connectme.sh` veya `install-desktop.sh`).
