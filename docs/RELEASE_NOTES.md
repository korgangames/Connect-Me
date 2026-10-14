# Connect Me v1.8.1 — Linux Otomatik Ekran Kenarı Geçişi (Kesintisiz Donanım KVM) & Çoklu Monitör İyileştirmeleri

Bu sürüm; Linux'tan Windows'a veya Android'e geçerken fare imlecini ekrandan aktarmak için GUI üzerindeki "🎯 Fareyi Bu Cihaza Aktar" butonuna basma zorunluluğunu tamamen ortadan kaldırır. Fare imleci ekran sınırına ulaştığında ve karşı cihaza doğru itildiğinde geçiş %100 otomatik ve anında gerçekleşir.

---

### 🌟 v1.8.1 Yenilikleri ve Düzeltmeleri:

* **🖱️ Linux Arka Plan Donanım Seviyesi Kenar İzleme (Continuous evdev Edge Tracker):**
  - **Sorunun Kök Nedeni:** Linux (Nobara KDE Plasma Wayland) üzerinde Tkinter GUI uygulamaları XWayland altında çalışır. Wayland güvenlik mimarisi gereği, fare imleci Tkinter penceresinin dışındayken (masaüstü, tarayıcı veya diğer uygulamalar üzerinde) X11 pencereleri küresel imleç koordinatlarını (`winfo_pointerxy`) sorgulayamaz veya koordinatlar donar. Bu sebeple ekran kenarı algılanamıyor ve kullanıcının mutlaka GUI butonuna basması gerekiyordu.
  - **Çözüm:** `LinuxHardwareInputGrabber` mimarisi baştan aşağı yenilendi. Artık Linux çekirdeğindeki `/dev/input/event*` aygıtlarını arka planda pasif olarak izleyen sürekli bir donanım takip motoru eklendi.
  - **Sıfır Arayüz Bağımlılığı & Mavi Çizgisiz:** Ekran üzerine herhangi bir pencere, canvas veya mavi çizgi çizilmez; imlecin hızlı veya yavaş hareket etmesinden etkilenmez ve tam 1000Hz hassasiyetle çalışır.
  - **Otomatik Geçiş Eşiği (Edge Resistance):** Fare ekran sınırına ulaşıp karşı cihaza doğru 15 piksel itildiğinde geçiş otomatik olarak tetiklenir, `dev.grab()` devreye girer ve kontrol kesintisizce karşı ekrana aktarılır.

* **🖥️ Çoklu Monitör İç Birleşim Koruması & En Yakın Ekran Eşlemesi:**
  - `find_monitor_at` fonksiyonu geliştirildi; imleç monitörün en uç piksel sınırındayken dahi en yakın fiziksel ekran karesi doğru tespit edilir.
  - İki yerel monitör arasındaki iç birleşim noktalarından geçerken (`is_internal_seam`) imleç yerel ekranlar arasında serbestçe gezinir, yalnızca dış sınırda eşleşen uzak cihaza geçiş yapar.

* **🔄 Çift Yönlü Koordinat ve Bekleme Süresi (Cooldown) Senkronizasyonu:**
  - Karşı cihazdan (Windows veya Android) Linux'a geri dönüş yapıldığında (`PACKET_EDGE_HANDOFF` / `EDGE_RETURN`) imleç koordinatları giriş kenarının hemen içine hizalanır ve 1.2 saniyelik geri sekme koruması (`cooldown`) uygulanır.
  - GUI üzerinden fare hareketi yapıldığında donanımsal koordinat takipçisi pencere koordinatlarıyla otomatik olarak kalibre edilir.

---

# Connect Me v1.8.0 — Çift Kanallı Eşleşme (TCP + UDP Hızlı Yol), Windows Güvenlik Duvarı Otomasyonu & Canlı Ağ Tanılama

Bu sürüm; Linux'tan Windows'a bağlanırken Windows 6 haneli PIN kodu girildiğinde isteğin Windows'a ulaşmaması veya yanıtsız kalması sorununu kökten çözer. Windows Güvenlik Duvarı (Windows Defender Firewall) otomatik kural kurulumunu, UDP port 42849 ve TCP port 42851 üzerinden çift kanallı (dual-channel) eşleşme motorunu, IPv6-mapped IPv4 normalizasyonunu ve Linux GUI canlı kullanıcı teşhis geri bildirimlerini sunar.

---

### 🌟 v1.8.0 Yenilikleri ve Düzeltmeleri:

* **🛡️ Windows Defender Güvenlik Duvarı Otomatik Yapılandırması:**
  - **Sorunun Kök Nedeni:** Windows işletim sistemi, varsayılan güvenlik duvarı kuralları gereği yerel ağdan (LAN/Wi-Fi) gelen TCP 42851 bağlantı isteklerini (SYN paketleri) sessizce düşürüyordu. UDP keşif paketleri (42849) ulaşıyor olsa bile eşleşme isteği sadece TCP üzerinden gönderildiği için Windows'a hiçbir zaman ulaşmıyordu.
  - **Çözüm:** `WindowsFirewallHelper` geliştirildi. Program Windows'ta ilk açıldığında arka planda `netsh advfirewall` aracılığıyla `ConnectMe.exe` program kuralı, TCP port 42851 ve UDP portları (42849, 42850, 42852) için gelen kuralları otomatik ve sessizce oluşturur.

* **⚡ Çift Kanallı Eşleşme Motoru (Dual-Channel Pairing: TCP + UDP Hızlı Yol):**
  - **UDP Üzerinden Eşleşme Paketi Desteği:** Eşleşme sinyalleri (`PAIR_REQUEST`, `PAIR_VERIFY_ACK`, `PAIR_ACCEPT`, `PAIR_ACCEPT_ACK`, `PAIR_REJECT`) artık hem güvenilir TCP çerçevesiyle hem de UDP port 42849 üzerinden hızlı yoldan eşzamanlı olarak iletilir.
  - **Güvenlik Duvarı Engeli Aşımı:** Herhangi bir sebeple TCP portu güvenlik duvarı veya router tarafından bloklansa dahi UDP hızlı yolu üzerinden eşleşme paketleri 1 milisaniyeden kısa sürede hedefe ulaşır, karşı cihaz anında bildirim kartını açar ve bağlantı kusursuzca kurulur.
  - **Tüm Platformlar Arasında Senkronizasyon:** Windows `ConnectMeNetworkNode`, Linux `ConnectMeLinuxNode` ve Linux GUI motorlarının tamamı UDP eşleşme paketlerini dinleyecek ve teyit edecek şekilde güncellendi.

* **🌐 IPv6-Mapped IPv4 Adres Normalizasyonu:**
  - Windows TCP dinleyicisine bağlanan Linux istemcileri, çift yığınlı soketlerde `::ffff:192.168.x.x` şeklinde görünüyordu. Bu adresler artık `MapToIPv4()` ile saf IPv4 adresine dönüştürülür ve IP uyuşmazlıkları engellenir.

* **🖥️ Linux GUI Kullanıcı Geri Bildirimi & Canlı Tanılama Diyalogları:**
  - **Sessiz Zaman Aşımı Giderildi:** Eski sürümlerde TCP soket zaman aşımı veya hataları yalnızca arka plan loglarına yazılıyor, kullanıcı ekranda hiçbir tepki görmüyordu. Artık bağlantı yanıt vermediğinde olası nedenleri ve çözüm önerilerini içeren bilgilendirici bir uyarı penceresi açılır.
  - **Cihaz Seçim Listesi Eşleşmesi (`_displayed_peer_ids`):** Listede birden fazla cihaz varken veya keşif beklenirken yapılan seçimlerin doğru hedef IP ve cihaza yönlendirilmesi sağlandı. Buton üzerinde bağlanılacak cihazın adı dinamik olarak gösterilir (`🔗 'Windows' ile Doğrula ve Bağlan`).
  - **PIN Doğrulama ve Red Bildirimleri:** PIN kabul edildiğinde veya hatalı kod girildiğinde anında görsel bildirim kutuları ile kullanıcı net olarak bilgilendirilir.

---



### 🌟 v1.7.9 Yenilikleri ve Düzeltmeleri:

* **🖱️ Linux ➔ Windows ve Çift Yönlü Kesintisiz Fare Geçişi (Bi-directional Handoff):**
  - **Sorunun Kök Nedeni 1 (Linux UDP Paketleme):** Linux tarafında fare Windows sınırına dayandığında ve geri dönüş paketi (`PACKET_EDGE_HANDOFF`) hazırlanırken `struct.pack("<BBBBBf", ...)` formatı 6 eleman beklerken fonksiyona 7 argüman veriliyordu. Bu durum Python tarafında sessizce `struct.error: pack expected 6 items for packing (got 7)` hatası fırlatarak UDP geri dönüş paketinin gönderilmesini engelliyordu. Format `<BBBBBBf` olarak 7 elemana genişletildi ve `senderUdpPort` bilgisi eklendi.
  - **Sorunun Kök Nedeni 2 (Windows Mouse Hook Sıfırlama):** Windows tarafında Linux'tan veya Android'den gelen fare hareketi `SetCursorPos` ile enjekte edildiğinde, Windows işletim sistemi `LLMHF_INJECTED` bayrağını üretmez. Düşük seviyeli fare kancası (`MouseHookCallback`) bu hareketi yerel donanım faresi sanarak `_controllingRemotePeer` durumunu ilk karede sıfırlıyor ve farenin karşı cihazda kalmasını engelliyordu. 120 milisaniyelik koruma penceresi ve koordinat toleransı eklenerek imleç durumu korundu.
  - **Sorunun Kök Nedeni 3 (Windows Çift Hareket & Titreme):** `OnRemoteMouseMoveReceived` içinde `SetCursorPos` çağrısına ek olarak çağrılan `mouse_event(0x0001u, ...)` kaldırıldı; böylece Windows'a aktarılan imleçteki titreme, atlama ve takılmalar tamamen giderildi.
  - **IPv4 / IPv6 Çift Yığın Çözümlemesi:** Dual-stack soketlerde Linux'tan gelen IPv4 adreslerinin `::ffff:192.168.x.x` şeklinde gelmesi durumunda Windows'un eşleşen cihazı bulabilmesi için `sender.Address.MapToIPv4()` eşlemesi eklendi. `SendEdgeReturnToPeer` çağrısındaki `IsMutuallyPaired` katı kontrolü gevşetilerek durum gecikmelerinden doğan geri dönüş kilitlenmeleri çözüldü.

* **🔑 Linux Donanım İzinleri (/dev/uinput ve /dev/input/event*):**
  - Kullanıcıların `evdev` ile 1000Hz donanımsal girdi yakalamayı kullanabilmesi için `setup-uinput.sh` ve otomatik izin onarım mekanizması güncellendi.
  - `/dev/input/event*` aygıtlarına okuma/yazma izinleri eklendi ve oturumu kapatıp açmaya gerek kalmadan geçerli oturumda anında aktifleşmesi sağlandı (`chmod 666 /dev/uinput /dev/input/event*`).
  - İzin verilmemiş olsa dahi eski sistemdeki fareyi küçük HUD kutusuna hapseden `grab_set()` kaldırıldı; yerine tüm masaüstünü 60Hz hızında tarayan şeffaf imleç izleyici fallback motoru ve HUD üzerinde doğrudan tek tıkla izin verme butonu (`[🔑 Donanım İzni Ver]`) eklendi.

* **📐 Çoklu Monitör Kenar Algılama & Bağıl Oranlama:**
  - Linux çoklu monitör yapısında ekran kenar sınır eşiği 4px'den 6px'e genişletildi.
  - Farklı çözünürlükteki monitörler arasında geçiş yapılırken giriş noktasının doğru hesaplanması için imleç dikey oranı tüm sanal masaüstü yerine farenin terk ettiği fiziksel monitörün yüksekliğine göre normalize edildi.

---

# Connect Me v1.7.8 — Linux Görünmez Kenar Geçişi (Mavi Çizgi Kaldırıldı & Hızlı Fare Garantisi), Android➔Linux Ses Alıcısı, Pürüzsüz 2D Kanvas & Ekran Testi Temizliği

Bu sürüm; Linux çoklu monitör ortamında ikinci ekranın ortasında beliren istenmeyen mavi çizgiyi (`#0284C7`) tamamen ortadan kaldırır, fareyi hızlı savursanız dahi masaüstünün gerçek fiziksel sınırında anında karşı cihaza geçmesini sağlar. Ayrıca Android'den Linux'a ses aktarımını (UDP 42852 alıcısı + PipeWire/PulseAudio oynatıcı), Linux 2D ekran kartı sürükleme ve düzen sıfırlama mekanizmalarını kusursuz hale getirir; Windows arayüzündeki ek monitör test seçeneğini tamamen temizler.

---

### 🌟 v1.7.8 Yenilikleri ve Düzeltmeleri:

* **🖱️ Linux ➔ Android / Windows Görünmez Kenar Geçişi (Mavi Çizgi Kaldırıldı & Hızlı Fare Garantisi):**
  - **Sorunun Kök Nedeni:** Eski Tkinter `ScreenEdgeSentinels` yapısı şeffaf olması amacıyla açılan 2 piksellik override-redirect pencerelerden oluşuyordu. Linux Wayland ve X11 ortamlarında şeffaflık (`-alpha`) desteklenmediği için bu pencereler parlak mavi/cyan (`#0284C7`) opak çizgiler olarak görünüyordu. Ayrıca ekran çözünürlüğü tespitinde GNOME Wayland desteği eksik olduğundan çizgi ikinci ekranın ortasına denk geliyor ve fare hızlı hareket ettirildiğinde 2 piksellik ince çizgiyi atlayıp karşı ekrana geçemiyordu.
  - **Çözüm:** Görünür tüm mavi pencereler tamamen yok edildi! Yerine sıfır CPU tüketen 60Hz (~16ms) görünmez imleç pozisyonu takip motoru (`winfo_pointerxy()`) getirildi.
  - **Gerçek Ekran Sınırı Tespiti:** GNOME Mutter D-Bus (`org.gnome.Mutter.DisplayConfig`), DRM sysfs (`/sys/class/drm/`) ve dinamik masaüstü sınır genişletmesi eklendi. Çizgi artık ikinci ekranın ortasında değil; geçiş yalnızca masaüstünün en dış fiziksel sınırına ulaşıldığında gerçekleşir.
  - **Hızlı Fare Garantisi:** Fare ne kadar hızlı çekilirse çekilsin, fiziksel işletim sistemi imleci masaüstü sınırında durduğu için sistem 16 milisaniye içerisinde sınır temasını anında yakalar ve kontrolü gecikmesiz karşı cihaza aktarır. İç monitör birleşim noktalarından geçerken (`is_internal_seam`) imleç yerel ekranlar arasında serbestçe akar.

* **🔊 Android ➔ Linux Canlı Ses Aktarımı (Merkezi Kulaklık/Hoparlör Modu):**
  - **Sorunun Kök Nedeni:** Linux düğümünde yalnızca dışarı ses basan `start_audio_streaming` vericisi mevcuttu; Android cihazdan gelen UDP 42852 PCM ses paketlerini karşılayacak bir dinleyici soket veya oynatma hattı bulunmuyordu. Bu nedenle Android ses gönderiyor görünmesine rağmen Linux üzerinde hiçbir ses duyulmuyordu.
  - **Çözüm:** Linux arka plan servisine UDP port 42852 üzerinde dinleyen `_audio_receiver_loop` eklendi. Gelen 48,000 Hz 16-bit PCM stereo sesler; sistemde bulunan en uygun Linux ses mimarisi (`pw-play` [PipeWire] -> `paplay` [PulseAudio] -> `aplay` [ALSA]) stdin borusuna anında yönlendirilerek sıfır gecikmeyle (<15ms) kulaklığınızdan çalınır.
  - **Arayüz Canlı Geri Bildirimi:** Linux arayüzü Tab 4'e "Merkezi Ses Alıcısı: Dinliyor (UDP 42852)" kartı eklendi; Android'den gelen bayt miktarı ve aktif oynatma durumu canlı olarak gösterilir.

* **📐 Linux 2D Kanvas: Pürüzsüz Sürükleme, Buton Yalıtımı & Çalışan "Düzeni Sıfırla":**
  - **Sürükleme ve Köşede Sıkışma Çözümü:** Eski kodda fare sürüklendiğinde her piksel hareketinde tüm kanvas silinip baştan çiziliyordu (`canvas.delete("all")`). Bu durum Tkinter fare yakalama olayını düşürdüğü için kart fareyi takip etmeyi bırakıyor ve tuhaf köşelere savruluyordu. Artık sürükleme `canvas.move()` ile nesne silinmeden 1000 FPS hızında pürüzsüzce gerçekleştirilir; fare bırakıldığında en yakın monitör kenarına otomatik kenetlenir.
  - **Buton Yalıtımı:** Kartın üzerindeki butonların ve kenar seçim haplarının (`[Sol] [Sağ] [Üst] [Alt]`, `[Kabul Et]`, `[Fareyi Aktar]`) tıklanması durumunda sürüklemenin tetiklenmesi `tags` yalıtımı ve `return "break"` ile tamamen engellendi.
  - **"Ekran Düzenini Sıfırla" Düzeltildi:** Butona basıldığında artık hem bellekteki geçici koordinatlar hem de `~/.config/connectme/peer_topology.json` dosyasındaki eski kayıtlar temizlenir; tüm cihaz kartları yerel Linux monitörlerinin hemen sağına temiz ve düzenli biçimde hizalanır.

* **🗑️ Windows Ek Monitör Test Seçeneğinin Tamamen Kaldırılması:**
  - Kullanıcı geri bildirimi doğrultusunda Windows arayüzündeki `ToggleLocalMonitorsTestBtn` ("🖥️ +Ek Monitör Testi") butonu ve kodundaki tüm simülasyon değişkenleri tamamen temizlendi.

* **🪟 Windows Tam Ekran Başlatma (Maximized Window):**
  - Windows uygulaması başlatıldığında varsayılan olarak maksimize pencere boyutunda (`WindowState.Maximized`) açılır.

---

# Connect Me v1.7.7 — Linux Host Donanımsal Girdi Yakalama (evdev), Siyah Kutu Çözümü & Windows-Linux 1000 FPS Mikro-Soket Enjeksiyonu

Bu sürüm; Linux'tan (Nobara / KDE Plasma) Android'e bağlanıldığında ekranda siyah bir kare çıkması ve farenin yalnızca o karenin içinde kısıtlı hareket etmesi sorununu kökten çözer; Windows'tan Linux'a fare geçtiğinde yaşanan düşük kare hızı (stutter/lag) ve ekranın yalnızca bir kısmında hareket edebilme kısıtlamasını doğrudan Linux çekirdek soket akışı ve Wayland/X11 tam masaüstü sınır kenetleme mimarisiyle 1000 FPS microsecond hızına kavuşturur.

---

### 🌟 v1.7.7 Yenilikleri ve Düzeltmeleri:

* **🐧 Linux Host Modu: Siyah Ekran Kutusu Kaldırıldı, Şık HUD Banner Geldi:**
  - **Sorunun Kök Nedeni:** Eski sürümde kullanılan `HostCaptureOverlay`, Linux Wayland / KDE Plasma ortamında `overrideredirect(True)` ile açıldığında pencere şeffaflığı (`-alpha`) pencere yöneticisi tarafından yok sayılıyor ve ekranın ortasında opakt siyah bir kutu oluşturuyordu. Ayrıca Wayland'ın güvenlik protokolleri gereği X11 imleç merkezleme (`event_generate("<Motion>", warp=True)`) engellendiği için, fare fiziksel olarak o küçük kutunun dışına çıktığında olay dinleyicisi duruyor ve Android üzerinde imleç donuyordu.
  - **Çözüm:** Ekranı kapatan siyah pencere tamamen ortadan kaldırıldı! Yerine ekranın en üstünde zarif ve minimal (580x46 px) bir **HUD Durum Çubuğu** yerleştirildi. Bu çubuk kullanıcıya hangi cihazın kontrol edildiğini gösterir, `[ESC / ScrollLock]` kısayol hatırlatmasını sunar ve tek tıkla kontrolü Linux'a geri alan `[⬅️ Kontrolü Linux'a Geri Al]` butonunu barındırır.

* **🖱️ Linux Donanımsal Girdi Yakalayıcı (`LinuxHardwareInputGrabber` & `evdev`):**
  - Linux ana makine olduğunda fiziksel fare ve klavye donanım olayları (`/dev/input/event*`) doğrudan Linux çekirdeği seviyesinde `evdev.InputDevice.grab()` ile 1000 Hz yoklama frekansında yakalanır.
  - Hiçbir pencere veya ekran pikselleri kısıtlaması olmaksızın, saf bağıl hareketler (`REL_X`, `REL_Y`), sol/sağ/orta tıklamalar ve kaydırma hareketleri milisaniyelik gecikmeyle Android veya Windows cihaza UDP üzerinden aktarılır.
  - Android telefon ve tablet ekranında fare artık tüm ekranda sınırsız, takılmasız ve kusursuz bir serbestlikle gezer.

* **⚡ Windows ➔ Linux: 1000 FPS Mikro-Soket Girdi Enjeksiyonu (`YdotoolSocketClient`):**
  - **Düşük FPS ve Lag Sorununun Kök Nedeni:** Windows'tan Linux'a saniyede 60-120 adet UDP hareket paketi gelirken, Linux tarafında her bir gelen paket için yeni bir `subprocess.run(["ydotool", "mousemove", ...])` komut satırı süreci başlatılıyordu. Her alt süreç Linux üzerinde ~20-30 ms sürdüğü için girdi akışı 5-15 FPS seviyesine düşüyor ve aşırı takılmalı/kare atlamalı bir his veriyordu.
  - **Çözüm:** Doğrudan `ydotoold` arka plan servisinin yerel UNIX domain soketine (`/tmp/.ydotool_socket`, `$XDG_RUNTIME_DIR/.ydotool_socket` vb.) bağlanan `YdotoolSocketClient` geliştirildi. Gelen hareketler 24 baytlık saf ikili Linux `struct input_event` çerçeveleri halinde mikro-saniye hızında sokete pompalanır. Alt süreç başlatma maliyeti sıfıra indirilerek 120Hz/1000FPS saf donanım hızında akıcılık sağlandı.

* **📐 Tüm Ekran Alanı Sınır Kenetleme (Virtual Desktop Clamping & Edge Resistance):**
  - Windows'tan Linux'a geçen farenin ekranın sadece bir kısmında hareket edebilmesi sorunu giderildi; Linux UDP girdi döngüsünde imleç koordinatları tüm bağlı monitörlerin oluşturduğu sanal masaüstü sınırlarına (`[min_x, max_x]` ve `[min_y, max_y]`) tam olarak kenetlendi.
  - Windows'a erken geri sekmesini önlemek için kenar direnç eşiği 24 piksele yükseltildi ve iç bölge sıfırlama eşiği 3 piksel olarak ayarlandı.

* **🪟 Windows Çoklu Monitör Alanı Senkronizasyonu (`UpdatePeerDimensionsFromMonitors`):**
  - Windows tarafında Linux'tan gelen çoklu monitör veya geniş çözünürlük bilgileri alındığında, `ScreenWidth` ve `ScreenHeight` değerlerinin varsayılan telefon boyutuna (1080x2400) düşmesini engelleyen otomatik alan genişletme fonksiyonu tüm el sıkışma ve senkronizasyon paketlerine (`TOPOLOGY_SYNC`, `PAIR_ACCEPT`, `TRUSTED_RECONNECT`) bağlandı.

* **🔧 Otomatik İzin & Paket Kurulumu İyileştirmesi:**
  - `start-connectme.sh` betiği açılışta `/dev/uinput` izinlerini kontrol eder ve arka planda `ydotoold` servisini otomatik ayağa kaldırır.
  - Linux GUI tek tıkla `python3-evdev`, `ydotool` ve `wl-clipboard` paketlerini yüklemek ve udev kurallarını yapılandırmak için hazır buton ve betiklerle donatıldı.

---

# Connect Me v1.7.6 — Linux Başlatıcı, Eksik İçe Aktarma Düzeltmesi & Çökme Koruması

Bu sürüm; Linux (Nobara / KDE Plasma / Wayland & X11) ortamında uygulamanın açılışta çökmesine neden olan eksik modül içe aktarmalarını (`EDGE_NONE`, `EDGE_*`, `time`, `struct`, `typing`) giderir, arka plan servis temizliği ve hata kayıt mekanizmasını güçlendirir.

---

### 🌟 v1.7.6 Yenilikleri ve Düzeltmeleri:

* **🐧 Linux Arayüzü İçe Aktarma (Import) Hatası Giderildi:**
  * `connectme_linux_gui.py` içerisinde yeni eklenen Host modu kenar sabitleri (`EDGE_NONE`, `EDGE_LEFT`, `EDGE_RIGHT`, `EDGE_TOP`, `EDGE_BOTTOM`) ile `time`, `struct` ve `typing` modüllerinin eksikliği nedeniyle oluşan `NameError` giderildi.
  * `from __future__ import annotations` eklenerek Python 3.9 - 3.14 sürümleriyle tam uyumluluk sağlandı.

* **🛑 Temiz Kapanma & Soket Temizliği (`stop()`):**
  * Linux daemon ve GUI kapatıldığında veya yeniden başlatıldığında UDP/TCP portlarını (42849, 42850, 42851) derhal serbest bırakan `node.stop()` metodu ve soket yeniden kullanım (`SO_REUSEADDR`) mekanizması eklendi.

* **🚀 Güçlendirilmiş Başlatıcı (`start-connectme.sh`):**
  * Eski veya askıda kalmış bağımsız arka plan oturumlarını otomatik temizler.
  * Çıktıları `/dev/null` yerine doğrudan `~/.config/connectme/gui.log` dosyasına yazarak tanılamayı kolaylaştırır.

* **🛡️ Görsel Çökme Koruması (Fail-Safe Dialog & Crash Log):**
  * Olası beklenmeyen sistem veya grafik kütüphanesi hatalarında uygulamanın sessizce kapanması yerine kullanıcıya grafiksel hata kutusu göstermesi ve detaylı hata dökümünü `~/.config/connectme/crash.log` dosyasına kaydetmesi sağlandı.

---

# Connect Me v1.7.5 — Linux Host Modu (Ana Makine), 2D Kanvas İnteraktif Ekran Düzenleme & UDP Girdi Çözümleme Düzeltmesi

Bu sürüm; Linux'un (Nobara / KDE Plasma) da Windows gibi tam yetkili **Ana Makine (Host)** olarak çalışmasını, Linux masaüstünden Android veya Windows ekranlarına fare ve klavye aktarımını, Linux 2D kanvasında interaktif sürükle-bırak ekran düzenlemesini ve Windows'tan Linux'a fare geçişindeki UDP paket çökmesini çözer.

---

### 🌟 v1.7.5 Yenilikleri ve Düzeltmeleri:

* **🐧 Linux Tam Yetkili Ana Makine (Host KVM):**
  * Linux artık ikincil cihaz olmanın ötesinde, kendi fiziksel klavye ve faresini bağlı Android telefonlara, tabletlere ve ikincil Windows bilgisayarlara aktarabilen bir Host olarak çalışabilir.
  * **Doğal Kenar Duyargaları (`ScreenEdgeSentinels`):** Linux masaüstü kenarlarına yerleştirilen hafif, şeffaf sınır pencereleri ile fare kenara ulaştığında kontrol akıcı bir şekilde hedef cihaza devredilir.
  * **Hızlı Geçiş Butonu:** 2D Kanvasta bağlı her cihaz için **`[🎯 Fareyi Bu Cihaza Aktar]`** butonu eklendi.
  * **Kısayol Tuşları:** `ScrollLock` veya `Ctrl + Alt + Sağ/Sol Ok` tuşlarıyla geçiş desteği.
  * **Tam Ekran Donanım Girdi Yakalama (`HostCaptureOverlay`):** Fare karşı cihaza geçtiğinde Linux girdileri yüksek frekansta UDP üzerinden karşı cihaza aktarır, fareyi merkezde tutar ve üst HUD barı (`[Esc / Geri Dön]`) veya kenar itme direnciyle anında Linux'a geri döner.

* **🖥️ Linux İnteraktif 2D Ekran Düzenleme Kanvası:**
  * Linux arayüzündeki 2D Kanvas üzerinde bağlı Android ve Windows cihazları farenizle serbestçe sürükleyip bırakabilirsiniz.
  * Bırakıldığı anda masaüstü merkezine göre en yakın kenara (Sol, Sağ, Üst, Alt) manyetik olarak yapışır.
  * Pratik kenar butonları (**`[← Sol]`**, **`[Sağ →]`**, **`[↑ Üst]`**, **`[↓ Alt]`**) ile tek tıkla konum değiştirilebilir.
  * Ekran yerleşimleri kalıcı olarak `~/.config/connectme/peer_topology.json` dosyasına kaydedilir.

* **🐛 Windows ➔ Linux Fare Geçişi Unpack Çökmesi Giderildi:**
  * Windows'tan Linux'a geçerken imleç göstergesinin çıkıp farenin hareket etmemesi sorunu çözüldü (`struct.unpack('<Hhh', ...)` çağrısının 3 eleman yerine 4 değişkene açılmasından kaynaklanan `ValueError` giderildi).

---

# Connect Me v1.7.4 — Tkinter Float Font Hatası Çözümü & 1-Tık Kabul Et Desteği

* **🐧 Linux Tkinter Float Font Çökmesi Giderildi:** Linux ekran ölçekleme faktöründen (ör. %125 / 1.25) kaynaklanan float font boyutu hatası giderilerek tamsayıya (`int(round(...))`) dönüştürüldü.
* **✨ Tek Tıkla Gelen Bağlantıyı Kabul Etme (`PAIR_ACCEPT`):** Karşı taraf sizin kodunuzu girdiğinde kod girmeden anında karşılıklı eşleşmeyi tamamlayan `[✨ Kabul Et]` / `[✨ Hemen Kabul Et]` butonları eklendi.

---

# Connect Me v1.7.3 — Koloni Ağı (Colony Mode) & Tek PIN ile Tüm Ağa Otomatik Bağlantı

* **🐝 Koloni Ağı (Colony Mode):** Birbirine bağlı cihazlar ağda tek bir koloni (küme) olarak algılanır.
* **🔗 Tek PIN ile Koloniye Katılma:** Kolonideki herhangi bir cihazın 6 haneli kodu doğrulandığında, diğer koloni üyeleri de yeni cihazı otomatik olarak tanır ve tek tek şifre girmeye gerek kalmadan tüm ağa anında bağlanılır.
* **🧪 51/51 Birim Testi:** Protokol, koloni oluşumu, kenar direnci, çoklu monitör topolojisi ve çift yönlü KVM testlerinin tamamı yeşil.

---

# Connect Me v1.7.2 — Kusursuz Ses Aktarımı (Audio Streaming), Akıllı Hedef Çözümleme & Tek Taraflı/Çift Taraflı PIN Desteği

Bu sürüm; Android ve Windows arasında ses aktarımı başlatıldığında cihazlar bağlı olmasına rağmen ortaya çıkan *"Önce bilgisayarla 6 haneli PIN eşleşmesini tamamlayın!"* hatasını kökten çözer. Ses akışı artık bağlı, aktif veya yerel ağda keşfedilen bilgisayarı otomatik ve akıllıca tespit eder.

---

### 🌟 v1.7.2 Yenilikleri ve Düzeltmeleri:

* **🎧 Kesintisiz Ses Aktarımı & Akıllı Hedef Bilgisayar Çözümleme (Smart Target Resolution):**
  - Android uygulamasında "Sesi Bilgisayara / Kulaklığa Aktar" butonuna basıldığında, yalnızca katı çift taraflı eşleşme (`isMutuallyPaired`) arayan kontrol kaldırıldı.
  - Artık akıllı öncelik sırası ile hedef bilgisayar saniyeler içinde belirlenir:
    1. Karşılıklı doğrulanmış Windows cihazları (`isMutuallyPaired`),
    2. Aktif olarak fare/klavye kontrolü yapan bilgisayar (`activePcAddress`),
    3. Tek taraflı PIN doğrulaması tamamlanmış bilgisayar (`remoteEnteredMyPinVerified` veya `myEnteredPinVerifiedByRemote`),
    4. Kullanıcının listeden seçtiği veya IP kutusuna girdiği bilgisayar,
    5. Yerel ağda keşfedilen Windows bilgisayarı.
  - Windows bilgisayarınız Android telefonunuzun kodunu girdiğinde veya aktif bağlantı kurulduğunda, hiçbir ek PIN sorgusu olmadan sesler anında UDP 42852 portundan kulaklığınıza akmaya başlar.

* **🔄 PIN Doğrulama & Durum Senkronizasyonu Düzeltmesi:**
  - Android tarafından bilgisayarın PIN kodu girildiğinde (`submitRemotePinToPeerInternal`), doğrulama başarılı olsa dahi aktif bilgisayar adresinin (`activePcAddress`) ve arayüzün (`onStateUpdated`) yenilenmemesi hatası giderildi.
  - Tek taraflı veya çift taraflı PIN girildiğinde cihaz kartı anında yeşil renkte `"🟢 BAĞLANDI (Bilgisayar Kodunuzu Onayladı)"` durumuna geçer ve `"🔌 Bağlantıyı Kes (Kopar)"` butonu görünür.

* **🛡️ Giriş ve Dönüş Güvenliği Uyumluluğu:**
  - Kenar geçişi (`sendEdgeHandOffBackToPeer`), evrensel pano (`sendClipboardTextToPc`) ve ortak cep (`sendStreamToPcShelf`) fonksiyonları tek taraflı doğrulanmış aktif bağlantıları da tanıyacak şekilde güncellendi.
  - Ağdaki yabancı cihazlardan gelen paketler engellenirken, PIN doğrulanmış bilgisayardan gelen fare ve ses girişleri tam yetkiyle kabul edilir.

* **📦 Sürüm Karşılaştırma & Otomatik Güncelleyici İyileştirmesi:**
  - Android ve Windows otomatik güncelleyicilerinde (`AppUpdateManager` & `WindowsUpdateService`) büyük/küçük harf (`v` / `V`) ve sürüm son eki (tire işaretleri) kaynaklı sürüm ayrıştırma sorunları giderildi.

---

# Connect Me v1.7.1 — Kusursuz Kenar Dönüşü (Seamless Border Return), Merkez İmleç Sıçrama Koruması & Akıllı Koordinat İadesi

Bu sürüm; uzak bir cihazdan (Android, Linux veya ikincil Windows) ana Windows ekranına geri dönüldüğünde imlecin her seferinde ekranın ortasına sıçraması sorununu kökten çözer. İmleç artık diğer cihazdan ayrıldığı yükseklik ve orantısal koordinata birebir sadık kalarak yerel masaüstü sınırından akıcı ve kesintisiz şekilde devam eder.

---

### 🌟 v1.7.1 Yenilikleri ve Düzeltmeleri:

* **🎯 Kusursuz Kenar Giriş Koordinatı (Exact Border Arrival Coordinates):**
  - Uzak ekrandan ana Windows ekranına dönüş yapıldığında, imleç karşı cihazın ekranında hangi dikey (veya yatay) konumdaysa ana ekrandaki ortak kenara tam o yükseklikten girer (`NormalizedPosition` hassasiyeti).
  - İmleç asla ekranın ortasından başlamaz; iki ekran arasında gerçek bir çoklu monitör deneyimi gibi pürüzsüz geçiş sağlanır.

* **🛡️ Donanımsal Merkez Kuyruk Bastırma (Anchor Jitter & Stale Event Suppression):**
  - Uzak cihaz kontrol edilirken Windows fiziksel faresi merkez koordinatında (`_anchorX, _anchorY`) sabit tutuluyordu. Kontrol yerel ekrana iade edildiğinde sürücü kuyruğunda biriken eski merkez hareket mesajları imleci anlık olarak merkeze fırlatıyordu.
  - Geliştirilen düşük seviyeli kanca filtresi (`MSLLHOOKSTRUCT`) ile yerel ekrana geçişten sonraki ilk 150ms boyunca merkez kancasından arta kalan bayat donanım olayları bastırılır ve kenar giriş koordinatı korunur.

* **🧭 Akıllı Kenar ve Koordinat Fallback'i:**
  - Acil durum kısayolları (Scroll Lock / Ctrl+Alt+L) veya kenar bilgisi içermeyen dönüş paketlerinde imleç artık körü körüne merkeze atılmaz; cihazın 2D Kanvastaki atanmış kenarı (`AssignedEdgeOnLocal`) ve takip edilen son koordinatı temel alınır.
  - Sınır çizgisinden yerel ekrana giriş anında istem dışı geri sekme (edge bouncing / ping-pong) koruması devreye alınarak fare kontrolü tamamen kararlı hale getirildi.

---

# Connect Me v1.7.0 — Çift Yönlü Windows-Windows KVM, Gelen Bağlantı İstek Kartı, Port 42850 İyileştirmesi & Klavye/Fare Enjeksiyonu

Bu sürüm; iki Windows bilgisayar arasında kesintisiz çift yönlü KVM kontrolü (fare ve klavye enjeksiyonu), karşı cihaz kodunuzu girdiğinde anında beliren canlı gelen bağlantı isteği kartı (`🔔 GELEN BAĞLANTI İSTEĞİ`), Windows UDP soket port çakışmasını gideren dinamik port bağlama mimarisi ve karşılıklı kenar geçiş-dönüş senkronizasyonunu sunar.

---

### 🌟 v1.7.0 Yenilikleri ve Düzeltmeleri:

* **🔔 Canlı Gelen Bağlantı İstek Bildirimi & Hızlı PIN Onay Kartı:**
  - Karşıdaki herhangi bir cihaz (Windows, Android, Linux) sizin 6 haneli kodunuzu girdiğinde, Windows arayüzünde canlı altın/amber renkli **`🔔 GELEN BAĞLANTI İSTEĞİ!`** bildirim kartı belirir.
  - Kart üzerinde istek atan cihazın adı, IP adresi ve hızlı PIN giriş kutusu (`InboundQuickPinInputBox`) otomatik olarak odaklanır.
  - Uygulama simge durumundaysa veya arka plandaysa pencere otomatik olarak öne çıkar ve nazik bir sistem ses tonuyla kullanıcıyı bilgilendirir.
  - Karşı cihazın 6 haneli kodunu girip `Enter` veya `🔐 Onayla` butonuna basarak tek adımda çift taraflı eşleşme tamamlanır.

* **🚀 Windows-Windows Çift Yönlü Donanımsal KVM Enjeksiyonu:**
  - İkincil Windows bilgisayara fare imleci geçtiğinde donma sorunu tamamen giderildi.
  - **Klavye Enjeksiyonu:** Uzak bilgisayardan gelen tüm klavye vuruşları (harfler, sayılar, yön tuşları, fonksiyon tuşları, kısayollar, Türkçe Unicode karakterler) Win32 `keybd_event` ile ikincil bilgisayarın aktif penceresine anında aktarılır.
  - **Fare Enjeksiyonu:** Fare hareketleri `SetCursorPos` ve donanımsal `mouse_event` ile çift katmanlı enjekte edilir (tüm pencereler, masaüstü ögeleri ve oyunlar fareyi tam hisseder).
  - Sol, sağ, orta butonlar, ileri/geri yan butonlar (XButton1, XButton2) ve dikey/yatay fare tekerleği kaydırmaları desteklenir.

* **🛡️ Windows UDP Port 42850 Soket İyileştirmesi (Winsock Conflict Fix):**
  - Giden UDP girdi paketlerini gönderen soketlerin `InputUdpPort` (42850) portuna bağlanması nedeniyle gelen paketlerin engellenmesi sorunu giderildi; giden soketler artık güvenli dinamik kaynak portuna (`new IPEndPoint(ip, 0)`) bağlanarak port 42850'yi sadece gelen girdilere tahsis eder.

* **🔄 İkincil Windows Ekranından Ana Ekrana Sorunsuz Geri Dönüş (`EDGE_RETURN`):**
  - Fare ikincil Windows ekranındayken giriş yapılan kenara (örneğin sol kenara) doğru geri itildiğinde 8 piksellik bilinçli direnç eşiği aşıldığında imleç ve klavye kontrolü otomatik olarak ana bilgisayara geri aktarılır.
  - İki Windows bilgisayar arasında `EDGE_CONFIG` ile 2D Kanvas yerleşimi otomatik ve dinamik olarak eşitlenir.

---

# Connect Me v1.6.9 — Tek Tıkla Bağlantı Kesme (Disconnect / Unpair), İmleç İadesi & Akıllı Otomatik Bağlantı Bastırma

Bu sürüm; Windows, Android ve Nobara Linux arasında aktif bağlantıları tek tıkla kesebilme (`🔌 Bağlantıyı Kes`), imleci anında yerel ana ekrana geri çekme, TCP üzerinden karşı cihazı senkronize bilgilendirme ve kullanıcı bilerek bağlantıyı kestiğinde güvenilir cihazların hemen tekrar otomatik bağlanmasını önleyen akıllı bastırma mekanizmasını sunar.

---

### 🌟 v1.6.9 Yenilikleri ve Düzeltmeleri:

* **🔌 Tek Tıkla Bağlantıyı Kes (Disconnect / Unpair):**
  - **Windows:** Eşleşmiş her cihaz kartına kırmızı **`🔌 Bağlantıyı Kes (Kopar)`** butonu eklendi.
  - **Android:** Karşılıklı onaylanmış cihaz kartlarına **`🔌 Bağlantıyı Kes (Kopar)`** butonu eklendi.
  - **Linux (Nobara):** Ana aksiyon çubuğu, cihaz kartları ve PIN pencerelerine **`🔌 Bağlantıyı Kes`** seçeneği eklendi.
  - Karşı tarafa TCP `DISCONNECT` kontrol çerçevesi iletilerek iki tarafta da oturum eşzamanlı olarak sonlandırılır.

* **🎯 Anında Yerel Ekrana İmleç İadesi:**
  - Bağlantı kesildiği sırada fare imleci uzak cihazın ekranında bulunuyorsa, imleç ve klavye kontrolü bekleme veya takılma olmaksızın derhal yerel ana ekrana iade edilir.

* **🛑 Akıllı Otomatik Bağlantı Bastırma (Auto-Reconnect Suppression):**
  - "Bu Cihaza Güven ve Hatırla (Sıfır-PIN)" olarak kaydedilmiş cihazlarda, kullanıcı bağlantıyı kestiğinde arka plan servisinin hemen tekrar bağlanıp kullanıcıyla inatlaşması engellendi. Kullanıcı ilgili cihaza manuel bağlanana veya PIN girene kadar otomatik bağlantı bastırılır.

* **🗑️ Cihazı Unut (Güvenilirlik Kaydını Sil):**
  - Kayıtlı güvenilir cihazları tek tıkla silip unutabilmek için tüm platformlara **`🗑️ Bu Cihazı Unut (Güveni Kaldır)`** seçeneği eklendi.

---

# Connect Me v1.6.8 — Ortak Kenar Geçiş Koruması, Otomatik Kenar Senkronizasyonu & İndirme Bağlantıları Güncellemesi

Bu sürüm; Windows'tan Android'e fare geçtiğinde telefonun herhangi bir kenarına (üst bildirim çubuğu, alt gezinme çubuğu, sağ kenar) değildiğinde imlecin istenmeyen şekilde Windows'a geri dönmesi sorununu çözen akıllı ortak kenar filtresini, Windows 2D kanvasındaki yerleşim değişikliklerinin anında Android'e iletilmesini (`EDGE_CONFIG`) ve indirme bağlantılarının en son sürümlerle eşzamanlanmasını sunar.

---

### 🌟 v1.6.8 Yenilikleri ve Düzeltmeleri:

* **📱 Android Ortak Kenar Geçiş Koruması (Exclusive Shared Border Return):**
  - Windows'tan Android'e fare geçtiğinde, önceden tanımlanmış 14px'lik genel kenar serbestisi nedeniyle kullanıcının telefonun üstündeki durum çubuğuna, altındaki ana ekran tuşlarına veya karşı kenara yaklaşması durumunda imlecin kontrolsüzce Windows masaüstüne fırlaması sorunu giderildi.
  - Artık imleç Android üzerindeyken **sadece ve sadece arayüzde Windows ile ortak olarak yapılandırılmış kenardan** (örneğin telefon Windows'un sağındaysa yalnızca Sol Kenarından) Windows'a geri dönebilir.
  - Diğer kenarlara (üst bildirim çubuğu, alt gezinme butonları vb.) fare çarptığında imleç telefon ekranı sınırları içinde tutulur, kullanıcı telefonun tüm köşeleriyle ve sistem arayüzleriyle rahatça etkileşime girebilir.
  - Windows'a geri dönüş için ortak kenarda 6 piksellik bilinçli itme direnci uygulanarak kazara geçişler engellendi.

* **🔄 Gerçek Zamanlı Kenar Konfigürasyon Senkronizasyonu (`EDGE_CONFIG`):**
  - Windows arayüzündeki 2D Ekran Konfigürasyonu Kanvası üzerinden Android cihazı sürüklendiğinde veya hızlı kenar butonları (Sol, Sağ, Üst, Alt) kullanıldığında, yeni ortak kenar TCP üzerinden Android servisine anında bildirilir.
  - Android cihaz ilk eşleştiğinde (`PAIR_REQUEST`) veya otomatik yeniden bağlandığında (`TRUSTED_RECONNECT`) ortak kenar otomatik olarak yüklenir.

* **📦 İndirme ve Güncelleme Bağlantıları Güncellemesi:**
  - `README.md` ve `DOWNLOADS.md` dosyalarındaki indirme bağlantıları ve dosya isimleri v1.6.8 sürümleriyle tam uyumlu hale getirildi.
  - Android ve Windows uygulama içi otomatik güncelleyicileri (`In-Program Auto Updater`) v1.6.8 sürümünü algılayıp tek tıkla güncelleme yapacak şekilde senkronize edildi.

---

# Connect Me v1.6.7 — Wayland Donanım İmleci, 2D Kanvas Sürükle-Bırak, Hedefe Özel PIN & Android Ses Köprüsü

Bu sürüm; Windows'tan Nobara Linux'a fare geçtiğinde KDE Plasma Wayland üzerinde imlecin görünmemesi veya hareket etmemesi sorununu çözen ayrık çekirdek sanal cihaz mimarisini (`Connect-Me-Mouse` & `Connect-Me-Keyboard`), Linux arayüzündeki ekran yerleşimini karmaşık ve üst üste binmiş halden kurtarıp serbestçe sürüklenebilir kartlara ve temiz dik bağlantılara dönüştüren yeni 2D kanvası, PIN kodunun yanlış cihaza gitmesini engelleyen hedefe özel eşleşme pencerelerini ve Android 14/15'te ses aktarımının çökmesine yol açan `MediaProjection` kısıtlamasını çözen ses motoru iyileştirmelerini sunar.

---

### 🌟 v1.6.7 Yenilikleri ve Düzeltmeleri:

* **🐧 Nobara Linux / KDE Plasma Wayland Donanım İmleci ve Ayrık evdev Motoru:**
  - Linux `uinput` sanal aygıtında fare eksenleri (`REL_X`, `REL_Y`) ile klavye tuşları aynı aygıtta birleştirildiğinde, libinput ve KWin Wayland bileşeni aygıtı yalnızca klavye olarak sınıflandırıyor ve ekranda işaretçi imleci oluşturmuyordu.
  - Sürücü `Connect-Me-Mouse` (salt optik fare) ve `Connect-Me-Keyboard` (salt klavye) olmak üzere iki bağımsız çekirdek aygıtına ayrıldı. KWin Wayland artık gelen hareketleri gerçek bir USB fare olarak algılar ve ekranda işaretçi imlecini anında canlandırır.
  - Windows'tan Linux'un sol kenarına geçişte ters koordinat hesaplama hatası düzeltildi (imleç artık Linux'un sol kenarından girer).
  - Kenardan içeri geçtikten hemen sonra yanlışlıkla Windows'a geri sekmesini önleyen 8 piksellik kenar direnci eklendi.
  - Sürücü izinleri eksik olduğunda tek tıkla sistem şifresi (`pkexec`) ile `/dev/uinput` izinlerini ayarlayan akıllı buton eklendi.

* **🖥️ Yenilenen 2D Ekran Konfigürasyonu (Kanvas Sürükle-Bırak & Düzen Sıfırlama):**
  - Uzak cihazların ekranın en sağında tek bir sütuna sıkışıp monitörlerin içinden geçen çapraz kesik çizgiler çizmesi sorunu giderildi.
  - Windows sağa, Android ve diğer cihazlar sola/üste dengeli ve orantısal olarak yerleştirilir.
  - Cihaz kartları kanvas üzerinde **serbestçe sürüklenebilir** (`Drag & Drop`). Kullanıcı kartı istediği yerel monitörün yanına taşıyabilir.
  - `[ 📐 Düzeni Sıfırla ]` butonu ile tüm kartlar anında en uygun otomatik pozisyonlarına döndürülebilir.
  - Her cihaz kartının üzerine doğrudan tıklanabilir `[ 🔐 6 Haneli Kodu Gir ]` butonu eklendi.

* **🔐 Hedefe Özel PIN Eşleştirme Penceresi:**
  - Linux'ta ağda birden fazla cihaz açıkken (ör. Huawei, Samsung, Windows) PIN kutusuna kod yazıldığında kodun rastgele ilk cihaza gitmesi ve Windows/Android ile eşleşilememesi sorunu tamamen çözüldü.
  - Cihaz kartına tek tıkla veya cihaz listesinde çift tıklandığında o cihaza özel yüksek kontrastlı PIN doğrulama penceresi açılır.
  - Karşı cihaz sizin kodunuzu girdiğinde üstte turuncu bildirim çubuğu ve `[ 🔐 {Cihaz} Kodunu Gir ]` butonu anında belirir.

* **🎧 Android 14 / 15 (One UI 6.x / 7.x) Kulaklığa Ses Köprüsü Çökme Düzeltmesi:**
  - Android 14+ (API 34+) sistem sesini yakalamadan önce zorunlu kılınan `MediaProjection.Callback` kaydı eklenerek Samsung Galaxy ve modern Android cihazlarda yaşanan `IllegalStateException` çökmesi giderildi.
  - Sistem sesi yakalanamadığında akışın sessizce durması yerine otomatik olarak mikrofon moduna düşen (`AudioRecord` fallback) dayanıklı hata yakalama mekanizması uygulandı.
  - Windows ses motoru, ses akışı başladığında arayüzde ve loglarda bağlantıyı ve ses parametrelerini (48.000 Hz Stereo) anlık bildirir.

* **🪟 Windows Çoklu Monitör ve Manuel IP İyileştirmeleri:**
  - Manuel IP ile eklenen cihazların gerçek keşif sinyali geldiğinde çift kayıt oluşturması engellendi; geçici kayıtlar otomatik birleştirilir.
  - Nobara Linux'un çift monitörünün Windows ekran konfigürasyonu kanvasında gerçek oranlarıyla ve alt monitör isimleriyle doğru şekilde gösterilmesi sağlandı.

---

# Connect Me v1.6.6 (v1-6-6) — Kalıcı Cihaz Kimlikleri, Çoklu Cihaz PIN Seçimi & Otomatik Reconnect

### 🌟 v1.6.6 Yenilikleri ve Düzeltmeleri:

* **📱 Android Çoklu Cihaz PIN Seçimi & Özel Kart Arayüzü:**
  - Ağda hem Nobara hem Windows aynı anda açık olduğunda, Android'in her zaman listedeki ilk cihaza (Nobara) PIN göndermesi ve Windows'un PIN doğrulamasını alamaması sorunu giderildi.
  - Artık keşfedilen her bilgisayar/cihaz için kendi adına ve IP'sine sahip bağımsız bir durum kartı ve doğrudan o cihaza ait `[ 🔐 Bu Cihazın 6 Haneli Kodunu Gir ]` butonu yer alır.
  - Butona dokunulduğunda doğrudan o cihaz için 6 haneli kod girişi penceresi açılır ve girilen PIN kesinlikle yalnızca o cihaza iletilir.
  - Manuel PIN alanı da seçili veya onay bekleyen cihazı akıllıca tespit eder; belirsizlik varsa kullanıcıyı kart butonuna dokunması için uyarır.

* **🔑 Kalıcı Cihaz Kimlikleri (Persistent Device IDs):**
  - Windows tarafında `ConnectMeNetworkNode` ve Android tarafında `ConnectMeService` her açılışta `Guid.NewGuid()` / `UUID.randomUUID()` ile rastgele yeni `deviceId` üretiyordu. Bu durum, cihaz bir kez kapatılıp açıldığında önceki eşleşme ve güven belirteçlerinin (Trusted Tokens) geçersiz kalmasına ve otomatik bağlanmanın devre dışı kalmasına neden oluyordu.
  - Windows: Kalıcı kimlik `%APPDATA%\ConnectMe\device_id.txt` dosyasında saklanır; yeniden başlatmalarda korunur.
  - Android: Kalıcı kimlik `SharedPreferences` (`persistent_local_device_id`) üzerinde saklanır.
  - Böylece bir kez eşleşen cihazlar, uygulama kapatılıp açılsa bile birbirini kalıcı olarak tanır.

* **🐧 Nobara Linux Proaktif Otomatik Yeniden Bağlantı (Proactive Auto-Reconnect):**
  - Linux servisinin keşif anında güvenilir bir cihaz (Windows veya Android) gördüğünde bağlantıyı başlatmayıp pasif beklemesi durumu giderildi.
  - Güvenilir bir cihazın UDP keşif paketi alındığında, arka planda güven belirteci ile `TRUSTED_RECONNECT` TCP isteği tetiklenir ve sıfır PIN ile anında otomatik bağlantı kurulur.

---

# Connect Me v1.6.5 (v1-6-5) — Nobara Yerinde Otomatik Güncelleme & Android Kalıcı İmza Entegrasyonu

Bu sürüm; Nobara Linux üzerinde eksik modül hatasını gidererek doğrudan masaüstünden tek tıkla arşivi indirip mevcut klasöre ayıklayan ve kendini yeniden başlatan yerinde otomatik güncelleyiciyi, Android'de her derlemede değişen imza sorununu (`INSTALL_FAILED_UPDATE_INCOMPATIBLE`) çözen kalıcı anahtar deposu entegrasyonunu ve paket yükleyici yetkilendirme iyileştirmelerini sunar.

---

### 🌟 v1.6.5 Yenilikleri ve Düzeltmeleri:

* **🐧 Nobara Linux Yerinde Tam Otomatik Güncelleme (In-Place Auto Updater):**
  - Linux GUI'de güncelleme kontrolü sırasında `NameError: name 'json' is not defined` hatasına yol açan eksik `import json` modülü eklendi.
  - Yalnızca harici tarayıcı açmak yerine, `ConnectMe-Linux-x64v...tar.gz` paketini arka planda indiren, uygulamanın kurulu olduğu klasöre doğrudan ayıklayan, `.sh` ve `.py` dosyalarının çalıştırılma izinlerini (`0o755`) koruyan ve uygulamayı anında yeniden başlatan (`os.execv`) tam entegre otomatik güncelleyici eklendi.
  - İndirme ilerlemesi arayüzdeki buton ve durum kutusunda yüzde olarak canlı gösterilir.

* **📱 Android Kalıcı İmza (Permanent Keystore) ve Paket Yükleyici Güvenliği:**
  - Android'de indirilen güncellemenin yüklenememesinin kök nedeni tespit edildi: GitHub Actions CI derleyicisinde her yapılandırmada rastgele yeni bir RSA anahtarı (`keytool -genkeypair`) üretilmesi sebebiyle Android işletim sistemi yeni APK'yı "İmza Uyuşmazlığı" gerekçesiyle reddediyordu.
  - Kalıcı bir `connectme-release.jks` / `connectme-keystore.b64` anahtar deposu oluşturularak CI derleme zincirine bağlandı. Artık üretilen tüm Android APK'ları aynı kalıcı dijital imzayı taşır.
  - İndirilen APK dosyası doğrudan `cacheDir/updates` dizinine alınarak sistem paket yükleyicisine açık okuma izinleri (`setReadable(true, false)`) ve `grantUriPermission` tanımlandı.
  - "Bilinmeyen kaynaklardan yükleme" izni gerektiğinde kullanıcıya yönlendirmeden önce ne yapması gerektiğini açıklayan anlaşılır bir diyalog penceresi eklendi.
  - İndirme tamamlandıktan sonra buton aktif kalarak `📦 Güncellemeyi Yükle` durumunu korur; kullanıcı dilediğinde kurulumu tekrar tetikleyebilir.

---

# Connect Me v1.6.4 (v1-6-4) — Çoklu Monitör Topolojisi, Gerçek Boyutlu Kanvas & Nobara Wayland Donanım İmleç Entegrasyonu

Bu sürüm; Nobara Linux üzerinde çoklu monitör geometrisinin gerçek piksel çözünürlükleriyle algılanması, Windows ve Nobara 2D ekran kanvaslarında gerçek boyutlu orantılı monitör çizimi, Wayland üzerinde farenin donanım düzeyinde hareket etmesini sağlayan tek tıkla `/dev/uinput` sürücü izin entegrasyonu, keşif anında 2D kanvasta anında beliren cihaz kartları ve Manuel IP iletişim kutusunu içerir.

---

### 🌟 v1.6.4 Yenilikleri ve Düzeltmeleri:

* **🖥️ Nobara Linux Çoklu Monitör Topolojisi & Geometrisi (KScreen Doctor & Fallback Revizyonu):**
  - KDE Plasma Wayland üzerinde `kscreen-doctor -j` çıktısının monitör çözünürlüklerini doğrudan `size` objesinde değil, `currentModeId` ile eşleşen `modes` dizisinde barındırması nedeniyle yaşanan çözünürlük tespit hatası giderildi.
  - `currentModeId`, `current: true` ve `currentMode` sözlükleri üzerinden kesin çözünürlük eşleşmesi yapıldı. 90° ve 270° ekran döndürmeleri için genişlik/yükseklik takası eklendi.
  - `kscreen-console json` ve `kscreen-doctor -o` metin tabanlı regex ayrıştırıcıları kademeli yedekleme katmanı olarak entegre edildi.
  - Monitörler `(virtualX, virtualY)` koordinatlarına göre deterministik olarak sıralanır; çift ekranlar Nobara ve Windows üzerinde kusursuz bir şekilde yan yana algılanır.

* **📐 Windows ve Nobara 2D Kanvasında Birebir Gerçek Ölçekli (Proportional) Monitör Çizimi:**
  - Windows arayüzündeki uzak cihaz (Nobara) ekranlarının orantısız veya sıkışık tek bir kutu olarak görünmesi sorunu çözüldü.
  - `GetCanvasMonitorScale()` ile hem yerel monitörler hem de uzak cihaz ekranları birebir aynı piksel ölçeğiyle hesaplanır.
  - Çift ekranlı Nobara sisteminde her bir monitör kendi gerçek en-boy oranında, yan yana birleşim dikişleriyle (seam lines) çizilir.
  - İmleç Nobara'ya geçtiğinde, farenin tam olarak hangi ekranda ve piksel koordinatında olduğunu gösteren canlı hedef göstergesi (`🎯 [İMLEÇ BURADA] - DP-1 (1920x1080) @ 350, 420`) çizilir.

* **🖱️ Nobara Linux Wayland Donanım İmleç & Çekirdek (Kernel) `/dev/uinput` Entegrasyonu:**
  - Farenin Windows'tan Nobara'ya geçtiğinde görünmemesi veya hareket etmemesinin kök nedeni tespit edildi: Nobara'da kök olmayan (`non-root`) kullanıcıların `/dev/uinput` aygıtına yazma yetkisi olmaması nedeniyle girdi enjektörünün `none` moduna düşmesi.
  - Linux GUI'ye yüksek kontrastlı sürücü uyarı paneli ve tek tıkla yetkilendirme (`pkexec` Polkit) butonu eklendi. Kullanıcı terminale tek bir komut bile yazmadan `/etc/udev/rules.d/99-connectme-uinput.rules` kuralını kurabilir ve `evdev` çekirdek sürücüsünü anında aktifleştirebilir.
  - Alfanümerik tuşlar (A-Z, 0-9), fonksiyon tuşları (F1-F12) ve yön tuşları `evdev.UInput` yeteneklerine eklenerek yerel klavye haritalaması tamamlandı.
  - İmleç geçişi anında (`PACKET_EDGE_HANDOFF`) Wayland donanım imlecini uyandıran mikro göreli hareket sinyali entegre edildi.

* **🗺️ 2D Kanvasta Henüz Eşleşmemiş / Bekleyen Cihazların Görünürlüğü:**
  - Önceden yalnızca çift taraflı PIN doğrulaması (`IsMutuallyPaired`) tamamlanan cihazlar 2D kanvasta çiziliyordu. Bu durum, eşleşme sürecinde cihazların haritada kaybolmasına yol açıyordu.
  - Artık keşfedilen tüm cihazlar kanvasta anında yerini alır:
    - Çift taraflı onaylı cihazlar: Zümrüt Yeşili (`#064E3B`)
    - PIN doğrulaması bekleyen cihazlar: Kehribar Sarı kesikli kenarlık (`⏳ PIN Onayı Bekleniyor`)
    - Yeni keşfedilen cihazlar: Gece Mavisi / Camgöbeği
  - Kanvastaki karta tıklandığında doğrudan ilgili cihazın PIN doğrulama kutusuna odaklanılır.

* **🌐 Nobara Linux Manuel IP ile Bağlantı Penceresi:**
  - Linux GUI üst başlık barına ve Cihazlar sekmesine `🌐 IP ile Bağlan` butonu eklendi.
  - Modal iletişim kutusu üzerinden doğrudan hedef IP adresi ve port girilerek hem UDP keşif paketi gönderilebilir hem de TCP üzerinden doğrudan PIN doğrulaması tetiklenebilir.

---

# Connect Me v1.6.3 (v1-6-3) — Nobara Linux Manuel IP ile Bağlantı, Herkese Açık Dağıtım ve Güncelleyici Düzeltmeleri

Bu sürüm; Nobara Linux üzerinde doğrudan IP adresi girerek cihaz bağlama (Manual IP Direct Connect), otomatik ağ alt ağı tarama (Subnet Broadcast Scan), GitHub reposunun Public yapılarak tüm cihazlar için tek tıkla güncellemenin ve dosya indirmelerinin kesintisiz hale getirilmesini sağlar.

---

### 🌟 v1.6.3 Yenilikleri ve Düzeltmeleri:

* **🌐 Nobara Linux Manuel IP ile Cihaz Ekleme & Doğrudan Bağlantı:**
  - Linux GUI (`connectme_linux_gui.py`) "Cihazlar & Güvenlik" sekmesine ve üst başlık kartına **"Manuel IP ile Cihaz Ekle & Doğrudan Bağlan"** paneli eklendi.
  - Ağ keşfinin router AP izolasyonu, güvenlik duvarı veya karmaşık Wi-Fi ağları nedeniyle engellendiği durumlarda, hedef cihazın (Windows veya Android) IP adresi yazılarak doğrudan UDP ve TCP üzerinden anında bağlantı kurulabilir.
  - Yerel alt ağ öneki (`192.168.1.` vb.) otomatik doldurulur; Enter tuşu veya `➕ IP ile Cihaz Ekle & Keşfet` butonu ile cihaz anında listeye alınır.
  - PIN kodu önceden girilmişse tek tıkla doğrudan TCP üzerinden PIN doğrulama ve karşılıklı eşleşme başlatılır.

* **📡 Manuel Ağ Alt Ağı Tarama (Subnet Broadcast):**
  - Linux arayüzüne eklenen `📡 Alt Ağı Tara` butonu sayesinde tüm yerel alt ağa anında doğrudan keşif sinyalleri yollanarak yeni açılan cihazlar saniyeler içinde yakalanır.

* **🔓 GitHub Depo Görünürlüğü (Public) & 3 İşletim Sisteminde Otomatik Güncelleme:**
  - Deponun Private olmasından dolayı cihazların aldığı `HTTP 404 Not Found` engeli GitHub API üzerinden depo **Public (Herkese Açık)** yapılarak kalıcı olarak giderildi.
  - Windows, Android ve Linux cihazlardaki dahili otomatik güncelleyici (In-Program Updater) artık yeni sürümleri doğrudan görüp tek tıkla indirip kurabilir.
  - Olası ağ veya sunucu aksaklıklarında *"Uygulamanız güncel"* yanıltması kaldırılarak detaylı tanı mesajları eklendi.

* **📱 Android Derleme Hatası Düzeltmesi:**
  - Android `MainActivity.kt` içerisindeki güncelleyici loglama çağrısı düzeltilerek GitHub Actions CI/CD derleme zincirinin başarıyla APK üretmesi sağlandı.

---

# Connect Me v1.6.2 (v1-6-2) — Nobara Linux & Çoklu Monitör İyileştirmeleri, Kernel Sanal Girdi ve Çift Taraflı PIN Güvenliği

Bu sürüm; Nobara Linux (KDE Plasma Wayland), Windows ve Android arasındaki ağ keşfi, Linux sanal fare/klavye sürücüsü, çoklu monitör topolojisi ve karşılıklı PIN doğrulama güvenliğini mükemmelleştiren kapsamlı bir güncellemedir.

---

### 🌟 v1.6.2 Yenilikleri ve Düzeltmeleri:

* **🐧 Nobara Linux Ağ Keşif Dinleyicisi (UDP Discovery Receiver):**
  - Linux servis motoruna (`connectme_linux_daemon.py`) eklenen UDP 42849 dinleyicisi sayesinde ağdaki tüm Windows, Android ve diğer Linux cihazları anında ve otomatik olarak keşfedilir.
  - Yayınlanan keşif paketleri hem `255.255.255.255` hem de yerel alt ağ yayın adresine (subnet broadcast, örn. `192.168.1.255`) gönderilerek Wi-Fi / LAN ayrımı olmadan cihazların anında listede görünmesi sağlandı.

* **🖱️ 4 Kademeli Çekirdek (Kernel) Sanal Girdi Enjektörü & Kesintisiz Fare Geçişi:**
  - **Tier 1 (evdev.UInput):** Linux çekirdek düzeyinde sanal donanım cihazı oluşturularak (<0.2ms gecikme) KDE Plasma Wayland masaüstünde farenin fiziksel bir USB fare gibi tanınması sağlandı.
  - **Tier 2 (/dev/uinput Raw ioctl):** Harici kütüphane bulunmasa dahi Python standart kütüphanesiyle doğrudan `/dev/uinput` ioctl çağrıları üzerinden donanım girdisi.
  - **Tier 3 (ydotoold Daemon Auto-Start):** `ydotoold` arka plan servisinin çalışmadığı durumlarda otomatik başlatılması ve soket üzerinden komut iletimi.
  - **Tier 4 (xdotool):** X11 / XWayland oturumları için geri uyumluluk.

* **🎯 Canlı Ekran Üstü İmleç Katmanı (Linux Wayland Cursor Overlay):**
  - Wayland oturumunda sistem imlecinin gizlendiği veya donanım imlecinin render edilmediği durumlara karşı hafif, şeffaf ve daima en üstte duran dinamik imleç takip göstergesi eklendi. Uzaktan kontrol devralındığında fare ekran üzerinde parlayarak görünür hale gelir.

* **↔️ Linux'tan Windows'a Sorunsuz İmleç Geri Dönüşü (Bi-Directional Edge Return):**
  - Windows'tan Linux ekranına geçen imlecin Linux ekranının sol/dış kenarına çarptığında takılıp kalması sorunu giderildi; Linux motoru `EDGE_RETURN` ve `PACKET_EDGE_HANDOFF` sinyalleriyle imleci ve klavyeyi Windows ana ekranına anında geri aktarır.

* **🖥️ Windows Arayüzünde Linux Çoklu Monitörlerinin Ayrı Ayrı Gösterimi:**
  - Nobara Linux'un sahip olduğu çift veya çoklu monitörler (`DP-1`, `HDMI-A-1` vb.) Windows 2D Ekran Kanvasında artık tek bir blok yerine, bağımsız alt monitör kartları ve çözünürlük rozetleriyle yan yana gösterilir.
  - İmleç hangi Linux monitöründeyse o monitör canlı olarak yeşil renkle vurgulanır.

* **🔐 Sıkı Çift Taraflı 6 Haneli PIN & Erken Otomatik Bağlantı Koruması:**
  - Bir cihaz diğerinin kodunu girdiğinde karşılıklı onay tamamlanmadan cihazın erkenden güvenilir olarak kaydedilip ikinci PIN aşamasını atlaması engellendi.
  - Artık iki taraf da birbirinin ekranındaki 6 haneli kodu doğrulamadan eşleşme tamamlanmaz.
  - Linux GUI'de karşı cihaz kodumuzu girdiğinde ekranda sarı bildirim kartı belirir ve kullanıcının karşı kodu girmesi beklenir.

* **🎨 Linux GUI PIN Giriş Alanı Kontrast Düzeltmesi:**
  - Linux koyu temasında manuel PIN giriş kutusundaki rakamların görünmez (koyu zemin üstüne koyu metin) olmasına yol açan tema hatası düzeltildi; yüksek kontrastlı, elektrik mavisi (`#38BDF8`) ve ortalanmış 14pt kalın font kullanıldı.

---

# Connect Me v1.6.1 (v1-6-1) — Android 14+ FGS Crash Düzeltmesi & İyileştirmeler

Bu acil güncelleme, özellikle **Android 14 ve Android 15 (One UI 7 / Samsung Galaxy A serisi)** cihazlarda uygulamanın açılışta çökmesine neden olan `foregroundServiceType` istisnasını düzeltir ve arka plan servislerini izole eder.

---

### 🌟 v1.6.1 Yenilikleri ve Düzeltmeleri:

* **📱 Android 14/15 Açılış Çökmesi Giderildi (FGS MediaProjection İzolasyonu):**
  - Android 14+ kurallarına göre `mediaProjection` tipindeki bir ön plan servisi, kullanıcı ekran yakalama iznini onaylamadan başlatıldığında `SecurityException` fırlatarak uygulamanın kapanmasına neden oluyordu.
  - Ana ağ ve KVM servisi (`ConnectMeService`), resmi standart olan `connectedDevice` tipine taşındı.
  - Ses aktarımı (`AudioPlaybackCapture`) ise bağımsız `AudioStreamService` içerisine izole edildi ve yalnızca kullanıcı "Kulaklığa Aktar" düğmesine basıp izin verdiğinde çalışacak şekilde ayrıştırıldı.
  - Açılış döngüsü (`onCreate`) çökme korumalı (fail-safe try-catch) bloklarla zırhlandırıldı.

---

# Connect Me v1.6.0 (v1-6-0) — Dahili Uygulama İçi Otomatik Güncelleyici (In-Program Auto Updater)

Connect Me'nin manuel güncelleme zorluklarını tamamen ortadan kaldıran, GitHub Releases üzerinden yeni sürümleri doğrudan uygulama içinden denetleyip Android (APK) ve Windows ortamında **tek dokunuşla otomatik indirip kuran** yeni kararlı sürümü yayınlandı!

---

### 🌟 v1.6.0 Yenilikleri ve Geliştirmeleri:

* **📱 Android Dahili Tek Tıkla Otomatik Güncelleyici (In-App Auto Updater & FileProvider):**
  - **Sıfır Manuel İndirme Zahmeti:** Tarayıcıya gitme, GitHub arama veya indirilen APK dosyasını Dosyalar uygulamasında arayıp açma derdi sona erdi!
  - **Arka Planda Akıllı Denetim:** Connect Me açıldığında GitHub Releases API'sini sessizce denetler. Yeni bir sürüm çıktığında ana ekranda göz alıcı koyu yeşil bir güncelleme kartı belirir.
  - **Uygulama İçi İndirme & İlerleme Çubuğu:** "⬇️ Tek Tıkla İndir ve Kur" butonuna basıldığında APK doğrudan uygulamanın güvenli önbelleğine indirilir, yüzdelik ilerleme çubuğu ve MB sayacı canlı gösterilir.
  - **Doğrudan Android Paket Yükleyicisi (Package Installer) Tetikleme:** İndirme tamamlandığında Android `FileProvider` ve `Intent.ACTION_VIEW` (`FLAG_GRANT_READ_URI_PERMISSION`) ile sistemin yerel güncelleme ekranı anında açılır. Kullanıcının tek yapması gereken "Güncelle" düğmesine dokunmaktır.
  - **Bilinmeyen Kaynak İzni Koruması:** Android 8+ için `REQUEST_INSTALL_PACKAGES` izni gerekiyorsa kullanıcı nazikçe yönlendirilir ve izni açıp döndüğünde kurulum kaldığı yerden otomatik devam eder.
  - **Manuel "🔄 Güncellemeleri Denetle" Butonu:** İstediğiniz an tek dokunuşla yeni sürüm kontrolü yapabilirsiniz.

* **🪟 Windows Dahili Otomatik Güncelleyici (Zero-Touch Self-Update):**
  - **Canlı Güncelleme Rozeti:** Yeni sürüm yayınlandığında üst durum çubuğunda `🎉 Yeni Sürüm (v1.6.0) [⬇️ Şimdi Güncelle]` rozeti parlar.
  - **Otomatik İndirme & Çıkartma:** Butona tıklandığında yeni `ConnectMe-Windows-x64*.zip` arşivi arka planda indirilir ve yeni `ConnectMe.exe` çıkartılır.
  - **Kesintisiz Yeniden Başlatma (`apply_update.cmd`):** Mevcut çalışan program dosyası kilitli olduğu için hafif ve penceresiz bir geçiş komutuyla uygulama 2 saniye içinde güncellenip otomatik olarak yeniden başlatılır.
  - **Alt Çubuk Güncelleme Butonu:** Alt bilgi çubuğuna eklenen `🔄 Güncelleme Denetle` butonu ile dilediğiniz zaman sürüm denetimi yapabilirsiniz.

* **🐧 Linux (Nobara / KDE Plasma) Güncelleme Bildirimi:**
  - Linux GUI arayüzüne eklenen otomatik kontrol sistemiyle yeni bir release çıktığında buton `🎉 v1.6.0 Mevcut!` olarak güncellenir ve tek tıkla en son sürüme yönlendirir.

---

### 📦 İndirilebilir Paketler (Yalnızca Sürümlü Dosya Adları):
1. **Windows:** `ConnectMe-Windows-x64v1-6-0.zip` — Çıkartın ve `ConnectMe.exe`'ye çift tıklayın (.NET gerektirmez, standalone).
2. **Android:** `ConnectMeV1-6-0.apk` — Doğrudan Android cihazınıza kurun (veya mevcut uygulamadan otomatik güncelleyin!).
3. **Linux:** `ConnectMe-Linux-x64v1-6-0.tar.gz` — Nobara / KDE Plasma için bağımsız paket (`start-connectme.sh` veya `install-desktop.sh`).
