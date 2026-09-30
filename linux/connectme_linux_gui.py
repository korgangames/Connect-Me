#!/usr/bin/env python3
"""
Connect Me — Linux (Nobara / KDE Plasma / Wayland & X11) Grafiksel Kontrol Paneli (GUI)
Korgan Games (Terminal / Konsol gerektirmeyen bağımsız GUI)

Bu uygulama tek tıkla açılır, arka planda terminal veya cmd penceresi açmaz.
Kullanıcıya 6 haneli PIN kodunu, monitör topolojisini, ağdaki cihazları (Windows, Android),
ortak cep (Drop Shelf) dosya paylaşımını ve güvenilir cihaz yönetimini görsel olarak sunar.
"""

import os
import sys
import threading
import subprocess
from pathlib import Path
import tkinter as tk
from tkinter import ttk, filedialog, messagebox

# Daemon dosyasını import et
sys.path.insert(0, str(Path(__file__).parent))
try:
    from connectme_linux_daemon import ConnectMeLinuxNode, asdict
except ImportError:
    from linux.connectme_linux_daemon import ConnectMeLinuxNode, asdict


class ConnectMeLinuxGui:
    def __init__(self, root: tk.Tk):
        self.root = root
        self.root.title("Connect Me — Linux Kontrol Merkezi")
        self.root.geometry("860x680")
        self.root.minsize(780, 580)
        self.root.configure(bg="#0F172A")

        # Daemon node başlat
        self.node = ConnectMeLinuxNode()
        self.node.start()

        self._apply_dark_theme()
        self._build_ui()
        self._start_refresh_timer()

    def _apply_dark_theme(self):
        style = ttk.Style()
        style.theme_use("clam")
        style.configure(".", background="#0F172A", foreground="#F8FAFC", font=("Segoe UI", 10))
        style.configure("TLabel", background="#0F172A", foreground="#F8FAFC")
        style.configure("Header.TLabel", font=("Segoe UI", 14, "bold"), foreground="#38BDF8")
        style.configure("Badge.TLabel", font=("Segoe UI", 13, "bold"), foreground="#4ADE80", background="#1E293B")
        style.configure("TFrame", background="#0F172A")
        style.configure("Card.TFrame", background="#1E293B", relief="flat")
        style.configure("Accent.TButton", font=("Segoe UI", 10, "bold"), background="#0284C7", foreground="#FFFFFF")
        style.map("Accent.TButton", background=[("active", "#0369A1")])
        style.configure("Success.TButton", font=("Segoe UI", 10, "bold"), background="#16A34A", foreground="#FFFFFF")
        style.map("Success.TButton", background=[("active", "#15803D")])

    def _build_ui(self):
        # Üst Başlık Barı
        header_frame = ttk.Frame(self.root, padding=16)
        header_frame.pack(fill=tk.X)

        title_lbl = ttk.Label(header_frame, text="⚡ Connect Me — Linux (Nobara KDE)", style="Header.TLabel")
        title_lbl.pack(side=tk.LEFT)

        status_badge = ttk.Label(header_frame, text=" 🟢 Çevrimiçi ", style="Badge.TLabel")
        status_badge.pack(side=tk.RIGHT)

        # Bilgi ve PIN Kartı
        info_card = ttk.Frame(self.root, style="Card.TFrame", padding=14)
        info_card.pack(fill=tk.X, padx=16, pady=4)

        pin_text = f"🔐 BU CİHAZIN 6 HANELİ KODU: {self.node.local_pin[:3]} {self.node.local_pin[3:]}"
        pin_lbl = ttk.Label(info_card, text=pin_text, style="Badge.TLabel", font=("Segoe UI", 12, "bold"))
        pin_lbl.pack(anchor=tk.W, pady=2)

        mons_summary = ", ".join(f"{m.name} ({m.width}x{m.height})" for m in self.node.topology.monitors)
        mon_lbl = ttk.Label(info_card, text=f"🖥️ Algılanan Ekranlar: {mons_summary}", foreground="#94A3B8")
        mon_lbl.pack(anchor=tk.W, pady=2)

        # Ana İçerik (2 Bölmeli Notebook / Tab)
        notebook = ttk.Notebook(self.root)
        notebook.pack(fill=tk.BOTH, expand=True, padx=16, pady=8)

        # Tab 1: Cihazlar & Bağlantı
        tab_devices = ttk.Frame(notebook, padding=12)
        notebook.add(tab_devices, text="🖥️ Cihazlar & Eşleşme")

        # Cihaz Listesi
        ttk.Label(tab_devices, text="Ağdaki Keşfedilen Cihazlar (Windows & Android):", font=("Segoe UI", 10, "bold")).pack(anchor=tk.W, pady=4)
        
        self.peer_list = tk.Listbox(tab_devices, bg="#1E293B", fg="#F8FAFC", selectbackground="#0284C7",
                                    font=("Consolas", 10), height=7, bd=0, highlightthickness=1, highlightbackground="#334155")
        self.peer_list.pack(fill=tk.X, pady=4)

        # Bağlantı & PIN Kontrolleri
        action_frame = ttk.Frame(tab_devices, padding=6)
        action_frame.pack(fill=tk.X, pady=4)

        ttk.Label(action_frame, text="Karşı Cihazın 6 Haneli Kodu:").pack(side=tk.LEFT, padx=4)
        self.pin_entry = ttk.Entry(action_frame, width=12, font=("Segoe UI", 11, "bold"))
        self.pin_entry.pack(side=tk.LEFT, padx=6)

        self.trust_var = tk.BooleanVar(value=True)
        trust_chk = tk.Checkbutton(action_frame, text="⭐ Bu Cihaza Güven (Otomatik Bağlan)",
                                   variable=self.trust_var, bg="#0F172A", fg="#F8FAFC",
                                   selectcolor="#1E293B", activebackground="#0F172A", activeforeground="#38BDF8")
        trust_chk.pack(side=tk.LEFT, padx=10)

        pair_btn = ttk.Button(action_frame, text="🔗 Doğrula ve Bağlan", style="Accent.TButton", command=self._on_pair_click)
        pair_btn.pack(side=tk.RIGHT, padx=4)

        # Tab 2: Ortak Cep (Drop Shelf)
        tab_shelf = ttk.Frame(notebook, padding=12)
        notebook.add(tab_shelf, text="📦 Ortak Cep (Drop Shelf)")

        shelf_btn_bar = ttk.Frame(tab_shelf)
        shelf_btn_bar.pack(fill=tk.X, pady=4)

        send_file_btn = ttk.Button(shelf_btn_bar, text="📤 Dosya Gönder...", style="Success.TButton", command=self._on_send_file)
        send_file_btn.pack(side=tk.LEFT, padx=4)

        open_folder_btn = ttk.Button(shelf_btn_bar, text="📂 Ortak Cep Klasörünü Aç", command=self._open_shelf_folder)
        open_folder_btn.pack(side=tk.LEFT, padx=6)

        self.shelf_list = tk.Listbox(tab_shelf, bg="#1E293B", fg="#F8FAFC", selectbackground="#0284C7",
                                     font=("Segoe UI", 10), height=9, bd=0, highlightthickness=1, highlightbackground="#334155")
        self.shelf_list.pack(fill=tk.BOTH, expand=True, pady=6)

        # Alt Bölüm: Canlı Log / Bildirim
        log_frame = ttk.Frame(self.root, padding=12)
        log_frame.pack(fill=tk.BOTH, padx=16, pady=4)

        ttk.Label(log_frame, text="Canlı Etkinlik Günlüğü:", font=("Segoe UI", 9, "bold"), foreground="#64748B").pack(anchor=tk.W)
        self.log_box = tk.Text(log_frame, bg="#020617", fg="#38BDF8", font=("Consolas", 9), height=5,
                               bd=0, state=tk.DISABLED, highlightthickness=1, highlightbackground="#1E293B")
        self.log_box.pack(fill=tk.X, pady=2)

    def _start_refresh_timer(self):
        self._refresh_state()
        self.root.after(1500, self._start_refresh_timer)

    def _refresh_state(self):
        # Peer Listesi Güncelle
        self.peer_list.delete(0, tk.END)
        if not self.node.peers:
            self.peer_list.insert(tk.END, "  [Henüz ağda başka bir Connect Me cihazı bulunamadı. Cihazlarınızda Connect Me'yi açın.]")
        else:
            for pid, peer in self.node.peers.items():
                name = peer.get("deviceName", pid)
                ip = peer.get("ipAddress", "")
                platform = peer.get("platform", "pc")
                is_paired = peer.get("isMutuallyPaired", False)
                status_str = "🟢 ÇİFT TARAFLI BAĞLI" if is_paired else "⚪ Eşleşme Bekleniyor"
                self.peer_list.insert(tk.END, f"  🖥️ {name} ({ip}) [{platform}] — {status_str}")

        # Ortak Cep Dosyaları Güncelle
        self.shelf_list.delete(0, tk.END)
        files = list(self.node.shelf_dir.glob("*"))
        if not files:
            self.shelf_list.insert(tk.END, "  Ortak cepte henüz dosya yok. Diğer cihazlardan sürükleyip bırakabilirsiniz.")
        else:
            for f in sorted(files, key=lambda x: x.stat().st_mtime, reverse=True)[:15]:
                size_kb = f.stat().st_size / 1024
                self.shelf_list.insert(tk.END, f"  📥 {f.name} ({size_kb:.1f} KB)")

    def _on_pair_click(self):
        pin = self.pin_entry.get().strip()
        if len(pin) != 6 or not pin.isdigit():
            messagebox.showwarning("Geçersiz Kod", "Lütfen karşı cihazın ekranında görünen 6 haneli PIN kodunu girin.")
            return

        if not self.node.peers:
            messagebox.showinfo("Cihaz Yok", "Ağda henüz eşleşilecek bir cihaz bulunamadı.")
            return

        target_peer = list(self.node.peers.values())[0]
        req_trust = self.trust_var.get()
        token = f"trust-{self.node.device_id}-{os.urandom(8).hex()}" if req_trust else ""

        self._log(f"'{target_peer.get('deviceName')}' için PIN ({pin}) doğrulaması gönderiliyor...")
        threading.Thread(target=self._send_pair_request, args=(target_peer, pin, req_trust, token), daemon=True).start()

    def _send_pair_request(self, peer: dict, pin: str, req_trust: bool, token: str):
        try:
            import socket, json, struct
            s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            s.settimeout(5.0)
            s.connect((peer["ipAddress"], peer.get("tcpControlPort", 42851)))
            hdr = {
                "type": "PAIR_REQUEST",
                "senderId": self.node.device_id,
                "senderName": self.node.device_name,
                "senderPlatform": self.node.platform,
                "targetPin": pin,
                "requestTrust": req_trust,
                "trustToken": token
            }
            self.node._send_tcp_frame(s, hdr)
            self._log("✅ PIN isteği gönderildi! Karşı tarafın onayı bekleniyor...")
            s.close()
        except Exception as e:
            self._log(f"⚠️ Bağlantı hatası: {e}")

    def _on_send_file(self):
        file_path = filedialog.askopenfilename(title="Ortak Cebe Gönderilecek Dosyayı Seçin")
        if not file_path:
            return

        p = Path(file_path)
        dest = self.node.shelf_dir / p.name
        try:
            import shutil
            shutil.copy2(p, dest)
            self._log(f"📤 '{p.name}' Ortak Cebe eklendi ve bağlı cihazlara sunuldu.")
            messagebox.showinfo("Başarılı", f"'{p.name}' dosyası Ortak Cebe kopyalandı!")
        except Exception as e:
            messagebox.showerror("Hata", f"Dosya kopyalanamadı: {e}")

    def _open_shelf_folder(self):
        try:
            subprocess.Popen(["xdg-open", str(self.node.shelf_dir)])
        except Exception:
            os.system(f"xdg-open '{self.node.shelf_dir}' &")

    def _log(self, text: str):
        def _append():
            self.log_box.config(state=tk.NORMAL)
            self.log_box.insert(tk.END, f"{text}\n")
            self.log_box.see(tk.END)
            self.log_box.config(state=tk.DISABLED)
        self.root.after(0, _append)


def main():
    root = tk.Tk()
    app = ConnectMeLinuxGui(root)
    root.protocol("WM_DELETE_WINDOW", root.destroy)
    root.mainloop()


if __name__ == "__main__":
    main()
