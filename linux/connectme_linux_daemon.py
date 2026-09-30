#!/usr/bin/env python3
"""
Connect Me — Nobara Linux (KDE Plasma Wayland / wlroots / X11) Multi-Monitor Daemon
Korgan Games (v1.3 Multi-Monitor Futureproof Edition)

Features:
1. Multi-Monitor Auto-Detection on Linux:
   - KDE Plasma Wayland / X11 via `kscreen-doctor -j`
   - wlroots / Sway / Hyprland via `wlr-randr --json`
   - X11 / XWayland fallback via `xrandr --query`
2. Internal Seam vs. Outer Edge Geometry:
   - Cursor moves freely across local physical monitors (e.g. DP-1 <-> HDMI-A-1)
   - Only triggers EdgeHandOff when pushing against an exposed outer boundary!
3. Mutual 6-Digit PIN Handshake, TOPOLOGY_SYNC, Wayland Clipboard (`wl-copy`/`wl-paste`),
   and Drop Shelf (`~/Downloads/ConnectMe-Shelf`).
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
from typing import Dict, List, Optional, Tuple

MAGIC_0 = 0x43  # 'C'
MAGIC_1 = 0x4D  # 'M'
PROTO_VER = 0x01

DISCOVERY_UDP_PORT = 42849
FAST_INPUT_UDP_PORT = 42850
DATA_CONTROL_TCP_PORT = 42851


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
        if edge == 1:  # Left
            probe_x = mon.virtualX - 4
        elif edge == 2:  # Right
            probe_x = mon.right + 4
        elif edge == 3:  # Top
            probe_y = mon.virtualY - 4
        elif edge == 4:  # Bottom
            probe_y = mon.bottom + 4
        else:
            return False

        return any(
            other.monitorId != mon.monitorId and other.contains(probe_x, probe_y)
            for other in self.monitors
        )

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


class ConnectMeLinuxNode:
    """Nobara Linux / KDE Plasma Wayland Multi-Monitor Daemon for Connect Me."""

    def __init__(self) -> None:
        self.device_id = f"linux-{socket.gethostname().lower()}"
        self.device_name = f"{socket.gethostname()} (Nobara KDE)"
        self.platform = "linux-nobara"
        self.local_pin = f"{random.randint(100000, 999999)}"
        self.topology = LinuxMultiMonitorTopology()
        self.shelf_dir = Path.home() / "Downloads" / "ConnectMe-Shelf"
        self.shelf_dir.mkdir(parents=True, exist_ok=True)
        self.trust_file = Path.home() / ".config" / "connectme" / "trusted_devices.json"
        self.trust_file.parent.mkdir(parents=True, exist_ok=True)
        self.trusted_devices: Dict[str, dict] = self._load_trusted_devices()
        self.peers: Dict[str, dict] = {}
        self.running = True

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

    def start(self) -> None:
        mons = self.topology.monitors
        _, _, vw, vh = self.topology.virtual_desktop_bounds()
        print("=" * 72)
        print(f" Connect Me — Nobara Linux (KDE Plasma) Çoklu Monitör Servisi")
        print(f" Cihaz Adı : {self.device_name} ({self.device_id})")
        print(f" 6 Haneli Kodu : {self.local_pin[:3]} {self.local_pin[3:]}")
        print(f" Algılanan Monitör Sayısı : {len(mons)} (Toplam Masaüstü: {vw}x{vh})")
        print(f" Kayıtlı Güvenilir Cihaz : {len(self.trusted_devices)}")
        for m in mons:
            prim_str = " [BİRİNCİL]" if m.isPrimary else ""
            print(f"   • {m.monitorId}: {m.width}x{m.height} @ ({m.virtualX}, {m.virtualY}) ölçek={m.scaleFactor}{prim_str}")
        print("=" * 72)

        threading.Thread(target=self._discovery_loop, daemon=True).start()
        threading.Thread(target=self._tcp_server_loop, daemon=True).start()
        threading.Thread(target=self._udp_input_loop, daemon=True).start()

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
                if target_pin == self.local_pin:
                    peer = self.peers.setdefault(sender_id, {"name": sender_name, "ip": remote_ip, "out_ok": False})
                    peer["in_ok"] = True
                    peer["monitors"] = header.get("senderMonitors", [])
                    
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
                        print(f"[Güvenlik] ⭐ '{sender_name}' güvenilir cihaz olarak kaydedildi.")

                    ack = {
                        "type": "PAIR_VERIFY_ACK",
                        "senderId": self.device_id,
                        "senderName": self.device_name,
                        "senderMonitors": [asdict(m) for m in self.topology.monitors],
                        "trustToken": trust_token if req_trust else None,
                        "isMutualComplete": bool(peer.get("out_ok", False)),
                    }
                    self._send_tcp_frame(conn, ack)
                    print(f"[PIN Doğrulama] '{sender_name}' ({remote_ip}) yerel kodumuzu doğruladı!")
                else:
                    self._send_tcp_frame(conn, {"type": "PAIR_REJECT", "senderId": self.device_id, "senderName": self.device_name})

            elif msg_type == "TRUSTED_RECONNECT":
                sender_id = header.get("senderId", remote_ip)
                sender_name = header.get("senderName", remote_ip)
                token = header.get("trustToken", "")
                saved_rec = self.trusted_devices.get(sender_id)
                if saved_rec and saved_rec.get("trustToken") == token and saved_rec.get("autoConnect", True):
                    peer = self.peers.setdefault(sender_id, {"name": sender_name, "ip": remote_ip})
                    peer["in_ok"] = True
                    peer["out_ok"] = True
                    peer["is_trusted"] = True
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
                    print(f"[Otomatik Bağlantı] ⭐ Güvenilir cihaz '{sender_name}' ({remote_ip}) PIN'siz otomatik bağlandı!")
                else:
                    self._send_tcp_frame(conn, {"type": "PAIR_REJECT", "senderId": self.device_id, "senderName": self.device_name})

            elif msg_type == "TOPOLOGY_SYNC":
                sender_name = header.get("senderName", remote_ip)
                mons = header.get("senderMonitors", [])
                print(f"[Çoklu Monitör Senk] '{sender_name}' {len(mons)} ekranlı topolojisini güncelledi.")

            elif msg_type == "CLIPBOARD_TEXT":
                text = header.get("text", "")
                if text and shutil.which("wl-copy"):
                    subprocess.run(["wl-copy"], input=text.encode("utf-8"), check=False)
                print(f"[Evrensel Pano] '{header.get('senderName')}' cihazından metin alındı ({len(text)} krk).")

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
                print(f"[Ortak Cep] '{fname}' ({bin_len} B) -> {dest} kaydedildi.")

    def _udp_input_loop(self) -> None:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.bind(("0.0.0.0", FAST_INPUT_UDP_PORT))
        while self.running:
            data, addr = sock.recvfrom(2048)
            if len(data) < 4 or data[0] != MAGIC_0 or data[1] != MAGIC_1 or data[2] != PROTO_VER:
                continue
            pkt_type = data[3]
            if pkt_type == 0x06 and len(data) >= 12:  # HeartbeatPing -> HeartbeatPong
                pong = bytearray(data[:12])
                pong[3] = 0x07
                sock.sendto(bytes(pong), addr)

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
