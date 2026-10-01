# Connect Me v1.6.3 (v1-6-3) — Nobara Linux Manuel IP ile Bağlantı, Herkese Açık Dağıtım ve Güncelleyici Düzeltmeleri

Bu sürüm; Nobara Linux üzerinde doğrudan IP adresi girerek cihaz bağlama (Manual IP Direct Connect), otomatik ağ alt ağı tarama (Subnet Broadcast Scan), GitHub reposunun Public yapılarak tüm cihazlar için tek tıkla güncellemenin ve dosya indirmelerinin kesintisiz hale getirilmesini sağlar.

---

### 🌟 v1.6.3 Yenilikleri ve Düzeltmeleri:

* **🌐 Nobara Linux Manuel IP ile Cihaz Ekleme & Doğrudan Bağlantı:**
  - Linux GUI (`connectme_linux_gui.py`) "Cihazlar & Güvenlik" sekmesine ve üst başlık kartına **"Manuel IP ile Cihaz Ekle & Doğrudan Bağlan"** paneli eklendi.
  - Ağ keşfinin router AP izolasyonu, güvenlik duvarı veya karmaşık Wi-Fi ağları nedeniyle engellendiği durumlarda, hedef cihazın (Windows veya Android) IP adresi yazılarak doğrudan UDP ve TCP üzerinden anında bağlantı kurulabilir.
  - Yerel alt ağ öneki (`192.168.1.` vb.) otomatik doldurulur; Enter tuşu veya `➕ IP ile Cihaz Ekle & Keşfet` butonu ile cihaz anında listeye alınır.
  - PIN kodu önceden girilmişse tek tıkla doğrudan TCP üzerinden PIN doğrulama ve karşılıklı eşleşme başlatılır.

* **📡 Manuel Ağ Alt Ağı Tarama (Subnet Broadcast):**
  - Linux arayüzüne eklenen `📡 Alt Ağı Tara` butonu sayesinde tüm yerel alt ağa anında doğrudan keşif sinyalleri yollanarak yeni açılan cihazlar saniyeler içinde yakalanır.

* **🔓 GitHub Depo Görünürlüğü (Public) & 3 İşletim Sisteminde Otomatik Güncelleme:**
  - Deponun Private olmasından dolayı cihazların aldığı `HTTP 404 Not Found` engeli GitHub API üzerinden depo **Public (Herkese Açık)** yapılarak kalıcı olarak giderildi.
  - Windows, Android ve Linux cihazlardaki dahili otomatik güncelleyici (In-Program Updater) artık yeni sürümleri doğrudan görüp tek tıkla indirip kurabilir.
  - Olası ağ veya sunucu aksaklıklarında *"Uygulamanız güncel"* yanıltması kaldırılarak detaylı tanı mesajları eklendi.

* **📱 Android Derleme Hatası Düzeltmesi:**
  - Android `MainActivity.kt` içerisindeki güncelleyici loglama çağrısı düzeltilerek GitHub Actions CI/CD derleme zincirinin başarıyla APK üretmesi sağlandı.

---

# Connect Me v1.6.2 (v1-6-2) — Nobara Linux & Çoklu Monitör İyileştirmeleri, Kernel Sanal Girdi ve Çift Taraflı PIN Güvenliği

Bu sürüm; Nobara Linux (KDE Plasma Wayland), Windows ve Android arasındaki ağ keşfi, Linux sanal fare/klavye sürücüsü, çoklu monitör topolojisi ve karşılıklı PIN doğrulama güvenliğini mükemmelleştiren kapsamlı bir güncellemedir.

---

### 🌟 v1.6.2 Yenilikleri ve Düzeltmeleri:

* **🐧 Nobara Linux Ağ Keşif Dinleyicisi (UDP Discovery Receiver):**
  - Linux servis motoruna (`connectme_linux_daemon.py`) eklenen UDP 42849 dinleyicisi sayesinde ağdaki tüm Windows, Android ve diğer Linux cihazları anında ve otomatik olarak keşfedilir.
  - Yayınlanan keşif paketleri hem `255.255.255.255` hem de yerel alt ağ yayın adresine (subnet broadcast, örn. `192.168.1.255`) gönderilerek Wi-Fi / LAN ayrımı olmadan cihazların anında listede görünmesi sağlandı.

* **🖱️ 4 Kademeli Çekirdek (Kernel) Sanal Girdi Enjektörü & Kesintisiz Fare Geçişi:**
  - **Tier 1 (evdev.UInput):** Linux çekirdek düzeyinde sanal donanım cihazı oluşturularak (<0.2ms gecikme) KDE Plasma Wayland masaüstünde farenin fiziksel bir USB fare gibi tanınması sağlandı.
  - **Tier 2 (/dev/uinput Raw ioctl):** Harici kütüphane bulunmasa dahi Python standart kütüphanesiyle doğrudan `/dev/uinput` ioctl çağrıları üzerinden donanım girdisi.
  - **Tier 3 (ydotoold Daemon Auto-Start):** `ydotoold` arka plan servisinin çalışmadığı durumlarda otomatik başlatılması ve soket üzerinden komut iletimi.
  - **Tier 4 (xdotool):** X11 / XWayland oturumları için geri uyumluluk.

* **🎯 Canlı Ekran Üstü İmleç Katmanı (Linux Wayland Cursor Overlay):**
  - Wayland oturumunda sistem imlecinin gizlendiği veya donanım imlecinin render edilmediği durumlara karşı hafif, şeffaf ve daima en üstte duran dinamik imleç takip göstergesi eklendi. Uzaktan kontrol devralındığında fare ekran üzerinde parlayarak görünür hale gelir.

* **↔️ Linux'tan Windows'a Sorunsuz İmleç Geri Dönüşü (Bi-Directional Edge Return):**
  - Windows'tan Linux ekranına geçen imlecin Linux ekranının sol/dış kenarına çarptığında takılıp kalması sorunu giderildi; Linux motoru `EDGE_RETURN` ve `PACKET_EDGE_HANDOFF` sinyalleriyle imleci ve klavyeyi Windows ana ekranına anında geri aktarır.

* **🖥️ Windows Arayüzünde Linux Çoklu Monitörlerinin Ayrı Ayrı Gösterimi:**
  - Nobara Linux'un sahip olduğu çift veya çoklu monitörler (`DP-1`, `HDMI-A-1` vb.) Windows 2D Ekran Kanvasında artık tek bir blok yerine, bağımsız alt monitör kartları ve çözünürlük rozetleriyle yan yana gösterilir.
  - İmleç hangi Linux monitöründeyse o monitör canlı olarak yeşil renkle vurgulanır.

* **🔐 Sıkı Çift Taraflı 6 Haneli PIN & Erken Otomatik Bağlantı Koruması:**
  - Bir cihaz diğerinin kodunu girdiğinde karşılıklı onay tamamlanmadan cihazın erkenden güvenilir olarak kaydedilip ikinci PIN aşamasını atlaması engellendi.
  - Artık iki taraf da birbirinin ekranındaki 6 haneli kodu doğrulamadan eşleşme tamamlanmaz.
  - Linux GUI'de karşı cihaz kodumuzu girdiğinde ekranda sarı bildirim kartı belirir ve kullanıcının karşı kodu girmesi beklenir.

* **🎨 Linux GUI PIN Giriş Alanı Kontrast Düzeltmesi:**
  - Linux koyu temasında manuel PIN giriş kutusundaki rakamların görünmez (koyu zemin üstüne koyu metin) olmasına yol açan tema hatası düzeltildi; yüksek kontrastlı, elektrik mavisi (`#38BDF8`) ve ortalanmış 14pt kalın font kullanıldı.

---

# Connect Me v1.6.1 (v1-6-1) — Android 14+ FGS Crash Düzeltmesi & İyileştirmeler

Bu acil güncelleme, özellikle **Android 14 ve Android 15 (One UI 7 / Samsung Galaxy A serisi)** cihazlarda uygulamanın açılışta çökmesine neden olan `foregroundServiceType` istisnasını düzeltir ve arka plan servislerini izole eder.

---

### 🌟 v1.6.1 Yenilikleri ve Düzeltmeleri:

* **📱 Android 14/15 Açılış Çökmesi Giderildi (FGS MediaProjection İzolasyonu):**
  - Android 14+ kurallarına göre `mediaProjection` tipindeki bir ön plan servisi, kullanıcı ekran yakalama iznini onaylamadan başlatıldığında `SecurityException` fırlatarak uygulamanın kapanmasına neden oluyordu.
  - Ana ağ ve KVM servisi (`ConnectMeService`), resmi standart olan `connectedDevice` tipine taşındı.
  - Ses aktarımı (`AudioPlaybackCapture`) ise bağımsız `AudioStreamService` içerisine izole edildi ve yalnızca kullanıcı "Kulaklığa Aktar" düğmesine basıp izin verdiğinde çalışacak şekilde ayrıştırıldı.
  - Açılış döngüsü (`onCreate`) çökme korumalı (fail-safe try-catch) bloklarla zırhlandırıldı.

---

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
