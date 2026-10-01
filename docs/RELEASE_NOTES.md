# Connect Me v1.4.2 (v1-4-2) — Kesintisiz Kenar Dönüşü & Butonsuz Canlı Evrensel Pano

Connect Me'nin telefondan bilgisayara anında geri dönüş (Immediate Edge Return), garantili çift kanallı el sıkışma (Dual UDP + TCP Handoff) ve tamamen butonsuz, kesintisiz canlı Evrensel Pano (Seamless Universal Clipboard) yeniliklerini içeren yeni kararlı sürümü yayınlandı!

### 🌟 v1.4.2 Yenilikleri ve Geliştirmeleri:

* **🖱️ Android'den Bilgisayara Kesintisiz ve Anında Dönüş (Immediate Multi-Border Edge Return):**
  - Farenin Android ekranına geçtikten sonra bilgisayara geri dönememesi ("mouse telefonda kilitli kaldı") sorunu tamamen çözüldü.
  - Önceki sürümlerdeki yüksek direnç (22px) ve ara fare paketlerindeki mikroskobik duraklamalarda direnç sayacının sıfırlanması sorunu giderildi; bilgisayara bakan kenarda direnç **3 piksele** indirilerek farenin bilgisayara yağ gibi akması sağlandı.
  - Kullanıcı hangi kenardan girmiş olursa olsun, telefonun herhangi bir dış kenarından dışa doğru itildiğinde (sağ, sol, üst, alt) bilgisayara dönüşü sağlayan çoklu kenar algılayıcı eklendi.
  - **İkili Handoff Güvencesi (Dual UDP + TCP Fallback):** Wi-Fi paket kayıplarının fareyi telefonda hapsetmesini önlemek için, hem anlık 3x UDP patlaması hem de garantili TCP `EDGE_RETURN` kontrol sinyali eşzamanlı gönderilir.
  - Android bildirim çekmecesine ve uygulama ekranına tek dokunuşla fareyi bilgisayara iade eden `"⬅️ PC'ye Dön"` aksiyonu eklendi.

* **📋 Butonsuz & Kesintisiz Canlı Evrensel Pano (Seamless Universal Clipboard):**
  - Kullanıcının artık panoyu paylaşmak için hiçbir butona ("Panoyu tüm onaylı cihazlara gönder") basmasına gerek yoktur!
  - İster Windows bilgisayarınızda bir metin kopyalayın (`Ctrl + C` veya Sağ Tık -> Kopyala), ister Android telefonunuzda bir metin seçip kopyalayın; metin arka planda otomatik olarak algılanır ve tüm onaylı cihazların panosuna anında yapıştırılmaya hazır hale getirilir.
  - Android tarafında `CursorAccessibilityService` ve `ConnectMeService` arka plan servislerine yerel `OnPrimaryClipChangedListener` entegre edildi.
  - Çift taraflı SHA-256 hash doğrulama mekanizması sayesinde kendi gönderdiği panoyu tekrar geri alma döngüsü (echo loop) tamamen engellendi.

---

### 📦 İndirilebilir Paketler (Yalnızca Sürümlü Dosya Adları):
1. **Windows:** `ConnectMe-Windows-x64v1-4-2.zip` — Çıkartın ve `ConnectMe.exe`'ye çift tıklayın (.NET gerektirmez, standalone).
2. **Android:** `ConnectMeV1-4-2.apk` — Doğrudan Android cihazınıza kurun.
3. **Linux:** `ConnectMe-Linux-x64v1-4-2.tar.gz` — Nobara / KDE Plasma için bağımsız paket (`start-connectme.sh` veya `install-desktop.sh`).
