# Connect Me v1.4.3 (v1-4-3) — Touchpad Çoklu Parmak Hareketleri & Canlı İmleç Konum Göstergesi

Connect Me'nin Windows touchpad çoklu parmak hareketlerini (3 ve 4 parmak kaydırmaları) odaktaki cihaza yönlendiren, ana makinede araya giren sistem hareketlerini bastıran ve arayüzde farenin hangi cihazda ve neresinde olduğunu gösteren canlı imleç radarını içeren yeni kararlı sürümü yayınlandı!

---

### 🌟 v1.4.3 Yenilikleri ve Geliştirmeleri:

* **👆 Windows Touchpad Çoklu Parmak Hareketlerinin Odaktaki Cihaza Yönlendirilmesi (Touchpad Gesture Routing):**
  - **Ana Makine Koruması (Modifier & Gesture Suppression):** Windows Precision Touchpad üzerinde yapılan 3 ve 4 parmak hareketleri normalde Windows işletim sistemine `Win+Tab`, `Alt+Tab`, `Win+D` ve `Ctrl+Win+Left/Right` komutları üretir. Önceki sürümlerde niteleyici tuşlar (`Win`, `Alt`, `Ctrl`) Windows'a sızdığı için ana bilgisayarda pencereler veya masaüstleri değişiyordu. v1.4.3 ile fare başka bir cihazı yönetirken tüm tuş vuruşları ve niteleyiciler Windows kanca katmanında tamamen yutulur (`(IntPtr)1`) ve doğrudan odaktaki cihaza aktarılır.
  - **Android Üzerinde Kusursuz Eşleme:**
    - **3 Parmak Yukarı / Alt+Tab / Win+Tab:** Android'in Son Uygulamalar ekranını (`GLOBAL_ACTION_RECENTS`) açar.
    - **3 Parmak Aşağı / Win+D / Win+H:** Android Ana Ekranına (`GLOBAL_ACTION_HOME`) döner.
    - **4 Parmak Sola/Sağa / Ctrl+Win+Sol/Sağ:** Android ana ekran sayfaları ve açık uygulamalar arasında tam ekran yatay kaydırma yapar (sanal masaüstü mantığı).
    - **Win+A / Win+N:** Android Hızlı Ayarlar ve Bildirim Çekmecesini (`GLOBAL_ACTION_NOTIFICATIONS`) indirir.
    - **2 Parmak Yatay Kaydırma (Horizontal Touchpad Scroll):** İki parmakla yana kaydırıldığında Android ekranında sayfalar veya listeler yatay olarak kaydırılır.
    - **Windows'a Temiz Dönüş:** Ana ekrana geri dönüldüğünde basılı kalma ihtimali olan tüm tuşlar `ReleaseHeldModifiers` ile otomatik olarak serbest bırakılır.

* **🎯 2D Haritada Canlı İmleç Konum ve Cihaz Göstergesi (Real-Time Mouse Location Indicator & Reticle):**
  - Çoklu cihaz KVM kullanımında en büyük ihtiyaç olan *"Fare şu an hangi cihazda ve neresinde?"* sorusu tamamen görselleştirildi.
  - **Canlı İmleç Radarı:** Farenin odakta olduğu cihaz kutusu üzerinde farenin ekran koordinatlarına `(X, Y)` göre canlı yeşil hedef imleci (target reticle) hareket eder.
  - **Görsel Odak Rozetleri:** Odaktaki cihaz `🎯 [İMLEÇ BURADA]` ve canlı piksel koordinatlarıyla vurgulanır. Bilgisayara dönüldüğünde ana ekran kutusu `🎯 [İMLEÇ YERELDE]` olarak güncellenir.
  - **Üst Durum Çubuğu:** Üst çubukta odaklanan cihaz ve o anki tahmini ekran konumu gösterilir.
  - **Sıfır CPU Yükü:** İmleç takip mekanizması yalnızca fare uzak cihazdayken 70ms aralıklarla çalışır, yerel bilgisayara dönüldüğünde otomatik durur.

---

### 📦 İndirilebilir Paketler (Yalnızca Sürümlü Dosya Adları):
1. **Windows:** `ConnectMe-Windows-x64v1-4-3.zip` — Çıkartın ve `ConnectMe.exe`'ye çift tıklayın (.NET gerektirmez, standalone).
2. **Android:** `ConnectMeV1-4-3.apk` — Doğrudan Android cihazınıza kurun.
3. **Linux:** `ConnectMe-Linux-x64v1-4-3.tar.gz` — Nobara / KDE Plasma için bağımsız paket (`start-connectme.sh` veya `install-desktop.sh`).
