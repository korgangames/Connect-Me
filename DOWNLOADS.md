# 📥 Connect Me — İndir ve Kurulum Kılavuzu

> [!TIP]
> **Konsol / Komut Satırı Gerektirmez:** Tüm paketler, son kullanıcı için özel olarak paketlenmiş **bağımsız grafik arayüz (GUI)** uygulamalarıdır. `cmd`, `PowerShell` veya Linux terminali açmanıza gerek yoktur; doğrudan çift tıklayarak çalışır.

---

## 🚀 Hızlı İndirme Bağlantıları (En Son Sürüm — v1.3.0)

| İşletim Sistemi | Paket Formatı | Boyut & Tür | İndirme Bağlantısı |
| :--- | :---: | :---: | :--- |
| 🪟 **Windows** (10 / 11 64-bit) | `.zip` (`ConnectMe.exe`) | Bağımsız Tek Dosya (Self-Contained GUI) | [⬇️ ConnectMe-Windows-x64.zip](https://github.com/korgangames/Connect-Me/releases/latest/download/ConnectMe-Windows-x64.zip) |
| 📱 **Android** (8.0 ve üzeri) | `.apk` | Doğrudan Kurulabilir APK | [⬇️ ConnectMe.apk](https://github.com/korgangames/Connect-Me/releases/latest/download/ConnectMe.apk) |
| 🐧 **Linux** (Nobara / KDE Plasma) | `.tar.gz` | Masaüstü Başlatıcılı GUI Paketi | [⬇️ ConnectMe-Linux-x64.tar.gz](https://github.com/korgangames/Connect-Me/releases/latest/download/ConnectMe-Linux-x64.tar.gz) |

> 📦 **Tüm Sürümler ve Kaynak Kodları:** [GitHub Releases Sayfası](https://github.com/korgangames/Connect-Me/releases)

---

## 🪟 Windows Kurulumu ve Çalıştırma

Windows paketi, .NET 10 çalışma zamanını kendi içerisinde barındıran **bağımsız (self-contained single-file)** bir masaüstü uygulamasıdır. Bilgisayarınızda hiçbir geliştirici aracı veya ek kütüphane kurulu olması gerekmez.

### Adım Adım:
1. [ConnectMe-Windows-x64.zip](https://github.com/korgangames/Connect-Me/releases/latest/download/ConnectMe-Windows-x64.zip) dosyasını indirin.
2. İndirdiğiniz `.zip` dosyasına sağ tıklayıp **"Tümünü Ayıkla..." (Extract All)** seçeneğini seçin.
3. Klasör içindeki **`ConnectMe.exe`** dosyasına çift tıklayın.
4. **Siyah konsol (cmd) penceresi AÇILMAZ.** Doğrudan koyu temalı, modern ekran yerleşim ve kontrol arayüzü ekrana gelir.

> [!NOTE]
> Windows Defender "Bilinmeyen Yayıncı" uyarısı verirse: Uygulama açık kaynaklı ve yerel olarak derlendiği için *"Ek bilgi"* butonuna tıklayıp *"Yine de çalıştır"* demeniz yeterlidir.

---

## 📱 Android Kurulumu ve Çalıştırma

Android paketi, 120Hz donanım hızlandırmalı şeffaf imleç bindirmesi (*overlay*), pano senkronizasyonu ve Ortak Cep dosya paylaşımını tek bir APK içerisinde sunar.

### Adım Adım:
1. Telefonunuzun veya tabletinizin tarayıcısından [ConnectMe.apk](https://github.com/korgangames/Connect-Me/releases/latest/download/ConnectMe.apk) bağlantısına dokunup indirin.
2. İndirme tamamlandığında bildirim çubuğundaki bildirime veya Dosyalar (*Files*) uygulamasındaki APK dosyasına dokunun ve **"Yükle"** butonuna basın.
   *(Gerekirse tarayıcınız için "Bilinmeyen uygulamaları yükle" iznine onay verin).*
3. Uygulamayı açtığınızda ana ekranda 2 adet izin butonu göreceksiniz:
   * **1. İzin (Diğer Uygulamaların Üzerinde Gösterme):** Fare imlecinin ekranınızda serbestçe gezebilmesi için gereklidir.
   * **2. İzin (Erişilebilirlik Servisi):** Bilgisayardan gelen fare tıklamalarını, kaydırmaları ve klavye girişlerini ekrana aktarmak için gereklidir.
4. İki izni de açtıktan sonra Android cihazınız eşleşmeye hazırdır. Üst kısımda 6 haneli kodunuzu görebilirsiniz.

---

## 🐧 Linux (Nobara / Fedora / KDE Plasma / Wayland) Kurulumu

Linux paketi, **Nobara Linux** ve **KDE Plasma Wayland** ortamında çoklu monitörleri otomatik algılayan, sıfır harici pip kütüphanesi gerektiren ve **kesinlikle terminal penceresi açmadan** masaüstünden çalışan grafik kontrol panelini içerir.

### Adım Adım:
1. [ConnectMe-Linux-x64.tar.gz](https://github.com/korgangames/Connect-Me/releases/latest/download/ConnectMe-Linux-x64.tar.gz) arşivini indirin ve sağ tıklayıp ayıklayın.
2. Klasör içindeki:
   * **Doğrudan Başlatmak İçin:** **`start-connectme.sh`** dosyasına çift tıklayın. Hiçbir konsol penceresi açılmaz; doğrudan Connect Me Linux Grafik Kontrol Paneli açılır.
   * **KDE / Başlat Menüsüne Eklemek İçin:** Klasördeki **`install-desktop.sh`** betiğine bir kez tıklayın. Connect Me anında Nobara / KDE Plasma başlat menünüze eklenir. Artık sistem menünüzden "Connect Me" aratarak tek tıkla başlatabilirsiniz.

---

## ❓ Sıkça Sorulan Sorular (SSS)

#### 1. Konsol veya Komut Satırı (CMD / Terminal) açmam gerekecek mi?
**Hayır.** Tüm sürümler (Windows, Android, Linux) tamamen grafik arayüzlü (GUI) olarak tasarlanmıştır. Çift tıkladığınızda veya dokunduğunuzda doğrudan pencere açılır.

#### 2. İnternet bağlantısı gerekli mi?
**Hayır.** Connect Me tamamen evinizdeki veya ofisinizdeki yerel ağ (Wi-Fi, Bluetooth veya yerel Ethernet) üzerinden çalışır. Hiçbir veriniz internete veya harici sunuculara gitmez.

#### 3. Cihazları nasıl birbirine bağlarım?
1. İki cihazınızda da Connect Me uygulamasını açın.
2. Her iki ekranda da o cihaza özel 6 haneli bir PIN kodu göreceksiniz.
3. Bir cihazdan diğerinin 6 haneli kodunu girip **"Doğrula ve Bağlan"** butonuna basın.
4. Karşı cihazda onay bildirimi çıktığında o da sizin kodunuzu girer; çift taraflı eşleşme tamamlanır.
5. *"⭐ Bu Cihaza Güven (Hatırla)"* seçeneğini işaretlerseniz, programlar açık olduğu sürece cihazlar sonraki seferlerde otomatik olarak bağlanır.

#### 4. Dosyaları nasıl paylaşırım?
Windows üzerinde bir dosyayı penceredeki Ortak Cep (Drop Shelf) alanına sürükleyip bırakın veya Android / Linux üzerindeki "Dosya Gönder" butonunu kullanın. Dosya anında bağlı diğer cihaza aktarılır.
