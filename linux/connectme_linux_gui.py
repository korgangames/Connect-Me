#!/usr/bin/env python3
"""
Connect Me — Linux (Nobara / KDE Plasma / Wayland & X11) Gelişmiş Kontrol Paneli (GUI)
Korgan Games (v1.6.6 Kalıcı Cihaz Kimlikleri, Çoklu Cihaz PIN & Otomatik Reconnect)

- 2D Ekran Konfigürasyonu Kanvası
- Çift Yönlü UDP Cihaz Keşfi (Canlı Algılama)
- Çift Taraflı 6 Haneli PIN Eşleşmesi (Yüksek Kontrastlı Görünür Metin)
- Canlı İmleç Katmanı (KDE Wayland'de İmlecin Daima Görünür Olması)
- Güvenilir Cihaz Yönetimi (Sıfır-PIN Otomatik Bağlantı)
- Çift Yönlü Evrensel Pano & Ortak Cep (Drop Shelf)
- Donanım Girdi Enjektörü & Çoklu Monitör Durumu
"""

import os
import sys
import json
import shutil
import socket
import datetime
import threading
import subprocess
import tarfile
import tempfile
import urllib.request
import urllib.error
from pathlib import Path
import tkinter as tk
from tkinter import ttk, filedialog, messagebox

sys.path.insert(0, str(Path(__file__).parent))
try:
    from connectme_linux_daemon import ConnectMeLinuxNode, asdict, PhysicalMonitor, VERSION
except ImportError:
    from linux.connectme_linux_daemon import ConnectMeLinuxNode, asdict, PhysicalMonitor, VERSION


class LinuxCursorOverlay:
    """Lightweight floating cursor indicator for Nobara Linux KDE Wayland."""
    def __init__(self, root: tk.Tk):
        self.root = root
        self.win = tk.Toplevel(root)
        self.win.overrideredirect(True)
        self.win.attributes("-topmost", True)
        self.win.config(bg="#38BDF8")
        self.canvas = tk.Canvas(self.win, width=24, height=24, bg="#0284C7", highlightthickness=1, highlightbackground="#38BDF8")
        self.canvas.pack()
        self.canvas.create_oval(3, 3, 21, 21, fill="#0F172A", outline="#38BDF8", width=2)
        self.canvas.create_oval(8, 8, 16, 16, fill="#4ADE80", outline="")
        self.win.withdraw()
        self.visible = False

    def update(self, active: bool, x: int, y: int):
        if active:
            self.win.geometry(f"24x24+{max(0, x-12)}+{max(0, y-12)}")
            if not self.visible:
                self.win.deiconify()
                self.win.lift()
                self.visible = True
        else:
            if self.visible:
                self.win.withdraw()
                self.visible = False


class ConnectMeLinuxGui:
    def __init__(self, root: tk.Tk):
        self.root = root
        self.root.title(f"Connect Me v{VERSION} — Linux Kontrol Merkezi")
        self.root.geometry("980x750")
        self.root.minsize(860, 620)
        self.root.configure(bg="#0B1120")

        self.selected_peer_id: str = ""
        self.peer_canvas_coords = {}
        self._drag_data = {"peer_id": None, "start_x": 0, "start_y": 0, "orig_x": 0, "orig_y": 0}

        # Daemon node başlat
        self.node = ConnectMeLinuxNode(on_log=self._log)
        self.node.on_cursor_update = self._on_cursor_update
        self.node.on_pin_request_received = self._on_pin_request_received
        self.node.start()

        # Ekran üstü imleç göstergesi
        self.cursor_overlay = LinuxCursorOverlay(self.root)

        self._apply_dark_theme()
        self._build_ui()
        self._start_refresh_timer()
        threading.Thread(target=self._check_for_updates, daemon=True).start()

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

        # Giriş kutuları (High-Contrast Input Fields)
        style.configure("TEntry", fieldbackground="#1E293B", foreground="#38BDF8", insertcolor="#38BDF8")
        style.map("TEntry", fieldbackground=[("active", "#1E293B"), ("focus", "#0F172A")], foreground=[("active", "#38BDF8"), ("focus", "#38BDF8")])

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
        ttk.Label(title_box, text=f"⚡ Connect Me v{VERSION} — Nobara Linux", style="Header.TLabel").pack(anchor=tk.W)
        ttk.Label(title_box, text="KDE Plasma Wayland Çoklu Monitör & KVM Kontrol Paneli", style="SubHeader.TLabel").pack(anchor=tk.W)

        status_box = ttk.Frame(header_frame)
        status_box.pack(side=tk.RIGHT)
        self.update_badge_btn = ttk.Button(status_box, text="🔄 Güncelleme Denetle", command=lambda: threading.Thread(target=self._check_for_updates, args=(True,), daemon=True).start())
        self.update_badge_btn.pack(side=tk.RIGHT, padx=4)
        self.injector_badge = ttk.Label(status_box, text=f" Girdi: {self.node.injector.mode.upper()} ", style="Badge.TLabel")
        self.injector_badge.pack(side=tk.RIGHT, padx=4)
        status_badge = ttk.Label(status_box, text=f" v{VERSION} | 🟢 Çevrimiçi ", style="Badge.TLabel")
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
        copy_pin_btn.pack(side=tk.LEFT, padx=(10, 4))

        ip_btn = ttk.Button(top_row, text="🌐 IP ile Bağlan", style="Accent.TButton", command=self._show_manual_ip_dialog)
        ip_btn.pack(side=tk.LEFT, padx=4)

        ip_addr = self._get_local_ip()
        ttk.Label(top_row, text=f"📱 IP: {ip_addr}  |  v{VERSION}  |  UDP: 42850  |  TCP: 42851  |  Ses: 42852", foreground="#94A3B8").pack(side=tk.RIGHT)

        # UInput Sürücü İzni Banner'ı (Wayland Fare Hareketi için)
        self.uinput_banner = ttk.Frame(self.root, style="Card.TFrame", padding=10)
        u_box = ttk.Frame(self.uinput_banner, style="Card.TFrame")
        u_box.pack(fill=tk.X)
        self.uinput_warn_lbl = ttk.Label(
            u_box,
            text="⚠️ Linux Fare Yetkisi Gerekiyor (Wayland): İmlecin serbestçe hareket etmesi için /dev/uinput izni verilmelidir.",
            font=("Segoe UI", 10, "bold"),
            foreground="#F59E0B"
        )
        self.uinput_warn_lbl.pack(side=tk.LEFT, fill=tk.X, expand=True)
        self.uinput_fix_btn = ttk.Button(
            u_box,
            text="🖱️ Tek Tıkla Sürücü İznini Etkinleştir",
            style="Accent.TButton",
            command=self._fix_uinput_permissions
        )
        self.uinput_fix_btn.pack(side=tk.RIGHT, padx=4)
        if self.node.injector.mode in ("none", "xdotool") or (os.path.exists("/dev/uinput") and not os.access("/dev/uinput", os.W_OK)):
            self.uinput_banner.pack(fill=tk.X, padx=16, pady=(4, 0))

        # Çoklu Sekme (Notebook)
        self.notebook = ttk.Notebook(self.root)
        self.notebook.pack(fill=tk.BOTH, expand=True, padx=16, pady=8)

        # TAB 1: 2D Ekran Konfigürasyonu Kanvası
        self._build_tab_canvas()

        # TAB 2: Cihazlar & PIN Eşleşmesi
        self._build_tab_devices()

        # TAB 3: Ortak Cep (Drop Shelf)
        self._build_tab_shelf()

        # TAB 4: Ses Yönlendirme (Audio Stream)
        self._build_tab_audio()

        # TAB 5: Ayarlar & Donanım
        self._build_tab_settings()

        # TAB 6: Canlı Tanılama & Etkinlik
        self._build_tab_logs()

    def _build_tab_canvas(self):
        tab = ttk.Frame(self.notebook, padding=12)
        self.notebook.add(tab, text="🖥️ 2D Ekran Konfigürasyonu")

        ctrl_bar = ttk.Frame(tab)
        ctrl_bar.pack(fill=tk.X, pady=4)
        ttk.Label(ctrl_bar, text="Linux ve bağlı cihazların 2D uzamsal kenar haritası:", font=("Segoe UI", 10, "bold")).pack(side=tk.LEFT)

        ip_canvas_btn = ttk.Button(ctrl_bar, text="🌐 IP ile Cihaz Ekle", style="Accent.TButton", command=self._show_manual_ip_dialog)
        ip_canvas_btn.pack(side=tk.RIGHT, padx=4)

        refresh_btn = ttk.Button(ctrl_bar, text="🔄 Ekranları Yeniden Tara", command=self._refresh_monitors)
        refresh_btn.pack(side=tk.RIGHT, padx=4)

        reset_btn = ttk.Button(ctrl_bar, text="📐 Düzeni Sıfırla", command=self._reset_canvas_layout)
        reset_btn.pack(side=tk.RIGHT, padx=4)

        self.canvas = tk.Canvas(tab, bg="#0F172A", highlightthickness=1, highlightbackground="#334155")
        self.canvas.pack(fill=tk.BOTH, expand=True, pady=6)

        legend_bar = ttk.Frame(tab)
        legend_bar.pack(fill=tk.X, pady=2)
        ttk.Label(legend_bar, text="🟦 Mavi: Yerel Linux Monitörleri   |   🟩 Yeşil: Bağlı Cihazlar (Aktif)   |   🟧 Sarı: PIN Bekleyen Cihazlar   |   🟪 Mor: İç Monitör Geçişi", foreground="#94A3B8", font=("Segoe UI", 9)).pack(side=tk.LEFT)

    def _build_tab_devices(self):
        tab = ttk.Frame(self.notebook, padding=14)
        self.notebook.add(tab, text="🔗 Cihazlar & Güvenlik")

        # Gelen PIN Talebi Uyarısı (Inbound PIN Request Banner)
        self.inbound_card = ttk.Frame(tab, style="Card.TFrame", padding=10)
        self.inbound_label = ttk.Label(self.inbound_card, text="", font=("Segoe UI", 10, "bold"), foreground="#F59E0B")
        self.inbound_label.pack(side=tk.LEFT, fill=tk.X, expand=True)
        self.inbound_btn = ttk.Button(self.inbound_card, text="🔐 Kodu Gir ve Bağlan", style="Accent.TButton", command=self._on_inbound_pair_click)
        self.inbound_btn.pack(side=tk.RIGHT, padx=6)
        # Initially not packed, packed dynamically on request

        # 1. Manuel IP ile Doğrudan Bağlan Kartı (Manual IP Connect Card)
        manual_card = ttk.Frame(tab, style="Card.TFrame", padding=10)
        manual_card.pack(fill=tk.X, pady=(0, 8))

        m_header = ttk.Frame(manual_card, style="Card.TFrame")
        m_header.pack(fill=tk.X)
        ttk.Label(m_header, text="🌐 Manuel IP ile Cihaz Ekle & Doğrudan Bağlan",
                  font=("Segoe UI", 10, "bold"), foreground="#38BDF8").pack(side=tk.LEFT)
        ttk.Label(m_header, text="(Ağ keşfi güvenlik duvarı veya router izolasyonu nedeniyle engelliyse karşı cihazın IP'sini yazın)",
                  font=("Segoe UI", 9), foreground="#94A3B8").pack(side=tk.LEFT, padx=8)

        m_row = ttk.Frame(manual_card, style="Card.TFrame")
        m_row.pack(fill=tk.X, pady=(6, 2))

        ttk.Label(m_row, text="Hedef IP:", font=("Segoe UI", 9, "bold")).pack(side=tk.LEFT, padx=(0, 4))

        local_ip = self._get_local_ip()
        parts = local_ip.split(".")
        default_prefix = f"{parts[0]}.{parts[1]}.{parts[2]}." if len(parts) == 4 and parts[0] != "127" else "192.168.1."

        self.manual_ip_entry = tk.Entry(
            m_row,
            width=18,
            font=("Consolas", 11),
            bg="#0F172A",
            fg="#38BDF8",
            insertbackground="#38BDF8",
            selectbackground="#0284C7",
            selectforeground="#FFFFFF",
            relief="solid",
            bd=1,
            highlightthickness=1,
            highlightbackground="#38BDF8",
            highlightcolor="#38BDF8"
        )
        self.manual_ip_entry.insert(0, default_prefix)
        self.manual_ip_entry.pack(side=tk.LEFT, padx=6)
        self.manual_ip_entry.bind("<Return>", lambda e: self._on_manual_ip_connect())

        manual_connect_btn = ttk.Button(m_row, text="➕ IP ile Cihaz Ekle & Keşfet",
                                        style="Accent.TButton", command=self._on_manual_ip_connect)
        manual_connect_btn.pack(side=tk.LEFT, padx=6)

        scan_btn = ttk.Button(m_row, text="📡 Alt Ağı Tara (Broadcast)", command=self._on_trigger_scan)
        scan_btn.pack(side=tk.LEFT, padx=4)

        # 2. Cihaz Listesi
        ttk.Label(tab, text="Ağdaki Keşfedilen Cihazlar (Windows, Android, Linux):", font=("Segoe UI", 10, "bold")).pack(anchor=tk.W, pady=(4, 2))
        self.peer_list = tk.Listbox(tab, bg="#1E293B", fg="#F8FAFC", selectbackground="#0284C7",
                                    font=("Consolas", 10), height=6, bd=0, highlightthickness=1, highlightbackground="#334155")
        self.peer_list.pack(fill=tk.X, pady=4)
        self.peer_list.bind("<<ListboxSelect>>", self._on_peer_select)
        self.peer_list.bind("<Double-Button-1>", self._on_peer_list_double_click)

        # PIN Doğrulama Giriş Çubuğu (Yüksek Kontrastlı tk.Entry)
        act_box = ttk.Frame(tab, style="Card.TFrame", padding=10)
        act_box.pack(fill=tk.X, pady=8)

        self.pair_prompt_lbl = ttk.Label(act_box, text="Karşı Cihazın 6 Haneli Kodu:", font=("Segoe UI", 10, "bold"))
        self.pair_prompt_lbl.pack(side=tk.LEFT, padx=6)

        # High-contrast Entry widget
        self.pin_entry = tk.Entry(
            act_box,
            width=12,
            font=("Consolas", 14, "bold"),
            bg="#1E293B",
            fg="#38BDF8",
            insertbackground="#38BDF8",
            selectbackground="#0284C7",
            selectforeground="#FFFFFF",
            justify="center",
            relief="solid",
            bd=1,
            highlightthickness=1,
            highlightbackground="#38BDF8",
            highlightcolor="#38BDF8"
        )
        self.pin_entry.pack(side=tk.LEFT, padx=6)

        self.trust_var = tk.BooleanVar(value=True)
        trust_chk = tk.Checkbutton(act_box, text="⭐ Bu Cihaza Güven ve Hatırla (Sıfır-PIN Otomatik Bağlan)",
                                   variable=self.trust_var, bg="#1E293B", fg="#F8FAFC",
                                   selectcolor="#0F172A", activebackground="#1E293B", activeforeground="#38BDF8")
        trust_chk.pack(side=tk.LEFT, padx=12)

        pair_btn = ttk.Button(act_box, text="🔗 Doğrula ve Bağlan", style="Accent.TButton", command=self._on_pair_click)
        pair_btn.pack(side=tk.RIGHT, padx=6)

        disc_btn = ttk.Button(act_box, text="🔌 Bağlantıyı Kes", style="Danger.TButton", command=self._on_disconnect_click)
        disc_btn.pack(side=tk.RIGHT, padx=6)

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

    def _build_tab_audio(self):
        tab = ttk.Frame(self.notebook, padding=16)
        self.notebook.add(tab, text="🎧 Ses Yönlendirme")

        card = ttk.Frame(tab, style="Card.TFrame", padding=14)
        card.pack(fill=tk.X, pady=6)
        ttk.Label(card, text="🎧 Merkez Bilgisayara (Kulaklığa) Ses Aktarımı", font=("Segoe UI", 12, "bold"), foreground="#38BDF8").pack(anchor=tk.W)
        ttk.Label(card, text="Bu Linux bilgisayarının tüm seslerini (müzik, video, oyun, sistem sesleri) ağ üzerinden merkez Windows bilgisayarına ve ona bağlı kulaklığa aktarır.",
                  foreground="#94A3B8", wraplength=700).pack(anchor=tk.W, pady=4)

        row = ttk.Frame(card, style="Card.TFrame")
        row.pack(fill=tk.X, pady=8)
        ttk.Label(row, text="Hedef Windows PC IP:", font=("Segoe UI", 10, "bold")).pack(side=tk.LEFT, padx=4)

        self.audio_ip_entry = tk.Entry(
            row,
            width=16,
            font=("Consolas", 10),
            bg="#1E293B",
            fg="#38BDF8",
            insertbackground="#38BDF8",
            selectbackground="#0284C7",
            selectforeground="#FFFFFF",
            relief="solid",
            bd=1,
            highlightthickness=1,
            highlightbackground="#38BDF8"
        )
        self.audio_ip_entry.pack(side=tk.LEFT, padx=6)

        self.audio_stream_btn = ttk.Button(row, text="🎧 Ses Akışını Başlat", style="Success.TButton", command=self._toggle_audio_stream)
        self.audio_stream_btn.pack(side=tk.LEFT, padx=8)

        self.audio_status_lbl = ttk.Label(card, text="Durum: Boşta (Ses gönderilmiyor)", foreground="#A5B4FC")
        self.audio_status_lbl.pack(anchor=tk.W, pady=4)

        info_card = ttk.Frame(tab, style="InnerCard.TFrame", padding=12)
        info_card.pack(fill=tk.X, pady=8)
        ttk.Label(info_card, text="💡 Ses Motoru Bilgisi:", font=("Segoe UI", 10, "bold"), foreground="#F8FAFC").pack(anchor=tk.W)
        pipewire_str = "Aktif (pw-record bulundu)" if shutil.which("pw-record") else ("Aktif (PulseAudio parec bulundu)" if shutil.which("parec") else "Bulunamadı")
        ttk.Label(info_card, text=f"• Ses Yakalayıcı: {pipewire_str}\n• Format: 48,000 Hz, 16-bit Stereo PCM (S16LE)\n• Port: UDP 42852 (<15ms gecikme)\n• Tüm sesler Windows'taki kulaklık çıkışından net ve senkron olarak çalınır.", foreground="#94A3B8").pack(anchor=tk.W, pady=2)

    def _toggle_audio_stream(self):
        if self.node.audio_streaming:
            self.node.stop_audio_streaming()
            self.audio_stream_btn.configure(text="🎧 Ses Akışını Başlat", style="Success.TButton")
            self.audio_status_lbl.configure(text="Durum: Boşta (Ses gönderilmiyor)", foreground="#A5B4FC")
        else:
            target_ip = self.audio_ip_entry.get().strip()
            if not target_ip:
                paired = [p for p in self.node.peers.values() if p.get("isMutuallyPaired")]
                if paired:
                    target_ip = paired[0].get("ipAddress", "")
            if not target_ip:
                messagebox.showwarning("Hedef IP Gerekli", "Lütfen sesin aktarılacağı Windows bilgisayarın IP adresini girin.")
                return
            self.audio_ip_entry.delete(0, tk.END)
            self.audio_ip_entry.insert(0, target_ip)
            ok = self.node.start_audio_streaming(target_ip)
            if ok:
                self.audio_stream_btn.configure(text="⏹️ Ses Akışını Durdur", style="Danger.TButton")
                self.audio_status_lbl.configure(text=f"Durum: Aktif -> {target_ip}:42852 (Ses kulaklığa akıyor)", foreground="#4ADE80")
            else:
                messagebox.showerror("Hata", "Ses akışı başlatılamadı. PipeWire (pw-record) veya PulseAudio (parec) yüklü olduğundan emin olun.")

    def _build_tab_settings(self):
        tab = ttk.Frame(self.notebook, padding=16)
        self.notebook.add(tab, text="⚙️ Ayarlar & Donanım")

        card1 = ttk.Frame(tab, style="Card.TFrame", padding=14)
        card1.pack(fill=tk.X, pady=6)
        ttk.Label(card1, text="🚀 Sistem Başlangıcı (Autostart)", font=("Segoe UI", 11, "bold"), foreground="#38BDF8").pack(anchor=tk.W)
        self.autostart_var = tk.BooleanVar(value=self._check_autostart())
        chk = tk.Checkbutton(card1, text="Nobara / KDE Plasma açılışında Connect Me'yi otomatik başlat",
                             variable=self.autostart_var, command=self._toggle_autostart,
                             bg="#1E293B", fg="#F8FAFC", selectcolor="#0F172A", activebackground="#1E293B", activeforeground="#38BDF8")
        chk.pack(anchor=tk.W, pady=6)

        card2 = ttk.Frame(tab, style="Card.TFrame", padding=14)
        card2.pack(fill=tk.X, pady=6)
        ttk.Label(card2, text="⌨️ Fare & Klavye Enjeksiyonu (Linux Virtual Input)", font=("Segoe UI", 11, "bold"), foreground="#38BDF8").pack(anchor=tk.W)
        ttk.Label(card2, text=f"Mevcut Aktif Enjektör: {self.node.injector.mode.upper()}", foreground="#4ADE80").pack(anchor=tk.W, pady=2)
        ttk.Label(card2, text="• evdev: Linux çekirdek düzeyinde sanal donanım sürücüsü (<0.2ms gecikme).\n• /dev/uinput: Raw çekirdek sanal fare/klavye arayüzü.\n• ydotool: Wayland için otomatik arka plan servisi.\n• xdotool: X11 oturumları için geri uyumluluk.",
                  foreground="#94A3B8").pack(anchor=tk.W, pady=4)

        card3 = ttk.Frame(tab, style="Card.TFrame", padding=14)
        card3.pack(fill=tk.X, pady=6)
        ttk.Label(card3, text="📋 Evrensel Pano (Universal Clipboard)", font=("Segoe UI", 11, "bold"), foreground="#38BDF8").pack(anchor=tk.W)
        clip_backend = "wl-clipboard (Wayland)" if shutil.which("wl-copy") else ("xclip (X11)" if shutil.which("xclip") else "Standart Python Pano Köprüsü")
        ttk.Label(card3, text=f"Pano Arka Ucu: {clip_backend}", foreground="#4ADE80").pack(anchor=tk.W, pady=2)
        ttk.Label(card3, text="Çift yönlü pano senkronizasyonu aktiftir. Metin kopyaladığınızda tüm onaylı Windows ve Android cihazlara anında iletilir.", foreground="#94A3B8").pack(anchor=tk.W)

    def _build_tab_logs(self):
        tab = ttk.Frame(self.notebook, padding=12)
        self.notebook.add(tab, text="📊 Canlı Tanılama & Ağ")

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

    def _on_peer_select(self, event):
        sel = self.peer_list.curselection()
        if not sel:
            return
        keys = list(self.node.peers.keys())
        if sel[0] < len(keys):
            self.selected_peer_id = keys[sel[0]]
            peer = self.node.peers[self.selected_peer_id]
            self.pair_prompt_lbl.configure(text=f"'{peer.get('deviceName')}' 6 Haneli Kodu:")
            self.pin_entry.focus_set()

    def _on_inbound_pair_click(self):
        if self.selected_peer_id and self.selected_peer_id in self.node.peers:
            self._show_enter_pin_dialog(self.node.peers[self.selected_peer_id])
        else:
            inbound = [p for p in self.node.peers.values() if p.get("in_ok")]
            if inbound:
                self._show_enter_pin_dialog(inbound[0])
            elif self.node.peers:
                self._show_enter_pin_dialog(list(self.node.peers.values())[0])

    def _on_peer_list_double_click(self, event):
        sel = self.peer_list.curselection()
        if not sel:
            return
        keys = list(self.node.peers.keys())
        if sel[0] < len(keys):
            peer_id = keys[sel[0]]
            peer = self.node.peers.get(peer_id)
            if peer:
                self._show_enter_pin_dialog(peer)

    def _on_pin_request_received(self, sender_id: str, sender_name: str):
        def _update():
            self.selected_peer_id = sender_id
            self.inbound_label.configure(
                text=f"🔔 '{sender_name}' sizin 6 haneli kodunuzu girdi! Eşleşmeyi tamamlamak için siz de onun kodunu girin."
            )
            self.inbound_btn.configure(text=f"🔐 '{sender_name}' Kodunu Gir")
            if not self.inbound_card.winfo_ismapped():
                self.inbound_card.pack(fill=tk.X, pady=(0, 8), before=self.peer_list)
            self.pair_prompt_lbl.configure(text=f"'{sender_name}' Ekranındaki Kod:")
            self.pin_entry.focus_set()
        self.root.after(0, _update)

    def _on_cursor_update(self, active: bool, x: int, y: int):
        self.root.after(0, lambda: self.cursor_overlay.update(active, x, y))

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
                mon_cnt = len(p.get("monitors", []))
                mon_str = f" [{mon_cnt} Ekran]" if mon_cnt > 1 else ""
                status = f"{tag}🟢 ÇİFT TARAFLI ONAYLI (Aktif)" if is_mut else (f"{tag}🟡 Karşı Onay Bekliyor" if p.get("out_ok") else f"{tag}⚪ Eşleşme Bekleniyor")
                self.peer_list.insert(tk.END, f"  🖥️ {name} ({ip}){mon_str} — {status}")

            keys = list(self.node.peers.keys())
            if self.selected_peer_id and self.selected_peer_id in keys:
                idx = keys.index(self.selected_peer_id)
                self.peer_list.selection_set(idx)

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

    def _reset_canvas_layout(self):
        self.peer_canvas_coords.clear()
        self._draw_2d_canvas()

    def _on_peer_drag_start(self, event, peer_id: str):
        self.selected_peer_id = peer_id
        coords = self.peer_canvas_coords.get(peer_id, (event.x - 90, event.y - 45))
        self._drag_data["peer_id"] = peer_id
        self._drag_data["start_x"] = event.x
        self._drag_data["start_y"] = event.y
        self._drag_data["orig_x"] = coords[0]
        self._drag_data["orig_y"] = coords[1]

    def _on_peer_drag_motion(self, event):
        pid = self._drag_data.get("peer_id")
        if not pid:
            return
        dx = event.x - self._drag_data["start_x"]
        dy = event.y - self._drag_data["start_y"]
        new_x = max(10, self._drag_data["orig_x"] + dx)
        new_y = max(10, self._drag_data["orig_y"] + dy)
        self.peer_canvas_coords[pid] = (new_x, new_y)
        self._draw_2d_canvas()

    def _on_peer_drag_release(self, event):
        self._drag_data["peer_id"] = None

    def _show_enter_pin_dialog(self, peer: dict):
        """Modal dialog to enter the 6-digit PIN code for a specific discovered peer."""
        dlg = tk.Toplevel(self.root)
        dev_name = peer.get("deviceName", peer.get("deviceId", "Cihaz"))
        dev_ip = peer.get("ipAddress", "")
        platform = peer.get("platform", "").lower()
        dev_icon = "🪟" if "win" in platform else ("📱" if "android" in platform else "💻")

        dlg.title(f"🔐 {dev_name} ile Eşleş")
        dlg.geometry("480x350")
        dlg.minsize(440, 310)
        dlg.configure(bg="#0B1120")
        dlg.transient(self.root)
        dlg.grab_set()

        frm = ttk.Frame(dlg, padding=18)
        frm.pack(fill=tk.BOTH, expand=True)

        ttk.Label(frm, text=f"{dev_icon} {dev_name} ({dev_ip})", font=("Segoe UI", 13, "bold"), foreground="#38BDF8").pack(anchor=tk.W, pady=(0, 4))

        in_ok = peer.get("in_ok", False)
        if in_ok:
            info_txt = f"🔔 '{dev_name}' sizin kodunuzu ({self.node.local_pin}) doğru girdi!\nBağlantıyı tamamlamak için şimdi siz de onun ekranındaki 6 haneli kodu girin:"
            info_fg = "#4ADE80"
        else:
            info_txt = f"'{dev_name}' ekranında görünen 6 haneli kodu (PIN) girin:"
            info_fg = "#94A3B8"

        ttk.Label(frm, text=info_txt, font=("Segoe UI", 9.5), foreground=info_fg, wraplength=430).pack(anchor=tk.W, pady=(0, 10))

        pin_box = tk.Entry(
            frm,
            width=10,
            font=("Consolas", 22, "bold"),
            bg="#1E293B",
            fg="#38BDF8",
            insertbackground="#38BDF8",
            justify="center",
            relief="solid",
            bd=1,
            highlightthickness=2,
            highlightbackground="#38BDF8",
            highlightcolor="#38BDF8"
        )
        pin_box.pack(pady=10)

        trust_var = tk.BooleanVar(value=True)
        chk = tk.Checkbutton(frm, text="⭐ Bu cihaza güven ve hatırla (Bir sonraki sefer PIN sormadan bağlan)",
                             variable=trust_var, bg="#0B1120", fg="#FBBF24", selectcolor="#0F172A",
                             activebackground="#0B1120", activeforeground="#FBBF24", font=("Segoe UI", 9))
        chk.pack(anchor=tk.W, pady=6)

        btn_box = ttk.Frame(frm)
        btn_box.pack(fill=tk.X, pady=(16, 0))

        def _do_pair():
            pin = pin_box.get().strip().replace(" ", "").replace("-", "")
            if len(pin) != 6 or not pin.isdigit():
                messagebox.showwarning("Geçersiz Kod", "Lütfen 6 haneli sayısal PIN kodunu girin.", parent=dlg)
                pin_box.focus_set()
                return

            req_trust = trust_var.get()
            token = f"trust-{self.node.device_id}-{os.urandom(8).hex()}" if req_trust else ""
            self._log(f"'{dev_name}' ({dev_ip}) için PIN ({pin}) doğrulanıyor...")
            threading.Thread(target=self._send_pair_request, args=(peer, pin, req_trust, token), daemon=True).start()
            dlg.destroy()
            self._refresh_state()

        if peer.get("isMutuallyPaired"):
            def _do_disconnect():
                self.node.disconnect_peer(peer.get("deviceId", ""))
                dlg.destroy()
                self._refresh_state()
            ttk.Button(btn_box, text="🔌 Bağlantıyı Kes", style="Danger.TButton", command=_do_disconnect).pack(side=tk.LEFT, padx=4)

        ttk.Button(btn_box, text="🔗 Doğrula ve Bağlan", style="Accent.TButton", command=_do_pair).pack(side=tk.RIGHT, padx=4)
        ttk.Button(btn_box, text="İptal", command=dlg.destroy).pack(side=tk.RIGHT, padx=4)

        pin_box.focus_set()
        pin_box.bind("<Return>", lambda e: _do_pair())

    def _select_peer_and_focus_pin(self, peer_id: str):
        self.selected_peer_id = peer_id
        if peer_id in self.node.peers:
            peer = self.node.peers[peer_id]
            self._show_enter_pin_dialog(peer)

    def _draw_2d_canvas(self):
        self.canvas.delete("all")
        cw = max(400, self.canvas.winfo_width())
        ch = max(300, self.canvas.winfo_height())
        if cw < 60 or ch < 60:
            return

        min_x, min_y, total_w, total_h = self.node.topology.virtual_desktop_bounds()
        all_peers = list(self.node.peers.values())

        # Determine canvas scaling and centering
        scale = min((cw - 180) / max(1, total_w * 1.35), (ch - 130) / max(1, total_h * 1.35), 0.13)
        scale = max(0.045, min(scale, 0.12))

        loc_w = total_w * scale
        loc_h = total_h * scale
        offset_x = max(20, (cw - loc_w) / 2.0)
        offset_y = max(20, (ch - loc_h) / 2.0)

        # 1. Subtle Background Grid
        for gx in range(0, cw, 40):
            self.canvas.create_line(gx, 0, gx, ch, fill="#111C33", width=1)
        for gy in range(0, ch, 40):
            self.canvas.create_line(0, gy, cw, gy, fill="#111C33", width=1)

        # 2. Local Linux Monitors (True to scale)
        local_mon_rects = []
        for m in self.node.topology.monitors:
            x1 = offset_x + (m.virtualX - min_x) * scale
            y1 = offset_y + (m.virtualY - min_y) * scale
            x2 = x1 + m.width * scale
            y2 = y1 + m.height * scale
            local_mon_rects.append((m, x1, y1, x2, y2))

            bg_color = "#172554" if m.isPrimary else "#0F172A"
            outline_color = "#38BDF8" if m.isPrimary else "#334155"
            self.canvas.create_rectangle(x1, y1, x2, y2, fill=bg_color, outline=outline_color, width=2)

            prim_txt = " ★ [BİRİNCİL]" if m.isPrimary else ""
            self.canvas.create_text(x1 + 10, y1 + 14, anchor=tk.W, text=f"🐧 {m.name}{prim_txt}", fill="#F8FAFC", font=("Segoe UI", 9, "bold"))
            self.canvas.create_text(x1 + 10, y1 + 32, anchor=tk.W, text=f"{m.width}x{m.height} @ ({m.virtualX}, {m.virtualY})", fill="#94A3B8", font=("Segoe UI", 8))

            if m.isPrimary and not self.node.active_remote_peer:
                self.canvas.create_text(x1 + 10, y1 + 50, anchor=tk.W, text="🎯 [İMLEÇ YERELDE]", fill="#4ADE80", font=("Segoe UI", 8, "bold"))

        # Internal Seam Lines between adjacent local monitors
        for i in range(len(local_mon_rects)):
            for j in range(i + 1, len(local_mon_rects)):
                ma, ax1, ay1, ax2, ay2 = local_mon_rects[i]
                mb, bx1, by1, bx2, by2 = local_mon_rects[j]
                if abs(ax2 - bx1) < 4.0 or abs(bx2 - ax1) < 4.0:
                    seam_x = bx1 if abs(ax2 - bx1) < 4.0 else ax1
                    s_top = max(ay1, by1) + 4
                    s_bot = min(ay2, by2) - 4
                    if s_bot > s_top:
                        self.canvas.create_line(seam_x, s_top, seam_x, s_bot, fill="#A855F7", dash=(3, 2), width=3)

        # 3. Discovered & Connected Peers
        if all_peers:
            for idx, p in enumerate(all_peers):
                pid = p["deviceId"]
                p_w = p.get("screenWidth", 1920)
                p_h = p.get("screenHeight", 1080)
                p_monitors = p.get("monitors", [])
                is_mut = p.get("isMutuallyPaired", False)
                is_pending = p.get("in_ok", False) or p.get("out_ok", False)

                card_w = max(180, min(240, int(p_w * scale + 24)))
                card_h = max(88, min(125, int(p_h * scale + 34)))

                # Determine card position
                if pid in self.peer_canvas_coords:
                    px1, py1 = self.peer_canvas_coords[pid]
                else:
                    is_win = "win" in p.get("platform", "").lower()
                    if is_win:
                        # Place to the right of local desktop
                        px1 = offset_x + loc_w + 35
                        py1 = offset_y + (loc_h - card_h) / 2.0
                    elif idx == 0:
                        # Place to the left of local desktop
                        px1 = max(15, offset_x - card_w - 35)
                        py1 = offset_y + (loc_h - card_h) / 2.0
                    else:
                        # Place above or stacked
                        px1 = offset_x + (loc_w - card_w) / 2.0 + (idx - 1) * 30
                        py1 = max(15, offset_y - card_h - 35)
                    self.peer_canvas_coords[pid] = (px1, py1)

                px2 = px1 + card_w
                py2 = py1 + card_h

                if is_mut:
                    card_fill = "#064E3B"
                    card_outline = "#10B981"
                    status_txt = "🟢 Çift Taraflı Bağlı (Aktif)"
                    status_col = "#4ADE80"
                    line_col = "#10B981"
                    line_dash = ()
                elif is_pending:
                    card_fill = "#451A03"
                    card_outline = "#F59E0B"
                    status_txt = "🟠 Sizin Kodunuzu Bekliyor" if p.get("in_ok") else "🟡 Karşı Onay Bekliyor"
                    status_col = "#FCD34D"
                    line_col = "#F59E0B"
                    line_dash = (4, 3)
                else:
                    card_fill = "#0F172A"
                    card_outline = "#38BDF8"
                    status_txt = "⚪ Keşfedildi (Eşleşin)"
                    status_col = "#93C5FD"
                    line_col = "#38BDF8"
                    line_dash = (2, 2)

                # Find closest local monitor point to connect cleanly
                best_mx, best_my, best_px, best_py = offset_x + loc_w, offset_y + loc_h / 2, px1, py1 + card_h / 2
                min_dist = 999999.0
                for _, mx1, my1, mx2, my2 in local_mon_rects:
                    if px1 >= mx2:
                        # Peer is right of monitor
                        d = px1 - mx2
                        if d < min_dist:
                            min_dist = d
                            best_mx, best_my = mx2, (my1 + my2) / 2
                            best_px, best_py = px1, py1 + card_h / 2
                    elif px2 <= mx1:
                        # Peer is left of monitor
                        d = mx1 - px2
                        if d < min_dist:
                            min_dist = d
                            best_mx, best_my = mx1, (my1 + my2) / 2
                            best_px, best_py = px2, py1 + card_h / 2
                    elif py1 >= my2:
                        # Peer is below monitor
                        d = py1 - my2
                        if d < min_dist:
                            min_dist = d
                            best_mx, best_my = (mx1 + mx2) / 2, my2
                            best_px, best_py = px1 + card_w / 2, py1
                    elif py2 <= my1:
                        # Peer is above monitor
                        d = my1 - py2
                        if d < min_dist:
                            min_dist = d
                            best_mx, best_my = (mx1 + mx2) / 2, my1
                            best_px, best_py = px1 + card_w / 2, py2

                # Draw clean connector line
                self.canvas.create_line(best_mx, best_my, best_px, best_py, fill=line_col, dash=line_dash, width=2)
                if is_mut:
                    # Portal node circle
                    mid_x = (best_mx + best_px) / 2
                    mid_y = (best_my + best_py) / 2
                    self.canvas.create_oval(mid_x - 4, mid_y - 4, mid_x + 4, mid_y + 4, fill="#10B981", outline="#FFFFFF")

                # Peer Card Box
                card_tag = f"peer_{pid}"
                self.canvas.create_rectangle(px1, py1, px2, py2, fill=card_fill, outline=card_outline, width=2, tags=(card_tag, "peer_card"))

                dev_icon = "🪟" if "win" in p.get("platform", "").lower() else ("📱" if "android" in p.get("platform", "").lower() else "💻")
                dev_name = p.get("deviceName", pid)[:18]
                self.canvas.create_text(px1 + 10, py1 + 14, anchor=tk.W, text=f"{dev_icon} {dev_name}", fill="#FFFFFF", font=("Segoe UI", 9.5, "bold"), tags=(card_tag,))
                self.canvas.create_text(px1 + 10, py1 + 32, anchor=tk.W, text=status_txt, fill=status_col, font=("Segoe UI", 8, "bold"), tags=(card_tag,))

                if p_monitors and len(p_monitors) > 1:
                    mon_str = " | ".join(f"{m.get('width', 1920)}x{m.get('height', 1080)}" for m in p_monitors[:2])
                    self.canvas.create_text(px1 + 10, py1 + 48, anchor=tk.W, text=f"🖥️ {len(p_monitors)} Ekran: {mon_str}", fill="#CBD5E1", font=("Segoe UI", 7.5), tags=(card_tag,))
                else:
                    self.canvas.create_text(px1 + 10, py1 + 48, anchor=tk.W, text=f"🖥️ Çözünürlük: {p_w}x{p_h}", fill="#94A3B8", font=("Segoe UI", 8), tags=(card_tag,))

                # Action button / status bar inside the card
                if not is_mut:
                    btn_x1 = px1 + 8
                    btn_y1 = py2 - 24
                    btn_x2 = px2 - 8
                    btn_y2 = py2 - 6
                    btn_tag = f"pin_btn_{pid}"
                    self.canvas.create_rectangle(btn_x1, btn_y1, btn_x2, btn_y2, fill="#0284C7", outline="#38BDF8", width=1, tags=(card_tag, btn_tag))
                    self.canvas.create_text((btn_x1 + btn_x2) / 2, (btn_y1 + btn_y2) / 2, text="🔐 6 Haneli Kodu Gir", fill="#FFFFFF", font=("Segoe UI", 8, "bold"), tags=(card_tag, btn_tag))
                    self.canvas.tag_bind(btn_tag, "<Button-1>", lambda e, peer_obj=p: self._show_enter_pin_dialog(peer_obj))
                else:
                    self.canvas.create_text(px1 + 10, py2 - 14, anchor=tk.W, text="🎯 Geçiş Kenarı Aktif", fill="#4ADE80", font=("Segoe UI", 8, "bold"), tags=(card_tag,))

                # Bind dragging and clicking
                self.canvas.tag_bind(card_tag, "<ButtonPress-1>", lambda e, p_id=pid: self._on_peer_drag_start(e, p_id))
                self.canvas.tag_bind(card_tag, "<B1-Motion>", self._on_peer_drag_motion)
                self.canvas.tag_bind(card_tag, "<ButtonRelease-1>", self._on_peer_drag_release)
                self.canvas.tag_bind(card_tag, "<Double-Button-1>", lambda e, peer_obj=p: self._show_enter_pin_dialog(peer_obj))
        else:
            empty_x = offset_x + loc_w + 30
            self.canvas.create_text(empty_x, ch / 2, anchor=tk.W,
                                    text="Ağda bağlı başka cihaz yok.\nWindows veya Android'de Connect Me'yi açın\nveya yukarıdaki '🌐 IP ile Cihaz Ekle' butonuna basın.",
                                    fill="#64748B", font=("Segoe UI", 9))

    def _show_manual_ip_dialog(self):
        """Opens a prominent modal dialog to directly connect to any device by IP address."""
        dlg = tk.Toplevel(self.root)
        dlg.title("🌐 Manuel IP ile Cihaz Ekle & Doğrudan Bağlan")
        dlg.geometry("480x280")
        dlg.minsize(440, 260)
        dlg.configure(bg="#0B1120")
        dlg.transient(self.root)
        dlg.grab_set()

        frm = ttk.Frame(dlg, padding=16)
        frm.pack(fill=tk.BOTH, expand=True)

        ttk.Label(frm, text="🌐 Cihaza Doğrudan IP ile Bağlan", font=("Segoe UI", 12, "bold"), foreground="#38BDF8").pack(anchor=tk.W, pady=(0, 4))
        ttk.Label(frm, text="Ağ keşfinin router veya güvenlik duvarı nedeniyle engellendiği durumlarda\nkarşı cihazın (Windows/Android/Linux) IP adresini girin:",
                  font=("Segoe UI", 9), foreground="#94A3B8").pack(anchor=tk.W, pady=(0, 12))

        grid = ttk.Frame(frm)
        grid.pack(fill=tk.X, pady=4)

        ttk.Label(grid, text="Hedef Cihazın IP'si:", font=("Segoe UI", 9, "bold")).grid(row=0, column=0, sticky=tk.W, pady=6)
        local_ip = self._get_local_ip()
        parts = local_ip.split(".")
        default_prefix = f"{parts[0]}.{parts[1]}.{parts[2]}." if len(parts) == 4 and parts[0] != "127" else "192.168.1."

        ip_ent = tk.Entry(grid, font=("Consolas", 11), bg="#1E293B", fg="#38BDF8", insertbackground="#38BDF8",
                          relief="solid", bd=1, highlightthickness=1, highlightbackground="#38BDF8")
        ip_ent.insert(0, default_prefix)
        ip_ent.grid(row=0, column=1, sticky=tk.EW, padx=(8, 0), pady=6)

        ttk.Label(grid, text="6 Haneli Kodu (PIN):", font=("Segoe UI", 9, "bold")).grid(row=1, column=0, sticky=tk.W, pady=6)
        pin_ent = tk.Entry(grid, font=("Consolas", 11, "bold"), bg="#1E293B", fg="#4ADE80", insertbackground="#4ADE80",
                           relief="solid", bd=1, highlightthickness=1, highlightbackground="#334155")
        pin_ent.grid(row=1, column=1, sticky=tk.EW, padx=(8, 0), pady=6)
        grid.columnconfigure(1, weight=1)

        trust_var = tk.BooleanVar(value=True)
        chk = tk.Checkbutton(frm, text="⭐ Bu cihaza güven ve hatırla (Bir sonraki açılışta PIN sorma)",
                             variable=trust_var, bg="#0B1120", fg="#FBBF24", selectcolor="#0F172A",
                             activebackground="#0B1120", activeforeground="#FBBF24", font=("Segoe UI", 9))
        chk.pack(anchor=tk.W, pady=6)

        btn_box = ttk.Frame(frm)
        btn_box.pack(fill=tk.X, pady=(12, 0))

        def _do_connect():
            target_ip = ip_ent.get().strip()
            import ipaddress
            try:
                ipaddress.ip_address(target_ip)
            except ValueError:
                messagebox.showwarning("Geçersiz IP", f"Lütfen geçerli bir IP adresi girin.\nGirilen: '{target_ip}'", parent=dlg)
                return

            peer = self.node.register_manual_peer(target_ip)
            self.selected_peer_id = peer["deviceId"]
            self.manual_ip_entry.delete(0, tk.END)
            self.manual_ip_entry.insert(0, target_ip)

            pin = pin_ent.get().strip()
            if len(pin) == 6 and pin.isdigit():
                req_trust = trust_var.get()
                token = f"trust-{self.node.device_id}-{os.urandom(8).hex()}" if req_trust else ""
                self._log(f"[Manuel IP] '{target_ip}' için PIN ({pin}) doğrulanıyor...")
                threading.Thread(target=self._send_pair_request, args=(peer, pin, req_trust, token), daemon=True).start()
            else:
                self.notebook.select(1)
                self.pin_entry.focus_set()

            self._refresh_state()
            dlg.destroy()
            messagebox.showinfo("Cihaz Eklendi", f"🌐 '{target_ip}' listeye eklendi ve bağlantı isteği gönderildi!", parent=self.root)

        ttk.Button(btn_box, text="🚀 Bağlan ve Ekle", style="Accent.TButton", command=_do_connect).pack(side=tk.RIGHT, padx=4)
        ttk.Button(btn_box, text="İptal", command=dlg.destroy).pack(side=tk.RIGHT, padx=4)

        ip_ent.focus_set()
        ip_ent.icursor(tk.END)
        ip_ent.bind("<Return>", lambda e: _do_connect())
        pin_ent.bind("<Return>", lambda e: _do_connect())

    def _fix_uinput_permissions(self):
        """Installs the udev rule and chmods /dev/uinput using Polkit (pkexec) to guarantee Wayland mouse input."""
        cmd = (
            'echo \'KERNEL=="uinput", GROUP="input", MODE="0660", TAG+="uaccess", OPTIONS+="static_node=uinput"\' > /etc/udev/rules.d/99-connectme-uinput.rules && '
            'chmod 666 /dev/uinput && '
            f'groupadd -f input && usermod -aG input "$USER" && '
            'udevadm control --reload-rules && udevadm trigger'
        )
        try:
            res = subprocess.run(["pkexec", "sh", "-c", cmd], capture_output=True, text=True, timeout=30.0)
            if res.returncode == 0:
                self.node.injector._init_injector()
                if self.node.injector.mode != "none":
                    self.uinput_banner.pack_forget()
                    self.injector_badge.configure(text=f" Girdi: {self.node.injector.mode.upper()} ", foreground="#4ADE80")
                    messagebox.showinfo("Başarılı", "✅ Linux sanal donanım sürücüsü (/dev/uinput) başarıyla etkinleştirildi!\nFare ve klavye KDE Plasma Wayland'de sorunsuz çalışacaktır.")
                else:
                    messagebox.showwarning("Yeniden Başlatma Gerekebilir", "İzinler ayarlandı. Lütfen oturumunuzu kapatıp açın veya bilgisayarı yeniden başlatın.")
            else:
                messagebox.showerror("Yetkilendirme Başarısız", f"İzin verilemedi veya iptal edildi:\n{res.stderr}")
        except Exception as ex:
            messagebox.showerror("Hata", f"Yetkilendirme penceresi açılamadı: {ex}")

    def _refresh_monitors(self):
        mons = self.node.topology.refresh_monitors()
        self._log(f"[Topoloji] Çoklu monitör düzeni güncellendi ({len(mons)} ekran algılandı).")
        self._draw_2d_canvas()

    def _jump_to_manual_ip(self):
        self.notebook.select(1)
        self.manual_ip_entry.focus_set()
        self.manual_ip_entry.icursor(tk.END)

    def _on_trigger_scan(self):
        self._log("[Keşif] 📡 Yerel ağ alt ağı taranıyor (Broadcast UDP sinyali gönderildi)...")
        threading.Thread(target=self.node.send_discovery_broadcast, daemon=True).start()

    def _on_manual_ip_connect(self):
        target_ip = self.manual_ip_entry.get().strip()
        import ipaddress
        try:
            ipaddress.ip_address(target_ip)
        except ValueError:
            messagebox.showwarning("Geçersiz IP", f"Lütfen geçerli bir IPv4 adresi girin.\nÖrnek: 192.168.1.45\nGirilen: '{target_ip}'")
            return

        peer = self.node.register_manual_peer(target_ip)
        self.selected_peer_id = peer["deviceId"]
        self._refresh_state()
        self.pair_prompt_lbl.configure(text=f"'{peer.get('deviceName')}' 6 Haneli Kodu:")

        # Eğer PIN kutusuna 6 haneli kod önceden girilmişse hemen TCP eşleştirmesini başlat
        pin = self.pin_entry.get().strip()
        if len(pin) == 6 and pin.isdigit():
            req_trust = self.trust_var.get()
            token = f"trust-{self.node.device_id}-{os.urandom(8).hex()}" if req_trust else ""
            self._log(f"[Manuel IP] '{target_ip}' için PIN ({pin}) doğrulanıyor...")
            threading.Thread(target=self._send_pair_request, args=(peer, pin, req_trust, token), daemon=True).start()
        else:
            self.pin_entry.focus_set()
            self._log(f"[Manuel IP] 🌐 '{target_ip}' listeye eklendi ve doğrudan keşif sinyali gönderildi. Karşı cihazın 6 haneli kodunu girip 'Doğrula ve Bağlan'a basın.")

    def _on_pair_click(self):
        pin = self.pin_entry.get().strip().replace(" ", "").replace("-", "")
        if len(pin) != 6 or not pin.isdigit():
            messagebox.showwarning("Geçersiz Kod", "Lütfen karşı cihazın ekranında görünen 6 haneli kodu girin.")
            return

        target_peer = None
        if self.selected_peer_id and self.selected_peer_id in self.node.peers:
            target_peer = self.node.peers[self.selected_peer_id]
        elif len(self.node.peers) == 1:
            target_peer = list(self.node.peers.values())[0]
            self.selected_peer_id = target_peer["deviceId"]
        else:
            inbound = [p for p in self.node.peers.values() if p.get("in_ok")]
            if inbound:
                target_peer = inbound[0]
                self.selected_peer_id = target_peer["deviceId"]
            elif len(self.node.peers) > 1:
                device_names = [f"• {p.get('deviceName')} ({p.get('ipAddress')})" for p in self.node.peers.values()]
                msg = "Ağda birden fazla cihaz bulundu:\n\n" + "\n".join(device_names) + "\n\nLütfen bağlanmak istediğiniz cihazı yukarıdaki listeden çift tıklayın veya 2D Kanvas sekmesinden seçin."
                messagebox.showinfo("Cihaz Seçin", msg)
                return
            else:
                manual_ip = self.manual_ip_entry.get().strip()
                import ipaddress
                try:
                    ipaddress.ip_address(manual_ip)
                    target_peer = self.node.register_manual_peer(manual_ip)
                    self.selected_peer_id = target_peer["deviceId"]
                except ValueError:
                    messagebox.showinfo("Cihaz Yok", "Ağda henüz eşleşilecek bir cihaz bulunamadı.\nLütfen yukarıdaki 'Hedef IP' alanına karşı cihazın IP adresini girip ekleyin.")
                    return

        req_trust = self.trust_var.get()
        token = f"trust-{self.node.device_id}-{os.urandom(8).hex()}" if req_trust else ""

        self._log(f"'{target_peer.get('deviceName')}' için PIN ({pin}) doğrulanıyor...")
        threading.Thread(target=self._send_pair_request, args=(target_peer, pin, req_trust, token), daemon=True).start()

    def _on_disconnect_click(self):
        target_peer = None
        if self.selected_peer_id and self.selected_peer_id in self.node.peers:
            target_peer = self.node.peers[self.selected_peer_id]
        else:
            paired = [p for p in self.node.peers.values() if p.get("isMutuallyPaired")]
            if len(paired) == 1:
                target_peer = paired[0]
            elif len(paired) > 1:
                sel = self.peer_list.curselection()
                if sel:
                    keys = list(self.node.peers.keys())
                    if sel[0] < len(keys):
                        target_peer = self.node.peers[keys[sel[0]]]
                if not target_peer:
                    messagebox.showinfo("Cihaz Seçin", "Lütfen bağlantısını kesmek istediğiniz cihazı listeden seçin.")
                    return

        if not target_peer:
            sel = self.peer_list.curselection()
            if sel:
                keys = list(self.node.peers.keys())
                if sel[0] < len(keys):
                    target_peer = self.node.peers[keys[sel[0]]]

        if not target_peer:
            messagebox.showinfo("Cihaz Yok", "Bağlantısı kesilecek aktif bir cihaz seçilmedi.")
            return

        dev_id = target_peer.get("deviceId", "")
        self.node.disconnect_peer(dev_id)
        self.status_lbl.config(text=f"🔌 '{target_peer.get('deviceName')}' ile bağlantı kesildi.", fg="#EF4444")
        self._refresh_state()

    def _send_pair_request(self, peer: dict, pin: str, req_trust: bool, token: str):
        self.node.suppressed_autoconnect.discard(peer.get("deviceId", ""))
        try:
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
                jlen, _ = struct.unpack("<iq", prefix)
                resp = json.loads(self.node._recv_exact(s, jlen).decode("utf-8"))
                if resp.get("type") == "PAIR_VERIFY_ACK":
                    peer["out_ok"] = True
                    if resp.get("senderMonitors"):
                        peer["monitors"] = resp.get("senderMonitors")
                    if resp.get("senderName"):
                        peer["deviceName"] = resp.get("senderName")
                    if resp.get("senderId"):
                        peer["deviceId"] = resp.get("senderId")

                    if resp.get("isMutualComplete"):
                        peer["isMutuallyPaired"] = True
                        if req_trust and token:
                            self.node.trusted_devices[peer["deviceId"]] = {
                                "deviceId": peer["deviceId"],
                                "deviceName": peer.get("deviceName", peer["deviceId"]),
                                "platform": peer.get("platform", "windows"),
                                "trustToken": token,
                                "autoConnect": True
                            }
                            self.node._save_trusted_devices()
                            peer["is_trusted"] = True
                        self._log(f"✅ '{peer.get('deviceName')}' ile karşılıklı eşleşme tamamlandı! [⭐ Cihaz Hatırlandı]")
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
                f"Sürüm: v{VERSION}\n"
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
                f.write(f"[Connect Me Linux Günlüğü Başlangıcı] v{VERSION}\n")
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

    def _check_for_updates(self, is_manual: bool = False):
        try:
            self._log("🔍 [Güncelleme] GitHub Releases üzerinden yeni sürüm denetleniyor...")
            req = urllib.request.Request(
                "https://api.github.com/repos/korgangames/Connect-Me/releases/latest",
                headers={"User-Agent": f"ConnectMe-Linux/{VERSION}", "Accept": "application/vnd.github.v3+json"}
            )
            with urllib.request.urlopen(req, timeout=12) as response:
                if response.status == 200:
                    data = json.loads(response.read().decode("utf-8"))
                    remote_tag = data.get("tag_name", "").strip()
                    if remote_tag:
                        r_parts = [int(p) for p in remote_tag.lstrip("v").split(".") if p.isdigit()]
                        c_parts = [int(p) for p in VERSION.split(".") if p.isdigit()]
                        is_newer = False
                        for i in range(max(len(r_parts), len(c_parts))):
                            r = r_parts[i] if i < len(r_parts) else 0
                            c = c_parts[i] if i < len(c_parts) else 0
                            if r > c:
                                is_newer = True
                                break
                            elif r < c:
                                break

                        if is_newer:
                            tar_url = data.get("html_url", "https://github.com/korgangames/Connect-Me/releases")
                            clean_tag = remote_tag.lstrip("v").lstrip("V")
                            fallback_url = None
                            for a in data.get("assets", []):
                                a_name = a.get("name", "")
                                if a_name.endswith(".tar.gz"):
                                    if clean_tag in a_name or remote_tag in a_name:
                                        tar_url = a.get("browser_download_url", tar_url)
                                        fallback_url = None
                                        break
                                    elif fallback_url is None:
                                        fallback_url = a.get("browser_download_url", tar_url)
                            if fallback_url and tar_url == data.get("html_url", "https://github.com/korgangames/Connect-Me/releases"):
                                tar_url = fallback_url
                            self.root.after(0, lambda: self._on_new_update_found(remote_tag, tar_url, data.get("name", remote_tag)))
                            return

            if is_manual:
                self.root.after(0, lambda: messagebox.showinfo("Connect Me Güncel", f"Tebrikler! Connect Me zaten en son sürümde (v{VERSION})."))
        except urllib.error.HTTPError as he:
            err_msg = f"GitHub HTTP {he.code}: {he.reason}"
            if he.code == 404:
                err_msg = "GitHub 404 Not Found (Depo 'Private' olabilir, güncelleme için 'Public' olmalıdır)."
            self._log(f"[Güncelleme Hatası] {err_msg}")
            if is_manual:
                self.root.after(0, lambda: messagebox.showwarning("Güncelleme Hatası", f"Güncelleme kontrolü başarısız oldu:\n\n{err_msg}"))
        except Exception as ex:
            self._log(f"[Güncelleme Hatası] {ex}")
            if is_manual:
                self.root.after(0, lambda: messagebox.showwarning("Güncelleme Hatası", f"Güncelleme kontrolü başarısız oldu:\n{ex}"))

    def _on_new_update_found(self, version_tag: str, download_url: str, title: str):
        self.update_badge_btn.configure(
            text=f"🎉 {version_tag} Güncelle",
            command=lambda: self._prompt_linux_update(version_tag, download_url, title)
        )
        self._log(f"[Güncelleme] ⭐ Yeni sürüm bulundu: {version_tag} ({title})")

    def _prompt_linux_update(self, version_tag: str, download_url: str, title: str):
        res = messagebox.askyesno(
            "Connect Me Otomatik Güncelleme",
            f"Yeni bir Connect Me sürümü mevcut!\n\n"
            f"Mevcut Sürüm: v{VERSION}\n"
            f"Yeni Sürüm: {version_tag}\n\n"
            f"{title}\n\n"
            f"Connect Me doğrudan bu bilgisayara indirilip otomatik olarak güncellensin ve yeniden başlatılsın mı?\n\n"
            f"(Hayır seçilirse GitHub indirme sayfası tarayıcınızda açılır)"
        )
        if res:
            threading.Thread(target=self._start_linux_auto_update, args=(version_tag, download_url), daemon=True).start()
        else:
            try:
                subprocess.Popen(["xdg-open", download_url])
            except Exception as ex:
                self._log(f"[Güncelleme] Tarayıcı açılamadı: {ex}")

    def _start_linux_auto_update(self, version_tag: str, download_url: str):
        self._log(f"[Güncelleme] ⏳ Connect Me {version_tag} indiriliyor...")
        self.root.after(0, lambda: self.update_badge_btn.configure(text="⏳ İndiriliyor...", state="disabled"))

        try:
            req = urllib.request.Request(
                download_url,
                headers={"User-Agent": f"ConnectMe-Linux/{VERSION}"}
            )

            with tempfile.NamedTemporaryFile(suffix=".tar.gz", delete=False) as tmp_file:
                tmp_path = Path(tmp_file.name)
                with urllib.request.urlopen(req, timeout=30) as resp:
                    total_len = int(resp.headers.get("Content-Length", 0))
                    downloaded = 0
                    last_pct = -1

                    while True:
                        chunk = resp.read(16384)
                        if not chunk:
                            break
                        tmp_file.write(chunk)
                        downloaded += len(chunk)
                        if total_len > 0:
                            pct = int((downloaded * 100) / total_len)
                            if pct != last_pct and pct % 10 == 0:
                                last_pct = pct
                                self._log(f"[Güncelleme] ⬇️ İndiriliyor... %{pct}")
                                self.root.after(0, lambda p=pct: self.update_badge_btn.configure(text=f"⏳ %{p} İndiriliyor..."))

            if not tmp_path.exists() or tmp_path.stat().st_size < 1024:
                raise RuntimeError("İndirilen güncelleme arşivi geçersiz veya boş.")

            self._log("[Güncelleme] 📦 Arşiv ayıklanıyor ve dosyalar güncelleniyor...")
            self.root.after(0, lambda: self.update_badge_btn.configure(text="📦 Kuruluyor..."))

            app_dir = Path(__file__).resolve().parent
            with tarfile.open(tmp_path, "r:gz") as tar:
                for member in tar.getmembers():
                    rel_name = member.name
                    if rel_name.startswith("ConnectMe-Linux/"):
                        rel_name = rel_name[len("ConnectMe-Linux/"):]
                    elif rel_name == "ConnectMe-Linux":
                        continue
                    if not rel_name:
                        continue
                    dest = app_dir / rel_name
                    if member.isdir():
                        dest.mkdir(parents=True, exist_ok=True)
                    elif member.isfile():
                        dest.parent.mkdir(parents=True, exist_ok=True)
                        extracted = tar.extractfile(member)
                        if extracted:
                            with open(dest, "wb") as f_out:
                                shutil.copyfileobj(extracted, f_out)
                        if dest.suffix in [".sh", ".py"]:
                            try:
                                dest.chmod(0o755)
                            except Exception:
                                pass

            try:
                tmp_path.unlink()
            except Exception:
                pass

            self._log(f"[Güncelleme] ✅ Connect Me {version_tag} kurulumu tamamlandı! Yeniden başlatılıyor...")

            def _notify_and_restart():
                messagebox.showinfo(
                    "Güncelleme Başarılı",
                    f"Connect Me başarıyla {version_tag} sürümüne güncellendi!\n\nUygulama şimdi yeniden başlatılıyor."
                )
                try:
                    self.node.stop()
                except Exception:
                    pass
                os.execv(sys.executable, [sys.executable] + sys.argv)

            self.root.after(0, _notify_and_restart)

        except Exception as ex:
            self._log(f"[Güncelleme Hatası] Otomatik güncelleme başarısız: {ex}")
            def _fail():
                self.update_badge_btn.configure(text=f"⚠️ {version_tag} Tekrar Dene", state="normal")
                res = messagebox.askyesno(
                    "Güncelleme Hatası",
                    f"Otomatik güncelleme tamamlanamadı:\n{ex}\n\nİndirme sayfasını tarayıcıda açmak ister misiniz?"
                )
                if res:
                    try:
                        subprocess.Popen(["xdg-open", download_url])
                    except Exception:
                        pass
            self.root.after(0, _fail)


def main():
    root = tk.Tk()
    app = ConnectMeLinuxGui(root)
    root.protocol("WM_DELETE_WINDOW", root.destroy)
    root.mainloop()


if __name__ == "__main__":
    main()
