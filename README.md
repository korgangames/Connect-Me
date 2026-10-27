# Connect Me 🌐🖱️⌨️

**Connect Me**, **Windows**, **Android** ve **Linux (Nobara — KDE Plasma Wayland)** cihazlarınızı ekran yansıtma (screen mirroring) olmadan tek bir fiziksel klavye ve fare ile kontrol etmenizi, ortak pano (Universal Clipboard) kullanmanızı ve cihazlar arasında sürükle-bırak / manyetik cep (Drop Shelf) ile kolayca eşya paylaşmanızı sağlayan hibrit (**Wi-Fi + Bluetooth**) bir ekosistem uygulamasıdır.

> 📄 **Hızlı Bağlantılar ve Belgeler:**
> - [**📥 İndirme Sayfası ve Kurulum Kılavuzu (`DOWNLOADS.md`)**](DOWNLOADS.md)
> - [**App Design Document (`docs/ADD.md` v1.3)**](docs/ADD.md)
> - [**Wire Protocol v1.3 (`protocol/WIRE_PROTOCOL.md`)**](protocol/WIRE_PROTOCOL.md)
> - [**Linux Kurulum ve Çalıştırma Kılavuzu (`linux/README.md`)**](linux/README.md)

---

### 📥 Hazır Paketleri İndir (Konsolsuz / Doğrudan Çalışan Sürümler)

Tüm paketler **bağımsız grafik arayüz (GUI)** olarak derlenmiştir; hiçbir geliştirici aracı veya konsol / cmd açılması gerekmez:

| İşletim Sistemi | Paket | Dosya Adı | Doğrudan İndir |
| :--- | :---: | :--- | :--- |
| 🪟 **Windows** | `.zip` | `ConnectMe-Windows-x64-v1.8.17.zip` | [⬇️ **ConnectMe-Windows-x64-v1.8.17.zip**](https://github.com/korgangames/Connect-Me/releases/download/v1.8.17/ConnectMe-Windows-x64-v1.8.17.zip) |
| 📱 **Android** | `.apk` | `ConnectMe-v1.8.17.apk` | [⬇️ **ConnectMe-v1.8.17.apk**](https://github.com/korgangames/Connect-Me/releases/download/v1.8.17/ConnectMe-v1.8.17.apk) |
| 🐧 **Linux (Nobara)** | `.tar.gz` | `ConnectMe-Linux-x64-v1.8.17.tar.gz` | [⬇️ **ConnectMe-Linux-x64-v1.8.17.tar.gz**](https://github.com/korgangames/Connect-Me/releases/download/v1.8.17/ConnectMe-Linux-x64-v1.8.17.tar.gz) |

> 📦 **GitHub Sürüm Sayfası:** [**Connect Me v1.8.17 Release Sayfası**](https://github.com/korgangames/Connect-Me/releases/tag/v1.8.17)  
> 📖 Detaylı adım adım resimli kurulum kılavuzu için [**DOWNLOADS.md**](DOWNLOADS.md) sayfasını inceleyebilirsiniz.

---

## ✨ Temel Özellikler

1. **🐧 Linux Ana Makine Modu (Linux as Host KVM):**
   - Nobara / KDE Plasma Linux bilgisayarınızı birincil ana makine yapabilir; fiziksel farenizi ve klavyenizi Android telefonunuza, tabletinize veya Windows PC'nize kesintisiz aktarabilirsiniz.
   - **Doğal Kenar Duyargaları (`ScreenEdgeSentinels`):** Linux ekran kenarlarına yaklaşıldığında fare akıcı bir şekilde Android/Windows ekranına geçer.
   - **Hızlı Geçiş Butonu:** 2D Kanvasta her cihaz için **`[🎯 Fareyi Bu Cihaza Aktar]`** butonu ve `ScrollLock` / `Ctrl+Alt+Yön` kısayolları.
   - **Geri Dönüş Barı:** Üst durum çubuğundaki `[Esc / Geri Dön]` veya kenar itme direnciyle anında yerel Linux masaüstüne dönüş.
2. **🐝 Koloni Modu (Colony Network — Tek PIN ile Tüm Ağa Bağlanma):**
   - Birbirine bağlı cihazlar ağda tek bir koloni (küme) olarak algılanır.
   - Kolonideki tek bir cihazın 6 haneli kodunu girdiğinizde, diğer koloni üyeleri yeni cihazı otomatik tanır ve tek tek şifre girmeden tüm ekosistem birbirine bağlanır.
3. **✨ Tek Tıkla Kabul Etme (`PAIR_ACCEPT`):**
   - Karşı taraf sizin PIN kodunuzu girdiğinde, kod aramakla uğraşmadan ekrandaki **`[✨ Hemen Kabul Et]`** butonuna basarak eşleşmeyi anında tamamlayabilirsiniz.
4. **Çoklu Monitör Sanal Masaüstü Desteği (Multi-Monitor Futureproof — Windows & Linux):**
   - Bilgisayarlar (Windows veya Nobara Linux) birden fazla fiziksel ekrana (ör. 2'li yatay, 3'lü dikey+yatay) sahip olabilir.
   - **İç Birleşim Koruması (Internal Seam Rule):** Bilgisayarın kendi fiziksel monitörleri arasında (ör. DP-1 ile HDMI-1 arasında) imleç yerel olarak serbestçe geçer. Connect Me imlece müdahale etmez. İmleç yalnızca dışa açık kenara çarptığında Android veya diğer bilgisayara geçer.
   - **Linux KDE Plasma Wayland Entegrasyonu:** `kscreen-doctor -j`, `wlr-randr` ve `xrandr` ile Linux'taki tüm çoklu ekran düzenlerini otomatik algılar.
5. **Ekran Ayarları Tarzı İnteraktif 2D Ekran Konfigürasyonu & Dinamik Kontakt Noktaları:**
   - Hem Windows hem de Linux arayüzünde bağlı cihazlar **2D Kanvas** üzerinde gerçek oranlarıyla kutular halinde gösterilir.
   - **Tek Kenara Çoklu Cihaz (Kontakt Noktaları):** Bir monitör kenarına birden fazla cihaz bağlandığında (ör. sağ kenara hem telefon hem dizüstü) kenar otomatik olarak orantılı dilimlere bölünür (`%0-%50`, `%50-%100`).
   - Fiziksel monitörün ilgili kenarında aktif geçiş aralığını gösteren neon parlayan portal çizgileri ve kontakt noktası rozetleri yer alır.
   - Windows ve Linux arayüzlerinde birebir tutarlı araç çubukları: Yakınlaştırma (Zoom) slider'ı, fare tekeri ile zoom, **`[🎯 Merkeze Sığdır]`**, **`[📐 Kenarlara Dağıt]`**, **`[🚀 Seçili Ekrana Şimdi Geç]`** ve alttaki hızlı yönlendirme butonları (`[ ⬅️ Sol ] [ ⬆️ Üst ] [ ⬇️ Alt ] [ Sağ ➡️ ]`).
6. **Çift Taraflı 6 Haneli PIN & Sıfır-PIN Otomatik Bağlantı:**
   - İki cihaz da birbirini onaylar; `⭐ Bu Cihaza Güven ve Hatırla` seçildiğinde bir daha şifre sormadan otomatik bağlanır.
7. **Evrensel Pano (Universal Clipboard) & Manyetik Cep (Drop Shelf):**
   - Çift taraflı onaylanmış tüm cihazlar arasında anlık metin/pano senkronizasyonu ve sürükle-bırak TCP dosya paylaşımı.
8. **🎧 Çift Yönlü Ses Yönlendirme Matrisi (Audio Matrix Hub):**
   - Windows WASAPI loopback yakalama motoru (32-bit float, 16/24/32-bit PCM ve USB DAC tam uyumu) ile masaüstü sesleri, Android cihazın ayrık düşük gecikmeli (<40ms) donanım ses kuyruğuna kusursuz iletilir.
   - Cihazlar ağdaki tüm ses çıkışlarını dinamik olarak keşfeder ve arayüzdeki "Ses Çıkışını Seç" menüsünden istenen cihaza anında yönlendirilebilir.
9. **🔄 Dahili Uygulama İçi Otomatik Güncelleyici (In-Program Auto Updater):**
   - Android, Windows ve Linux ortamlarında tek tıkla kendini güncelleme ve yeniden başlatma.

---

## 🚀 Hızlı Başlangıç

### 1. Windows Uygulamasını Çalıştırma
```powershell
dotnet run --project "windows\ConnectMe.Windows\ConnectMe.Windows.csproj" -c Release
```
* **Çoklu Monitör Test Butonu:** Arayüzdeki **`🖥️ +Ek Monitör Testi`** butonuna basarak anında Tek Ekran, Çift Monitör (Dual) ve Üçlü Monitör (Triple - Sol Dikey Ekran) düzenlerini deneyimleyebilirsiniz.
* **İmleci Geri Çağırma / Ekran Kilidi:** Klavyeden **`Scroll Lock`** / **`Ctrl + Alt + L`** (acil durum için `Ctrl + Alt + Shift + Esc`) tuşlarına basarak anında ana ekrana dönebilirsiniz.

### 2. Linux (Nobara / KDE Plasma Wayland) Başlatma
```bash
./linux/start-connectme.sh
# veya grafik başlatıcı:
python3 linux/connectme_linux_gui.py
```
* Detaylı Linux talimatları için [**`linux/README.md`**](linux/README.md) dosyasına göz atabilirsiniz.

### 3. Android Uygulamasını Kurma
* Her `git push` işleminde **GitHub Actions** ([`.github/workflows/build.yml`](.github/workflows/build.yml)) otomatik olarak hem Windows sürümünü hem de **`ConnectMe-v1.8.17.apk`** dosyasını derler.

### 4. Test Paketi Çalıştırma (60/60 Test — %100 Başarı)
```powershell
dotnet run --project "windows\ConnectMe.Tests\ConnectMe.Tests.csproj" -c Release
```

---
*Geliştirici: **Korgan Games***
