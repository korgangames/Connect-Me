# Connect Me — Linux (Nobara / KDE Plasma Wayland) Kurulum ve Çalıştırma Kılavuzu

Connect Me, **Nobara Linux** (KDE Plasma 5 & 6 Wayland) ve tüm modern Linux masaüstü ortamlarında çoklu monitörlü sistemleri yerel olarak destekler.

---

## 🌟 Çoklu Monitör Özellikleri (Linux)
1. **KDE Plasma Wayland Otomatik Algılama (`kscreen-doctor -j`):**
   - KDE Plasma'nın yerel KScreen servisi üzerinden tüm bağlı fiziksel ekranları (`DP-1`, `DP-2`, `HDMI-A-1` vb.), sanal masaüstü koordinatlarını `(virtualX, virtualY)`, çözünürlüklerini ve ölçeklerini (`scale`) otomatik olarak okur.
2. **wlroots / Sway / Hyprland Desteği (`wlr-randr --json`):**
   - wlroots tabanlı Wayland yöneticilerinde ekran düzenini JSON formatında çözer.
3. **X11 / XWayland Geri Uyumluluk (`xrandr --query`):**
   - X11 ve XWayland oturumlarında `xrandr` çıktısını regex ile ayrıştırır. Negatif koordinatlı dikey veya sol ekranları tam hassasiyetle haritalar.
4. **İç Birleşim Çizgisi (Internal Seam) Koruması:**
   - Linux bilgisayarınızın kendi fiziksel monitörleri arasında (ör. DP-1 ile HDMI-1 arasında) imleç serbestçe hareket eder. İmleç sadece en dıştaki fiziksel kenara çarptığında Windows veya Android cihaza geçiş yapar.
5. **Çift Taraflı 6 Haneli PIN Doğrulaması:**
   - Karşılıklı PIN onayı tamamlanmadan hiçbir komut veya pano verisi iletilmez.

---

## 🚀 Çalıştırma

### Yöntem 1: Bağımsız Python Daemon (Nobara / KDE Plasma için hazır)
Hiçbir harici pip kütüphanesi gerektirmez (yalnızca Python 3 standart kütüphanesi).

```bash
# Servisi başlatın:
python3 linux/connectme_linux_daemon.py
```

Başlatıldığında ekranda şu çıktıyı görürsünüz:
```text
========================================================================
 Connect Me — Nobara Linux (KDE Plasma) Çoklu Monitör Servisi
 Cihaz Adı : nobara-pc (Nobara KDE) (linux-nobara-pc)
 6 Haneli Kodu : 841 923
 Algılanan Monitör Sayısı : 2 (Toplam Masaüstü: 4480x1440)
   • DP-1: 2560x1440 @ (0, 0) ölçek=1.0 [BİRİNCİL]
   • HDMI-A-1: 1920x1080 @ (2560, 0) ölçek=1.0
========================================================================
```

### Yöntem 2: .NET 10 ile Linux Üzerinde Core Kütüphanesini Çalıştırma
```bash
dotnet test windows/ConnectMe.Tests/ConnectMe.Tests.csproj
```

---

## 📋 İsteğe Bağlı Paketler (Wayland Pano & Giriş)
- **Wayland Pano Senkronizasyonu:** `sudo dnf install wl-clipboard`
- **X11 Pano Senkronizasyonu:** `sudo dnf install xclip`
