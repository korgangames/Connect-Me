# Connect Me 🌐🖱️⌨️

**Connect Me**, **Windows**, **Android** ve **Linux (Nobara — KDE Plasma Wayland)** cihazlarınızı ekran yansıtma (screen mirroring) olmadan tek bir fiziksel klavye ve fare ile kontrol etmenizi, ortak pano (Universal Clipboard) kullanmanızı ve cihazlar arasında sürükle-bırak / manyetik cep (Drop Shelf) ile kolayca eşya paylaşmanızı sağlayan hibrit (**Wi-Fi + Bluetooth**) bir ekosistem uygulamasıdır.

> 📄 **Hızlı Bağlantılar ve Belgeler:**
> - [**📥 İndirme Sayfası ve Kurulum Kılavuzu (`DOWNLOADS.md`)**](DOWNLOADS.md)
> - [**App Design Document (`docs/ADD.md` v1.3)**](docs/ADD.md)
> - [**Wire Protocol v1.3 (`protocol/WIRE_PROTOCOL.md`)**](protocol/WIRE_PROTOCOL.md)
> - [**Linux Kurulum ve Çalıştırma Kılavuzu (`linux/README.md`)**](linux/README.md)

---

## 📥 Hazır Paketleri İndir (Konsolsuz / Doğrudan Çalışan Sürümler)

Tüm paketler **bağımsız grafik arayüz (GUI)** olarak derlenmiştir; hiçbir geliştirici aracı veya konsol / cmd açılması gerekmez:

| İşletim Sistemi | Paket | Açıklama | Doğrudan İndir |
| :--- | :---: | :--- | :--- |
| 🪟 **Windows** | `.zip` | Çift tıkla çalışan bağımsız WPF GUI (.NET gömülü) | [⬇️ **ConnectMe-Windows-x64.zip**](https://github.com/korgangames/Connect-Me/releases/latest/download/ConnectMe-Windows-x64.zip) |
| 📱 **Android** | `.apk` | Doğrudan yüklenebilir APK (120Hz KVM & Pano) | [⬇️ **ConnectMe.apk**](https://github.com/korgangames/Connect-Me/releases/latest/download/ConnectMe.apk) |
| 🐧 **Linux (Nobara)** | `.tar.gz` | Masaüstü başlatıcılı ve menü entegrasyonlu GUI | [⬇️ **ConnectMe-Linux-x64.tar.gz**](https://github.com/korgangames/Connect-Me/releases/latest/download/ConnectMe-Linux-x64.tar.gz) |

> 📖 Detaylı adım adım resimli kurulum kılavuzu için [**DOWNLOADS.md**](DOWNLOADS.md) sayfasını inceleyebilirsiniz.

---

## ✨ Temel Özellikler

1. **Çoklu Monitör Sanal Masaüstü Desteği (Multi-Monitor Futureproof — Windows & Linux):**
   - Bilgisayarlar (Windows veya Nobara Linux) birden fazla fiziksel ekrana (ör. 2'li yatay, 3'lü dikey+yatay) sahip olabilir.
   - **İç Birleşim Koruması (Internal Seam Rule):** Bilgisayarın kendi fiziksel monitörleri arasında (ör. DP-1 ile HDMI-1 arasında) imleç yerel olarak serbestçe geçer. Connect Me imlece müdahale etmez. İmleç yalnızca dışa açık kenara çarptığında Android veya diğer bilgisayara geçer.
   - **Monitör Bazlı Kenetleme (`AttachedLocalMonitorId`):** 2D Ekran Haritasında Android veya Linux cihazı istediğiniz yerel monitörün kenarına yapıştırabilirsiniz.
   - **Linux KDE Plasma Wayland Entegrasyonu:** `kscreen-doctor -j`, `wlr-randr` ve `xrandr` ile Linux'taki tüm çoklu ekran düzenlerini otomatik algılar.
2. **Ekran Ayarları Tarzı 2D Sürükle-Bırak Ekran Konfigürasyonu GUI'si:**
   - Yerel bilgisayarın tüm monitörleri ve bağlı tüm cihazlar **2D Ekran Konfigürasyonu Kanvası (Display Arrangement Canvas)** üzerinde gerçek oranlarıyla kutular halinde gösterilir.
   - Yakınlaştırma (Zoom Slider + Fare Tekerleği) ve `🎯 Merkeze Sığdır` desteği ile tüm çalışma alanını rahatça yönetebilirsiniz.
3. **Çift Taraflı 6 Haneli PIN Kodu Doğrulaması (Mutual Dual-PIN Pairing):**
   - İki cihazda da program açıkken her cihaz kendi **6 haneli PIN kodunu** üretir.
   - Bağlantının aktifleşmesi için iki tarafın da karşı cihazın 6 haneli kodunu girerek bağlantıyı karşılıklı onaylaması gerekir (`Mutual Verified`).
4. **Bu Cihaza Güven ve Hatırla (Sıfır-PIN Otomatik Bağlantı):**
   - Eşleşme esnasında `⭐ Bu Cihaza Güven ve Hatırla` seçeneği işaretlendiğinde cihazlar birbirine kriptografik bir güven belirteci (`TrustToken`) atar ve bunu kalıcı diske kaydeder (`trusted_devices.json`).
   - Cihazlar aynı ağdayken programlar açık olduğu sürece **artık bir daha PIN kodu girmeye gerek kalmadan** arka planda otomatik olarak bağlanır (`TRUSTED_RECONNECT`) ve 2D ekran konfigürasyonundaki yerini korur.
   - İstenildiğinde arayüzden tek tıkla `🗑️ Bu Cihazın Güvenini Kaldır (Unut)` butonuna basılarak güven iptal edilebilir.
5. **Evrensel Pano (Universal Clipboard) & Manyetik Cep (Drop Shelf):**
   - Çift taraflı onaylanmış tüm cihazlar arasında anlık metin/görsel pano senkronizasyonu ve sürükle-bırak dosya paylaşımı.

---

## 🚀 Hızlı Başlangıç

### 1. Windows Uygulamasını Çalıştırma
```powershell
dotnet run --project "windows\ConnectMe.Windows\ConnectMe.Windows.csproj" -c Release
```
* **Çoklu Monitör Test Butonu:** Arayüzdeki **`🖥️ +Ek Monitör Testi`** butonuna basarak anında Tek Ekran, Çift Monitör (Dual) ve Üçlü Monitör (Triple - Sol Dikey Ekran) düzenlerini deneyimleyebilirsiniz.
* **Çoklu Cihaz Simülatörü ile Anında Test:** **`🧪 +Simüle Et`** butonuna basarak sanal Android Telefon, Android Tablet, Windows İş İstasyonu veya çoklu monitörlü Nobara Linux PC oluşturabilirsiniz.
* **İmleci Geri Çağırma / Ekran Kilidi:** Klavyeden **`Scroll Lock`** / **`Ctrl + Alt + L`** (acil durum için `Ctrl + Alt + Shift + Esc`) tuşlarına basarak anında ana ekrana dönebilirsiniz.

### 2. Linux (Nobara / KDE Plasma Wayland) Daemon'ı Çalıştırma
```bash
python3 linux/connectme_linux_daemon.py
```
* Detaylı Linux talimatları için [**`linux/README.md`**](linux/README.md) dosyasına göz atabilirsiniz.

### 3. Android Uygulamasını Kurma
* Her `git push` işleminde **GitHub Actions** ([`.github/workflows/build.yml`](.github/workflows/build.yml)) otomatik olarak hem Windows sürümünü hem de **`ConnectMe-Android-APK`** dosyasını derler.

### 4. Test Paketi Çalıştırma (31/31 Test)
```powershell
dotnet run --project "windows\ConnectMe.Tests\ConnectMe.Tests.csproj" -c Release
```

---
*Geliştirici: **Korgan Games***
