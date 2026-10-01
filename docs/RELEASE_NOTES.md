# Connect Me v1.4.1 (v1-4-1) — Bağımsız Çalıştırılabilir Sürümler (Windows, Android & Linux)

Connect Me'nin çoklu ağ kartı UDP yönlendirme, Android yerel erişilebilirlik imleci (TYPE_ACCESSIBILITY_OVERLAY), Windows fare merkezleme ve kilitlenme önleme düzeltmelerini içeren yeni kararlı sürümü yayınlandı!

### 🌟 v1.4.1 Yenilikleri ve Hata Düzeltmeleri:

* **🚀 Çoklu Ağ Kartı (Multi-NIC) UDP Otomatik Yönlendirme (Subnet-Bound UDP Routing):**
  - Windows bilgisayarda Ethernet (kurumsal VPN / dock) ve Wi-Fi kartı aynı anda aktifken, UDP girdi ve kenar geçiş paketlerinin yanlış ağ kartından (Ethernet) çıkması ve Android cihaza ulaşamaması sorunu çözüldü.
  - Artık hedef cihazın IP adresi (örn. `192.168.1.106`) yerel ağ adaptörlerinin alt ağıyla (`192.168.1.104`) eşleştirilir ve UDP paketleri doğrudan doğru ağ kartına (`Bind(localSubnetIp, InputUdpPort)`) bağlanarak telefona ulaştırılır.

* **🖱️ Kesintisiz ve Sınırsız Windows Fare Kontrolü (Mouse Centering & Re-anchoring):**
  - İmleç diğer ekrana (Android / Linux) geçtiğinde Windows tarafında ekranın kenarında sıkışıp kalma ("mouse dondu") ve kullanıcıyı `Ctrl + Shift + Alt + Esc` tuşuna zorlama sorunu giderildi.
  - Uzak cihaz kontrol edilirken Windows yerel imleci ana ekranın merkezine çekilir (`SetCursorPos`) ve her hareket adımında merkezlenerek kullanıcının sağa, sola, yukarı ve aşağı 360 derece sınırsız ve pürüzsüz hareket etmesi sağlandı.

* **📱 Android TYPE_ACCESSIBILITY_OVERLAY ile Kısıtlanmamış İmleç:**
  - Android ve Samsung (One UI) cihazlarda dışarıdan yüklenen uygulamalar için "Kısıtlanmış Ayar" sebebiyle "Diğer uygulamaların üzerinde gösterme" (`SYSTEM_ALERT_WINDOW`) izninin açılamaması durumunda bile, Erişilebilirlik Servisi aktif olduğunda imlecin doğrudan ekranda belirmesini sağlayan `TYPE_ACCESSIBILITY_OVERLAY` desteği eklendi.
  - Android alt kenar dönüş hesaplamasındaki X/Y delta karışıklığı düzeltildi.

* **🔄 İki Yönlü Pürüzsüz Kenar Geçişi:**
  - Telefondan bilgisayara dönüşte paketlerin doğrudan Windows'un dinlediği UDP portuna (`42850`) yedekli olarak iletilmesi sağlandı.

---

### 📦 İndirilebilir Paketler:
1. **Windows:** `ConnectMe-Windows-x64v1-4-1.zip` — Çıkartın ve `ConnectMe.exe`'ye çift tıklayın (.NET gerektirmez, standalone).
2. **Android:** `ConnectMeV1-4-1.apk` — Doğrudan cihaza kurun.
3. **Linux:** `ConnectMe-Linux-x64v1-4-1.tar.gz` — Arşivi açın, `start-connectme.sh` ile başlatın veya `install-desktop.sh` ile menüye ekleyin.
