# Connect Me v1.4.0 (v1-4-0) — Bağımsız Çalıştırılabilir Sürümler (Windows, Android & Linux)

Connect Me'nin tüm işletim sistemleri için son kullanıcıya hazır, çoklu ağ yönlendirme ve tek tıkla log paylaşım özellikleri içeren bağımsız ve versiyonlanmış paketleri yayınlandı!

### 🌟 v1.4.0 Yenilikleri ve Öne Çıkanlar:

* **📋 Tek Tıkla Log Dışa Aktarma & Paylaşım (Tüm Platformlarda):**
  - **Android:** "Logları Dışa Aktar & Paylaş" butonu eklendi. Cihaz bilgisi, KVM girdi durumu, PIN kayıtları ve tanılama logları içeren `ConnectMe-Android-Logs-*.txt` raporu oluşturulur. Android sistem paylaşım menüsü (Share Sheet — WhatsApp, Drive, Gmail vb.) açılarak veya panoya kopyalanarak sorunlar anında incelenebilir.
  - **Windows:** "Logları Dışa Aktar" butonu ile ayrıntılı sistem tanı raporu doğrudan kaydedilebilir, `ConnectMe.log` dosyası veya logs klasörü tek tıkla açılabilir.
  - **Linux:** GUI kontrol panelinden tanılama günlüğü dışa aktarma ve `~/.config/connectme/` klasörüne doğrudan erişim sağlandı.

* **🌐 Çoklu Ağ Kartı (Multi-NIC) Alt Ağ Otomatik Yönlendirme (Subnet-Bound Socket Routing):**
  - Bilgisayarda hem Ethernet (kurumsal / sanal ağ / VPN) hem de Wi-Fi adaptörleri aktif olduğunda Windows varsayılan yönlendirmesinin yanlış ağ kartını seçmesi sonucu ortaya çıkan *"Hedef makine etkin olarak reddettiğinden bağlantı kurulamadı"* (WSAECONNREFUSED) sorunu çözüldü.
  - Artık hedef cihazın IP adresi (örn. `192.168.1.106`) yerel ağ adaptörlerinin alt ağları (`192.168.1.x`) ile eşleştirilir ve TCP soketi doğrudan doğru yerel adaptöre (`192.168.1.104`) bağlanarak hedefe iletilir.

* **🔍 Ayrıntılı PIN & Bağlantı Denetim Günlükleri:**
  - PIN doğrulama sürecinde soketin bağlandığı yerel IP, uzak IP, yerel PIN ve karşı PIN onay adımları milisaniye hassasiyetiyle log paneline yansıtılır.

* **🏷️ Görünür Sürüm Bilgisi (v1.4.0 / v1-4-0):**
  - Windows: Pencere başlığı, üst çubuk rozeti ve alt durum satırında `v1.4.0 (v1-4-0)` sürüm göstergesi.
  - Android: Uygulama başlığı, ana sayfa başlığı ve IP/durum bilgisinde `v1.4.0 (v1-4-0)` göstergesi.
  - Linux: Pencere kenarlığı/başlığı, üst başlık ve çevrimiçi rozetinde `v1.4.0` sürüm göstergesi.

* **📦 Sadece Versiyonlu Paketler:**
  - GitHub Releases üzerinde sadece açıkça versiyonlanmış paketler yer alır; çifte/isimsiz paket karmaşası engellenmiştir.

---

### 📦 İndirilebilir Paketler:
1. **Windows:** `ConnectMe-Windows-x64v1-4-0.zip` — Çıkartın ve `ConnectMe.exe`'ye çift tıklayın (.NET gerektirmez, standalone).
2. **Android:** `ConnectMeV1-4-0.apk` — Doğrudan cihaza kurun.
3. **Linux:** `ConnectMe-Linux-x64v1-4-0.tar.gz` — Arşivi açın, `start-connectme.sh` ile başlatın veya `install-desktop.sh` ile menüye ekleyin.
