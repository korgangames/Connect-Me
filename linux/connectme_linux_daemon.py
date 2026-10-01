#!/usr/bin/env python3
"""
Connect Me — Nobara Linux (KDE Plasma Wayland / wlroots / X11) Tam Entegre Sistem Servisi & KVM Motoru
Korgan Games (v1.3 Tam Linux Entegrasyonu)

Özellikler:
1. Çoklu Monitör Otomatik Algılama:
   - KDE Plasma Wayland (`kscreen-doctor -j`)
   - wlroots / Sway / Hyprland (`wlr-randr --json`)
   - X11 / XWayland (`xrandr --query`)
2. Akıllı Kenar Topolojisi & İç Birleşim (Internal Seam) Koruması:
   - Linux'un kendi fiziksel ekranları arasında serbest geçiş, dış kenarlarda Windows/Android'e geçiş.
3. Çift Yönlü Girdi Enjeksiyonu (Linux Virtual Input):
   - /dev/uinput (Yerel Linux sanal fare/klavye sürücüsü)
   - ydotool (Nobara / Wayland komut tabanlı enjektör)
   - xdotool (X11 / XWayland geri uyumluluk)
4. Çift Yönlü Evrensel Pano (Universal Clipboard):
   - wl-copy / wl-paste & xclip ile anlık arka plan senkronizasyonu
5. Ortak Cep (Drop Shelf) TCP Dosya Akışı:
   - Windows ve Android ile doğrudan TCP dosya gönderme ve alma
6. Çift Taraflı 6 Haneli PIN & "Bu Cihaza Güven (Sıfır-PIN Otomatik Bağlantı)"
"""

import json
import os
import random
import re
import shutil
import socket
import struct
import subprocess
import threading
import time
from dataclasses import dataclass, asdict
from pathlib import Path
from typing import Callable, Dict, List, Optional, Tuple

MAGIC_0 = 0x43  # 'C'
MAGIC_1 = 0x4D  # 'M'
PROTO_VER = 0x01
VERSION = "1.3.0"

DISCOVERY_UDP_PORT = 42849
FAST_INPUT_UDP_PORT = 42850
DATA_CONTROL_TCP_PORT = 42851

PACKET_MOUSE_MOVE = 0x01
PACKET_MOUSE_BUTTON = 0x02
PACKET_MOUSE_SCROLL = 0x03
PACKET_KEY_EVENT = 0x04
PACKET_EDGE_HANDOFF = 0x05
PACKET_HEARTBEAT_PING = 0x06
PACKET_HEARTBEAT_PONG = 0x07

EDGE_NONE = 0
EDGE_LEFT = 1
EDGE_RIGHT = 2
EDGE_TOP = 3
EDGE_BOTTOM = 4

BUTTON_LEFT = 1
BUTTON_RIGHT = 2
BUTTON_MIDDLE = 3


@dataclass
class PhysicalMonitor:
    monitorId: str
    name: str
    virtualX: int
    virtualY: int
    width: int
    height: int
    scaleFactor: float = 1.0
    isPrimary: bool = False

    @property
    def right(self) -> int:
        return self.virtualX + self.width

    @property
    def bottom(self) -> int:
        return self.virtualY + self.height

    def contains(self, x: int, y: int) -> bool:
        return self.virtualX <= x < self.right and self.virtualY <= y < self.bottom


class LinuxMultiMonitorTopology:
    """Detects and manages all physical monitors on Linux (KDE Plasma Wayland, wlroots, X11)."""

    XRANDR_REGEX = re.compile(
        r"^(?P<name>[\w\-\.]+)\s+connected\s+(?P<primary>primary\s+)?(?P<w>\d+)x(?P<h>\d+)(?P<x>[+\-]\d+)(?P<y>[+\-]\d+)",
        re.MULTILINE,
    )

    def __init__(self) -> None:
        self.monitors: List[PhysicalMonitor] = []
        self.refresh_monitors()

    def refresh_monitors(self) -> List[PhysicalMonitor]:
        detected = (
            self._try_kscreen_doctor()
            or self._try_wlr_randr()
            or self._try_xrandr()
        )
        if not detected:
            detected = [
                PhysicalMonitor(
                    monitorId="DP-1",
                    name="Monitör 1 (DP-1 — Birincil)",
                    virtualX=0,
                    virtualY=0,
                    width=2560,
                    height=1440,
                    scaleFactor=1.0,
                    isPrimary=True,
                )
            ]
        if not any(m.isPrimary for m in detected):
            detected[0].isPrimary = True
        self.monitors = detected
        return self.monitors

    def virtual_desktop_bounds(self) -> Tuple[int, int, int, int]:
        min_x = min(m.virtualX for m in self.monitors)
        min_y = min(m.virtualY for m in self.monitors)
        max_x = max(m.right for m in self.monitors)
        max_y = max(m.bottom for m in self.monitors)
        return min_x, min_y, max(320, max_x - min_x), max(240, max_y - min_y)

    def is_internal_seam(self, mon: PhysicalMonitor, edge: int, x: int, y: int) -> bool:
        """Returns True if stepping 4px across `edge` lands inside another local physical monitor."""
        probe_x, probe_y = x, y
        if edge == EDGE_LEFT:
            probe_x = mon.virtualX - 4
        elif edge == EDGE_RIGHT:
            probe_x = mon.right + 4
        elif edge == EDGE_TOP:
            probe_y = mon.virtualY - 4
        elif edge == EDGE_BOTTOM:
            probe_y = mon.bottom + 4
        else:
            return False

        return any(
            other.monitorId != mon.monitorId and other.contains(probe_x, probe_y)
            for other in self.monitors
        )

    def find_monitor_at(self, x: int, y: int) -> Optional[PhysicalMonitor]:
        for m in self.monitors:
            if m.contains(x, y):
                return m
        return self.monitors[0] if self.monitors else None

    @staticmethod
    def _run_cmd(cmd: List[str]) -> Optional[str]:
        if not shutil.which(cmd[0]):
            return None
        try:
            proc = subprocess.run(cmd, capture_output=True, text=True, timeout=1.5)
            return proc.stdout if proc.returncode == 0 else None
        except Exception:
            return None

    def _try_kscreen_doctor(self) -> List[PhysicalMonitor]:
        out = self._run_cmd(["kscreen-doctor", "-j"])
        if not out:
            return []
        try:
            clean = re.sub(r"\x1B\[[0-9;]*[a-zA-Z]", "", out)
            idx = clean.find("{")
            if idx > 0:
                clean = clean[idx:]
            data = json.loads(clean)
            result: List[PhysicalMonitor] = []
            for i, item in enumerate(data.get("outputs", []), start=1):
                if not item.get("connected", True) or not item.get("enabled", True):
                    continue
                name = item.get("name", f"DP-{i}")
                pos = item.get("pos", {})
                size = item.get("size", {})
                is_prim = bool(item.get("primary", False)) or (item.get("priority") == 1)
                result.append(
                    PhysicalMonitor(
                        monitorId=name,
                        name=f"Monitör {i} ({name})",
                        virtualX=int(pos.get("x", 0)),
                        virtualY=int(pos.get("y", 0)),
                        width=max(320, int(size.get("width", 1920))),
                        height=max(240, int(size.get("height", 1080))),
                        scaleFactor=float(item.get("scale", 1.0)),
                        isPrimary=is_prim,
                    )
                )
            return result
        except Exception:
            return []

    def _try_wlr_randr(self) -> List[PhysicalMonitor]:
        out = self._run_cmd(["wlr-randr", "--json"])
        if not out:
            return []
        try:
            data = json.loads(out)
            result: List[PhysicalMonitor] = []
            for i, item in enumerate(data, start=1):
                if not item.get("enabled", True):
                    continue
                name = item.get("name", f"WL-{i}")
                pos = item.get("position", {})
                w, h = 1920, 1080
                for mode in item.get("modes", []):
                    if mode.get("current"):
                        w = int(mode.get("width", 1920))
                        h = int(mode.get("height", 1080))
                        break
                result.append(
                    PhysicalMonitor(
                        monitorId=name,
                        name=f"Monitör {i} ({name})",
                        virtualX=int(pos.get("x", 0)),
                        virtualY=int(pos.get("y", 0)),
                        width=w,
                        height=h,
                        scaleFactor=float(item.get("scale", 1.0)),
                        isPrimary=(i == 1),
                    )
                )
            return result
        except Exception:
            return []

    def _try_xrandr(self) -> List[PhysicalMonitor]:
        out = self._run_cmd(["xrandr", "--query"])
        if not out:
            return []
        result: List[PhysicalMonitor] = []
        for i, m in enumerate(self.XRANDR_REGEX.finditer(out), start=1):
            name = m.group("name")
            is_prim = bool(m.group("primary"))
            result.append(
                PhysicalMonitor(
                    monitorId=name,
                    name=f"Monitör {i} ({name})",
                    virtualX=int(m.group("x")),
                    virtualY=int(m.group("y")),
                    width=int(m.group("w")),
                    height=int(m.group("h")),
                    scaleFactor=1.0,
                    isPrimary=is_prim,
                )
            )
        return result


class LinuxInputInjector:
    """Hardware input injector supporting ydotool (Wayland/Nobara), uinput, and xdotool (X11)."""

    def __init__(self) -> None:
        self.mode = "none"
        if shutil.which("ydotool"):
            self.mode = "ydotool"
        elif shutil.which("xdotool"):
            self.mode = "xdotool"
        elif os.path.exists("/dev/uinput") and os.access("/dev/uinput", os.W_OK):
            self.mode = "uinput"

    def move_relative(self, dx: int, dy: int) -> None:
        if self.mode == "ydotool":
            subprocess.run(["ydotool", "mousemove", "-x", str(dx), "-y", str(dy)], check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        elif self.mode == "xdotool":
            subprocess.run(["xdotool", "mousemove_relative", "--", str(dx), str(dy)], check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    def mouse_button(self, btn: int, is_pressed: bool) -> None:
        # btn: 1=Left, 2=Right, 3=Middle
        act = "down" if is_pressed else "up"
        if self.mode == "ydotool":
            ydo_code = "0xC0" if btn == BUTTON_LEFT else ("0xC1" if btn == BUTTON_RIGHT else "0xC2")
            subprocess.run(["ydotool", "click", f"{ydo_code}{'d' if is_pressed else 'u'}"], check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        elif self.mode == "xdotool":
            subcmd = "mousedown" if is_pressed else "mouseup"
            subprocess.run(["xdotool", subcmd, str(btn)], check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    def mouse_scroll(self, sx: int, sy: int) -> None:
        if self.mode == "xdotool":
            btn = "4" if sy > 0 else "5"
            clicks = max(1, abs(sy) // 40)
            subprocess.run(["xdotool", "click", "--repeat", str(clicks), btn], check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        elif self.mode == "ydotool":
            # Wheel click
            wheel_arg = f"-w{sy}"
            subprocess.run(["ydotool", "mousemove", wheel_arg], check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    def key_event(self, vk: int, is_pressed: bool, ch: str) -> None:
        if not is_pressed:
            return
        if self.mode in ("xdotool", "ydotool"):
            if vk == 0x08:  # Backspace
                cmd = ["ydotool", "key", "14:1", "14:0"] if self.mode == "ydotool" else ["xdotool", "key", "BackSpace"]
            elif vk == 0x0D:  # Enter
                cmd = ["ydotool", "key", "28:1", "28:0"] if self.mode == "ydotool" else ["xdotool", "key", "Return"]
            elif vk == 0x1B:  # Escape
                cmd = ["ydotool", "key", "1:1", "1:0"] if self.mode == "ydotool" else ["xdotool", "key", "Escape"]
            elif ch and ord(ch) >= 32:
                cmd = ["ydotool", "type", ch] if self.mode == "ydotool" else ["xdotool", "type", ch]
            else:
                return
            subprocess.run(cmd, check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


class ConnectMeLinuxNode:
    """Nobara Linux / KDE Plasma Wayland Full-Featured Node for Connect Me."""

    def __init__(self, on_log: Optional[Callable[[str], None]] = None) -> None:
        self.device_id = f"linux-{socket.gethostname().lower()}"
        self.device_name = f"{socket.gethostname()} (Nobara KDE)"
        self.platform = "linux-nobara"
        self.local_pin = f"{random.randint(100000, 999999)}"
        self.topology = LinuxMultiMonitorTopology()
        self.injector = LinuxInputInjector()

        self.shelf_dir = Path.home() / "Downloads" / "ConnectMe-Shelf"
        self.shelf_dir.mkdir(parents=True, exist_ok=True)
        self.trust_file = Path.home() / ".config" / "connectme" / "trusted_devices.json"
        self.trust_file.parent.mkdir(parents=True, exist_ok=True)
        self.trusted_devices: Dict[str, dict] = self._load_trusted_devices()

        self.peers: Dict[str, dict] = {}
        self.active_peer_addr: Optional[Tuple[str, int]] = None
        self.running = True
        self.on_log = on_log

        # Clipboard tracking
        self.last_copied_clipboard = ""
        self.udp_sock: Optional[socket.socket] = None

    def log(self, text: str) -> None:
        print(text)
        if self.on_log:
            try:
                self.on_log(text)
            except Exception:
                pass

    def _load_trusted_devices(self) -> Dict[str, dict]:
        if not self.trust_file.exists():
            return {}
        try:
            with open(self.trust_file, "r", encoding="utf-8") as f:
                data = json.load(f)
                return {d["deviceId"]: d for d in data if "deviceId" in d}
        except Exception:
            return {}

    def _save_trusted_devices(self) -> None:
        try:
            with open(self.trust_file, "w", encoding="utf-8") as f:
                json.dump(list(self.trusted_devices.values()), f, indent=2)
        except Exception:
            pass

    def revoke_trust(self, device_id: str) -> None:
        if device_id in self.trusted_devices:
            del self.trusted_devices[device_id]
            self._save_trusted_devices()
            if device_id in self.peers:
                self.peers[device_id]["is_trusted"] = False
                self.peers[device_id]["in_ok"] = False
                self.peers[device_id]["out_ok"] = False
            self.log(f"[Güvenlik] 🗑️ '{device_id}' için güvenilirlik kaydı silindi.")

    def start(self) -> None:
        mons = self.topology.monitors
        _, _, vw, vh = self.topology.virtual_desktop_bounds()
        self.log("=" * 72)
        self.log(f" Connect Me v{VERSION} — Nobara Linux (KDE Plasma) Çoklu Monitör & KVM Servisi")
        self.log(f" Cihaz Adı : {self.device_name} ({self.device_id})")
        self.log(f" 6 Haneli Kodu : {self.local_pin[:3]} {self.local_pin[3:]}")
        self.log(f" Algılanan Ekran Sayısı : {len(mons)} (Toplam Alan: {vw}x{vh})")
        self.log(f" Girdi Enjektör Modu : {self.injector.mode.upper()}")
        for m in mons:
            prim_str = " [BİRİNCİL]" if m.isPrimary else ""
            self.log(f"   • {m.monitorId}: {m.width}x{m.height} @ ({m.virtualX}, {m.virtualY}) ölçek={m.scaleFactor}{prim_str}")
        self.log("=" * 72)

        threading.Thread(target=self._discovery_loop, daemon=True).start()
        threading.Thread(target=self._tcp_server_loop, daemon=True).start()
        threading.Thread(target=self._udp_input_loop, daemon=True).start()
        threading.Thread(target=self._clipboard_monitor_loop, daemon=True).start()

    def build_beacon_json(self) -> bytes:
        _, _, vw, vh = self.topology.virtual_desktop_bounds()
        payload = {
            "type": "DISCOVER_BEACON",
            "deviceId": self.device_id,
            "deviceName": self.device_name,
            "platform": self.platform,
            "udpInputPort": FAST_INPUT_UDP_PORT,
            "tcpControlPort": DATA_CONTROL_TCP_PORT,
            "screenWidth": vw,
            "screenHeight": vh,
            "monitors": [asdict(m) for m in self.topology.monitors],
            "timestamp": int(time.time() * 1000),
        }
        return json.dumps(payload).encode("utf-8")

    def _discovery_loop(self) -> None:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            sock.bind(("0.0.0.0", DISCOVERY_UDP_PORT))
        except OSError:
            pass

        while self.running:
            try:
                sock.sendto(self.build_beacon_json(), ("255.255.255.255", DISCOVERY_UDP_PORT))
            except OSError:
                pass
            time.sleep(3.0)

    def _tcp_server_loop(self) -> None:
        srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        srv.bind(("0.0.0.0", DATA_CONTROL_TCP_PORT))
        srv.listen(8)
        while self.running:
            conn, addr = srv.accept()
            threading.Thread(target=self._handle_tcp_client, args=(conn, addr[0]), daemon=True).start()

    def _handle_tcp_client(self, conn: socket.socket, remote_ip: str) -> None:
        with conn:
            prefix = self._recv_exact(conn, 12)
            if len(prefix) < 12:
                return
            json_len, bin_len = struct.unpack("<iq", prefix)
            header_bytes = self._recv_exact(conn, json_len)
            header = json.loads(header_bytes.decode("utf-8"))
            msg_type = header.get("type", "")

            if msg_type == "PAIR_REQUEST":
                target_pin = str(header.get("targetPin", "")).strip()
                sender_id = header.get("senderId", remote_ip)
                sender_name = header.get("senderName", remote_ip)
                peer = self.peers.setdefault(sender_id, {
                    "deviceId": sender_id,
                    "deviceName": sender_name,
                    "ipAddress": remote_ip,
                    "udpInputPort": header.get("senderUdpPort", FAST_INPUT_UDP_PORT),
                    "tcpControlPort": header.get("senderTcpPort", DATA_CONTROL_TCP_PORT),
                    "out_ok": False
                })
                peer["monitors"] = header.get("senderMonitors", [])

                if target_pin == self.local_pin:
                    peer["in_ok"] = True
                    req_trust = bool(header.get("requestTrust", False))
                    trust_token = header.get("trustToken", "")
                    if req_trust and trust_token:
                        self.trusted_devices[sender_id] = {
                            "deviceId": sender_id,
                            "deviceName": sender_name,
                            "platform": header.get("senderPlatform", "windows"),
                            "trustToken": trust_token,
                            "autoConnect": True
                        }
                        self._save_trusted_devices()
                        peer["is_trusted"] = True
                        self.log(f"[Güvenlik] ⭐ '{sender_name}' güvenilir cihaz olarak kaydedildi.")

                    is_mut = bool(peer.get("out_ok", False))
                    peer["isMutuallyPaired"] = is_mut
                    ack = {
                        "type": "PAIR_VERIFY_ACK",
                        "senderId": self.device_id,
                        "senderName": self.device_name,
                        "senderMonitors": [asdict(m) for m in self.topology.monitors],
                        "trustToken": trust_token if req_trust else None,
                        "isMutualComplete": is_mut,
                    }
                    self._send_tcp_frame(conn, ack)
                    self.log(f"[PIN Onayı] 🔔 '{sender_name}' bizim 6 haneli kodumuzu doğruladı!")
                else:
                    self._send_tcp_frame(conn, {"type": "PAIR_REJECT", "senderId": self.device_id, "senderName": self.device_name})
                    self.log(f"[Güvenlik] ⚠️ '{sender_name}' hatalı kod denedi ({target_pin}).")

            elif msg_type == "TRUSTED_RECONNECT":
                sender_id = header.get("senderId", remote_ip)
                sender_name = header.get("senderName", remote_ip)
                token = header.get("trustToken", "")
                saved_rec = self.trusted_devices.get(sender_id)
                if saved_rec and saved_rec.get("trustToken") == token and saved_rec.get("autoConnect", True):
                    peer = self.peers.setdefault(sender_id, {
                        "deviceId": sender_id,
                        "deviceName": sender_name,
                        "ipAddress": remote_ip,
                        "udpInputPort": header.get("senderUdpPort", FAST_INPUT_UDP_PORT),
                        "tcpControlPort": header.get("senderTcpPort", DATA_CONTROL_TCP_PORT),
                    })
                    peer["in_ok"] = True
                    peer["out_ok"] = True
                    peer["is_trusted"] = True
                    peer["isMutuallyPaired"] = True
                    peer["monitors"] = header.get("senderMonitors", [])
                    ack = {
                        "type": "TRUSTED_RECONNECT_ACK",
                        "senderId": self.device_id,
                        "senderName": self.device_name,
                        "senderMonitors": [asdict(m) for m in self.topology.monitors],
                        "trustToken": token,
                        "isMutualComplete": True,
                    }
                    self._send_tcp_frame(conn, ack)
                    self.log(f"[Otomatik Bağlantı] ⭐ Güvenilir cihaz '{sender_name}' ({remote_ip}) PIN'siz otomatik bağlandı!")
                else:
                    self._send_tcp_frame(conn, {"type": "PAIR_REJECT", "senderId": self.device_id, "senderName": self.device_name})

            elif msg_type == "CLIPBOARD_TEXT":
                text = header.get("text", "")
                if text:
                    self.last_copied_clipboard = text
                    if shutil.which("wl-copy"):
                        subprocess.run(["wl-copy"], input=text.encode("utf-8"), check=False)
                    elif shutil.which("xclip"):
                        subprocess.run(["xclip", "-selection", "clipboard"], input=text.encode("utf-8"), check=False)
                    self.log(f"[Evrensel Pano] '{header.get('senderName')}' cihazından metin kopyalandı ({len(text)} krk).")

            elif msg_type == "SHELF_FILE" and bin_len >= 0:
                fname = os.path.basename(header.get("fileName", "dosya.bin"))
                dest = self.shelf_dir / fname
                remaining = bin_len
                with open(dest, "wb") as f:
                    while remaining > 0:
                        chunk = conn.recv(min(65536, remaining))
                        if not chunk:
                            break
                        f.write(chunk)
                        remaining -= len(chunk)
                self.log(f"[Ortak Cep] 📥 '{fname}' ({bin_len} B) -> {dest} kaydedildi!")
                if shutil.which("notify-send"):
                    subprocess.run(["notify-send", "Connect Me: Ortak Cep", f"Yeni dosya alındı:\n{fname}"], check=False)

    def _udp_input_loop(self) -> None:
        self.udp_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.udp_sock.bind(("0.0.0.0", FAST_INPUT_UDP_PORT))
        while self.running:
            data, addr = self.udp_sock.recvfrom(2048)
            if len(data) < 4 or data[0] != MAGIC_0 or data[1] != MAGIC_1 or data[2] != PROTO_VER:
                continue

            pkt_type = data[3]
            if pkt_type == PACKET_HEARTBEAT_PING and len(data) >= 12:
                pong = bytearray(data[:12])
                pong[3] = PACKET_HEARTBEAT_PONG
                self.udp_sock.sendto(bytes(pong), addr)

            elif pkt_type == PACKET_MOUSE_MOVE and len(data) >= 10:
                _, _, dx, dy = struct.unpack("<Hhh", data[4:10])
                self.injector.move_relative(dx, dy)

            elif pkt_type == PACKET_MOUSE_BUTTON and len(data) >= 6:
                btn, pressed = struct.unpack("<BB", data[4:6])
                self.injector.mouse_button(btn, pressed != 0)

            elif pkt_type == PACKET_MOUSE_SCROLL and len(data) >= 8:
                sx, sy = struct.unpack("<hh", data[4:8])
                self.injector.mouse_scroll(sx, sy)

            elif pkt_type == PACKET_KEY_EVENT and len(data) >= 12:
                vk, sc, pressed, mods, char_code = struct.unpack("<HHBBH", data[4:12])
                ch = chr(char_code) if char_code > 0 else ""
                self.injector.key_event(vk, pressed != 0, ch)

            elif pkt_type == PACKET_EDGE_HANDOFF and len(data) >= 10:
                edge, dragging, norm_pos = struct.unpack("<BBf", data[4:10])
                self.log(f"[Kenar Geçişi] İmleç Linux ekranına giriş yaptı (Kenar: {edge}, Konum: %{int(norm_pos * 100)}).")

    def _clipboard_monitor_loop(self) -> None:
        """Polls local clipboard and broadcasts newly copied text to paired devices."""
        while self.running:
            time.sleep(0.6)
            current_text = ""
            if shutil.which("wl-paste"):
                try:
                    res = subprocess.run(["wl-paste", "-n"], capture_output=True, text=True, timeout=0.5)
                    if res.returncode == 0:
                        current_text = res.stdout
                except Exception:
                    pass
            elif shutil.which("xclip"):
                try:
                    res = subprocess.run(["xclip", "-selection", "clipboard", "-o"], capture_output=True, text=True, timeout=0.5)
                    if res.returncode == 0:
                        current_text = res.stdout
                except Exception:
                    pass

            if current_text and current_text != self.last_copied_clipboard and len(current_text) <= 500000:
                self.last_copied_clipboard = current_text
                self.broadcast_clipboard(current_text)

    def broadcast_clipboard(self, text: str) -> None:
        paired = [p for p in self.peers.values() if p.get("isMutuallyPaired")]
        if not paired:
            return
        hdr = {
            "type": "CLIPBOARD_TEXT",
            "senderId": self.device_id,
            "senderName": self.device_name,
            "text": text,
        }
        for p in paired:
            try:
                s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
                s.settimeout(2.0)
                s.connect((p["ipAddress"], p.get("tcpControlPort", DATA_CONTROL_TCP_PORT)))
                self._send_tcp_frame(s, hdr)
                s.close()
            except Exception:
                pass
        self.log(f"[Evrensel Pano] 📋 Yerel metin ({len(text)} krk) bağlı cihazlara iletildi.")

    def send_file_to_peer(self, file_path: Path, peer_id: Optional[str] = None) -> bool:
        """Streams a local file to paired peer(s) over framed TCP."""
        if not file_path.exists():
            return False

        targets = [p for p in self.peers.values() if p.get("isMutuallyPaired")]
        if peer_id and peer_id in self.peers:
            targets = [self.peers[peer_id]]

        if not targets:
            self.log("[Uyarı] Dosya göndermek için önce en az bir cihazla PIN doğrulamasını tamamlayın.")
            return False

        file_size = file_path.stat().st_size
        hdr = {
            "type": "SHELF_FILE",
            "senderId": self.device_id,
            "senderName": self.device_name,
            "fileName": file_path.name,
            "fileSize": file_size,
        }

        success = False
        for peer in targets:
            try:
                s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
                s.settimeout(10.0)
                s.connect((peer["ipAddress"], peer.get("tcpControlPort", DATA_CONTROL_TCP_PORT)))
                jb = json.dumps(hdr).encode("utf-8")
                prefix = struct.pack("<iq", len(jb), file_size)
                s.sendall(prefix + jb)

                with open(file_path, "rb") as f:
                    while True:
                        buf = f.read(65536)
                        if not buf:
                            break
                        s.sendall(buf)
                s.close()
                self.log(f"[Drop Shelf] 📤 '{file_path.name}' -> '{peer.get('deviceName')}' Ortak Cebine gönderildi!")
                success = True
            except Exception as e:
                self.log(f"[Drop Shelf Hata] Dosya gönderilemedi ({peer.get('deviceName')}): {e}")

        return success

    @staticmethod
    def _send_tcp_frame(conn: socket.socket, header: dict, payload: bytes = b"") -> None:
        jb = json.dumps(header).encode("utf-8")
        prefix = struct.pack("<iq", len(jb), len(payload))
        conn.sendall(prefix + jb + payload)

    @staticmethod
    def _recv_exact(conn: socket.socket, length: int) -> bytes:
        buf = bytearray()
        while len(buf) < length:
            chunk = conn.recv(length - len(buf))
            if not chunk:
                break
            buf.extend(chunk)
        return bytes(buf)


if __name__ == "__main__":
    node = ConnectMeLinuxNode()
    node.start()
    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        print("\n[Sistem] Connect Me Linux servisi kapatıldı.")
