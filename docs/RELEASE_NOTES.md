# Connect Me v1.6.0 (v1-6-0) — Dahili Uygulama İçi Otomatik Güncelleyici (In-Program Auto Updater)

Connect Me'nin manuel güncelleme zorluklarını tamamen ortadan kaldıran, GitHub Releases üzerinden yeni sürümleri doğrudan uygulama içinden denetleyip Android (APK) ve Windows ortamında **tek dokunuşla otomatik indirip kuran** yeni kararlı sürümü yayınlandı!

---

### 🌟 v1.6.0 Yenilikleri ve Geliştirmeleri:

* **📱 Android Dahili Tek Tıkla Otomatik Güncelleyici (In-App Auto Updater & FileProvider):**
  - **Sıfır Manuel İndirme Zahmeti:** Tarayıcıya gitme, GitHub arama veya indirilen APK dosyasını Dosyalar uygulamasında arayıp açma derdi sona erdi!
  - **Arka Planda Akıllı Denetim:** Connect Me açıldığında GitHub Releases API'sini sessizce denetler. Yeni bir sürüm çıktığında ana ekranda göz alıcı koyu yeşil bir güncelleme kartı belirir.
  - **Uygulama İçi İndirme & İlerleme Çubuğu:** "⬇️ Tek Tıkla İndir ve Kur" butonuna basıldığında APK doğrudan uygulamanın güvenli önbelleğine indirilir, yüzdelik ilerleme çubuğu ve MB sayacı canlı gösterilir.
  - **Doğrudan Android Paket Yükleyicisi (Package Installer) Tetikleme:** İndirme tamamlandığında Android `FileProvider` ve `Intent.ACTION_VIEW` (`FLAG_GRANT_READ_URI_PERMISSION`) ile sistemin yerel güncelleme ekranı anında açılır. Kullanıcının tek yapması gereken "Güncelle" düğmesine dokunmaktır.
  - **Bilinmeyen Kaynak İzni Koruması:** Android 8+ için `REQUEST_INSTALL_PACKAGES` izni gerekiyorsa kullanıcı nazikçe yönlendirilir ve izni açıp döndüğünde kurulum kaldığı yerden otomatik devam eder.
  - **Manuel "🔄 Güncellemeleri Denetle" Butonu:** İstediğiniz an tek dokunuşla yeni sürüm kontrolü yapabilirsiniz.

* **🪟 Windows Dahili Otomatik Güncelleyici (Zero-Touch Self-Update):**
  - **Canlı Güncelleme Rozeti:** Yeni sürüm yayınlandığında üst durum çubuğunda `🎉 Yeni Sürüm (v1.6.0) [⬇️ Şimdi Güncelle]` rozeti parlar.
  - **Otomatik İndirme & Çıkartma:** Butona tıklandığında yeni `ConnectMe-Windows-x64*.zip` arşivi arka planda indirilir ve yeni `ConnectMe.exe` çıkartılır.
  - **Kesintisiz Yeniden Başlatma (`apply_update.cmd`):** Mevcut çalışan program dosyası kilitli olduğu için hafif ve penceresiz bir geçiş komutuyla uygulama 2 saniye içinde güncellenip otomatik olarak yeniden başlatılır.
  - **Alt Çubuk Güncelleme Butonu:** Alt bilgi çubuğuna eklenen `🔄 Güncelleme Denetle` butonu ile dilediğiniz zaman sürüm denetimi yapabilirsiniz.

* **🐧 Linux (Nobara / KDE Plasma) Güncelleme Bildirimi:**
  - Linux GUI arayüzüne eklenen otomatik kontrol sistemiyle yeni bir release çıktığında buton `🎉 v1.6.0 Mevcut!` olarak güncellenir ve tek tıkla en son sürüme yönlendirir.

---

### 📦 İndirilebilir Paketler (Yalnızca Sürümlü Dosya Adları):
1. **Windows:** `ConnectMe-Windows-x64v1-6-0.zip` — Çıkartın ve `ConnectMe.exe`'ye çift tıklayın (.NET gerektirmez, standalone).
2. **Android:** `ConnectMeV1-6-0.apk` — Doğrudan Android cihazınıza kurun (veya mevcut uygulamadan otomatik güncelleyin!).
3. **Linux:** `ConnectMe-Linux-x64v1-6-0.tar.gz` — Nobara / KDE Plasma için bağımsız paket (`start-connectme.sh` veya `install-desktop.sh`).
