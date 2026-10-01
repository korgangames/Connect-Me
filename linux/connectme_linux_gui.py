#!/usr/bin/env python3
"""
Connect Me — Linux (Nobara / KDE Plasma / Wayland & X11) Gelişmiş Kontrol Paneli (GUI)
Korgan Games (v1.3 Tam Linux Entegrasyonu)

- 2D Ekran Konfigürasyonu Kanvası
- Çift Taraflı 6 Haneli PIN Eşleşmesi
- Güvenilir Cihaz Yönetimi (Sıfır-PIN Otomatik Bağlantı)
- Çift Yönlü Evrensel Pano & Ortak Cep (Drop Shelf)
- Donanım Girdi Enjektörü & Çoklu Monitör Durumu
"""

import os
import sys
import shutil
import datetime
import threading
import subprocess
from pathlib import Path
import tkinter as tk
from tkinter import ttk, filedialog, messagebox

sys.path.insert(0, str(Path(__file__).parent))
try:
    from connectme_linux_daemon import ConnectMeLinuxNode, asdict, PhysicalMonitor
except ImportError:
    from linux.connectme_linux_daemon import ConnectMeLinuxNode, asdict, PhysicalMonitor


class ConnectMeLinuxGui:
    def __init__(self, root: tk.Tk):
        self.root = root
        self.root.title("Connect Me v1.4.3 — Linux Kontrol Merkezi")
        self.root.geometry("980x740")
        self.root.minsize(860, 620)
        self.root.configure(bg="#0B1120")

        # Daemon node başlat
        self.node = ConnectMeLinuxNode(on_log=self._log)
        self.node.start()

        self._apply_dark_theme()
        self._build_ui()
        self._start_refresh_timer()

    def _apply_dark_theme(self):
        style = ttk.Style()
        style.theme_use("clam")
        style.configure(".", background="#0B1120", foreground="#F8FAFC", font=("Segoe UI", 10))
        style.configure("TLabel", background="#0B1120", foreground="#F8FAFC")
        style.configure("Header.TLabel", font=("Segoe UI", 14, "bold"), foreground="#38BDF8")
        style.configure("SubHeader.TLabel", font=("Segoe UI", 10), foreground="#94A3B8")
        style.configure("Badge.TLabel", font=("Segoe UI", 12, "bold"), foreground="#4ADE80", background="#1E293B")
        style.configure("TFrame", background="#0B1120")
        style.configure("Card.TFrame", background="#1E293B", relief="flat")
        style.configure("InnerCard.TFrame", background="#0F172A", relief="flat")
        
        # Sekmeler
        style.configure("TNotebook", background="#0B1120", tabmargins=[4, 4, 4, 0])
        style.configure("TNotebook.Tab", background="#1E293B", foreground="#94A3B8", padding=[14, 6], font=("Segoe UI", 10, "bold"))
        style.map("TNotebook.Tab", background=[("selected", "#0284C7")], foreground=[("selected", "#FFFFFF")])

        # Düğmeler
        style.configure("Accent.TButton", font=("Segoe UI", 10, "bold"), background="#0284C7", foreground="#FFFFFF")
        style.map("Accent.TButton", background=[("active", "#0369A1")])
        style.configure("Success.TButton", font=("Segoe UI", 10, "bold"), background="#16A34A", foreground="#FFFFFF")
        style.map("Success.TButton", background=[("active", "#15803D")])
        style.configure("Danger.TButton", font=("Segoe UI", 10), background="#DC2626", foreground="#FFFFFF")
        style.map("Danger.TButton", background=[("active", "#B91C1C")])

    def _build_ui(self):
        # Üst Başlık Barı
        header_frame = ttk.Frame(self.root, padding=16)
        header_frame.pack(fill=tk.X)

        title_box = ttk.Frame(header_frame)
        title_box.pack(side=tk.LEFT)
        ttk.Label(title_box, text="⚡ Connect Me v1.4.3 — Nobara Linux", style="Header.TLabel").pack(anchor=tk.W)
        ttk.Label(title_box, text="KDE Plasma Wayland Çoklu Monitör & KVM Kontrol Paneli", style="SubHeader.TLabel").pack(anchor=tk.W)

        status_box = ttk.Frame(header_frame)
        status_box.pack(side=tk.RIGHT)
        self.injector_badge = ttk.Label(status_box, text=f" Girdi: {self.node.injector.mode.upper()} ", style="Badge.TLabel")
        self.injector_badge.pack(side=tk.RIGHT, padx=4)
        status_badge = ttk.Label(status_box, text=" v1.4.3 | 🟢 Çevrimiçi ", style="Badge.TLabel")
        status_badge.pack(side=tk.RIGHT, padx=4)

        # Hızlı Bilgi & PIN Kartı
        info_card = ttk.Frame(self.root, style="Card.TFrame", padding=12)
        info_card.pack(fill=tk.X, padx=16, pady=4)

        top_row = ttk.Frame(info_card, style="Card.TFrame")
        top_row.pack(fill=tk.X)

        pin_str = f"{self.node.local_pin[:3]} {self.node.local_pin[3:]}"
        self.pin_label = ttk.Label(top_row, text=f"🔐 BU CİHAZIN 6 HANELİ KODU: {pin_str}",
                                   style="Badge.TLabel", font=("Segoe UI", 12, "bold"))
        self.pin_label.pack(side=tk.LEFT)

        copy_pin_btn = ttk.Button(top_row, text="Kodu Kopyala", command=self._copy_local_pin)
        copy_pin_btn.pack(side=tk.LEFT, padx=10)

        ip_addr = self._get_local_ip()
        ttk.Label(top_row, text=f"📱 IP: {ip_addr}  |  v1.3.0 (v1-3-0)  |  UDP: 42850  |  TCP: 42851", foreground="#94A3B8").pack(side=tk.RIGHT)

        # Çoklu Sekme (Notebook)
        self.notebook = ttk.Notebook(self.root)
        self.notebook.pack(fill=tk.BOTH, expand=True, padx=16, pady=8)

        # TAB 1: 2D Ekran Konfigürasyonu Kanvası
        self._build_tab_canvas()

        # TAB 2: Cihazlar & PIN Eşleşmesi
        self._build_tab_devices()

        # TAB 3: Ortak Cep (Drop Shelf)
        self._build_tab_shelf()

        # TAB 4: Ayarlar & Sistem
        self._build_tab_settings()

        # TAB 5: Canlı Tanılama & Etkinlik
        self._build_tab_logs()

    def _build_tab_canvas(self):
        tab = ttk.Frame(self.notebook, padding=12)
        self.notebook.add(tab, text="🖥️ 2D Ekran Konfigürasyonu")

        ctrl_bar = ttk.Frame(tab)
        ctrl_bar.pack(fill=tk.X, pady=4)
        ttk.Label(ctrl_bar, text="Linux ve bağlı cihazların 2D uzamsal kenar haritası:", font=("Segoe UI", 10, "bold")).pack(side=tk.LEFT)
        refresh_btn = ttk.Button(ctrl_bar, text="🔄 Ekranları Yeniden Tara", command=self._refresh_monitors)
        refresh_btn.pack(side=tk.RIGHT)

        self.canvas = tk.Canvas(tab, bg="#0F172A", highlightthickness=1, highlightbackground="#334155")
        self.canvas.pack(fill=tk.BOTH, expand=True, pady=6)

        legend_bar = ttk.Frame(tab)
        legend_bar.pack(fill=tk.X, pady=2)
        ttk.Label(legend_bar, text="🟦 Mavi: Yerel Linux Monitörleri   |   🟩 Yeşil: Dış Geçiş Kenarları   |   🟪 Mor: İç Birleşim Çizgisi (İmleç Serbest)", foreground="#94A3B8", font=("Segoe UI", 9)).pack(side=tk.LEFT)

    def _build_tab_devices(self):
        tab = ttk.Frame(self.notebook, padding=14)
        self.notebook.add(tab, text="🔗 Cihazlar & Güvenlik")

        # Cihaz Listesi
        ttk.Label(tab, text="Ağdaki Keşfedilen Cihazlar (Windows, Android, Linux):", font=("Segoe UI", 10, "bold")).pack(anchor=tk.W, pady=4)
        self.peer_list = tk.Listbox(tab, bg="#1E293B", fg="#F8FAFC", selectbackground="#0284C7",
                                    font=("Consolas", 10), height=7, bd=0, highlightthickness=1, highlightbackground="#334155")
        self.peer_list.pack(fill=tk.X, pady=4)

        # PIN Doğrulama Giriş Çubuğu
        act_box = ttk.Frame(tab, style="Card.TFrame", padding=10)
        act_box.pack(fill=tk.X, pady=8)

        ttk.Label(act_box, text="Karşı Cihazın 6 Haneli Kodu:", font=("Segoe UI", 10, "bold")).pack(side=tk.LEFT, padx=6)
        self.pin_entry = ttk.Entry(act_box, width=12, font=("Segoe UI", 12, "bold"))
        self.pin_entry.pack(side=tk.LEFT, padx=6)

        self.trust_var = tk.BooleanVar(value=True)
        trust_chk = tk.Checkbutton(act_box, text="⭐ Bu Cihaza Güven ve Hatırla (Sıfır-PIN Otomatik Bağlan)",
                                   variable=self.trust_var, bg="#1E293B", fg="#F8FAFC",
                                   selectcolor="#0F172A", activebackground="#1E293B", activeforeground="#38BDF8")
        trust_chk.pack(side=tk.LEFT, padx=12)

        pair_btn = ttk.Button(act_box, text="🔗 Doğrula ve Bağlan", style="Accent.TButton", command=self._on_pair_click)
        pair_btn.pack(side=tk.RIGHT, padx=6)

        # Kayıtlı Güvenilir Cihazlar
        ttk.Label(tab, text="Kayıtlı Güvenilir Cihazlar (Otomatik Bağlananlar):", font=("Segoe UI", 10, "bold")).pack(anchor=tk.W, pady=(12, 4))
        self.trusted_list = tk.Listbox(tab, bg="#1E293B", fg="#4ADE80", selectbackground="#0284C7",
                                       font=("Consolas", 10), height=4, bd=0, highlightthickness=1, highlightbackground="#334155")
        self.trusted_list.pack(fill=tk.X, pady=4)

        t_btn_bar = ttk.Frame(tab)
        t_btn_bar.pack(fill=tk.X, pady=2)
        revoke_btn = ttk.Button(t_btn_bar, text="🗑️ Seçili Cihazın Güvenini Kaldır (Unut)", style="Danger.TButton", command=self._on_revoke_trust)
        revoke_btn.pack(side=tk.RIGHT)

    def _build_tab_shelf(self):
        tab = ttk.Frame(self.notebook, padding=14)
        self.notebook.add(tab, text="📦 Ortak Cep (Drop Shelf)")

        bar = ttk.Frame(tab)
        bar.pack(fill=tk.X, pady=4)

        send_btn = ttk.Button(bar, text="📤 Dosya Gönder (Windows / Android)...", style="Success.TButton", command=self._on_send_file)
        send_btn.pack(side=tk.LEFT, padx=4)

        open_folder_btn = ttk.Button(bar, text="📂 Ortak Cep Klasörünü Aç", command=self._open_shelf_folder)
        open_folder_btn.pack(side=tk.LEFT, padx=6)

        clear_btn = ttk.Button(bar, text="Temizle", command=self._clear_shelf_folder)
        clear_btn.pack(side=tk.RIGHT, padx=4)

        ttk.Label(tab, text="Ortak Cepteki Dosyalar (~/Downloads/ConnectMe-Shelf):", font=("Segoe UI", 10, "bold")).pack(anchor=tk.W, pady=(8, 4))
        self.shelf_list = tk.Listbox(tab, bg="#1E293B", fg="#F8FAFC", selectbackground="#0284C7",
                                     font=("Segoe UI", 10), height=12, bd=0, highlightthickness=1, highlightbackground="#334155")
        self.shelf_list.pack(fill=tk.BOTH, expand=True, pady=4)

    def _build_tab_settings(self):
        tab = ttk.Frame(self.notebook, padding=16)
        self.notebook.add(tab, text="⚙️ Ayarlar & Donanım")

        # Sistem Başlangıcı
        card1 = ttk.Frame(tab, style="Card.TFrame", padding=14)
        card1.pack(fill=tk.X, pady=6)
        ttk.Label(card1, text="🚀 Sistem Başlangıcı (Autostart)", font=("Segoe UI", 11, "bold"), foreground="#38BDF8").pack(anchor=tk.W)
        self.autostart_var = tk.BooleanVar(value=self._check_autostart())
        chk = tk.Checkbutton(card1, text="Nobara / KDE Plasma açılışında Connect Me'yi otomatik başlat",
                             variable=self.autostart_var, command=self._toggle_autostart,
                             bg="#1E293B", fg="#F8FAFC", selectcolor="#0F172A", activebackground="#1E293B", activeforeground="#38BDF8")
        chk.pack(anchor=tk.W, pady=6)

        # Girdi Enjeksiyonu
        card2 = ttk.Frame(tab, style="Card.TFrame", padding=14)
        card2.pack(fill=tk.X, pady=6)
        ttk.Label(card2, text="⌨️ Fare & Klavye Enjeksiyonu (Linux Virtual Input)", font=("Segoe UI", 11, "bold"), foreground="#38BDF8").pack(anchor=tk.W)
        ttk.Label(card2, text=f"Mevcut Aktif Enjektör: {self.node.injector.mode.upper()}", foreground="#4ADE80").pack(anchor=tk.W, pady=2)
        ttk.Label(card2, text="• ydotool: Nobara Linux Wayland / KDE Plasma için en hızlı yerel enjektör.\n• /dev/uinput: Çekirdek düzeyinde sanal fare ve klavye sürücüsü.\n• xdotool: X11 ve XWayland oturumları için geri uyumluluk köprüsü.",
                  foreground="#94A3B8").pack(anchor=tk.W, pady=4)

        # Evrensel Pano Durumu
        card3 = ttk.Frame(tab, style="Card.TFrame", padding=14)
        card3.pack(fill=tk.X, pady=6)
        ttk.Label(card3, text="📋 Evrensel Pano (Universal Clipboard)", font=("Segoe UI", 11, "bold"), foreground="#38BDF8").pack(anchor=tk.W)
        clip_backend = "wl-clipboard (Wayland)" if shutil.which("wl-copy") else ("xclip (X11)" if shutil.which("xclip") else "Standart Python Pano Köprüsü")
        ttk.Label(card3, text=f"Pano Arka Ucu: {clip_backend}", foreground="#4ADE80").pack(anchor=tk.W, pady=2)
        ttk.Label(card3, text="Çift yönlü pano senkronizasyonu aktiftir. Metin kopyaladığınızda tüm onaylı Windows ve Android cihazlara anında iletilir.", foreground="#94A3B8").pack(anchor=tk.W)

    def _build_tab_logs(self):
        tab = ttk.Frame(self.notebook, padding=12)
        self.notebook.add(tab, text="📊 Canlı Tanılama & Ağ")

        # Üst Araç Çubuğu
        btn_bar = ttk.Frame(tab)
        btn_bar.pack(fill=tk.X, pady=(0, 6))

        ttk.Button(btn_bar, text="📋 Panoya Kopyala", command=self._copy_logs_to_clipboard).pack(side=tk.LEFT, padx=3)
        ttk.Button(btn_bar, text="💾 Farklı Kaydet (Dışa Aktar)...", style="Success.TButton", command=self._export_logs).pack(side=tk.LEFT, padx=3)
        ttk.Button(btn_bar, text="📄 Log Dosyasını Aç", command=self._open_log_file).pack(side=tk.LEFT, padx=3)
        ttk.Button(btn_bar, text="📂 Klasörü Aç", command=self._open_log_folder).pack(side=tk.LEFT, padx=3)
        ttk.Button(btn_bar, text="🧹 Temizle", command=self._clear_logs).pack(side=tk.RIGHT, padx=3)

        self.log_box = tk.Text(tab, bg="#020617", fg="#38BDF8", font=("Consolas", 9),
                               bd=0, state=tk.DISABLED, highlightthickness=1, highlightbackground="#1E293B")
        self.log_box.pack(fill=tk.BOTH, expand=True, pady=4)

    def _start_refresh_timer(self):
        self._refresh_state()
        self.root.after(1500, self._start_refresh_timer)

    def _refresh_state(self):
        # 1. Cihazlar Listesi
        self.peer_list.delete(0, tk.END)
        if not self.node.peers:
            self.peer_list.insert(tk.END, "  [Henüz ağda başka bir Connect Me cihazı bulunamadı. Cihazlarınızda programı açık tutun.]")
        else:
            for pid, p in self.node.peers.items():
                name = p.get("deviceName", pid)
                ip = p.get("ipAddress", "")
                is_mut = p.get("isMutuallyPaired", False)
                is_trust = p.get("is_trusted", False)
                tag = "⭐ " if is_trust else ""
                status = f"{tag}🟢 ÇİFT TARAFLI ONNAYLI (Aktif)" if is_mut else (f"{tag}🟡 Karşı Onay Bekliyor" if p.get("out_ok") else f"{tag}⚪ Eşleşme Bekleniyor")
                self.peer_list.insert(tk.END, f"  🖥️ {name} ({ip}) — {status}")

        # 2. Güvenilir Cihazlar Listesi
        self.trusted_list.delete(0, tk.END)
        if not self.node.trusted_devices:
            self.trusted_list.insert(tk.END, "  (Henüz kayıtlı güvenilir cihaz yok)")
        else:
            for d in self.node.trusted_devices.values():
                self.trusted_list.insert(tk.END, f"  ⭐ {d.get('deviceName')} ({d.get('deviceId')}) — Otomatik Bağlantı: AÇIK")

        # 3. Ortak Cep Dosyaları
        self.shelf_list.delete(0, tk.END)
        files = list(self.node.shelf_dir.glob("*"))
        if not files:
            self.shelf_list.insert(tk.END, "  Ortak cepte henüz dosya yok. Bilgisayar veya Android'den dosya gönderebilirsiniz.")
        else:
            for f in sorted(files, key=lambda x: x.stat().st_mtime, reverse=True)[:20]:
                size_kb = f.stat().st_size / 1024
                self.shelf_list.insert(tk.END, f"  📥 {f.name} ({size_kb:.1f} KB)")

        # 4. 2D Kanvas Çizimi
        self._draw_2d_canvas()

    def _draw_2d_canvas(self):
        self.canvas.delete("all")
        cw = self.canvas.winfo_width()
        ch = self.canvas.winfo_height()
        if cw < 50 or ch < 50:
            return

        min_x, min_y, total_w, total_h = self.node.topology.virtual_desktop_bounds()
        scale = min((cw - 120) / total_w, (ch - 100) / total_h)
        offset_x = (cw - total_w * scale) / 2
        offset_y = (ch - total_h * scale) / 2

        # Grid arka plan
        for gx in range(0, cw, 40):
            self.canvas.create_line(gx, 0, gx, ch, fill="#131F37", width=1)
        for gy in range(0, ch, 40):
            self.canvas.create_line(0, gy, cw, gy, fill="#131F37", width=1)

        # Yerel Monitörler
        for m in self.node.topology.monitors:
            x1 = offset_x + (m.virtualX - min_x) * scale
            y1 = offset_y + (m.virtualY - min_y) * scale
            x2 = x1 + m.width * scale
            y2 = y1 + m.height * scale

            # Monitör kutusu
            self.canvas.create_rectangle(x1, y1, x2, y2, fill="#1E293B", outline="#38BDF8", width=2)
            prim_txt = " [BİRİNCİL]" if m.isPrimary else ""
            self.canvas.create_text(x1 + 10, y1 + 14, anchor=tk.W, text=f"{m.name}{prim_txt}", fill="#F8FAFC", font=("Segoe UI", 9, "bold"))
            self.canvas.create_text(x1 + 10, y1 + 32, anchor=tk.W, text=f"{m.width}x{m.height} @ ({m.virtualX}, {m.virtualY})", fill="#94A3B8", font=("Segoe UI", 8))

        # Bağlı Cihazlar (Sağ ve Sol Kenarlara Temsili Eşleşmeler)
        paired_peers = [p for p in self.node.peers.values() if p.get("isMutuallyPaired")]
        for idx, p in enumerate(paired_peers):
            px1 = offset_x + total_w * scale + 15
            py1 = offset_y + idx * 80
            px2 = px1 + 110
            py2 = py1 + 65
            self.canvas.create_rectangle(px1, py1, px2, py2, fill="#14532D", outline="#4ADE80", width=2)
            self.canvas.create_text(px1 + 8, py1 + 14, anchor=tk.W, text=f"📱 {p.get('deviceName', 'Peer')[:10]}", fill="#FFFFFF", font=("Segoe UI", 8, "bold"))
            self.canvas.create_text(px1 + 8, py1 + 30, anchor=tk.W, text="Bağlı", fill="#86EFAC", font=("Segoe UI", 8))
            self.canvas.create_line(offset_x + total_w * scale, py1 + 32, px1, py1 + 32, fill="#4ADE80", dash=(4, 2), width=2)

    def _refresh_monitors(self):
        mons = self.node.topology.refresh_monitors()
        self._log(f"[Topoloji] Çoklu monitör düzeni güncellendi ({len(mons)} ekran algılandı).")
        self._draw_2d_canvas()

    def _on_pair_click(self):
        pin = self.pin_entry.get().strip()
        if len(pin) != 6 or not pin.isdigit():
            messagebox.showwarning("Geçersiz Kod", "Lütfen karşı cihazın ekranında görünen 6 haneli kodu girin.")
            return

        if not self.node.peers:
            messagebox.showinfo("Cihaz Yok", "Ağda henüz eşleşilecek bir cihaz bulunamadı.")
            return

        target_peer = list(self.node.peers.values())[0]
        req_trust = self.trust_var.get()
        token = f"trust-{self.node.device_id}-{os.urandom(8).hex()}" if req_trust else ""

        self._log(f"'{target_peer.get('deviceName')}' için PIN ({pin}) doğrulanıyor...")
        threading.Thread(target=self._send_pair_request, args=(target_peer, pin, req_trust, token), daemon=True).start()

    def _send_pair_request(self, peer: dict, pin: str, req_trust: bool, token: str):
        try:
            import socket
            s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            s.settimeout(5.0)
            s.connect((peer["ipAddress"], peer.get("tcpControlPort", 42851)))
            hdr = {
                "type": "PAIR_REQUEST",
                "senderId": self.node.device_id,
                "senderName": self.node.device_name,
                "senderPlatform": self.node.platform,
                "senderUdpPort": 42850,
                "senderTcpPort": 42851,
                "targetPin": pin,
                "requestTrust": req_trust,
                "trustToken": token,
                "senderMonitors": [asdict(m) for m in self.node.topology.monitors]
            }
            self.node._send_tcp_frame(s, hdr)
            prefix = self.node._recv_exact(s, 12)
            if len(prefix) == 12:
                import struct, json
                jlen, _ = struct.unpack("<iq", prefix)
                resp = json.loads(self.node._recv_exact(s, jlen).decode("utf-8"))
                if resp.get("type") == "PAIR_VERIFY_ACK":
                    peer["out_ok"] = True
                    if resp.get("isMutualComplete"):
                        peer["isMutuallyPaired"] = True
                        self._log(f"✅ '{peer.get('deviceName')}' ile karşılıklı eşleşme tamamlandı!")
                    else:
                        self._log(f"🔔 '{peer.get('deviceName')}' kodunuzu onayladı! Bağlantıyı tamamlamak için o da sizin kodunuzu ({self.node.local_pin}) girmeli.")
                elif resp.get("type") == "PAIR_REJECT":
                    self._log(f"⚠️ Karşı taraf girilen PIN kodunu reddetti.")
            s.close()
        except Exception as e:
            self._log(f"⚠️ Eşleşme bağlantı hatası: {e}")

    def _on_revoke_trust(self):
        sel = self.trusted_list.curselection()
        if not sel:
            messagebox.showinfo("Seçim Yapın", "Lütfen güvenini kaldırmak istediğiniz cihazı listeden seçin.")
            return
        keys = list(self.node.trusted_devices.keys())
        if sel[0] < len(keys):
            target_id = keys[sel[0]]
            self.node.revoke_trust(target_id)
            messagebox.showinfo("Başarılı", "Cihaz güvenilirler listesinden çıkarıldı.")

    def _on_send_file(self):
        file_path = filedialog.askopenfilename(title="Ortak Cebe Gönderilecek Dosyayı Seçin")
        if not file_path:
            return
        p = Path(file_path)
        threading.Thread(target=self._send_file_async, args=(p,), daemon=True).start()

    def _send_file_async(self, p: Path):
        self._log(f"📤 '{p.name}' ({p.stat().st_size / 1024:.1f} KB) bağlı cihazlara aktarılıyor...")
        ok = self.node.send_file_to_peer(p)
        if ok:
            self._log(f"✅ '{p.name}' başarıyla gönderildi!")
        else:
            self._log(f"⚠️ Dosya gönderilemedi. Cihazların bağlı ve onaylı olduğundan emin olun.")

    def _open_shelf_folder(self):
        try:
            subprocess.Popen(["xdg-open", str(self.node.shelf_dir)])
        except Exception:
            os.system(f"xdg-open '{self.node.shelf_dir}' &")

    def _clear_shelf_folder(self):
        if messagebox.askyesno("Ortak Cebi Temizle", "Ortak cep klasöründeki dosyalar silinsin mi?"):
            for f in self.node.shelf_dir.glob("*"):
                try:
                    f.unlink()
                except Exception:
                    pass
            self._log("[Ortak Cep] Klasör temizlendi.")

    def _copy_local_pin(self):
        self.root.clipboard_clear()
        self.root.clipboard_append(self.node.local_pin)
        messagebox.showinfo("Kopyalandı", f"PIN Kodu ({self.node.local_pin}) panoya kopyalandı!")

    def _get_local_ip(self) -> str:
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            s.connect(("8.8.8.8", 80))
            ip = s.getsockname()[0]
            s.close()
            return ip
        except Exception:
            return "127.0.0.1"

    def _check_autostart(self) -> bool:
        auto_file = Path.home() / ".config" / "autostart" / "ConnectMe.desktop"
        return auto_file.exists()

    def _toggle_autostart(self):
        auto_dir = Path.home() / ".config" / "autostart"
        auto_dir.mkdir(parents=True, exist_ok=True)
        auto_file = auto_dir / "ConnectMe.desktop"
        
        if self.autostart_var.get():
            curr_dir = Path(__file__).resolve().parent
            content = f"""[Desktop Entry]
Version=1.0
Type=Application
Name=Connect Me
GenericName=Çapraz Cihaz KVM ve Pano Paylaşımı
Exec=python3 "{curr_dir}/connectme_linux_gui.py"
Path={curr_dir}
Icon=preferences-desktop-display
Terminal=false
Categories=Utility;Network;
StartupNotify=false
X-GNOME-Autostart-enabled=true
"""
            with open(auto_file, "w", encoding="utf-8") as f:
                f.write(content)
            self._log("[Sistem] Nobara / KDE Plasma başlangıcında otomatik başlatma AÇILDI.")
        else:
            if auto_file.exists():
                auto_file.unlink()
            self._log("[Sistem] Başlangıçta otomatik başlatma KAPATILDI.")

    def _log(self, text: str):
        def _append():
            if not hasattr(self, "log_box"):
                return
            self.log_box.config(state=tk.NORMAL)
            self.log_box.insert(tk.END, f"{text}\n")
            self.log_box.see(tk.END)
            self.log_box.config(state=tk.DISABLED)
        self.root.after(0, _append)

    def _get_full_logs(self) -> str:
        log_file = Path.home() / ".config" / "connectme" / "connectme.log"
        if log_file.exists():
            try:
                with open(log_file, "r", encoding="utf-8") as f:
                    return f.read()
            except Exception:
                pass
        if hasattr(self, "log_box"):
            return self.log_box.get("1.0", tk.END).strip()
        return ""

    def _copy_logs_to_clipboard(self):
        content = self._get_full_logs()
        if not content:
            messagebox.showinfo("Connect Me", "Kopyalanacak log kaydı bulunamadı.")
            return
        self.root.clipboard_clear()
        self.root.clipboard_append(content)
        self._log("📋 [Log] Tüm tanılama logları panoya kopyalandı!")

    def _export_logs(self):
        content = self._get_full_logs()
        timestamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
        target = filedialog.asksaveasfilename(
            title="Connect Me Linux Tanılama Günlüğünü Dışa Aktar",
            initialfile=f"ConnectMe-Linux-Logs-{timestamp}.txt",
            defaultextension=".txt",
            filetypes=[("Metin Dosyaları", "*.txt;*.log"), ("Tüm Dosyalar", "*.*")]
        )
        if not target:
            return
        try:
            report = (
                f"=== Connect Me Linux Tanılama Günlüğü ===\n"
                f"Oluşturulma Tarihi: {datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n"
                f"Sürüm: v1.4.3\n"
                f"Cihaz: {self.node.device_name} ({self.node.device_id})\n"
                f"Platform: {self.node.platform}\n"
                f"Aktif Girdi Enjektörü: {self.node.injector.mode.upper()}\n"
                f"Monitör Sayısı: {len(self.node.topology.monitors)}\n"
                f"Yerel PIN: {self.node.local_pin}\n"
                f"Kayıtlı Güvenilir Cihaz Sayısı: {len(self.node.trusted_devices)}\n"
                f"===========================================\n\n"
                f"{content}\n"
            )
            with open(target, "w", encoding="utf-8") as f:
                f.write(report)
            self._log(f"💾 [Dışa Aktar] Log dosyası başarıyla kaydedildi: '{target}'")
            messagebox.showinfo("Connect Me", f"Loglar başarıyla kaydedildi:\n{target}")
        except Exception as ex:
            messagebox.showerror("Hata", f"Log kaydedilemedi: {ex}")

    def _open_log_file(self):
        log_file = Path.home() / ".config" / "connectme" / "connectme.log"
        log_file.parent.mkdir(parents=True, exist_ok=True)
        if not log_file.exists():
            with open(log_file, "w", encoding="utf-8") as f:
                f.write(f"[Connect Me Linux Günlüğü Başlangıcı] v1.4.3\n")
        try:
            subprocess.Popen(["xdg-open", str(log_file)])
        except Exception as ex:
            self._log(f"[Hata] Log dosyası açılamadı: {ex}")

    def _open_log_folder(self):
        log_dir = Path.home() / ".config" / "connectme"
        log_dir.mkdir(parents=True, exist_ok=True)
        try:
            subprocess.Popen(["xdg-open", str(log_dir)])
        except Exception as ex:
            self._log(f"[Hata] Klasör açılamadı: {ex}")

    def _clear_logs(self):
        if hasattr(self, "log_box"):
            self.log_box.config(state=tk.NORMAL)
            self.log_box.delete("1.0", tk.END)
            self.log_box.config(state=tk.DISABLED)
        log_file = Path.home() / ".config" / "connectme" / "connectme.log"
        try:
            if log_file.exists():
                with open(log_file, "w", encoding="utf-8") as f:
                    f.write(f"[Connect Me Linux Günlüğü Sıfırlandı]\n")
            self._log("🧹 [Temizle] Canlı telemetri ve log dosyası temizlendi.")
        except Exception as ex:
            self._log(f"[Hata] Log temizlenemedi: {ex}")


def main():
    root = tk.Tk()
    app = ConnectMeLinuxGui(root)
    root.protocol("WM_DELETE_WINDOW", root.destroy)
    root.mainloop()


if __name__ == "__main__":
    main()
