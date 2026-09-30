# Connect Me 🌐🖱️⌨️

**Connect Me**, **Windows**, **Android** ve **Linux (Nobara — KDE Plasma Wayland)** cihazlarınızı ekran yansıtma (screen mirroring) olmadan tek bir fiziksel klavye ve fare ile kontrol etmenizi, ortak pano (Universal Clipboard) kullanmanızı ve cihazlar arasında sürükle-bırak / manyetik cep (Drop Shelf) ile kolayca eşya paylaşmanızı sağlayan hibrit (**Wi-Fi + Bluetooth**) bir ekosistem uygulamasıdır.

> 📄 **Mimari ve Protokol Dokümanları:**
> - [**App Design Document (`docs/ADD.md`)**](docs/ADD.md)
> - [**Wire Protocol v1 (`protocol/WIRE_PROTOCOL.md`)**](protocol/WIRE_PROTOCOL.md)

---

## 🚀 Hızlı Başlangıç (Faz 1 & 2 — Windows ↔ Android)

### 1. Windows Uygulamasını Çalıştırma
```powershell
dotnet run --project "windows\ConnectMe.Windows\ConnectMe.Windows.csproj" -c Release
```
* **Simülatör ile Anında Test:** Henüz telefon bağlamadan bile arayüzdeki **🧪 Simülatör Ekle** butonuna tıklayıp farenizi Windows ekranının sağ kenarına iterek imleç geçişini ve `<1.5ms` UDP paket akışını canlı test edebilirsiniz.
* **İmleci Geri Çağırma / Ekran Kilidi:** İmleç Android ekranındayken ekranın kenarından geri çekebilir veya klavyeden **`Scroll Lock`** / **`Ctrl + Alt + L`** (acil durum için `Ctrl + Alt + Shift + Esc`) tuşlarına basarak anında Windows'a dönebilirsiniz.

### 2. Android Uygulamasını Kurma
* Her `git push` işleminde **GitHub Actions** ([`.github/workflows/build.yml`](.github/workflows/build.yml)) otomatik olarak hem Windows sürümünü hem de **`ConnectMe-Android-APK`** dosyasını derler.
* Android uygulamasını açtığınızda:
  1. **1. İzin (120Hz Overlay İmleç):** *"Diğer Uygulamaların Üzerinde Göster"* iznini verin.
  2. **2. İzin (Tıklama & Klavye Köprüsü):** *"Erişilebilirlik Servisleri -> Connect Me"* servisini aktif edin.
  3. Aynı Wi-Fi ağındayken Windows bilgisayarınız otomatik bulunur (veya Windows ekranında yazan yerel IP adresini girebilirsiniz).

### 3. Test Paketi Çalıştırma
```powershell
dotnet run --project "windows\ConnectMe.Tests\ConnectMe.Tests.csproj" -c Release
```

---
*Geliştirici: **Korgan Games***
