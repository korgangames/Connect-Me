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

from __future__ import annotations

import os
import sys
import time
import json
import struct
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
from typing import Optional, Dict, Set, List, Tuple, Any, Callable
import tkinter as tk
from tkinter import ttk, filedialog, messagebox

sys.path.insert(0, str(Path(__file__).parent))
try:
    from connectme_linux_daemon import (
        ConnectMeLinuxNode, asdict, PhysicalMonitor, VERSION,
        EDGE_NONE, EDGE_LEFT, EDGE_RIGHT, EDGE_TOP, EDGE_BOTTOM,
        FAST_INPUT_UDP_PORT, DATA_CONTROL_TCP_PORT, AUDIO_STREAM_UDP_PORT,
    )
except ImportError:
    from linux.connectme_linux_daemon import (
        ConnectMeLinuxNode, asdict, PhysicalMonitor, VERSION,
        EDGE_NONE, EDGE_LEFT, EDGE_RIGHT, EDGE_TOP, EDGE_BOTTOM,
        FAST_INPUT_UDP_PORT, DATA_CONTROL_TCP_PORT, AUDIO_STREAM_UDP_PORT,
    )


class LinuxCursorOverlay:
    """Disabled floating cursor indicator (native OS system cursor is used instead)."""
    def __init__(self, root: tk.Tk):
        self.root = root
        self.visible = False

    def update(self, active: bool, x: int, y: int):
        pass


KEYSYM_TO_VK = {
    "BackSpace": 0x08,
    "Tab": 0x09,
    "Clear": 0x0C,
    "Return": 0x0D,
    "Shift_L": 0x10,
    "Shift_R": 0x10,
    "Control_L": 0x11,
    "Control_R": 0x11,
    "Alt_L": 0x12,
    "Alt_R": 0x12,
    "Pause": 0x13,
    "Caps_Lock": 0x14,
    "Escape": 0x1B,
    "space": 0x20,
    "Prior": 0x21,
    "Next": 0x22,
    "End": 0x23,
    "Home": 0x24,
    "Left": 0x25,
    "Up": 0x26,
    "Right": 0x27,
    "Down": 0x28,
    "Print": 0x2A,
    "Insert": 0x2D,
    "Delete": 0x2E,
    "Super_L": 0x5B,
    "Super_R": 0x5C,
    "Menu": 0x5D,
    "Scroll_Lock": 0x91,
}
for _c in range(ord('a'), ord('z') + 1):
    KEYSYM_TO_VK[chr(_c)] = ord(chr(_c).upper())
    KEYSYM_TO_VK[chr(_c).upper()] = ord(chr(_c).upper())
for _c in range(ord('0'), ord('9') + 1):
    KEYSYM_TO_VK[chr(_c)] = _c
for _i in range(1, 13):
    KEYSYM_TO_VK[f"F{_i}"] = 0x6F + _i


class ScreenEdgeSentinels:
    """Invisible high-precision 60Hz edge monitor for Linux host KVM.
    Polls global pointer coordinates using Tkinter root.winfo_pointerxy() to detect when cursor reaches
    the physical outermost screen boundary without rendering any visual artifacts or blue lines.
    Completely immune to fast mouse gestures (physical pointer stops at the desktop edge).
    """

    def __init__(self, root: tk.Tk, node: ConnectMeLinuxNode, on_edge_triggered: Callable[[dict, int, float], None], gui: Optional[Any] = None):
        self.root = root
        self.node = node
        self.gui = gui
        self.on_edge_triggered = on_edge_triggered
        self.cooldown_until = 0.0
        self.enabled = True
        self._poll_job: Optional[str] = None
        self._start_poll()

    def set_cooldown(self, duration_sec: float = 0.8):
        self.cooldown_until = time.time() + duration_sec

    def hide(self):
        self.enabled = False

    def show_with_cooldown(self, cooldown: float = 0.8):
        self.enabled = True
        self.set_cooldown(cooldown)

    def update_sentinels(self):
        """Called when peer topology or monitor layout changes."""
        pass

    def _start_poll(self):
        if self._poll_job is not None:
            try:
                self.root.after_cancel(self._poll_job)
            except Exception:
                pass
        self._poll_job = self.root.after(16, self._check_edge)

    def _check_edge(self):
        try:
            self._do_check()
        except Exception:
            pass
        finally:
            if self.root:
                try:
                    self._poll_job = self.root.after(16, self._check_edge)
                except Exception:
                    pass

    def _do_check(self):
        if not self.enabled or time.time() < self.cooldown_until:
            return
        if self.node.controlling_remote_peer is not None:
            return
        if hasattr(self.node, "hardware_grabber") and self.node.hardware_grabber.is_active:
            return
        if self.gui and hasattr(self.gui, "host_capture_overlay") and self.gui.host_capture_overlay.is_active:
            return

        if not any(p.get("isMutuallyPaired") for p in self.node.peers.values()):
            return

        try:
            cur_x, cur_y = self.root.winfo_pointerxy()
        except Exception:
            return

        if hasattr(self.node, "hardware_grabber"):
            self.node.hardware_grabber.sync_cursor(cur_x, cur_y)

        mon = self.node.topology.find_monitor_at(cur_x, cur_y)
        if not mon:
            mon = self.node.topology.monitors[0] if self.node.topology.monitors else None
        if not mon:
            return

        rel_x = cur_x - mon.virtualX
        rel_y = cur_y - mon.virtualY

        # Dead-corner koruması (16px): Pencere kapatma ('X') ve köşe butonları sırasında yanlışlıkla geçişi engeller
        dead_zone = 16
        in_v_corner = rel_y < dead_zone or rel_y > (mon.height - dead_zone)
        in_h_corner = rel_x < dead_zone or rel_x > (mon.width - dead_zone)

        hit_edge = EDGE_NONE
        norm_pos = 0.5

        if rel_x >= mon.width - 8 and not in_v_corner:
            hit_edge = EDGE_RIGHT
            norm_pos = max(0.0, min(1.0, rel_y / float(max(1, mon.height))))
        elif rel_x <= 7 and not in_v_corner:
            hit_edge = EDGE_LEFT
            norm_pos = max(0.0, min(1.0, rel_y / float(max(1, mon.height))))
        elif rel_y <= 7 and not in_h_corner:
            hit_edge = EDGE_TOP
            norm_pos = max(0.0, min(1.0, rel_x / float(max(1, mon.width))))
        elif rel_y >= mon.height - 8 and not in_h_corner:
            hit_edge = EDGE_BOTTOM
            norm_pos = max(0.0, min(1.0, rel_x / float(max(1, mon.width))))

        if hit_edge == EDGE_NONE:
            return

        # Fiziksel monitörler arası iç dikiş (Internal Seam) kontrolü
        if self.node.topology.is_internal_seam(mon, hit_edge, cur_x, cur_y):
            return

        mon_id = getattr(mon, "monitorId", None)
        target = self.node.find_adjacent_peer_in_colony(self.node.device_id, hit_edge, norm_pos)
        if not target:
            target = self.node.resolve_peer_for_edge_and_norm(hit_edge, norm_pos, mon_id)
        if not target and mon_id:
            target = self.node.resolve_peer_for_edge_and_norm(hit_edge, norm_pos, None)
        if target:
            self.set_cooldown(0.35)
            if self.on_edge_triggered:
                self.on_edge_triggered(target, hit_edge, norm_pos)
            return


class HostCaptureOverlay:
    """Sleek top floating HUD and hardware input capture controller for when Linux acts as Host controlling Android or Windows."""

    def __init__(self, root: tk.Tk, node: ConnectMeLinuxNode, gui: Optional[Any] = None):
        self.root = root
        self.node = node
        self.gui = gui
        self.active_peer: Optional[dict] = None
        self.exit_edge: int = EDGE_NONE
        self.overlay: Optional[tk.Toplevel] = None
        self.is_active = False
        self.last_x_root = 0
        self.last_y_root = 0
        self._push_accum = 0.0
        self._fallback_job: Optional[str] = None
        self._pageup_down = False
        self._pagedown_down = False

        if hasattr(self.node, "hardware_grabber"):
            self.node.hardware_grabber.on_deactivated = lambda: self.root.after(0, lambda: self.deactivate(notify_peer=False))

    def activate(self, peer: dict, exit_edge: int, norm_pos: float = 0.5):
        if self.is_active:
            self.deactivate(notify_peer=False)

        self.active_peer = peer
        self.exit_edge = exit_edge
        self.is_active = True
        self._push_accum = 0.0

        # Try kernel-level hardware grabber (evdev) first
        grab_ok = False
        if hasattr(self.node, "hardware_grabber"):
            if not self.node.hardware_grabber.is_active:
                grab_ok = self.node.hardware_grabber.activate(peer, exit_edge, norm_pos)
            else:
                grab_ok = True

        # Create Top Floating HUD Banner (Compact, never covers the user's screen or traps clicks)
        min_x, min_y, vw, vh = self.node.topology.virtual_desktop_bounds()
        hud_w = 710
        hud_h = 46
        hud_x = min_x + max(20, (vw - hud_w) // 2)
        hud_y = min_y + 16

        self.overlay = tk.Toplevel(self.root)
        self.overlay.overrideredirect(True)
        try:
            self.overlay.attributes("-topmost", True)
        except Exception:
            pass
        self.overlay.configure(bg="#0F172A", bd=1, relief="ridge")
        self.overlay.geometry(f"{hud_w}x{hud_h}+{hud_x}+{hud_y}")

        peer_name = peer.get("deviceName", "Cihaz")
        grab_tag = " [Donanım 1000Hz]" if grab_ok else " [Yazılımsal Takip]"
        lbl = tk.Label(
            self.overlay,
            text=f"🟢 Kontrol '{peer_name}' Ekranında{grab_tag}  |  Geri Dönüş: [PageUp+PageDown] veya [ScrollLock] tuşu veya",
            fg="#4ADE80",
            bg="#0F172A",
            font=("Segoe UI", 9, "bold")
        )
        lbl.pack(side=tk.LEFT, padx=(14, 8), pady=8)

        btn = tk.Button(
            self.overlay,
            text="⬅️ Kontrolü Linux'a Geri Al",
            fg="#FFFFFF",
            bg="#DC2626",
            activebackground="#B91C1C",
            activeforeground="#FFFFFF",
            bd=0,
            padx=10,
            pady=4,
            font=("Segoe UI", 8, "bold"),
            command=lambda: self.deactivate(notify_peer=True)
        )
        btn.pack(side=tk.LEFT, padx=(0, 8), pady=8)

        if not grab_ok and self.gui:
            fix_btn = tk.Button(
                self.overlay,
                text="🔑 Donanım İzni Ver",
                fg="#FFFFFF",
                bg="#0284C7",
                activebackground="#0369A1",
                activeforeground="#FFFFFF",
                bd=0,
                padx=8,
                pady=4,
                font=("Segoe UI", 8, "bold"),
                command=self._on_fix_permissions_click
            )
            fix_btn.pack(side=tk.LEFT, padx=(0, 8), pady=8)

        # Global return keys on overlay
        self.overlay.bind("<KeyPress>", self._on_key_press)
        self.overlay.bind("<KeyRelease>", self._on_key_release)

        # Fallback 60Hz global pointer polling across the entire desktop if hardware grabber not yet permitted
        if not grab_ok:
            try:
                self.last_x_root, self.last_y_root = self.root.winfo_pointerxy()
            except Exception:
                self.last_x_root, self.last_y_root = 0, 0
            self._start_fallback_poll()

        self.overlay.deiconify()
        self.overlay.lift()

    def _on_fix_permissions_click(self):
        if self.gui and hasattr(self.gui, "_fix_uinput_permissions"):
            self.gui._fix_uinput_permissions()
            if hasattr(self.node, "hardware_grabber") and self.node.hardware_grabber.can_grab():
                self.node.hardware_grabber.activate(self.active_peer, self.exit_edge)
                self._stop_fallback_poll()

    def _start_fallback_poll(self):
        self._stop_fallback_poll()
        self._fallback_job = self.root.after(16, self._fallback_tick)

    def _stop_fallback_poll(self):
        if self._fallback_job is not None:
            try:
                self.root.after_cancel(self._fallback_job)
            except Exception:
                pass
            self._fallback_job = None

    def _fallback_tick(self):
        if not self.is_active or not self.active_peer:
            return
        if hasattr(self.node, "hardware_grabber") and self.node.hardware_grabber.is_active:
            return
        try:
            cur_x, cur_y = self.root.winfo_pointerxy()
            if self.last_x_root == 0 and self.last_y_root == 0:
                self.last_x_root, self.last_y_root = cur_x, cur_y
            else:
                dx = cur_x - self.last_x_root
                dy = cur_y - self.last_y_root
                if dx != 0 or dy != 0:
                    self.node.send_mouse_move(self.active_peer, dx, dy)
                    self.last_x_root, self.last_y_root = cur_x, cur_y
        except Exception:
            pass
        if self.is_active:
            self._fallback_job = self.root.after(16, self._fallback_tick)

    def _on_button(self, btn: int, is_pressed: bool):
        if not self.is_active or not self.active_peer:
            return
        self.node.send_mouse_button(self.active_peer, btn, is_pressed)

    def _on_scroll(self, sx: int, sy: int):
        if not self.is_active or not self.active_peer:
            return
        self.node.send_mouse_scroll(self.active_peer, sx, sy)

    def _on_key_press(self, event):
        if not self.is_active or not self.active_peer:
            return
        if event.keysym in ("Prior", "Page_Up"):
            self._pageup_down = True
        elif event.keysym in ("Next", "Page_Down"):
            self._pagedown_down = True

        if (self._pageup_down and self._pagedown_down) or event.keysym == "Scroll_Lock":
            self._pageup_down = False
            self._pagedown_down = False
            self.deactivate(notify_peer=True)
            return

        vk = KEYSYM_TO_VK.get(event.keysym, ord(event.char.upper()) if event.char else 0)
        mods = 0
        if event.state & 0x01:
            mods |= 1
        if event.state & 0x04:
            mods |= 2
        if event.state & 0x08 or event.state & 0x18:
            mods |= 4
        if event.state & 0x40:
            mods |= 8

        char_code = ord(event.char) if event.char else 0
        self.node.send_key_event(self.active_peer, vk, event.keycode, True, mods, char_code)

    def _on_key_release(self, event):
        if not self.is_active or not self.active_peer:
            return
        if event.keysym in ("Prior", "Page_Up"):
            self._pageup_down = False
        elif event.keysym in ("Next", "Page_Down"):
            self._pagedown_down = False

        vk = KEYSYM_TO_VK.get(event.keysym, ord(event.char.upper()) if event.char else 0)
        mods = 0
        char_code = ord(event.char) if event.char else 0
        self.node.send_key_event(self.active_peer, vk, event.keycode, False, mods, char_code)

    def deactivate(self, notify_peer: bool = True):
        if not self.is_active:
            return
        self.is_active = False
        self._pageup_down = False
        self._pagedown_down = False
        self._stop_fallback_poll()
        self.last_x_root = 0
        self.last_y_root = 0
        self.active_peer = None

        if hasattr(self.node, "hardware_grabber") and self.node.hardware_grabber.is_active:
            self.node.hardware_grabber.deactivate(notify_peer=False)

        if self.overlay:
            try:
                self.overlay.destroy()
            except Exception:
                pass
            self.overlay = None

        if notify_peer:
            self.node.return_host_control_to_local(notify_peer=True)

        if self.gui and hasattr(self.gui, "edge_sentinels"):
            self.gui.edge_sentinels.show_with_cooldown(0.8)


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
        self._pageup_down = False
        self._pagedown_down = False

        # Daemon node başlat
        self.node = ConnectMeLinuxNode(on_log=self._log)
        self.node.on_cursor_update = self._on_cursor_update
        self.node.on_pin_request_received = self._on_pin_request_received
        self.node.on_peer_updated = lambda p: self.root.after(0, self._refresh_state)
        self.node.on_topology_changed = lambda: self.root.after(0, self._on_display_topology_updated)
        self.node.on_audio_sinks_updated = lambda: self.root.after(0, self._refresh_audio_sinks_dropdown)
        self.node.on_audio_route_changed = lambda r: self.root.after(0, lambda: self._on_audio_route_changed_gui(r))
        self.node.start()

        # Ekran üstü imleç göstergesi (Client modu)
        self.cursor_overlay = LinuxCursorOverlay(self.root)

        # Host Modu (Linux ana makine: fare/klavye aktarımı & kenar bariyerleri)
        self.host_capture_overlay = HostCaptureOverlay(self.root, self.node, self)
        self.edge_sentinels = ScreenEdgeSentinels(self.root, self.node, self._on_sentinel_triggered, self)
        self.node.on_host_control_switched = lambda peer, edge, norm: self.root.after(0, lambda: self._on_host_control_switched(peer, edge, norm))
        self.node.on_host_control_returned = lambda: self.root.after(0, self._on_host_control_returned)

        # Global Kısayol Tuşları (KVM Hotkeys & Emergency Reclaim)
        self.root.bind_all("<Scroll_Lock>", lambda e: self._toggle_host_capture())
        self.root.bind_all("<Control-Alt-Right>", lambda e: self._switch_to_edge_peer(EDGE_RIGHT))
        self.root.bind_all("<Control-Alt-Left>", lambda e: self._switch_to_edge_peer(EDGE_LEFT))
        self.root.bind_all("<KeyPress-Prior>", self._on_emergency_pageup_press, add="+")
        self.root.bind_all("<KeyRelease-Prior>", self._on_emergency_pageup_release, add="+")
        self.root.bind_all("<KeyPress-Next>", self._on_emergency_pagedown_press, add="+")
        self.root.bind_all("<KeyRelease-Next>", self._on_emergency_pagedown_release, add="+")
        self.root.bind_all("<Motion>", lambda e: getattr(self.node, "hardware_grabber", None) and self.node.hardware_grabber.sync_cursor(e.x_root, e.y_root), add="+")

        self._apply_dark_theme()
        self._build_ui()
        self._start_refresh_timer()
        self._periodic_display_check()
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

        # Dropdown Menüler (High-Contrast Dark Mode Combobox & Popdown Listbox)
        style.configure("TCombobox", background="#1E293B", foreground="#F8FAFC", fieldbackground="#1E293B",
                        arrowcolor="#38BDF8", darkcolor="#1E293B", lightcolor="#1E293B", bordercolor="#334155")
        style.map("TCombobox",
                  fieldbackground=[("readonly", "#1E293B"), ("active", "#1E293B"), ("focus", "#0F172A")],
                  foreground=[("readonly", "#F8FAFC"), ("active", "#38BDF8"), ("focus", "#38BDF8")],
                  selectbackground=[("readonly", "#0284C7")],
                  selectforeground=[("readonly", "#FFFFFF")])
        self.root.option_add("*TCombobox*Listbox.background", "#1E293B")
        self.root.option_add("*TCombobox*Listbox.foreground", "#F8FAFC")
        self.root.option_add("*TCombobox*Listbox.selectBackground", "#0284C7")
        self.root.option_add("*TCombobox*Listbox.selectForeground", "#FFFFFF")
        self.root.option_add("*TCombobox*Listbox.font", ("Segoe UI", 10))

    def _build_ui(self):
        # Üst Başlık Barı
        header_frame = ttk.Frame(self.root, padding=16)
        header_frame.pack(fill=tk.X)

        title_box = ttk.Frame(header_frame)
        title_box.pack(side=tk.LEFT)
        ttk.Label(title_box, text="⚡ Connect Me — Nobara Linux", style="Header.TLabel").pack(anchor=tk.W)
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
            text="⚠️ Linux Donanım Fare/Klavye İzni Gerekiyor: Ekran kenarından otomatik geçiş ve donanım yakalama için aygıt izinleri (/dev/input, /dev/uinput) verilmelidir.",
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
        needs_perm = (
            self.node.injector.mode in ("none", "xdotool")
            or (os.path.exists("/dev/uinput") and not os.access("/dev/uinput", os.W_OK))
            or (hasattr(self.node, "hardware_grabber") and not self.node.hardware_grabber.can_grab())
        )
        if needs_perm:
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

        # ÜST ARAÇ ÇUBUĞU (Windows UX ile birebir tutarlı: Yakınlaştırma, Merkeze Sığdır, Kenarlara Dağıt, Seçili Ekrana Geç)
        ctrl_bar = ttk.Frame(tab)
        ctrl_bar.pack(fill=tk.X, pady=(0, 6))

        # Sol taraf: Zoom ve otomatik düzen butonları
        left_ctrl = ttk.Frame(ctrl_bar)
        left_ctrl.pack(side=tk.LEFT, fill=tk.X)

        ttk.Label(left_ctrl, text="Yakınlaştırma:", font=("Segoe UI", 9), foreground="#94A3B8").pack(side=tk.LEFT, padx=(0, 4))
        self.zoom_slider = ttk.Scale(left_ctrl, from_=0.6, to=1.4, value=1.0, length=100, command=self._on_zoom_slider_change)
        self.zoom_slider.pack(side=tk.LEFT, padx=2)
        self.zoom_percent_lbl = ttk.Label(left_ctrl, text="100%", font=("Segoe UI", 9, "bold"), foreground="#38BDF8", width=5)
        self.zoom_percent_lbl.pack(side=tk.LEFT, padx=(2, 6))

        ttk.Button(left_ctrl, text="🎯 Merkeze Sığdır", command=self._fit_canvas_view).pack(side=tk.LEFT, padx=3)
        ttk.Button(left_ctrl, text="📐 Kenarlara Dağıt", command=self._auto_arrange_screens).pack(side=tk.LEFT, padx=3)

        # Sağ taraf: Hızlı aksiyon butonları
        right_ctrl = ttk.Frame(ctrl_bar)
        right_ctrl.pack(side=tk.RIGHT)

        self.switch_peer_btn = ttk.Button(right_ctrl, text="🚀 Seçili Ekrana Şimdi Geç", style="Accent.TButton", command=self._switch_to_selected_peer)
        self.switch_peer_btn.pack(side=tk.RIGHT, padx=4)

        ttk.Button(right_ctrl, text="🌐 IP ile Cihaz Ekle", command=self._show_manual_ip_dialog).pack(side=tk.RIGHT, padx=3)
        ttk.Button(right_ctrl, text="🔄 Ekranları Tara", command=self._refresh_monitors).pack(side=tk.RIGHT, padx=3)
        ttk.Button(right_ctrl, text="📐 Sıfırla", command=self._reset_canvas_layout).pack(side=tk.RIGHT, padx=3)

        # GENİŞ 2D EKRAN DİZİLİM KANVASI
        self.canvas = tk.Canvas(tab, bg="#090D13", highlightthickness=1, highlightbackground="#21262D")
        self.canvas.pack(fill=tk.BOTH, expand=True, pady=4)
        self.canvas.bind("<MouseWheel>", self._on_canvas_mousewheel)
        self.canvas.bind("<Button-4>", lambda e: self._zoom_canvas(0.08))
        self.canvas.bind("<Button-5>", lambda e: self._zoom_canvas(-0.08))
        self.canvas.bind("<Configure>", lambda e: self._draw_2d_canvas())

        # ALT ÇUBUK: BİLGİ ETİKETİ VE HIZLI KENAR TAŞIMA BUTONLARI (Windows UX ile birebir tutarlı)
        bottom_frame = ttk.Frame(tab)
        bottom_frame.pack(fill=tk.X, pady=(4, 0))

        # Satır 1: Dinamik Kontakt Noktası & Seçim Bilgisi
        self.canvas_info_lbl = ttk.Label(
            bottom_frame,
            text="Ekran kutularını sürükleyip merkez ekranın istediğiniz kenarına yapıştırın veya aşağıdaki butonlarla kenarlara dağıtın.",
            font=("Segoe UI", 10),
            foreground="#38BDF8"
        )
        self.canvas_info_lbl.pack(fill=tk.X, pady=(0, 4))

        # Satır 2: Sol = Lejant, Sağ = Hızlı Kenar Butonları
        row2 = ttk.Frame(bottom_frame)
        row2.pack(fill=tk.X)

        legend_box = ttk.Frame(row2)
        legend_box.pack(side=tk.LEFT)
        ttk.Label(
            legend_box,
            text="🟦 Yerel Monitör  |  🟩 Aktif Cihaz  |  🟧 PIN Bekleyen  |  🟦 Parlak Mavi: Kontakt Noktası",
            foreground="#64748B",
            font=("Segoe UI", 9)
        ).pack(side=tk.LEFT)

        move_box = ttk.Frame(row2)
        move_box.pack(side=tk.RIGHT)
        ttk.Label(move_box, text="Seçili Cihazı Taşı:", font=("Segoe UI", 9), foreground="#94A3B8").pack(side=tk.LEFT, padx=(0, 6))
        ttk.Button(move_box, text="⬅️ Sol", command=lambda: self._quick_assign_selected_peer(EDGE_LEFT)).pack(side=tk.LEFT, padx=2)
        ttk.Button(move_box, text="⬆️ Üst", command=lambda: self._quick_assign_selected_peer(EDGE_TOP)).pack(side=tk.LEFT, padx=2)
        ttk.Button(move_box, text="⬇️ Alt", command=lambda: self._quick_assign_selected_peer(EDGE_BOTTOM)).pack(side=tk.LEFT, padx=2)
        ttk.Button(move_box, text="Sağ ➡️", command=lambda: self._quick_assign_selected_peer(EDGE_RIGHT)).pack(side=tk.LEFT, padx=2)

    def _build_tab_devices(self):
        tab = ttk.Frame(self.notebook, padding=14)
        self.notebook.add(tab, text="🔗 Cihazlar & Güvenlik")

        # Gelen PIN Talebi Uyarısı (Inbound PIN Request Banner)
        self.inbound_card = ttk.Frame(tab, style="Card.TFrame", padding=10)
        self.inbound_label = ttk.Label(self.inbound_card, text="", font=("Segoe UI", 10, "bold"), foreground="#F59E0B")
        self.inbound_label.pack(side=tk.LEFT, fill=tk.X, expand=True)
        self.inbound_accept_btn = ttk.Button(self.inbound_card, text="✨ Hemen Kabul Et", style="Success.TButton", command=self._on_inbound_accept_click)
        self.inbound_accept_btn.pack(side=tk.RIGHT, padx=4)
        self.inbound_btn = ttk.Button(self.inbound_card, text="🔐 PIN Kodunu Gir", style="Accent.TButton", command=self._on_inbound_pair_click)
        self.inbound_btn.pack(side=tk.RIGHT, padx=4)
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

        self.pair_btn = ttk.Button(act_box, text="🔗 Doğrula ve Bağlan", style="Accent.TButton", command=self._on_pair_click)
        self.pair_btn.pack(side=tk.RIGHT, padx=6)

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
        self.notebook.add(tab, text="🎧 Ses Matrisi")

        # 1. Ses Yönlendirme Kartı (Matrix Control)
        matrix_card = ttk.Frame(tab, style="Card.TFrame", padding=14)
        matrix_card.pack(fill=tk.X, pady=(0, 8))

        ttk.Label(matrix_card, text="🎧 Çoklu Cihaz Ses Matrisi (Audio Routing Matrix)", font=("Segoe UI", 12, "bold"), foreground="#4ADE80").pack(anchor=tk.W)
        ttk.Label(matrix_card, text="Ağdaki tüm bağlı cihazların (Windows, Linux, Android) ses çıkışları (hoparlör/kulaklık) aşağıda listelenir. Seçtiğiniz ses çıkışına tüm bağlı cihazların sesleri anında yönlendirilir.",
                  foreground="#94A3B8", wraplength=720).pack(anchor=tk.W, pady=(4, 8))

        # Dropdown selection row
        sel_row = ttk.Frame(matrix_card, style="Card.TFrame")
        sel_row.pack(fill=tk.X, pady=4)
        ttk.Label(sel_row, text="Hedef Ses Çıkışı:", font=("Segoe UI", 10, "bold")).pack(side=tk.LEFT, padx=(0, 6))

        self.audio_target_var = tk.StringVar()
        self.audio_target_combo = ttk.Combobox(sel_row, textvariable=self.audio_target_var, state="readonly", width=48, font=("Segoe UI", 10))
        self.audio_target_combo.pack(side=tk.LEFT, padx=4)
        self.audio_target_combo.bind("<<ComboboxSelected>>", self._on_audio_combo_selected)

        # Buttons row
        btn_row = ttk.Frame(matrix_card, style="Card.TFrame")
        btn_row.pack(fill=tk.X, pady=(8, 4))

        self.audio_apply_btn = ttk.Button(btn_row, text="🎯 Seçili Çıkışa Yönlendir", style="Success.TButton", command=self._apply_audio_route_click)
        self.audio_apply_btn.pack(side=tk.LEFT, padx=(0, 6))

        self.audio_reset_btn = ttk.Button(btn_row, text="🔄 Sıfırla (Yerel Mod)", style="Danger.TButton", command=self._reset_audio_route_click)
        self.audio_reset_btn.pack(side=tk.LEFT, padx=6)

        self.audio_refresh_sinks_btn = ttk.Button(btn_row, text="🔍 Çıkışları Yenile", command=self._refresh_audio_sinks_click)
        self.audio_refresh_sinks_btn.pack(side=tk.LEFT, padx=6)

        # Status badge / indicator
        self.audio_matrix_status_lbl = ttk.Label(matrix_card, text="Durum: ⚪ Boşta (Her cihaz kendi yerel sesini çalıyor)", font=("Segoe UI", 10, "bold"), foreground="#A5B4FC")
        self.audio_matrix_status_lbl.pack(anchor=tk.W, pady=(8, 2))

        # 2. Canlı Ses Alıcısı & Telemetri Kartı
        rx_card = ttk.Frame(tab, style="Card.TFrame", padding=14)
        rx_card.pack(fill=tk.X, pady=6)
        ttk.Label(rx_card, text="📡 Canlı Ses Telemetrisi & Alıcı Durumu", font=("Segoe UI", 11, "bold"), foreground="#38BDF8").pack(anchor=tk.W)
        self.audio_rx_status_lbl = ttk.Label(rx_card, text="Durum: 🎧 Dinliyor (UDP Port: 42852) — 0 bayt alındı", font=("Segoe UI", 10), foreground="#94A3B8")
        self.audio_rx_status_lbl.pack(anchor=tk.W, pady=2)

        # 3. Bilgi Kartı
        info_card = ttk.Frame(tab, style="InnerCard.TFrame", padding=12)
        info_card.pack(fill=tk.X, pady=8)
        ttk.Label(info_card, text="💡 Ses Matrisi Çalışma Mantığı:", font=("Segoe UI", 10, "bold"), foreground="#F8FAFC").pack(anchor=tk.W)
        pipewire_str = "Aktif (pw-record / pw-play)" if shutil.which("pw-record") else ("Aktif (PulseAudio parec / paplay)" if shutil.which("parec") else "Bulunamadı")
        ttk.Label(info_card, text=f"• Linux Ses Motoru: {pipewire_str}\n"
                                  "• Format: 48,000 Hz, 16-bit Stereo PCM (S16LE) (<15ms gecikme)\n"
                                  "• Hedef bir çıkış seçildiğinde: O makine alıcı olup seçilen hoparlör/kulaklığa çalar, diğer tüm bağlı cihazlar ise masaüstü seslerini UDP 42852 üzerinden oraya canlı yayınlar.\n"
                                  "• Sıfırla / Yerel Mod seçildiğinde: Tüm akışlar durur ve her cihaz kendi sesini yerel çalar.",
                  foreground="#94A3B8").pack(anchor=tk.W, pady=2)

        self._audio_sink_items: List[Dict[str, Any]] = []
        self._is_refreshing_audio_sinks = False
        self._refresh_audio_sinks_dropdown()

    def _on_audio_combo_selected(self, event=None):
        if getattr(self, "_is_refreshing_audio_sinks", False):
            return
        self._apply_audio_route_click()

    def _refresh_audio_sinks_dropdown(self):
        if not hasattr(self, "audio_target_combo"):
            return

        self._is_refreshing_audio_sinks = True
        try:
            items: List[Dict[str, Any]] = [
                {
                    "displayText": "⚪ Yerel Mod / Devre Dışı (Herkes kendi sesini çalsın)",
                    "deviceId": "NONE",
                    "deviceName": "Yerel Mod",
                    "sinkId": "NONE",
                    "sinkName": "Devre Dışı",
                    "ipAddress": "",
                    "isDisable": True
                }
            ]

            # 1. Local Linux Sinks with real LAN IP (not 127.0.0.1, so remote peers can stream to it!)
            local_ip = self._get_local_ip()
            local_sinks = getattr(self.node, "local_audio_sinks", [])
            if not local_sinks:
                local_sinks = self.node.enumerate_local_audio_sinks()
                self.node.local_audio_sinks = local_sinks

            for s in local_sinks:
                def_str = " (Varsayılan)" if s.get("isDefault") else ""
                items.append({
                    "displayText": f"[Bu Bilgisayar] {s.get('sinkName', 'Ses Çıkışı')}{def_str}",
                    "deviceId": self.node.device_id,
                    "deviceName": self.node.device_name,
                    "sinkId": s.get("sinkId", "default"),
                    "sinkName": s.get("sinkName", "Varsayılan Ses Çıkışı"),
                    "ipAddress": local_ip,
                    "isDisable": False
                })

            # 2. Remote Paired Peers' Sinks
            for peer in self.node.peers.values():
                if not peer.get("isMutuallyPaired"):
                    continue
                dev_id = peer.get("deviceId", "")
                dev_name = peer.get("deviceName", dev_id)
                dev_ip = peer.get("ipAddress", "")
                remote_sinks = self.node.remote_audio_sinks.get(dev_id, [])

                if remote_sinks:
                    for rs in remote_sinks:
                        def_str = " (Varsayılan)" if rs.get("isDefault") else ""
                        items.append({
                            "displayText": f"[{dev_name}] {rs.get('sinkName', 'Ses Çıkışı')}{def_str}",
                            "deviceId": dev_id,
                            "deviceName": dev_name,
                            "sinkId": rs.get("sinkId", "default"),
                            "sinkName": rs.get("sinkName", "Ses Çıkışı"),
                            "ipAddress": dev_ip,
                            "isDisable": False
                        })
                else:
                    items.append({
                        "displayText": f"[{dev_name}] Varsayılan Ses Çıkışı",
                        "deviceId": dev_id,
                        "deviceName": dev_name,
                        "sinkId": "default",
                        "sinkName": "Varsayılan Ses Çıkışı",
                        "ipAddress": dev_ip,
                        "isDisable": False
                    })

            self._audio_sink_items = items
            display_names = [it["displayText"] for it in items]
            self.audio_target_combo["values"] = display_names

            # Preserve or reflect current route selection
            curr_route = self.node.active_audio_route
            match_idx = None
            if curr_route and curr_route.get("targetDeviceId") not in (None, "", "NONE"):
                tgt_dev = curr_route.get("targetDeviceId")
                tgt_sink = curr_route.get("targetSinkId")
                # Pass 1: exact deviceId AND exact sinkId
                for idx, it in enumerate(items):
                    if it["deviceId"] == tgt_dev and it["sinkId"] == tgt_sink:
                        match_idx = idx
                        break
                # Pass 2: deviceId fallback
                if match_idx is None:
                    for idx, it in enumerate(items):
                        if it["deviceId"] == tgt_dev:
                            match_idx = idx
                            break

            if match_idx is not None:
                self.audio_target_combo.current(match_idx)
            self._on_audio_route_changed_gui(curr_route)
        finally:
            self._is_refreshing_audio_sinks = False

    def _apply_audio_route_click(self):
        idx = self.audio_target_combo.current()
        if idx < 0 or idx >= len(self._audio_sink_items):
            return

        item = self._audio_sink_items[idx]
        if item.get("isDisable"):
            self.node.set_and_broadcast_audio_route(None)
            self._log("[Ses Matrisi] ⚪ Ses yönlendirme devre dışı bırakıldı. Her cihaz kendi sesini çalıyor.")
        else:
            route = {
                "targetDeviceId": item["deviceId"],
                "targetDeviceName": item["deviceName"],
                "targetSinkId": item["sinkId"],
                "targetSinkName": item["sinkName"],
                "targetIp": item["ipAddress"],
                "targetUdpPort": 42852
            }
            self.node.set_and_broadcast_audio_route(route)
            self._log(f"[Ses Matrisi] 🎧 Tüm sesler -> '{item['deviceName']}' ({item['sinkName']}) hedefine yönlendirildi.")

        self._on_audio_route_changed_gui(self.node.active_audio_route)

    def _reset_audio_route_click(self):
        self.node.set_and_broadcast_audio_route(None)
        self._log("[Ses Matrisi] 🔄 Ses matrisi sıfırlandı. Yerel çalma moduna dönüldü.")
        self._refresh_audio_sinks_dropdown()

    def _refresh_audio_sinks_click(self):
        self.node.local_audio_sinks = self.node.enumerate_local_audio_sinks()
        self.node.broadcast_local_audio_sinks()
        self._refresh_audio_sinks_dropdown()
        self._log(f"[Ses Matrisi] 🔍 Yerel ses çıkışları tarandı ({len(self.node.local_audio_sinks)} aygıt).")

    def _on_audio_route_changed_gui(self, route: Optional[Dict[str, Any]]):
        if not hasattr(self, "audio_matrix_status_lbl"):
            return

        if not route or route.get("targetDeviceId") in (None, "", "NONE"):
            self.audio_matrix_status_lbl.configure(
                text="Durum: ⚪ Boşta (Her cihaz kendi yerel sesini çalıyor)",
                foreground="#A5B4FC"
            )
        elif route.get("targetDeviceId") == self.node.device_id:
            sink_str = route.get("targetSinkName", "Varsayılan")
            self.audio_matrix_status_lbl.configure(
                text=f"Durum: 🟢 Aktif Alıcı — Diğer cihazların sesleri bu Linux'a ({sink_str}) akıyor.",
                foreground="#4ADE80"
            )
        else:
            dev_str = route.get("targetDeviceName", "Merkez Cihaz")
            sink_str = route.get("targetSinkName", "Kulaklık/Hoparlör")
            self.audio_matrix_status_lbl.configure(
                text=f"Durum: 📡 Ses Yayını Aktif — Bu Linux'un sesi -> {dev_str} ({sink_str}) hedefine canlı akıyor.",
                foreground="#FBBF24"
            )

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
        if hasattr(self, "_displayed_peer_ids") and sel[0] < len(self._displayed_peer_ids):
            self.selected_peer_id = self._displayed_peer_ids[sel[0]]
        else:
            keys = list(self.node.peers.keys())
            if sel[0] < len(keys):
                self.selected_peer_id = keys[sel[0]]
            else:
                return

        peer = self.node.peers.get(self.selected_peer_id)
        if peer:
            self.pair_prompt_lbl.configure(text=f"'{peer.get('deviceName')}' 6 Haneli Kodu:")
            if hasattr(self, "pair_btn"):
                self.pair_btn.configure(text=f"🔗 '{peer.get('deviceName')}' ile Doğrula ve Bağlan")
            self.pin_entry.focus_set()

    def _on_inbound_accept_click(self):
        target_id = self.selected_peer_id
        if not target_id or target_id not in self.node.peers or not self.node.peers[target_id].get("in_ok"):
            inbound = [p for p in self.node.peers.values() if p.get("in_ok")]
            if inbound:
                target_id = inbound[0].get("deviceId")
        if target_id and target_id in self.node.peers:
            peer = self.node.peers[target_id]
            self._log(f"'{peer.get('deviceName')}' bağlantı isteği tek tıkla onaylanıyor...")
            threading.Thread(target=self.node.accept_inbound_pairing, args=(target_id, True), daemon=True).start()
            if self.inbound_card.winfo_ismapped():
                self.inbound_card.pack_forget()
            self._refresh_state()

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
        if hasattr(self, "_displayed_peer_ids") and sel[0] < len(self._displayed_peer_ids):
            peer_id = self._displayed_peer_ids[sel[0]]
            peer = self.node.peers.get(peer_id)
            if peer:
                self._show_enter_pin_dialog(peer)

    def _on_pin_request_received(self, sender_id: str, sender_name: str):
        def _update():
            self.selected_peer_id = sender_id
            self.inbound_label.configure(
                text=f"🔔 '{sender_name}' sizin 6 haneli kodunuzu girdi! Hemen kabul edebilir veya kodunu girebilirsiniz."
            )
            self.inbound_accept_btn.configure(text=f"✨ '{sender_name}' Hemen Kabul Et")
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
        self._displayed_peer_ids = []
        if not self.node.peers:
            self.peer_list.insert(tk.END, "  [Henüz ağda başka bir Connect Me cihazı bulunamadı. Cihazlarınızda programı açık tutun.]")
        else:
            for pid, p in self.node.peers.items():
                self._displayed_peer_ids.append(pid)
                name = p.get("deviceName", pid)
                ip = p.get("ipAddress", "")
                is_mut = p.get("isMutuallyPaired", False)
                is_trust = p.get("is_trusted", False)
                tag = "⭐ " if is_trust else ""
                mon_cnt = len(p.get("monitors", []))
                mon_str = f" [{mon_cnt} Ekran]" if mon_cnt > 1 else ""
                if is_mut:
                    status = f"{tag}🟢 ÇİFT TARAFLI ONAYLI (Aktif)"
                elif p.get("in_ok"):
                    status = f"{tag}🔔 Karşı Taraf Onayladı (Kabul Bekliyor)"
                elif p.get("out_ok"):
                    status = f"{tag}🟡 Karşı Onay Bekliyor"
                else:
                    status = f"{tag}⚪ Eşleşme Bekleniyor"
                self.peer_list.insert(tk.END, f"  🖥️ {name} ({ip}){mon_str} — {status}")

            keys = list(self.node.peers.keys())
            if self.selected_peer_id and self.selected_peer_id in keys:
                idx = keys.index(self.selected_peer_id)
                self.peer_list.selection_set(idx)

        has_pending_inbound = any(p.get("in_ok") and not p.get("isMutuallyPaired") for p in self.node.peers.values())
        if not has_pending_inbound and self.inbound_card.winfo_ismapped():
            self.inbound_card.pack_forget()

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
        for pid, p in self.node.peers.items():
            if p.get("canvasX") is None:
                self.peer_canvas_coords.pop(pid, None)
        self._draw_2d_canvas()

        # 5. Kenar Geçiş Bariyerlerini Güncelle (Host Modu)
        if hasattr(self, "edge_sentinels") and not self.host_capture_overlay.is_active:
            self.edge_sentinels.update_sentinels()

        # 6. Gelen Ses Alıcısı Durumunu Güncelle
        if hasattr(self, "audio_rx_status_lbl"):
            rx_bytes = getattr(self.node, "audio_rx_bytes", 0)
            rx_active = getattr(self.node, "audio_rx_active", False)
            last_sender = getattr(self.node, "audio_rx_last_sender", "")
            last_player = getattr(self.node, "audio_rx_last_player", "")
            kb = rx_bytes / 1024.0
            mb = kb / 1024.0
            size_str = f"{mb:.1f} MB" if mb >= 1.0 else f"{kb:.0f} KB"
            if rx_active:
                player_str = f" [{last_player}]" if last_player else ""
                self.audio_rx_status_lbl.configure(
                    text=f"Durum: 🟢 Aktif Alınıyor & Çalınıyor{player_str} (Kaynak: {last_sender} — {size_str} alındı)",
                    foreground="#4ADE80"
                )
            else:
                bytes_info = f" — {size_str} alındı" if rx_bytes > 0 else " — 0 bayt alındı"
                self.audio_rx_status_lbl.configure(
                    text=f"Durum: 🎧 Dinliyor (UDP Port: 42852){bytes_info}",
                    foreground="#38BDF8"
                )

    def _reset_canvas_layout(self):
        self.peer_canvas_coords.clear()
        self.node.reset_peer_arrangements()
        self._refresh_monitors()
        self._draw_2d_canvas()
        if hasattr(self, "edge_sentinels"):
            self.edge_sentinels.update_sentinels()
        self._log("[Ekran Düzeni] 📐 2D ekran yerleşimi sıfırlandı ve cihazlar yerel monitörlerin sağına hizalandı.")
        if hasattr(self, "canvas_info_lbl"):
            self.canvas_info_lbl.config(text="📐 2D ekran yerleşimi sıfırlandı.")

    def _get_colony_layout_metrics(self):
        """Calculates colony bounding box, uniform scaling factor, and centering offsets."""
        self.node.recompute_colony_world_coordinates()
        cw = max(420, self.canvas.winfo_width())
        ch = max(280, self.canvas.winfo_height())
        min_vx, min_vy, total_w, total_h = self.node.topology.virtual_desktop_bounds()
        all_peers = list(self.node.peers.values())

        min_x = float(self.node.local_colony_x)
        min_y = float(self.node.local_colony_y)
        max_x = float(self.node.local_colony_x + total_w)
        max_y = float(self.node.local_colony_y + total_h)

        for p in all_peers:
            pw = p.get("screenWidth", 1920) or 1920
            ph = p.get("screenHeight", 1080) or 1080
            cx = float(p.get("colonyX", 0.0))
            cy = float(p.get("colonyY", 0.0))
            if cx < min_x:
                min_x = cx
            if cy < min_y:
                min_y = cy
            if cx + pw > max_x:
                max_x = cx + pw
            if cy + ph > max_y:
                max_y = cy + ph

        span_w = max(800.0, max_x - min_x)
        span_h = max(600.0, max_y - min_y)
        base_scale = min((cw - 140.0) / span_w, (ch - 110.0) / span_h)
        base_scale = max(0.035, min(0.16, base_scale))
        scale = base_scale * getattr(self, "canvas_scale", 1.0)

        draw_w = span_w * scale
        draw_h = span_h * scale
        offset_x = (cw - draw_w) / 2.0 - min_x * scale
        offset_y = (ch - draw_h) / 2.0 - min_y * scale

        return scale, offset_x, offset_y, min_vx, min_vy, total_w, total_h

    def _get_current_canvas_scale(self) -> float:
        scale, _, _, _, _, _, _ = self._get_colony_layout_metrics()
        return scale

    def _get_canvas_local_mon_rects(self) -> list:
        scale, offset_x, offset_y, min_vx, min_vy, _, _ = self._get_colony_layout_metrics()
        rects = []
        for m in self.node.topology.monitors:
            mx1 = offset_x + (self.node.local_colony_x + (m.virtualX - min_vx)) * scale
            my1 = offset_y + (self.node.local_colony_y + (m.virtualY - min_vy)) * scale
            mx2 = mx1 + max(70.0, m.width * scale)
            my2 = my1 + max(50.0, m.height * scale)
            rects.append((m, mx1, my1, mx2, my2))
        return rects

    def _on_zoom_slider_change(self, val):
        try:
            self.canvas_scale = float(val)
            if hasattr(self, "zoom_percent_lbl"):
                self.zoom_percent_lbl.config(text=f"{int(self.canvas_scale * 100)}%")
            self._draw_2d_canvas()
        except Exception:
            pass

    def _on_canvas_mousewheel(self, event):
        delta = 0.08 if event.delta > 0 else -0.08
        self._zoom_canvas(delta)

    def _zoom_canvas(self, delta: float):
        new_scale = max(0.60, min(1.40, getattr(self, "canvas_scale", 1.0) + delta))
        self.canvas_scale = new_scale
        if hasattr(self, "zoom_slider"):
            self.zoom_slider.set(new_scale)
        if hasattr(self, "zoom_percent_lbl"):
            self.zoom_percent_lbl.config(text=f"{int(new_scale * 100)}%")
        self._draw_2d_canvas()

    def _fit_canvas_view(self):
        self.canvas_scale = 1.0
        if hasattr(self, "zoom_slider"):
            self.zoom_slider.set(1.0)
        if hasattr(self, "zoom_percent_lbl"):
            self.zoom_percent_lbl.config(text="100%")
        self._draw_2d_canvas()

    def _auto_arrange_screens(self):
        """Automatically distributes all paired peers cleanly across screen edges."""
        self.peer_canvas_coords.clear()
        self.node.auto_arrange_peers()
        for p in self.node.peers.values():
            if p.get("isMutuallyPaired"):
                p["attachedToDeviceId"] = self.node.device_id
        self.node.recompute_colony_world_coordinates()
        self.node.send_colony_topology_sync_to_all_peers()
        self._refresh_state()
        if hasattr(self, "edge_sentinels"):
            self.edge_sentinels.update_sentinels()
        self._draw_2d_canvas()
        self._log("[Ekran Konfigürasyonu] 📐 Tüm ekranlar kenarlara dengeli olarak dağıtıldı.")
        if hasattr(self, "canvas_info_lbl"):
            self.canvas_info_lbl.config(text="📐 Tüm bağlı cihazlar kenarlara otomatik ve dengeli olarak dağıtıldı.")

    def _switch_to_selected_peer(self):
        """Switches mouse/keyboard control immediately to the selected peer."""
        target = None
        if self.selected_peer_id and self.selected_peer_id in self.node.peers:
            target = self.node.peers[self.selected_peer_id]
        if not target or not target.get("isMutuallyPaired"):
            for p in self.node.peers.values():
                if p.get("isMutuallyPaired"):
                    target = p
                    break
        if target and target.get("isMutuallyPaired"):
            edge = target.get("assignedEdge", EDGE_RIGHT)
            seg_s = float(target.get("edgeOffsetStart", 0.0))
            seg_e = float(target.get("edgeOffsetEnd", 1.0))
            mid = (seg_s + seg_e) / 2.0
            self._activate_host_capture(target, edge, mid)
        else:
            if hasattr(self, "canvas_info_lbl"):
                self.canvas_info_lbl.config(text="⚠️ Fare aktarımı için önce aktif bağlı bir cihaz seçin.")

    def _quick_assign_selected_peer(self, edge: int):
        """Moves selected peer to the requested edge and evenly distributes peers on that edge."""
        if not self.selected_peer_id:
            paired = [p for p in self.node.peers.values() if p.get("isMutuallyPaired")]
            if len(paired) == 1:
                self.selected_peer_id = paired[0]["deviceId"]
            elif self.node.peers:
                self.selected_peer_id = next(iter(self.node.peers.keys()))

        if not self.selected_peer_id or self.selected_peer_id not in self.node.peers:
            if hasattr(self, "canvas_info_lbl"):
                self.canvas_info_lbl.config(text="⚠️ Kenar atamak için önce kanvastan bir cihaz kutusuna tıklayın.")
            return

        self._set_peer_edge(self.selected_peer_id, edge)
        p = self.node.peers[self.selected_peer_id]
        edge_names = {EDGE_LEFT: "Sol", EDGE_RIGHT: "Sağ", EDGE_TOP: "Üst", EDGE_BOTTOM: "Alt"}
        seg_s = int(float(p.get("edgeOffsetStart", 0.0)) * 100)
        seg_e = int(float(p.get("edgeOffsetEnd", 1.0)) * 100)
        dev_name = p.get("deviceName", p["deviceId"])
        mon_id = p.get("attachedMonitorId") or "DISPLAY1"
        msg = f"🎯 '{dev_name}' -> {mon_id} {edge_names.get(edge, 'Sağ')} Kenar (%{seg_s} - %{seg_e} kesiti) hizalandı."
        self._log(f"[Ekran Konfigürasyonu] {msg}")
        if hasattr(self, "canvas_info_lbl"):
            self.canvas_info_lbl.config(text=msg)

    def _set_peer_edge(self, pid: str, edge: int):
        if pid not in self.node.peers:
            return
        p = self.node.peers[pid]
        old_edge = p.get("assignedEdge", EDGE_RIGHT)
        outermost = self.node.topology.get_outermost_monitor_for_edge(edge)
        mon_id = getattr(outermost, "monitorId", "") if outermost else ""

        p["assignedEdge"] = edge
        p["attachedMonitorId"] = mon_id
        p["attachedToDeviceId"] = self.node.device_id
        p["canvasX"] = None
        p["canvasY"] = None
        self.peer_canvas_coords.pop(pid, None)

        if old_edge != edge:
            self.node.distribute_peers_on_edge(old_edge)
        self.node.distribute_peers_on_edge(edge)

        self.node.recompute_colony_world_coordinates()
        self.node.send_edge_config_to_peer(p)
        self.node.send_colony_topology_sync_to_all_peers()

        self._refresh_state()
        if hasattr(self, "edge_sentinels"):
            self.edge_sentinels.update_sentinels()
        self._draw_2d_canvas()

    def _compute_canvas_pos_from_edge_and_segment(
        self,
        peer: dict,
        local_mon_rects: list,
        card_w: float,
        card_h: float
    ) -> Tuple[float, float]:
        gap = 12.0
        arr_edge = peer.get("assignedEdge", EDGE_RIGHT)
        att_id = peer.get("attachedMonitorId")
        attached_box = None
        if att_id:
            for mon_item in local_mon_rects:
                if getattr(mon_item[0], "monitorId", "") == att_id:
                    attached_box = mon_item
                    break
        if not attached_box:
            outermost = self.node.topology.get_outermost_monitor_for_edge(arr_edge)
            for mon_item in local_mon_rects:
                if getattr(mon_item[0], "monitorId", "") == getattr(outermost, "monitorId", ""):
                    attached_box = mon_item
                    break
        if not attached_box and local_mon_rects:
            attached_box = local_mon_rects[0]

        if not attached_box:
            return 100.0, 100.0

        _, amx1, amy1, amx2, amy2 = attached_box
        mon_w = max(1.0, amx2 - amx1)
        mon_h = max(1.0, amy2 - amy1)

        s_start = float(peer.get("edgeOffsetStart", 0.0))
        s_end = float(peer.get("edgeOffsetEnd", 1.0))
        mid = (s_start + s_end) / 2.0

        if arr_edge == EDGE_LEFT:
            px = amx1 - card_w - gap
            py = amy1 + mid * mon_h - card_h / 2.0
        elif arr_edge == EDGE_RIGHT:
            px = amx2 + gap
            py = amy1 + mid * mon_h - card_h / 2.0
        elif arr_edge == EDGE_TOP:
            px = amx1 + mid * mon_w - card_w / 2.0
            py = amy1 - card_h - gap
        elif arr_edge == EDGE_BOTTOM:
            px = amx1 + mid * mon_w - card_w / 2.0
            py = amy2 + gap
        else:
            px = amx2 + gap
            py = amy1 + mid * mon_h - card_h / 2.0

        return px, py

    def _on_peer_drag_start(self, event, peer_id: str):
        self.selected_peer_id = peer_id
        scale = self._get_current_canvas_scale()
        p = self.node.peers.get(peer_id, {})
        pw = p.get("screenWidth", 1920) or 1920
        ph = p.get("screenHeight", 1080) or 1080
        card_w = max(120.0, pw * scale)
        card_h = max(70.0, ph * scale)
        coords = self.peer_canvas_coords.get(peer_id, (event.x - card_w / 2.0, event.y - card_h / 2.0))
        self._drag_data["peer_id"] = peer_id
        self._drag_data["start_x"] = event.x
        self._drag_data["start_y"] = event.y
        self._drag_data["offset_x"] = event.x - coords[0]
        self._drag_data["offset_y"] = event.y - coords[1]
        self._drag_data["card_x"] = coords[0]
        self._drag_data["card_y"] = coords[1]
        self._drag_data["card_w"] = card_w
        self._drag_data["card_h"] = card_h
        try:
            self.canvas.lift(f"peer_{peer_id}")
            self.canvas.itemconfig(f"peer_box_{peer_id}", outline="#38BDF8", width=3)
        except Exception:
            pass

        if peer_id in self.node.peers:
            p = self.node.peers[peer_id]
            dev_name = p.get("deviceName", peer_id)
            edge_names = {EDGE_LEFT: "Sol", EDGE_RIGHT: "Sağ", EDGE_TOP: "Üst", EDGE_BOTTOM: "Alt"}
            cur_edge = p.get("assignedEdge", EDGE_RIGHT)
            seg_s = int(float(p.get("edgeOffsetStart", 0.0)) * 100)
            seg_e = int(float(p.get("edgeOffsetEnd", 1.0)) * 100)
            target_desc = p.get("attachedToDeviceId") or p.get("attachedMonitorId") or "DISPLAY1"
            if target_desc in self.node.peers:
                target_desc = self.node.peers[target_desc].get("deviceName", target_desc)
            msg = f"🎯 {dev_name} -> {target_desc} {edge_names.get(cur_edge, 'Sağ')} Kenar (%{seg_s} - %{seg_e} kesiti)"
            if hasattr(self, "canvas_info_lbl"):
                self.canvas_info_lbl.config(text=msg)

    def _on_peer_drag_motion(self, event):
        pid = self._drag_data.get("peer_id")
        if not pid or pid not in self.node.peers:
            return

        card_w = self._drag_data.get("card_w", 120.0)
        card_h = self._drag_data.get("card_h", 70.0)
        raw_x = event.x - self._drag_data.get("offset_x", card_w / 2.0)
        raw_y = event.y - self._drag_data.get("offset_y", card_h / 2.0)
        self._drag_data["card_x"] = raw_x
        self._drag_data["card_y"] = raw_y

        local_mon_rects = self._get_canvas_local_mon_rects()
        snapped_edge, final_x, final_y, seg_start, seg_end, target_id, target_name, is_local, target_mon_id = self._snap_peer_box_to_anchors_on_canvas(
            pid, raw_x, raw_y, card_w, card_h, local_mon_rects
        )

        # Smooth real-time snapping: move visual box so user sees exact snapped placement as they drag
        card_coords = self.canvas.coords(f"peer_box_{pid}")
        if card_coords and len(card_coords) == 4:
            dx = final_x - card_coords[0]
            dy = final_y - card_coords[1]
            try:
                self.canvas.move(f"peer_{pid}", dx, dy)
            except Exception:
                pass

        # Update real-time glowing portal segment and connector line
        self._update_portal_preview(pid, snapped_edge, seg_start, seg_end, target_id, is_local, target_mon_id, final_x, final_y, card_w, card_h, local_mon_rects)

        edge_names = {EDGE_LEFT: "Sol", EDGE_RIGHT: "Sağ", EDGE_TOP: "Üst", EDGE_BOTTOM: "Alt"}
        dev_name = self.node.peers[pid].get("deviceName", pid)
        msg = f"🎯 {dev_name} -> {target_name} {edge_names.get(snapped_edge, 'Sağ')} Kenar (%{int(seg_start*100)} - %{int(seg_end*100)} kesiti)"
        if hasattr(self, "canvas_info_lbl"):
            self.canvas_info_lbl.config(text=msg)

    def _update_portal_preview(
        self,
        pid: str,
        arr_edge: int,
        seg_start: float,
        seg_end: float,
        target_id: str,
        is_local: bool,
        target_mon_id: str,
        final_x: float,
        final_y: float,
        card_w: float,
        card_h: float,
        local_mon_rects: list
    ):
        """Redraws or repositions the glowing portal segment and connector line in real-time during drag."""
        scale = self._get_current_canvas_scale()
        amx1, amy1, amx2, amy2 = 0, 0, 100, 100

        if not is_local and target_id in self.node.peers:
            parent_p = self.node.peers[target_id]
            parent_w = max(120.0, (parent_p.get("screenWidth", 1920) or 1920) * scale)
            parent_h = max(70.0, (parent_p.get("screenHeight", 1080) or 1080) * scale)
            if target_id in self.peer_canvas_coords:
                parent_x, parent_y = self.peer_canvas_coords[target_id]
            else:
                metrics = self._get_colony_layout_metrics()
                parent_x = metrics[1] + float(parent_p.get("colonyX", 0.0)) * scale
                parent_y = metrics[2] + float(parent_p.get("colonyY", 0.0)) * scale
            amx1, amy1, amx2, amy2 = parent_x, parent_y, parent_x + parent_w, parent_y + parent_h
        else:
            attached_box = None
            if target_mon_id:
                for mon_item in local_mon_rects:
                    if getattr(mon_item[0], "monitorId", "") == target_mon_id:
                        attached_box = mon_item
                        break
            if not attached_box and local_mon_rects:
                attached_box = local_mon_rects[0]
            if attached_box:
                _, amx1, amy1, amx2, amy2 = attached_box

        am_w = max(1.0, amx2 - amx1)
        am_h = max(1.0, amy2 - amy1)
        p_s = seg_start
        p_e = seg_end

        if arr_edge == EDGE_LEFT:
            plx1 = plx2 = amx1 - 2
            ply1 = amy1 + p_s * am_h
            ply2 = amy1 + p_e * am_h
            c_anchor_x, c_anchor_y = final_x + card_w, final_y + card_h / 2.0
        elif arr_edge == EDGE_RIGHT:
            plx1 = plx2 = amx2 + 2
            ply1 = amy1 + p_s * am_h
            ply2 = amy1 + p_e * am_h
            c_anchor_x, c_anchor_y = final_x, final_y + card_h / 2.0
        elif arr_edge == EDGE_TOP:
            ply1 = ply2 = amy1 - 2
            plx1 = amx1 + p_s * am_w
            plx2 = amx1 + p_e * am_w
            c_anchor_x, c_anchor_y = final_x + card_w / 2.0, final_y + card_h
        elif arr_edge == EDGE_BOTTOM:
            ply1 = ply2 = amy2 + 2
            plx1 = amx1 + p_s * am_w
            plx2 = amx1 + p_e * am_w
            c_anchor_x, c_anchor_y = final_x + card_w / 2.0, final_y
        else:
            plx1 = plx2 = amx2 + 2
            ply1 = amy1
            ply2 = amy2
            c_anchor_x, c_anchor_y = final_x, final_y + card_h / 2.0

        portal_tag = f"peer_portal_{pid}"
        connector_tag = f"peer_line_{pid}"
        pmx = (plx1 + plx2) / 2.0
        pmy = (ply1 + ply2) / 2.0

        try:
            self.canvas.delete(portal_tag)
            self.canvas.delete(connector_tag)
            self.canvas.create_line(plx1, ply1, plx2, ply2, fill="#0284C7", width=8, capstyle=tk.ROUND, tags=(portal_tag,))
            self.canvas.create_line(plx1, ply1, plx2, ply2, fill="#38BDF8", width=4, capstyle=tk.ROUND, tags=(portal_tag,))
            self.canvas.create_oval(pmx - 4, pmy - 4, pmx + 4, pmy + 4, fill="#38BDF8", outline="#FFFFFF", width=2, tags=(portal_tag,))
            self.canvas.create_line(pmx, pmy, c_anchor_x, c_anchor_y, fill="#38BDF8", width=2, tags=(connector_tag,))
            self.canvas.lift(f"peer_{pid}")
        except Exception:
            pass

    def _snap_peer_box_to_anchors_on_canvas(
        self,
        dragging_pid: str,
        dragged_x: float,
        dragged_y: float,
        card_w: float,
        card_h: float,
        local_mon_rects: list
    ):
        """
        Snaps a dragged peer box to the nearest anchor on canvas
        (either a local monitor or another peer card in the colony mesh).
        Returns: (snapped_edge, final_x, final_y, seg_start, seg_end, target_id, target_name, is_local, target_mon_id)
        """
        scale = self._get_current_canvas_scale()
        anchors = []

        # Local physical monitors
        for mon, mx1, my1, mx2, my2 in local_mon_rects:
            m_id = getattr(mon, "monitorId", "DISPLAY1")
            anchors.append({
                "id": self.node.device_id,
                "monitor_id": m_id,
                "name": f"{self.node.device_name} ({mon.name})",
                "is_local": True,
                "left": mx1,
                "top": my1,
                "right": mx2,
                "bottom": my2,
                "w": max(1.0, mx2 - mx1),
                "h": max(1.0, my2 - my1)
            })

        # Other peer display cards in colony
        for opid, op in self.node.peers.items():
            if opid == dragging_pid:
                continue
            op_w = op.get("screenWidth", 1920) or 1920
            op_h = op.get("screenHeight", 1080) or 1080
            ow = max(120.0, op_w * scale)
            oh = max(70.0, op_h * scale)
            if opid in self.peer_canvas_coords:
                ox1, oy1 = self.peer_canvas_coords[opid]
            else:
                metrics = self._get_colony_layout_metrics()
                ox1 = metrics[1] + float(op.get("colonyX", 0.0)) * scale
                oy1 = metrics[2] + float(op.get("colonyY", 0.0)) * scale
            anchors.append({
                "id": opid,
                "monitor_id": "",
                "name": op.get("deviceName", opid),
                "is_local": False,
                "left": ox1,
                "top": oy1,
                "right": ox1 + ow,
                "bottom": oy1 + oh,
                "w": ow,
                "h": oh
            })

        if not anchors:
            return EDGE_RIGHT, dragged_x, dragged_y, 0.0, 1.0, self.node.device_id, "Ana Ekran", True, "DISPLAY1"

        peer_cx = dragged_x + card_w / 2.0
        peer_cy = dragged_y + card_h / 2.0

        best = min(
            anchors,
            key=lambda a: (
                (peer_cx - (a["left"] + a["w"] / 2.0)) ** 2
                + (peer_cy - (a["top"] + a["h"] / 2.0)) ** 2
            )
        )

        a_left = best["left"]
        a_top = best["top"]
        a_right = best["right"]
        a_bottom = best["bottom"]
        a_w = best["w"]
        a_h = best["h"]

        norm_dx = (peer_cx - (a_left + a_w / 2.0)) / (a_w / 2.0)
        norm_dy = (peer_cy - (a_top + a_h / 2.0)) / (a_h / 2.0)

        gap = 8.0
        if abs(norm_dx) >= abs(norm_dy):
            snapped_edge = EDGE_LEFT if norm_dx < 0 else EDGE_RIGHT
            final_x = (a_left - card_w - gap) if snapped_edge == EDGE_LEFT else (a_right + gap)

            norm_mid = max(0.08, min(0.92, (peer_cy - a_top) / a_h))
            half_span = max(0.10, min(0.40, (card_h / 2.0) / a_h))

            if card_h >= a_h * 0.85 and abs(norm_mid - 0.5) < 0.12:
                seg_start = 0.0
                seg_end = 1.0
            elif norm_mid < 0.5:
                seg_start = 0.0
                seg_end = max(0.20, min(1.0, norm_mid + half_span))
            else:
                seg_start = max(0.0, min(0.80, norm_mid - half_span))
                seg_end = 1.0

            final_y = a_top + norm_mid * a_h - card_h / 2.0
        else:
            snapped_edge = EDGE_TOP if norm_dy < 0 else EDGE_BOTTOM
            final_y = (a_top - card_h - gap) if snapped_edge == EDGE_TOP else (a_bottom + gap)

            norm_mid = max(0.08, min(0.92, (peer_cx - a_left) / a_w))
            half_span = max(0.10, min(0.40, (card_w / 2.0) / a_w))

            if card_w >= a_w * 0.85 and abs(norm_mid - 0.5) < 0.12:
                seg_start = 0.0
                seg_end = 1.0
            elif norm_mid < 0.5:
                seg_start = 0.0
                seg_end = max(0.20, min(1.0, norm_mid + half_span))
            else:
                seg_start = max(0.0, min(0.80, norm_mid - half_span))
                seg_end = 1.0

            final_x = a_left + norm_mid * a_w - card_w / 2.0

        return snapped_edge, final_x, final_y, seg_start, seg_end, best["id"], best["name"], best["is_local"], best.get("monitor_id", "")

    def _snap_peer_box_to_local_monitors_on_canvas(
        self,
        dragged_x: float,
        dragged_y: float,
        card_w: float,
        card_h: float,
        local_mon_rects: list
    ):
        """Backward compatibility wrapper."""
        edge, fx, fy, ss, se, tid, _, _, _ = self._snap_peer_box_to_anchors_on_canvas(
            "", dragged_x, dragged_y, card_w, card_h, local_mon_rects
        )
        return edge, fx, fy, ss, se, tid

    def _on_peer_drag_release(self, event):
        pid = self._drag_data.get("peer_id")
        self._drag_data["peer_id"] = None
        try:
            self.canvas.config(cursor="")
        except Exception:
            pass
        if not pid or pid not in self.node.peers:
            return

        scale = self._get_current_canvas_scale()
        peer = self.node.peers[pid]
        p_w = peer.get("screenWidth", 1920) or 1920
        p_h = peer.get("screenHeight", 1080) or 1080
        card_w = max(120.0, p_w * scale)
        card_h = max(70.0, p_h * scale)
        local_mon_rects = self._get_canvas_local_mon_rects()

        cur_x = self._drag_data.get("card_x", self.peer_canvas_coords.get(pid, (0, 0))[0])
        cur_y = self._drag_data.get("card_y", self.peer_canvas_coords.get(pid, (0, 0))[1])

        snapped_edge, final_x, final_y, seg_start, seg_end, target_id, target_name, is_local, target_mon_id = self._snap_peer_box_to_anchors_on_canvas(
            pid, cur_x, cur_y, card_w, card_h, local_mon_rects
        )

        self.peer_canvas_coords[pid] = (final_x, final_y)
        peer["canvasX"] = final_x
        peer["canvasY"] = final_y
        peer["assignedEdge"] = snapped_edge
        peer["edgeOffsetStart"] = seg_start
        peer["edgeOffsetEnd"] = seg_end

        if is_local:
            peer["attachedMonitorId"] = target_mon_id or "DISPLAY1"
            peer["attachedToDeviceId"] = self.node.device_id
            if self.node.local_attached_to_device_id == pid:
                self.node.local_attached_to_device_id = ""
        else:
            peer["attachedMonitorId"] = ""
            peer["attachedToDeviceId"] = target_id
            if self.node.local_attached_to_device_id == pid:
                self.node.local_attached_to_device_id = ""

        self.node.set_peer_arrangement(
            pid,
            snapped_edge,
            edge_offset=(seg_start + seg_end) / 2.0,
            canvas_x=final_x,
            canvas_y=final_y,
            edge_offset_start=seg_start,
            edge_offset_end=seg_end,
            attached_monitor_id=peer.get("attachedMonitorId"),
            attached_to_device_id=peer.get("attachedToDeviceId")
        )
        self.node.recompute_colony_world_coordinates()
        self.node.send_edge_config_to_peer(peer)
        self.node.send_colony_topology_sync_to_all_peers()

        if hasattr(self, "edge_sentinels"):
            self.edge_sentinels.update_sentinels()
        self._draw_2d_canvas()

        edge_names = {EDGE_LEFT: "Sol", EDGE_RIGHT: "Sağ", EDGE_TOP: "Üst", EDGE_BOTTOM: "Alt"}
        dev_name = peer.get("deviceName", pid)
        msg = f"🎯 '{dev_name}' -> {target_name} {edge_names.get(snapped_edge, 'Sağ')} Kenar (%{int(seg_start*100)} - %{int(seg_end*100)} kesiti) hizalandı."
        self._log(f"[Ekran Konfigürasyonu] {msg}")
        if hasattr(self, "canvas_info_lbl"):
            self.canvas_info_lbl.config(text=msg)

    def _toggle_host_capture(self):
        if self.host_capture_overlay.is_active:
            self._deactivate_host_capture(notify_peer=True)
            return

        target = None
        for p in self.node.peers.values():
            if p.get("isMutuallyPaired"):
                target = p
                break
        if target:
            edge = target.get("assignedEdge", EDGE_RIGHT)
            self._activate_host_capture(target, edge, 0.5)

    def _switch_to_edge_peer(self, edge: int):
        for p in self.node.peers.values():
            if p.get("isMutuallyPaired") and p.get("assignedEdge") == edge:
                self._activate_host_capture(p, edge, 0.5)
                break

    def _activate_host_capture(self, peer: dict, edge: int, norm_pos: float = 0.5):
        if not peer or not peer.get("isMutuallyPaired"):
            return
        self.node.switch_control_to_peer(peer, edge, norm_pos)

    def _deactivate_host_capture(self, notify_peer: bool = True):
        self.host_capture_overlay.deactivate(notify_peer=notify_peer)
        self.edge_sentinels.show_with_cooldown(0.6)
        self._refresh_state()

    def _on_sentinel_triggered(self, peer: dict, edge: int, norm_pos: float):
        self._activate_host_capture(peer, edge, norm_pos)

    def _on_host_control_switched(self, peer: dict, edge: int, norm_pos: float):
        self.edge_sentinels.hide()
        self.host_capture_overlay.activate(peer, edge, norm_pos)
        self._refresh_state()

    def _on_host_control_returned(self):
        self.host_capture_overlay.deactivate(notify_peer=False)
        self.edge_sentinels.show_with_cooldown(0.6)
        self._refresh_state()

    def _show_enter_pin_dialog(self, peer: dict):
        """Modal dialog to enter the 6-digit PIN code for a specific discovered peer."""
        dev_name = peer.get("deviceName", peer.get("deviceId", "Cihaz"))
        dev_ip = peer.get("ipAddress", "")
        platform = peer.get("platform", "").lower()
        dev_icon = "🪟" if "win" in platform else ("📱" if "android" in platform else "💻")

        dlg = tk.Toplevel(self.root)
        dlg.title(f"🔐 {dev_name} ile Eşleş")
        dlg.geometry("490x380")
        dlg.minsize(440, 320)
        dlg.configure(bg="#0B1120")
        dlg.transient(self.root)

        frm = ttk.Frame(dlg, padding=18)
        frm.pack(fill=tk.BOTH, expand=True)

        ttk.Label(frm, text=f"{dev_icon} {dev_name} ({dev_ip})", font=("Segoe UI", 13, "bold"), foreground="#38BDF8").pack(anchor=tk.W, pady=(0, 4))

        in_ok = peer.get("in_ok", False)
        if in_ok:
            info_txt = f"🔔 '{dev_name}' sizin kodunuzu ({self.node.local_pin}) doğru girdi!\n'Hemen Kabul Et' butonuna basarak tek tıkla onaylayabilir veya siz de onun 6 haneli kodunu girebilirsiniz:"
            info_fg = "#4ADE80"
        else:
            info_txt = f"'{dev_name}' ekranında görünen 6 haneli kodu (PIN) girin:"
            info_fg = "#94A3B8"

        ttk.Label(frm, text=info_txt, font=("Segoe UI", 10), foreground=info_fg, wraplength=440).pack(anchor=tk.W, pady=(0, 10))

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

        if in_ok:
            def _do_quick_accept():
                remember = trust_var.get()
                self._log(f"'{dev_name}' bağlantı isteği tek tıkla onaylanıyor...")
                threading.Thread(target=self.node.accept_inbound_pairing, args=(peer.get("deviceId", ""), remember), daemon=True).start()
                dlg.destroy()
                self._refresh_state()
            ttk.Button(btn_box, text="✨ Hemen Kabul Et", style="Success.TButton", command=_do_quick_accept).pack(side=tk.LEFT, padx=4)

        if peer.get("isMutuallyPaired"):
            def _do_disconnect():
                self.node.disconnect_peer(peer.get("deviceId", ""))
                dlg.destroy()
                self._refresh_state()
            ttk.Button(btn_box, text="🔌 Bağlantıyı Kes", style="Danger.TButton", command=_do_disconnect).pack(side=tk.LEFT, padx=4)

        ttk.Button(btn_box, text="🔗 Doğrula ve Bağlan", style="Accent.TButton", command=_do_pair).pack(side=tk.RIGHT, padx=4)
        ttk.Button(btn_box, text="İptal", command=dlg.destroy).pack(side=tk.RIGHT, padx=4)

        try:
            dlg.grab_set()
        except Exception:
            pass

        pin_box.focus_set()
        pin_box.bind("<Return>", lambda e: _do_pair())

    def _select_peer_and_focus_pin(self, peer_id: str):
        self.selected_peer_id = peer_id
        if peer_id in self.node.peers:
            peer = self.node.peers[peer_id]
            self._show_enter_pin_dialog(peer)

    def _draw_2d_canvas(self):
        self.canvas.delete("all")
        cw = max(420, self.canvas.winfo_width())
        ch = max(280, self.canvas.winfo_height())
        if cw < 60 or ch < 60:
            return

        scale, offset_x, offset_y, min_vx, min_vy, total_w, total_h = self._get_colony_layout_metrics()
        local_mon_rects = self._get_canvas_local_mon_rects()
        all_peers = list(self.node.peers.values())

        # 1. Subtle Background Grid
        for gx in range(0, cw, 40):
            self.canvas.create_line(gx, 0, gx, ch, fill="#111C33", width=1)
        for gy in range(0, ch, 40):
            self.canvas.create_line(0, gy, cw, gy, fill="#111C33", width=1)

        # 2. Local Linux Monitors (True to scale in colony world)
        for m, x1, y1, x2, y2 in local_mon_rects:
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
                p_w = p.get("screenWidth", 1920) or 1920
                p_h = p.get("screenHeight", 1080) or 1080
                p_monitors = p.get("monitors", [])
                is_mut = p.get("isMutuallyPaired", False)
                is_pending = p.get("in_ok", False) or p.get("out_ok", False)
                is_selected = (getattr(self, "selected_peer_id", None) == pid)
                is_controlling = bool(self.node.controlling_remote_peer and (self.node.controlling_remote_peer.get("deviceId") == pid or self.node.controlling_remote_peer.get("ipAddress") == p.get("ipAddress")))

                arr_edge = p.get("assignedEdge", EDGE_RIGHT)
                card_w = max(120.0, p_w * scale)
                card_h = max(70.0, p_h * scale)

                # Determine card position
                if self._drag_data.get("peer_id") == pid and self._drag_data.get("card_x") is not None:
                    px1 = self._drag_data["card_x"]
                    py1 = self._drag_data["card_y"]
                else:
                    px1 = offset_x + float(p.get("colonyX", 0.0)) * scale
                    py1 = offset_y + float(p.get("colonyY", 0.0)) * scale

                self.peer_canvas_coords[pid] = (px1, py1)
                p["canvasX"] = px1
                p["canvasY"] = py1

                px2 = px1 + card_w
                py2 = py1 + card_h

                if is_controlling:
                    card_fill = "#064E3B"
                    card_outline = "#4ADE80"
                    outline_w = 3
                    status_txt = "🎯 İmleç Bu Ekranda (Aktif)"
                    status_col = "#4ADE80"
                    line_col = "#4ADE80"
                elif not is_mut:
                    if is_pending:
                        card_fill = "#451A03"
                        card_outline = "#F59E0B"
                        outline_w = 2
                        status_txt = "🟠 Sizin Kodunuzu Bekliyor" if p.get("in_ok") else "🟡 Karşı Onay Bekliyor"
                        status_col = "#FCD34D"
                        line_col = "#F59E0B"
                    else:
                        card_fill = "#1E293B" if is_selected else "#0F172A"
                        card_outline = "#38BDF8" if is_selected else "#475569"
                        outline_w = 3 if is_selected else 2
                        status_txt = "⚪ Keşfedildi (Eşleşin)"
                        status_col = "#94A3B8"
                        line_col = "#38BDF8"
                else:
                    if is_selected:
                        card_fill = "#0C243B"
                        card_outline = "#38BDF8"
                        outline_w = 3
                    else:
                        card_fill = "#0B192C"
                        card_outline = "#10B981"
                        outline_w = 2
                    status_txt = "🟢 Çift Taraflı Aktif"
                    status_col = "#34D399"
                    line_col = "#38BDF8" if is_selected else "#10B981"

                # Attached anchor portal bar (local monitor or parent peer)
                if is_mut:
                    att_dev_id = p.get("attachedToDeviceId")
                    attached_box = None

                    if att_dev_id and att_dev_id != self.node.device_id and att_dev_id in self.node.peers:
                        parent_p = self.node.peers[att_dev_id]
                        parent_w = max(120.0, (parent_p.get("screenWidth", 1920) or 1920) * scale)
                        parent_h = max(70.0, (parent_p.get("screenHeight", 1080) or 1080) * scale)
                        if att_dev_id in self.peer_canvas_coords:
                            parent_x, parent_y = self.peer_canvas_coords[att_dev_id]
                        else:
                            parent_x = offset_x + float(parent_p.get("colonyX", 0.0)) * scale
                            parent_y = offset_y + float(parent_p.get("colonyY", 0.0)) * scale
                        amx1, amy1, amx2, amy2 = parent_x, parent_y, parent_x + parent_w, parent_y + parent_h
                    else:
                        att_id = p.get("attachedMonitorId")
                        if att_id:
                            for mon_item in local_mon_rects:
                                if getattr(mon_item[0], "monitorId", "") == att_id:
                                    attached_box = mon_item
                                    break
                        if not attached_box:
                            outermost = self.node.topology.get_outermost_monitor_for_edge(arr_edge)
                            for mon_item in local_mon_rects:
                                if getattr(mon_item[0], "monitorId", "") == getattr(outermost, "monitorId", ""):
                                    attached_box = mon_item
                                    break
                        if not attached_box and local_mon_rects:
                            attached_box = local_mon_rects[0]

                        if attached_box:
                            _, amx1, amy1, amx2, amy2 = attached_box
                        else:
                            amx1, amy1, amx2, amy2 = offset_x, offset_y, offset_x + max(100.0, total_w * scale), offset_y + max(100.0, total_h * scale)

                    am_w = max(1.0, amx2 - amx1)
                    am_h = max(1.0, amy2 - amy1)
                    p_s = max(0.0, min(1.0, float(p.get("edgeOffsetStart", 0.0))))
                    p_e = max(p_s + 0.05, min(1.0, float(p.get("edgeOffsetEnd", 1.0))))

                    if arr_edge == EDGE_LEFT:
                        plx1 = plx2 = amx1 - 2
                        ply1 = amy1 + p_s * am_h
                        ply2 = amy1 + p_e * am_h
                        c_anchor_x, c_anchor_y = px2, py1 + card_h / 2.0
                    elif arr_edge == EDGE_RIGHT:
                        plx1 = plx2 = amx2 + 2
                        ply1 = amy1 + p_s * am_h
                        ply2 = amy1 + p_e * am_h
                        c_anchor_x, c_anchor_y = px1, py1 + card_h / 2.0
                    elif arr_edge == EDGE_TOP:
                        ply1 = ply2 = amy1 - 2
                        plx1 = amx1 + p_s * am_w
                        plx2 = amx1 + p_e * am_w
                        c_anchor_x, c_anchor_y = px1 + card_w / 2.0, py2
                    elif arr_edge == EDGE_BOTTOM:
                        ply1 = ply2 = amy2 + 2
                        plx1 = amx1 + p_s * am_w
                        plx2 = amx1 + p_e * am_w
                        c_anchor_x, c_anchor_y = px1 + card_w / 2.0, py1
                    else:
                        plx1 = plx2 = amx2 + 2
                        ply1 = amy1
                        ply2 = amy2
                        c_anchor_x, c_anchor_y = px1, py1 + card_h / 2.0

                    portal_glow = "#0284C7" if is_selected else "#065F46"
                    portal_core = "#38BDF8" if is_selected else "#10B981"
                    portal_line_tag = f"peer_portal_{pid}"
                    connector_line_tag = f"peer_line_{pid}"
                    # Glowing portal segment line on local monitor edge
                    self.canvas.create_line(plx1, ply1, plx2, ply2, fill=portal_glow, width=8, capstyle=tk.ROUND, tags=(portal_line_tag,))
                    self.canvas.create_line(plx1, ply1, plx2, ply2, fill=portal_core, width=4, capstyle=tk.ROUND, tags=(portal_line_tag,))

                    # Portal midpoint & node circle
                    pmx = (plx1 + plx2) / 2.0
                    pmy = (ply1 + ply2) / 2.0
                    self.canvas.create_oval(pmx - 4, pmy - 4, pmx + 4, pmy + 4, fill=portal_core, outline="#FFFFFF", width=2, tags=(portal_line_tag,))

                    # Clean connector line between portal and peer card
                    self.canvas.create_line(pmx, pmy, c_anchor_x, c_anchor_y, fill=line_col, width=2, tags=(connector_line_tag,))
                else:
                    # Find closest local monitor point to connect for unpaired/pending
                    connector_line_tag = f"peer_line_{pid}"
                    best_mx, best_my, best_px, best_py = offset_x + max(100.0, total_w * scale), offset_y + max(100.0, total_h * scale) / 2, px1, py1 + card_h / 2
                    min_dist = 999999.0
                    for _, mx1, my1, mx2, my2 in local_mon_rects:
                        if px1 >= mx2:
                            d = px1 - mx2
                            if d < min_dist:
                                min_dist = d
                                best_mx, best_my = mx2, (my1 + my2) / 2
                                best_px, best_py = px1, py1 + card_h / 2
                        elif px2 <= mx1:
                            d = mx1 - px2
                            if d < min_dist:
                                min_dist = d
                                best_mx, best_my = mx1, (my1 + my2) / 2
                                best_px, best_py = px2, py1 + card_h / 2
                        elif py1 >= my2:
                            d = py1 - my2
                            if d < min_dist:
                                min_dist = d
                                best_mx, best_my = (mx1 + mx2) / 2, my2
                                best_px, best_py = px1 + card_w / 2, py1
                        elif py2 <= my1:
                            d = my1 - py2
                            if d < min_dist:
                                min_dist = d
                                best_mx, best_my = (mx1 + mx2) / 2, my1
                                best_px, best_py = px1 + card_w / 2, py2
                    self.canvas.create_line(best_mx, best_my, best_px, best_py, fill=line_col, dash=(3, 3), width=2, tags=(connector_line_tag,))

                # Peer Card Box
                card_tag = f"peer_{pid}"
                self.canvas.create_rectangle(px1, py1, px2, py2, fill=card_fill, outline=card_outline, width=outline_w, tags=(card_tag, f"peer_box_{pid}", "peer_card"))

                dev_icon = "🪟" if "win" in p.get("platform", "").lower() else ("📱" if "android" in p.get("platform", "").lower() else "💻")
                dev_name = p.get("deviceName", pid)[:18]
                self.canvas.create_text(px1 + 10, py1 + 14, anchor=tk.W, text=f"{dev_icon} {dev_name}", fill="#FFFFFF", font=("Segoe UI", 10, "bold"), tags=(card_tag,))
                self.canvas.create_text(px1 + 10, py1 + 32, anchor=tk.W, text=status_txt, fill=status_col, font=("Segoe UI", 8, "bold"), tags=(card_tag,))

                if p_monitors and len(p_monitors) > 1:
                    mon_str = " | ".join(f"{m.get('width', 1920)}x{m.get('height', 1080)}" for m in p_monitors[:2])
                    self.canvas.create_text(px1 + 10, py1 + 48, anchor=tk.W, text=f"🖥️ {len(p_monitors)} Ekran: {mon_str}", fill="#CBD5E1", font=("Segoe UI", 8), tags=(card_tag,))
                else:
                    self.canvas.create_text(px1 + 10, py1 + 48, anchor=tk.W, text=f"🖥️ Çözünürlük: {p_w}x{p_h}", fill="#94A3B8", font=("Segoe UI", 8), tags=(card_tag,))

                # Contact point / Edge info badge for paired peers
                if is_mut:
                    edge_names = {EDGE_LEFT: "Sol", EDGE_RIGHT: "Sağ", EDGE_TOP: "Üst", EDGE_BOTTOM: "Alt"}
                    edge_icons = {EDGE_LEFT: "👈", EDGE_RIGHT: "👉", EDGE_TOP: "👆", EDGE_BOTTOM: "👇"}
                    cur_edge = p.get("assignedEdge", EDGE_RIGHT)
                    seg_s = int(float(p.get("edgeOffsetStart", 0.0)) * 100)
                    seg_e = int(float(p.get("edgeOffsetEnd", 1.0)) * 100)
                    att_dev = p.get("attachedToDeviceId")
                    if att_dev and att_dev in self.node.peers:
                        target_label = self.node.peers[att_dev].get("deviceName", att_dev)
                    else:
                        target_label = p.get("attachedMonitorId") or "DISPLAY1"
                    contact_txt = f"📍 {edge_icons.get(cur_edge, '👉')} {edge_names.get(cur_edge, 'Sağ')} (%{seg_s} - %{seg_e}) [{target_label}]"

                    # Subtle badge pill
                    self.canvas.create_rectangle(px1 + 8, py1 + 60, px2 - 8, py1 + 78, fill="#0F172A", outline="#1E293B", width=1, tags=(card_tag,))
                    self.canvas.create_text(px1 + 12, py1 + 69, anchor=tk.W, text=contact_txt, fill="#38BDF8", font=("Segoe UI", 8, "bold"), tags=(card_tag,))

                # Action button inside the card
                if not is_mut:
                    if p.get("in_ok"):
                        acc_x1 = px1 + 8
                        acc_y1 = py2 - 28
                        acc_x2 = px1 + card_w // 2 - 4
                        acc_y2 = py2 - 6
                        acc_tag = f"accept_btn_{pid}"
                        self.canvas.create_rectangle(acc_x1, acc_y1, acc_x2, acc_y2, fill="#16A34A", outline="#4ADE80", width=1, tags=(acc_tag, card_tag))
                        self.canvas.create_text((acc_x1 + acc_x2) / 2, (acc_y1 + acc_y2) / 2, text="✨ Kabul Et", fill="#FFFFFF", font=("Segoe UI", 8, "bold"), tags=(acc_tag, card_tag))
                        self.canvas.tag_bind(acc_tag, "<Button-1>", lambda e, p_id=pid: (self.node.accept_inbound_pairing(p_id, remember=True), "break")[1])
                        self.canvas.tag_bind(acc_tag, "<Enter>", lambda e: self.canvas.config(cursor="hand2"))
                        self.canvas.tag_bind(acc_tag, "<Leave>", lambda e: self.canvas.config(cursor="fleur"))

                        btn_x1 = px1 + card_w // 2 + 4
                        btn_y1 = py2 - 28
                        btn_x2 = px2 - 8
                        btn_y2 = py2 - 6
                        btn_tag = f"pin_btn_{pid}"
                        self.canvas.create_rectangle(btn_x1, btn_y1, btn_x2, btn_y2, fill="#0284C7", outline="#38BDF8", width=1, tags=(btn_tag, card_tag))
                        self.canvas.create_text((btn_x1 + btn_x2) / 2, (btn_y1 + btn_y2) / 2, text="🔐 Kod Gir", fill="#FFFFFF", font=("Segoe UI", 8, "bold"), tags=(btn_tag, card_tag))
                        self.canvas.tag_bind(btn_tag, "<Button-1>", lambda e, peer_obj=p: (self._show_enter_pin_dialog(peer_obj), "break")[1])
                        self.canvas.tag_bind(btn_tag, "<Enter>", lambda e: self.canvas.config(cursor="hand2"))
                        self.canvas.tag_bind(btn_tag, "<Leave>", lambda e: self.canvas.config(cursor="fleur"))
                    else:
                        btn_x1 = px1 + 8
                        btn_y1 = py2 - 28
                        btn_x2 = px2 - 8
                        btn_y2 = py2 - 6
                        btn_tag = f"pin_btn_{pid}"
                        self.canvas.create_rectangle(btn_x1, btn_y1, btn_x2, btn_y2, fill="#0284C7", outline="#38BDF8", width=1, tags=(btn_tag, card_tag))
                        self.canvas.create_text((btn_x1 + btn_x2) / 2, (btn_y1 + btn_y2) / 2, text="🔐 6 Haneli Kodu Gir", fill="#FFFFFF", font=("Segoe UI", 8, "bold"), tags=(btn_tag, card_tag))
                        self.canvas.tag_bind(btn_tag, "<Button-1>", lambda e, peer_obj=p: (self._show_enter_pin_dialog(peer_obj), "break")[1])
                        self.canvas.tag_bind(btn_tag, "<Enter>", lambda e: self.canvas.config(cursor="hand2"))
                        self.canvas.tag_bind(btn_tag, "<Leave>", lambda e: self.canvas.config(cursor="fleur"))
                else:
                    act_btn_x1 = px1 + 8
                    act_btn_y1 = py2 - 28
                    act_btn_x2 = px2 - 8
                    act_btn_y2 = py2 - 6
                    act_tag = f"host_switch_{pid}"
                    act_fill = "#DC2626" if is_controlling else "#16A34A"
                    act_out = "#F87171" if is_controlling else "#4ADE80"
                    act_txt = "🔴 Kontrol Bu Ekranda (Bırak)" if is_controlling else "🎯 Fareyi Bu Cihaza Aktar"
                    self.canvas.create_rectangle(act_btn_x1, act_btn_y1, act_btn_x2, act_btn_y2, fill=act_fill, outline=act_out, width=1, tags=(act_tag, card_tag))
                    self.canvas.create_text((act_btn_x1 + act_btn_x2) / 2, (act_btn_y1 + act_btn_y2) / 2, text=act_txt, fill="#FFFFFF", font=("Segoe UI", 8, "bold"), tags=(act_tag, card_tag))
                    self.canvas.tag_bind(act_tag, "<Enter>", lambda e: self.canvas.config(cursor="hand2"))
                    self.canvas.tag_bind(act_tag, "<Leave>", lambda e: self.canvas.config(cursor="fleur"))
                    if is_controlling:
                        self.canvas.tag_bind(act_tag, "<Button-1>", lambda e: (self._deactivate_host_capture(notify_peer=True), "break")[1])
                    else:
                        self.canvas.tag_bind(act_tag, "<Button-1>", lambda e, peer_obj=p: (self._activate_host_capture(peer_obj, peer_obj.get("assignedEdge", EDGE_RIGHT)), "break")[1])

                # Bind dragging, selecting, and pin dialog
                self.canvas.tag_bind(card_tag, "<ButtonPress-1>", lambda e, p_id=pid: self._on_peer_drag_start(e, p_id))
                self.canvas.tag_bind(card_tag, "<B1-Motion>", self._on_peer_drag_motion)
                self.canvas.tag_bind(card_tag, "<ButtonRelease-1>", self._on_peer_drag_release)
                self.canvas.tag_bind(card_tag, "<Double-Button-1>", lambda e, peer_obj=p: self._show_enter_pin_dialog(peer_obj))
                self.canvas.tag_bind(card_tag, "<Enter>", lambda e: self.canvas.config(cursor="fleur"))
                self.canvas.tag_bind(card_tag, "<Leave>", lambda e: self.canvas.config(cursor=""))
        else:
            empty_x = offset_x + max(100.0, total_w * scale) + 30
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
        """Installs the udev rule and chmods /dev/uinput and /dev/input/event* using Polkit (pkexec) to guarantee hardware input."""
        cmd = (
            'echo \'KERNEL=="uinput", GROUP="input", MODE="0666", OPTIONS+="static_node=uinput"\' > /etc/udev/rules.d/99-connectme-uinput.rules && '
            'echo \'SUBSYSTEM=="input", KERNEL=="event*", GROUP="input", MODE="0666"\' >> /etc/udev/rules.d/99-connectme-uinput.rules && '
            'chmod 666 /dev/uinput /dev/input/event* 2>/dev/null || true && '
            'groupadd -f input && '
            'udevadm control --reload-rules && udevadm trigger && '
            '(which dnf >/dev/null 2>&1 && dnf install -y python3-evdev ydotool wl-clipboard 2>/dev/null || which apt-get >/dev/null 2>&1 && apt-get install -y python3-evdev ydotool wl-clipboard 2>/dev/null || true)'
        )
        try:
            res = subprocess.run(["pkexec", "sh", "-c", cmd], capture_output=True, text=True, timeout=30.0)
            if res.returncode == 0:
                self.node.injector._init_injector()
                if hasattr(self.node, "hardware_grabber"):
                    self.node.hardware_grabber._refresh_and_start_device_threads()
                if self.node.injector.mode != "none" or (hasattr(self.node, "hardware_grabber") and self.node.hardware_grabber.can_grab()):
                    self.uinput_banner.pack_forget()
                    self.injector_badge.configure(text=f" Girdi: {self.node.injector.mode.upper()} ", foreground="#4ADE80")
                    messagebox.showinfo("Başarılı", "✅ Linux donanım sürücüsü ve aygıt izinleri (/dev/uinput, /dev/input/event*) başarıyla etkinleştirildi!\nFare ve klavye donanım seviyesinde 1000Hz hızla sorunsuz çalışacaktır.")
                else:
                    messagebox.showinfo("Başarılı", "✅ Donanım izinleri başarıyla tanımlandı!")
            else:
                messagebox.showerror("Yetkilendirme Başarısız", f"İzin verilemedi veya iptal edildi:\n{res.stderr}")
        except Exception as ex:
            messagebox.showerror("Hata", f"Yetkilendirme penceresi açılamadı: {ex}")

    _fix_input_permissions = _fix_uinput_permissions

    def _on_emergency_pageup_press(self, event=None):
        self._pageup_down = True
        self._check_emergency_reclaim()

    def _on_emergency_pageup_release(self, event=None):
        self._pageup_down = False

    def _on_emergency_pagedown_press(self, event=None):
        self._pagedown_down = True
        self._check_emergency_reclaim()

    def _on_emergency_pagedown_release(self, event=None):
        self._pagedown_down = False

    def _check_emergency_reclaim(self):
        if self._pageup_down and self._pagedown_down:
            self._pageup_down = False
            self._pagedown_down = False
            self._log("[Acil Durum] ⬅️ [PageUp + PageDown] basıldı, fare ve klavye kontrolü anında Linux'a geri alınıyor.")
            if self.host_capture_overlay and self.host_capture_overlay.is_active:
                self.host_capture_overlay.deactivate(notify_peer=True)
            if hasattr(self.node, "hardware_grabber") and self.node.hardware_grabber.is_active:
                self.node.hardware_grabber.deactivate(notify_peer=True)

    def _on_display_topology_updated(self):
        self._refresh_monitors()

    def _periodic_display_check(self):
        try:
            rw = self.root.winfo_screenwidth()
            rh = self.root.winfo_screenheight()
            if hasattr(self, "_last_gui_rw") and (rw != self._last_gui_rw or rh != self._last_gui_rh):
                self._last_gui_rw = rw
                self._last_gui_rh = rh
                self._refresh_monitors()
            else:
                self._last_gui_rw = rw
                self._last_gui_rh = rh
        except Exception:
            pass
        finally:
            if self.root:
                try:
                    self.root.after(2000, self._periodic_display_check)
                except Exception:
                    pass

    def _refresh_monitors(self):
        try:
            rw = self.root.winfo_screenwidth()
            rh = self.root.winfo_screenheight()
        except Exception:
            rw, rh = None, None
        mons = self.node.topology.refresh_monitors(root_w=rw, root_h=rh)
        self._log(f"[Topoloji] Çoklu monitör düzeni güncellendi ({len(mons)} ekran algılandı).")
        self._draw_2d_canvas()
        if hasattr(self, "edge_sentinels"):
            self.edge_sentinels.update_sentinels()

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
        peer_name = peer.get("deviceName", peer.get("ipAddress", "Cihaz"))
        remote_ip = peer.get("ipAddress", "")
        tcp_port = peer.get("tcpControlPort", 42851)
        target_dev_id = peer.get("deviceId", "")

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

        # 1. Hızlı Yol: UDP port 42849 üzerinden doğrudan PAIR_REQUEST gönder (Windows Güvenlik Duvarı TCP engelini aşar)
        try:
            u_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            u_sock.sendto(json.dumps(hdr).encode("utf-8"), (remote_ip, 42849))
            u_sock.close()
            self._log(f"[Hızlı Eşleşme] ⚡ '{peer_name}' ({remote_ip}) için UDP 42849 kanalından eşleşme paketi iletildi.")
        except Exception as u_err:
            self._log(f"[UDP Eşleşme Uyarı] UDP paketi gönderilemedi: {u_err}")

        # 2. Güvenilir Çerçeveli TCP Kanalı (42851)
        tcp_success = False
        tcp_err_msg = ""
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            s.settimeout(5.0)
            s.connect((remote_ip, tcp_port))
            self.node._send_tcp_frame(s, hdr)
            prefix = self.node._recv_exact(s, 12)
            if len(prefix) == 12:
                jlen, _ = struct.unpack("<iq", prefix)
                resp = json.loads(self.node._recv_exact(s, jlen).decode("utf-8"))
                s.close()
                if resp.get("type") == "PAIR_VERIFY_ACK":
                    tcp_success = True
                    peer["out_ok"] = True
                    if resp.get("senderMonitors"):
                        peer["monitors"] = resp.get("senderMonitors")
                    if resp.get("senderName"):
                        peer["deviceName"] = resp.get("senderName")
                    if resp.get("senderId"):
                        peer["deviceId"] = resp.get("senderId")

                    if resp.get("isMutualComplete") or peer.get("in_ok"):
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
                        self.node.on_peer_mutually_paired(peer)
                        self._log(f"✅ '{peer.get('deviceName')}' ile karşılıklı eşleşme tamamlandı! [⭐ Cihaz Hatırlandı]")
                        self.root.after(0, lambda: messagebox.showinfo(
                            "Bağlantı Başarılı",
                            f"✅ '{peer.get('deviceName')}' ile çift taraflı eşleşme sağlandı!\nEkran Kanvası sekmesinden cihazı dilediğiniz kenara taşıyabilirsiniz."
                        ))
                    else:
                        self._log(f"🔔 '{peer.get('deviceName')}' kodunuzu onayladı! Karşı cihazda 'Kabul Et'e basılmalı veya sizin kodunuz ({self.node.local_pin}) girilmeli.")
                        self.root.after(0, lambda: messagebox.showinfo(
                            "Kod Doğrulandı",
                            f"🔔 '{peer.get('deviceName')}' kodunuzu onayladı!\n\nBağlantının tamamlanması için karşı cihaz ekranındaki bildirimden 'Kabul Et'e basın veya sizin 6 haneli kodunuzu ({self.node.local_pin}) girin."
                        ))
                elif resp.get("type") == "PAIR_REJECT":
                    self._log(f"⚠️ Karşı taraf girilen PIN kodunu reddetti.")
                    self.root.after(0, lambda: messagebox.showwarning(
                        "PIN Reddedildi",
                        f"❌ '{peer_name}' girdiğiniz 6 haneli kodu ({pin}) reddetti.\nLütfen karşı cihazın ekranında görünen güncel 6 haneli kodu kontrol edin."
                    ))
                    self.root.after(0, self._refresh_state)
                    return
            else:
                s.close()
        except Exception as e:
            tcp_err_msg = str(e)
            self._log(f"⚠️ TCP eşleşme bağlantı uyarısı: {e}")

        # Eğer TCP zaman aşımına uğramışsa ancak UDP hızlı yoluyla teyit alındıysa:
        time.sleep(0.5)
        latest_peer = self.node.peers.get(target_dev_id, peer)
        if latest_peer.get("out_ok") or latest_peer.get("isMutuallyPaired"):
            self._log(f"⚡ '{peer_name}' UDP hızlı yolu üzerinden başarıyla eşleşti!")
            self.root.after(0, self._refresh_state)
            return

        # Hem TCP başarısız oldu hem de UDP'den yanıt gelmediyse kullanıcıya açıklayıcı teşhis uyarısı göster:
        if not tcp_success:
            self.root.after(0, lambda: messagebox.showwarning(
                "Bağlantı Yanıt Vermedi",
                f"⚠️ '{peer_name}' ({remote_ip}) cihazına eşleşme isteği ulaştırılamadı.\n\n"
                f"Olası Nedenler & Çözümler:\n"
                f"1. Windows Güvenlik Duvarı (Windows Defender Firewall) gelen TCP bağlantılarını engelliyor olabilir.\n"
                f"   Windows bilgisayarda Connect Me'yi v1.8.0 sürümüne güncelleyin veya bir kez yönetici olarak çalıştırın.\n"
                f"2. Karşı cihazda Connect Me programının açık ve ekranda 6 haneli PIN'in görünür olduğunu teyit edin.\n"
                f"3. Her iki cihazın aynı Wi-Fi / yerel ağda olduğundan emin olun.\n\n"
                f"Hata detayı: {tcp_err_msg or 'Karşı taraftan yanıt alınamadı'}"
            ))

        self.root.after(0, self._refresh_state)

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
            remote_tag = None
            tar_url = None
            title = None

            try:
                req = urllib.request.Request(
                    f"https://api.github.com/repos/korgangames/Connect-Me/releases/latest?_={int(time.time())}",
                    headers={"User-Agent": f"ConnectMe-Linux/{VERSION}", "Accept": "application/vnd.github.v3+json"}
                )
                with urllib.request.urlopen(req, timeout=12) as response:
                    if response.status == 200:
                        data = json.loads(response.read().decode("utf-8"))
                        remote_tag = data.get("tag_name", "").strip()
                        title = data.get("name", remote_tag)
                        if remote_tag:
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
                            if fallback_url and not tar_url:
                                tar_url = fallback_url
            except Exception as api_err:
                self._log(f"[Güncelleme] API doğrudan yanıt vermedi ({api_err}), GitHub yönlendirmesi deneniyor...")

            # Fallback: GitHub web 302 redirect (Rate limit aşımı durumunda API yerine çalışır)
            if not remote_tag:
                try:
                    class NoRedirect(urllib.request.HTTPRedirectHandler):
                        def redirect_request(self, req, fp, code, msg, headers, newurl):
                            self.redir = newurl
                            return None
                    nr = NoRedirect()
                    opener = urllib.request.build_opener(nr)
                    try:
                        opener.open(urllib.request.Request("https://github.com/korgangames/Connect-Me/releases/latest", headers={"User-Agent": "Mozilla/5.0"}), timeout=8)
                    except urllib.error.HTTPError as he:
                        target = getattr(nr, "redir", None) or he.headers.get("Location")
                        if target:
                            t = target.rstrip("/").split("/")[-1].strip()
                            if t:
                                remote_tag = t
                                title = f"Connect Me {remote_tag}"
                                tar_url = f"https://github.com/korgangames/Connect-Me/releases/download/{remote_tag}/ConnectMe-Linux-x64.tar.gz"
                except Exception:
                    pass

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
                    if not tar_url:
                        tar_url = f"https://github.com/korgangames/Connect-Me/releases/download/{remote_tag}/ConnectMe-Linux-x64.tar.gz"
                    self.root.after(0, lambda: self._on_new_update_found(remote_tag, tar_url, title or remote_tag))
                    return

            if is_manual:
                self.root.after(0, lambda: messagebox.showinfo("Connect Me Güncel", f"Tebrikler! Connect Me zaten en son sürümde (v{VERSION})."))
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
                script_path = str(Path(__file__).resolve())
                os.execv(sys.executable, [sys.executable, script_path])

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

    def _on_close(self):
        try:
            if hasattr(self, "edge_sentinels"):
                self.edge_sentinels.hide()
        except Exception:
            pass
        try:
            if hasattr(self, "host_capture_overlay") and self.host_capture_overlay.is_active:
                self.host_capture_overlay.deactivate(notify_peer=True)
        except Exception:
            pass
        try:
            self.node.stop()
        except Exception:
            pass
        try:
            self.root.destroy()
        except Exception:
            pass


def main():
    try:
        root = tk.Tk()
        app = ConnectMeLinuxGui(root)
        root.protocol("WM_DELETE_WINDOW", app._on_close)
        root.mainloop()
    except Exception as e:
        import traceback
        err_txt = traceback.format_exc()
        print(err_txt, file=sys.stderr)
        log_dir = Path.home() / ".config" / "connectme"
        log_dir.mkdir(parents=True, exist_ok=True)
        with open(log_dir / "crash.log", "w", encoding="utf-8") as f:
            f.write(err_txt)
        try:
            err_root = tk.Tk()
            err_root.withdraw()
            messagebox.showerror(
                "Connect Me Başlatma Hatası",
                f"Connect Me başlatılırken bir hata oluştu:\n\n{e}\n\nDetaylar: ~/.config/connectme/crash.log"
            )
            err_root.destroy()
        except Exception:
            pass


if __name__ == "__main__":
    main()

