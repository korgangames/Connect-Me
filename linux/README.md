# Connect Me — Linux (Nobara / KDE Plasma Wayland) Kurulum ve Kullanım Kılavuzu

Connect Me, **Nobara Linux** (KDE Plasma 5 & 6 Wayland) ve tüm modern Linux masaüstü ortamlarında çoklu monitörlü sistemleri, donanım düzeyinde sanal girdi enjeksiyonunu, çift yönlü panoyu ve Ortak Cep dosya paylaşımını yerel olarak destekler.

---

## 🌟 Temel Yetenekler (Linux Entegrasyonu)

1. **2D Grafiksel Ekran Konfigürasyonu (GUI):**
   - Konsol / terminal gerektirmeyen tam donanımlı Tkinter masaüstü kontrol merkezi (`connectme_linux_gui.py`).
   - Bağlı tüm yerel monitörleri (`DP-1`, `HDMI-A-1`) ve kenarlara kenetlenmiş Windows/Android cihazları görsel 2D kanvas üzerinde gösterir.
2. **KDE Plasma Wayland Otomatik Algılama (`kscreen-doctor -j`):**
   - KDE Plasma'nın yerel KScreen servisi üzerinden tüm bağlı fiziksel ekranları, sanal masaüstü koordinatlarını `(virtualX, virtualY)`, çözünürlüklerini ve ölçeklerini (`scale`) otomatik okur.
   - `wlr-randr --json` ve `xrandr --query` ile wlroots ve X11 oturumlarında da eksiksiz çalışır.
3. **İç Birleşim Çizgisi (Internal Seam) Koruması:**
   - Linux bilgisayarınızın kendi fiziksel monitörleri arasında imleç yerel olarak serbestçe hareket eder. İmleç sadece en dıştaki açık sınıra çarptığında Windows veya Android cihaza geçiş yapar.
4. **Donanım Girdi Enjeksiyonu (Linux Virtual Input):**
   - **`ydotool` (Öncelikli):** Nobara Linux Wayland / KDE Plasma oturumunda klavye ve fare komutlarını sıfır gecikmeyle işletim sistemine aktarır.
   - **`/dev/uinput`:** Çekirdek düzeyinde sanal fare ve klavye sürücüsü.
   - **`xdotool`:** X11 / XWayland oturumları için otomatik geri uyumluluk.
5. **Çift Yönlü Evrensel Pano (Universal Clipboard):**
   - Nobara Wayland'da `wl-clipboard` (`wl-copy`/`wl-paste`) veya X11'de `xclip` ile sistem panosundaki değişiklikleri yakalayıp Windows ve Android cihazlara yayınlar; karşı taraftan gelen kopyalanan metinleri doğrudan Linux panonuza yazar.
6. **Ortak Cep (Drop Shelf) TCP Dosya Akışı:**
   - Çift tıklamayla veya sürükle-bırakla seçilen dosyaları yerel ağdaki Windows veya Android cihazın Ortak Cebine doğrudan TCP üzerinden aktarır. Alınan dosyalar anında `~/Downloads/ConnectMe-Shelf` klasörüne iner ve `notify-send` ile masaüstü bildirimi verir.
7. **Çift Taraflı 6 Haneli PIN & Sıfır-PIN Otomatik Bağlantı:**
   - İlk eşleşmede karşılıklı 6 haneli PIN doğrulaması yapılır. "Bu Cihaza Güven ve Hatırla" seçeneği işaretlenirse cihazlar program açıkken şifresiz otomatik bağlanır.

---

## 🚀 Çalıştırma Yöntemleri

### Yöntem 1: Masaüstünden Doğrudan Başlatma (Tavsiye Edilen — Sıfır Konsol)
* Arşivden çıkan **`start-connectme.sh`** dosyasına çift tıklayın. Hiçbir konsol penceresi açılmaz; doğrudan Connect Me Linux Grafik Kontrol Paneli açılır.

### Yöntem 2: Nobara / KDE Başlat Menüsüne Ekleme
Klasör içindeki kurulum betiğini çalıştırın:
```bash
./install-desktop.sh
```
Bu işlem Connect Me'yi `~/.local/share/applications/ConnectMe.desktop` konumuna ekler. Artık uygulama başlatıcınızdan (Application Launcher) "Connect Me" aratarak tek tıkla başlatabilirsiniz.

### Yöntem 3: Python Komut Satırı ile Başlatma
```bash
# Grafik Arayüz ile başlatma:
python3 linux/connectme_linux_gui.py

# Yalnızca arka plan servisi (daemon) olarak çalıştırma:
python3 linux/connectme_linux_daemon.py
```

---

## 📋 Nobara / Fedora Önerilen Paketler

Nobara Linux genellikle bu paketleri kurulu olarak getirir. Eksik olması durumunda şu komutla kurabilirsiniz:

```bash
# Wayland Pano Senkronizasyonu (Çift Yönlü):
sudo dnf install wl-clipboard

# Wayland Giriş Enjektörü:
sudo dnf install ydotool

# Masaüstü Bildirimleri:
sudo dnf install libnotify
```
