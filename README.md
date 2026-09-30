# Connect Me 🌐🖱️⌨️

**Connect Me**, **Windows**, **Android** ve **Linux (Nobara — KDE Plasma Wayland)** cihazlarınızı ekran yansıtma (screen mirroring) olmadan tek bir fiziksel klavye ve fare ile kontrol etmenizi, ortak pano (Universal Clipboard) kullanmanızı ve cihazlar arasında sürükle-bırak / manyetik cep (Drop Shelf) ile kolayca eşya paylaşmanızı sağlayan hibrit (**Wi-Fi + Bluetooth**) bir ekosistem uygulamasıdır.

> 📄 **Mimari ve Protokol Dokümanları:**
> - [**App Design Document (`docs/ADD.md` v1.2)**](docs/ADD.md)
> - [**Wire Protocol v1.2 (`protocol/WIRE_PROTOCOL.md`)**](protocol/WIRE_PROTOCOL.md)

---

## ✨ Temel Özellikler

1. **Çoklu Cihaz (Multi-PC & Multi-Android) Eşzamanlı Bağlantı:**
   - Aynı anda birden fazla bilgisayar (Windows, Nobara Linux vb.) ve birden fazla Android cihaz (telefon, tablet) tek bir oturumda birbirine bağlanabilir.
2. **Ekran Ayarları Tarzı 2D Sürükle-Bırak Ekran Konfigürasyonu GUI'si:**
   - Bağlı tüm cihazlar sol panelde listelenir ve sağdaki **2D Ekran Konfigürasyonu Kanvası (Display Arrangement Canvas)** üzerinde gerçek çözünürlük oranlarıyla kutular halinde gösterilir.
   - Ekran kutularını fareyle tutup merkez ekranın **Sol**, **Sağ**, **Üst** veya **Alt** kenarına (hatta bir kenarın belirli bir kesitine, örn. sağ üst yarısına Laptop, sağ alt yarısına Android telefon) manyetik olarak yerleştirebilirsiniz!
3. **Çift Taraflı 6 Haneli PIN Kodu Doğrulaması (Mutual Dual-PIN Pairing):**
   - İki cihazda da program açıkken her cihaz kendi **6 haneli PIN kodunu** üretir.
   - Bağlantının aktifleşmesi için iki tarafın da karşı cihazın 6 haneli kodunu girerek bağlantıyı karşılıklı onaylaması gerekir (`Mutual Verified`).
4. **Evrensel Pano (Universal Clipboard) & Manyetik Cep (Drop Shelf):**
   - Çift taraflı onaylanmış tüm cihazlar arasında anlık metin/görsel pano senkronizasyonu ve sürükle-bırak dosya paylaşımı.

---

## 🚀 Hızlı Başlangıç

### 1. Windows Uygulamasını Çalıştırma
```powershell
dotnet run --project "windows\ConnectMe.Windows\ConnectMe.Windows.csproj" -c Release
```
* **Çoklu Cihaz Simülatörü ile Anında Test:** Arayüzdeki **🧪 Simülasyon Cihazı Ekle** butonuna basarak sanal Android Telefon, Android Tablet veya Nobara Linux PC oluşturabilir; çift taraflı 6 haneli PIN onayını deneyebilir ve **2D Ekran Kanvası** üzerinde ekranları sürükleyip kenarlara yerleştirerek çoklu cihaz kenar geçişini test edebilirsiniz.
* **İmleci Geri Çağırma / Ekran Kilidi:** İmleç başka bir cihazdayken ekran kenarından geri çekebilir veya klavyeden **`Scroll Lock`** / **`Ctrl + Alt + L`** (acil durum için `Ctrl + Alt + Shift + Esc`) tuşlarına basarak anında ana ekrana dönebilirsiniz.

### 2. Android Uygulamasını Kurma
* Her `git push` işleminde **GitHub Actions** ([`.github/workflows/build.yml`](.github/workflows/build.yml)) otomatik olarak hem Windows sürümünü hem de **`ConnectMe-Android-APK`** dosyasını derler.
* Android uygulamasında kendi 6 haneli PIN kodunuzu görebilir ve bağlanmak istediğiniz bilgisayarın 6 haneli PIN kodunu girerek çift taraflı eşleşmeyi tamamlayabilirsiniz.

### 3. Test Paketi Çalıştırma
```powershell
dotnet run --project "windows\ConnectMe.Tests\ConnectMe.Tests.csproj" -c Release
```

---
*Geliştirici: **Korgan Games***
