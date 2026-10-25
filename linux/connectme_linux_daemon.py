#!/usr/bin/env python3
"""
Connect Me — Nobara Linux (KDE Plasma Wayland / wlroots / X11) Tam Entegre Sistem Servisi & KVM Motoru
Korgan Games (v1.6.6 Kalıcı Cihaz Kimlikleri, Çoklu Cihaz PIN & Otomatik Reconnect)

Özellikler:
1. Çoklu Monitör Otomatik Algılama & 2D Topoloji:
   - KDE Plasma Wayland (`kscreen-doctor -j`, `kscreen-doctor -o`, `kscreen-console json`)
   - wlroots / Sway / Hyprland (`wlr-randr --json`)
   - X11 / XWayland (`xrandr --query`)
2. Çift Yönlü UDP Keşif & Doğrudan Manuel IP Bağlantısı:
   - UDP 42849 portunda tüm Windows, Android ve Linux cihazlarını canlı algılama
   - Güvenlik duvarı arkasındaki cihazlara doğrudan unicast keşif ve tek tıkla IP bağlantısı
3. 4 Kademeli Yüksek Performanslı Linux Girdi Enjeksiyonu (Linux Virtual Input):
   - Tier 1: evdev.UInput (Kernel seviyesinde sanal USB fare/klavye, <0.2ms gecikme, KDE Wayland doğrudan tanır)
   - Tier 2: /dev/uinput raw fcntl ioctl (Harici kütüphane gerektirmeyen çekirdek sürücüsü)
   - Tier 3: ydotoold otomatik başlatma ve soket üzerinden kontrol
   - Tier 4: xdotool (X11 / XWayland geri uyumluluk)
4. Kesintisiz Çift Yönlü Kenar Geçişi (Windows <-> Nobara Linux):
   - İmleç Linux ekranına geçtiğinde sanal koordinat takibi
   - İmleç Linux'un sol/dış kenarına çarptığında TCP/UDP EDGE_RETURN ile Windows'a anında dönüş
5. Güvenli Çift Taraflı 6 Haneli PIN & Gerçek Karşılıklı Eşleşme:
   - İki taraf da birbirinin kodunu girmeden bağlantı kurulmaz, güvenilirlik erken kaydedilmez
6. Çift Yönlü Evrensel Pano & Ortak Cep (Drop Shelf)
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
from typing import Callable, Dict, List, Optional, Set, Tuple

MAGIC_0 = 0x43  # 'C'
MAGIC_1 = 0x4D  # 'M'
PROTO_VER = 0x01

def _resolve_app_version() -> str:
    for candidate in [Path(__file__).resolve().parent / "VERSION", Path(__file__).resolve().parent.parent / "VERSION"]:
        if candidate.exists():
            try:
                v = candidate.read_text(encoding="utf-8").strip()
                if v:
                    return v
            except Exception:
                pass
    return "1.8.16"

VERSION = _resolve_app_version()

DISCOVERY_UDP_PORT = 42849
FAST_INPUT_UDP_PORT = 42850
DATA_CONTROL_TCP_PORT = 42851
AUDIO_STREAM_UDP_PORT = 42852

PACKET_MOUSE_MOVE = 0x01
PACKET_MOUSE_BUTTON = 0x02
PACKET_MOUSE_SCROLL = 0x03
PACKET_KEY_EVENT = 0x04
PACKET_EDGE_HANDOFF = 0x05
PACKET_HEARTBEAT_PING = 0x06
PACKET_HEARTBEAT_PONG = 0x07
PACKET_AUDIO_CHUNK = 0x08

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

    def refresh_monitors(self, root_w: Optional[int] = None, root_h: Optional[int] = None) -> List[PhysicalMonitor]:
        detected = (
            self._try_kscreen_doctor()
            or self._try_kscreen_console()
            or self._try_kscreen_doctor_text()
            or self._try_wlr_randr()
            or self._try_gnome_mutter()
            or self._try_drm_sysfs()
            or self._try_xrandr()
        )
        if not detected:
            def_w = max(1920, root_w or 1920)
            def_h = max(1080, root_h or 1080)
            detected = [
                PhysicalMonitor(
                    monitorId="DP-1",
                    name="Monitör 1 (DP-1 — Birincil)",
                    virtualX=0,
                    virtualY=0,
                    width=def_w,
                    height=def_h,
                    scaleFactor=1.0,
                    isPrimary=True,
                )
            ]
        elif root_w and root_h and len(detected) == 1 and root_w > detected[0].width:
            m1 = detected[0]
            rem_w = root_w - m1.width
            detected.append(PhysicalMonitor(
                monitorId="DISP-2",
                name="Monitör 2 (Genişletilmiş)",
                virtualX=m1.right,
                virtualY=m1.virtualY,
                width=rem_w,
                height=root_h,
                scaleFactor=1.0,
                isPrimary=False,
            ))
        if not any(m.isPrimary for m in detected):
            detected[0].isPrimary = True
        # Sort monitors deterministically by X then Y
        detected.sort(key=lambda m: (m.virtualX, m.virtualY))
        self.monitors = detected
        return self.monitors

    def ensure_bounds(self, cur_x: int, cur_y: int) -> None:
        """Dynamically expands known monitor dimensions if cursor travels beyond current bounds."""
        if not self.monitors:
            return
        max_r = max(m.right for m in self.monitors)
        max_b = max(m.bottom for m in self.monitors)
        if cur_x >= max_r:
            rightmost = max(self.monitors, key=lambda m: m.right)
            rightmost.width += (cur_x - max_r + 1)
        if cur_y >= max_b:
            bottommost = max(self.monitors, key=lambda m: m.bottom)
            bottommost.height += (cur_y - max_b + 1)

    def virtual_desktop_bounds(self) -> Tuple[int, int, int, int]:
        min_x = min(m.virtualX for m in self.monitors)
        min_y = min(m.virtualY for m in self.monitors)
        max_x = max(m.right for m in self.monitors)
        max_y = max(m.bottom for m in self.monitors)
        return min_x, min_y, max(320, max_x - min_x), max(240, max_y - min_y)

    @property
    def total_width(self) -> int:
        _, _, vw, _ = self.virtual_desktop_bounds()
        return vw

    @property
    def total_height(self) -> int:
        _, _, _, vh = self.virtual_desktop_bounds()
        return vh

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
        if not self.monitors:
            return None
        def dist_sq(m: PhysicalMonitor) -> float:
            dx = max(0, max(m.virtualX - x, x - (m.right - 1)))
            dy = max(0, max(m.virtualY - y, y - (m.bottom - 1)))
            return dx * dx + dy * dy
        return min(self.monitors, key=dist_sq)

    def get_outermost_monitor_for_edge(self, edge: int) -> Optional[PhysicalMonitor]:
        if not self.monitors:
            return None
        if edge == EDGE_LEFT:
            return min(self.monitors, key=lambda m: m.virtualX)
        elif edge == EDGE_RIGHT:
            return max(self.monitors, key=lambda m: m.right)
        elif edge == EDGE_TOP:
            return min(self.monitors, key=lambda m: m.virtualY)
        elif edge == EDGE_BOTTOM:
            return max(self.monitors, key=lambda m: m.bottom)
        return self.monitors[0]

    @staticmethod
    def _run_cmd(cmd: List[str]) -> Optional[str]:
        if not shutil.which(cmd[0]):
            return None
        try:
            proc = subprocess.run(cmd, capture_output=True, text=True, timeout=2.0)
            return proc.stdout if proc.returncode == 0 else None
        except Exception:
            return None

    def _parse_kscreen_json(self, raw_json: str) -> List[PhysicalMonitor]:
        clean = re.sub(r"\x1B\[[0-9;]*[a-zA-Z]", "", raw_json)
        idx = clean.find("{")
        if idx >= 0:
            clean = clean[idx:]
        data = json.loads(clean)
        result: List[PhysicalMonitor] = []
        outputs = data.get("outputs", [])
        if isinstance(outputs, dict):
            outputs = list(outputs.values())

        for i, item in enumerate(outputs, start=1):
            if not item.get("connected", True) or not item.get("enabled", True):
                continue
            name = str(item.get("name") or f"DP-{i}")
            scale = float(item.get("scale", 1.0))
            if scale <= 0.05:
                scale = 1.0
            is_prim = bool(item.get("primary", False)) or (item.get("priority") == 1)

            # 1. Parse position (virtualX, virtualY)
            pos = item.get("pos") or item.get("position") or {}
            if isinstance(pos, dict):
                vx = int(pos.get("x", 0))
                vy = int(pos.get("y", 0))
            elif isinstance(pos, (list, tuple)) and len(pos) >= 2:
                vx = int(pos[0])
                vy = int(pos[1])
            else:
                vx, vy = 0, 0

            # 2. Parse resolution (width and height) via currentModeId or modes
            raw_w, raw_h = 0, 0
            cur_mode_id = str(item.get("currentModeId") or "")
            modes = item.get("modes", [])
            if isinstance(modes, dict):
                modes = list(modes.values())

            # Find matching mode by ID
            if cur_mode_id and isinstance(modes, list):
                for m in modes:
                    if str(m.get("id", "")) == cur_mode_id:
                        sz = m.get("size", {})
                        if isinstance(sz, dict):
                            raw_w = int(sz.get("width", 0))
                            raw_h = int(sz.get("height", 0))
                        break

            # If not matched, look for mode where current is True
            if (raw_w <= 0 or raw_h <= 0) and isinstance(modes, list):
                for m in modes:
                    if m.get("current") or m.get("active"):
                        sz = m.get("size", {})
                        if isinstance(sz, dict):
                            raw_w = int(sz.get("width", 0))
                            raw_h = int(sz.get("height", 0))
                        break

            # Fallback to currentMode dict if present
            if raw_w <= 0 or raw_h <= 0:
                cm = item.get("currentMode")
                if isinstance(cm, dict):
                    sz = cm.get("size", {})
                    if isinstance(sz, dict):
                        raw_w = int(sz.get("width", 0))
                        raw_h = int(sz.get("height", 0))

            # Fallback to direct size dict
            if raw_w <= 0 or raw_h <= 0:
                sz = item.get("size", {})
                if isinstance(sz, dict):
                    raw_w = int(sz.get("width", 0))
                    raw_h = int(sz.get("height", 0))

            # Fallback to first available mode
            if (raw_w <= 0 or raw_h <= 0) and isinstance(modes, list) and modes:
                sz = modes[0].get("size", {})
                if isinstance(sz, dict):
                    raw_w = int(sz.get("width", 1920))
                    raw_h = int(sz.get("height", 1080))

            if raw_w <= 0:
                raw_w = 1920
            if raw_h <= 0:
                raw_h = 1080

            # Rotation (2=90, 4=270 deg)
            rot = item.get("rotation", 1)
            if rot in (2, 4, 90, 270):
                raw_w, raw_h = raw_h, raw_w

            result.append(
                PhysicalMonitor(
                    monitorId=name,
                    name=f"Monitör {i} ({name})",
                    virtualX=vx,
                    virtualY=vy,
                    width=raw_w,
                    height=raw_h,
                    scaleFactor=scale,
                    isPrimary=is_prim,
                )
            )
        return result

    def _try_kscreen_doctor(self) -> List[PhysicalMonitor]:
        out = self._run_cmd(["kscreen-doctor", "-j"])
        if not out:
            return []
        try:
            return self._parse_kscreen_json(out)
        except Exception:
            return []

    def _try_kscreen_console(self) -> List[PhysicalMonitor]:
        out = self._run_cmd(["kscreen-console", "json"])
        if not out:
            return []
        try:
            return self._parse_kscreen_json(out)
        except Exception:
            return []

    def _try_kscreen_doctor_text(self) -> List[PhysicalMonitor]:
        out = self._run_cmd(["kscreen-doctor", "-o"])
        if not out:
            return []
        try:
            result: List[PhysicalMonitor] = []
            lines = out.splitlines()
            current_name = ""
            current_prim = False
            current_vx, current_vy = 0, 0
            current_w, current_h = 0, 0
            current_scale = 1.0
            idx = 1

            for line in lines:
                line_str = line.strip()
                if line_str.startswith("Output:"):
                    if current_name and (current_w > 0 or current_h > 0):
                        result.append(PhysicalMonitor(
                            monitorId=current_name,
                            name=f"Monitör {idx} ({current_name})",
                            virtualX=current_vx,
                            virtualY=current_vy,
                            width=current_w or 1920,
                            height=current_h or 1080,
                            scaleFactor=current_scale,
                            isPrimary=current_prim,
                        ))
                        idx += 1
                    current_name = ""
                    current_prim = False
                    current_vx, current_vy = 0, 0
                    current_w, current_h = 0, 0
                    current_scale = 1.0

                    parts = line_str.split()
                    if len(parts) >= 3:
                        current_name = parts[2]
                    current_prim = "primary" in line_str
                    if "disabled" in line_str:
                        current_name = ""
                elif current_name:
                    geom_m = re.search(r"(\d+),(\d+)\s+(\d+)x(\d+)", line_str)
                    if geom_m:
                        current_vx = int(geom_m.group(1))
                        current_vy = int(geom_m.group(2))
                        current_w = int(geom_m.group(3))
                        current_h = int(geom_m.group(4))
                    mode_m = re.search(r"(\d+)x(\d+)@[\d\.]+\*", line_str)
                    if mode_m and current_w == 0:
                        current_w = int(mode_m.group(1))
                        current_h = int(mode_m.group(2))
                    scale_m = re.search(r"scale\s*[:=]?\s*([\d\.]+)", line_str, re.IGNORECASE)
                    if scale_m:
                        current_scale = float(scale_m.group(1))

            if current_name and (current_w > 0 or current_h > 0):
                result.append(PhysicalMonitor(
                    monitorId=current_name,
                    name=f"Monitör {idx} ({current_name})",
                    virtualX=current_vx,
                    virtualY=current_vy,
                    width=current_w or 1920,
                    height=current_h or 1080,
                    scaleFactor=current_scale,
                    isPrimary=current_prim,
                ))
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

    def _try_gnome_mutter(self) -> List[PhysicalMonitor]:
        out = self._run_cmd(["gdbus", "call", "--session", "--dest", "org.gnome.Mutter.DisplayConfig",
                             "--object-path", "/org/gnome/Mutter/DisplayConfig",
                             "--method", "org.gnome.Mutter.DisplayConfig.GetCurrentState"])
        if not out:
            return []
        try:
            mon_re = re.compile(
                r"\(\s*(?P<x>-?\d+)\s*,\s*(?P<y>-?\d+)\s*,\s*(?P<scale>[\d\.]+)\s*,\s*(?P<trans>\d+)\s*,\s*(?P<prim>true|false)\s*,\s*\[\s*\(\s*'(?P<name>[^']+)'",
                re.IGNORECASE
            )
            mode_re = re.compile(r"'(?P<mid>[^']+)'\s*,\s*(?P<w>\d+)\s*,\s*(?P<h>\d+)", re.IGNORECASE)
            matches = list(mon_re.finditer(out))
            if not matches:
                return []
            modes_map: Dict[str, Tuple[int, int]] = {}
            for m in mode_re.finditer(out):
                modes_map[m.group("mid")] = (int(m.group("w")), int(m.group("h")))

            result: List[PhysicalMonitor] = []
            for i, match in enumerate(matches, start=1):
                vx = int(match.group("x"))
                vy = int(match.group("y"))
                scale = float(match.group("scale"))
                is_prim = match.group("prim").lower() == "true"
                cname = match.group("name")
                w, h = 1920, 1080
                for mid, (mw, mh) in modes_map.items():
                    if cname in mid:
                        w, h = mw, mh
                        break
                result.append(PhysicalMonitor(
                    monitorId=cname,
                    name=f"Monitör {i} ({cname})",
                    virtualX=vx,
                    virtualY=vy,
                    width=w,
                    height=h,
                    scaleFactor=scale if scale > 0.05 else 1.0,
                    isPrimary=is_prim
                ))
            return result
        except Exception:
            return []

    def _try_drm_sysfs(self) -> List[PhysicalMonitor]:
        try:
            drm_path = Path("/sys/class/drm")
            if not drm_path.exists():
                return []
            connected_outputs = []
            for p in sorted(drm_path.glob("card*-*")):
                status_file = p / "status"
                if status_file.exists():
                    try:
                        content = status_file.read_text(encoding="utf-8").strip()
                        if content == "connected":
                            name = p.name.split("-", 1)[1] if "-" in p.name else p.name
                            w, h = 1920, 1080
                            modes_file = p / "modes"
                            if modes_file.exists():
                                lines = modes_file.read_text(encoding="utf-8").splitlines()
                                if lines and "x" in lines[0]:
                                    parts = lines[0].strip().split("x")
                                    w, h = int(parts[0]), int(parts[1])
                            connected_outputs.append((name, w, h))
                    except Exception:
                        pass
            if not connected_outputs:
                return []
            result: List[PhysicalMonitor] = []
            cur_x = 0
            for i, (name, w, h) in enumerate(connected_outputs, start=1):
                result.append(PhysicalMonitor(
                    monitorId=name,
                    name=f"Monitör {i} ({name})",
                    virtualX=cur_x,
                    virtualY=0,
                    width=w,
                    height=h,
                    scaleFactor=1.0,
                    isPrimary=(i == 1)
                ))
                cur_x += w
            return result
        except Exception:
            return []


class YdotoolSocketClient:
    """Direct Unix domain socket client for ydotoold daemon to achieve zero-overhead 1000Hz injection."""

    def __init__(self):
        self.sock: Optional[socket.socket] = None
        self._last_connect_try = 0.0

    def _find_socket(self) -> Optional[str]:
        candidates = []
        if os.environ.get("YDOTOOL_SOCKET"):
            candidates.append(os.environ["YDOTOOL_SOCKET"])
        xdg_rt = os.environ.get("XDG_RUNTIME_DIR")
        if xdg_rt:
            candidates.append(os.path.join(xdg_rt, ".ydotool_socket"))
            candidates.append(os.path.join(xdg_rt, ".ydotoold_socket"))
            candidates.append(os.path.join(xdg_rt, "ydotoold.socket"))
        uid = os.getuid() if hasattr(os, "getuid") else 1000
        candidates.extend([
            f"/run/user/{uid}/.ydotool_socket",
            f"/run/user/{uid}/.ydotoold_socket",
            "/tmp/.ydotool_socket",
            "/tmp/.ydotoold_socket",
        ])
        for p in candidates:
            if os.path.exists(p):
                return p
        return None

    def connect(self) -> bool:
        if self.sock is not None:
            return True
        now = time.time()
        if now - self._last_connect_try < 1.0:
            return False
        self._last_connect_try = now

        sock_path = self._find_socket()
        if not sock_path:
            return False
        try:
            s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            s.settimeout(0.5)
            s.connect(sock_path)
            s.setblocking(False)
            self.sock = s
            return True
        except Exception:
            self.sock = None
            return False

    def close(self):
        if self.sock:
            try:
                self.sock.close()
            except Exception:
                pass
            self.sock = None

    def move_relative(self, dx: int, dy: int) -> bool:
        if not self.connect() or not self.sock:
            return False
        try:
            pkt = (
                struct.pack("@qqHHi", 0, 0, 0x02, 0x00, int(dx)) +
                struct.pack("@qqHHi", 0, 0, 0x02, 0x01, int(dy)) +
                struct.pack("@qqHHi", 0, 0, 0x00, 0x00, 0)
            )
            self.sock.sendall(pkt)
            return True
        except Exception:
            self.close()
            return False

    def mouse_button(self, btn: int, is_pressed: bool) -> bool:
        if not self.connect() or not self.sock:
            return False
        try:
            code = 0x110 if btn == BUTTON_LEFT else (0x111 if btn == BUTTON_RIGHT else 0x112)
            val = 1 if is_pressed else 0
            pkt = (
                struct.pack("@qqHHi", 0, 0, 0x01, code, val) +
                struct.pack("@qqHHi", 0, 0, 0x00, 0x00, 0)
            )
            self.sock.sendall(pkt)
            return True
        except Exception:
            self.close()
            return False

    def mouse_scroll(self, sx: int, sy: int) -> bool:
        if not self.connect() or not self.sock:
            return False
        try:
            val = 1 if sy > 0 else (-1 if sy < 0 else 0)
            if val == 0:
                return True
            pkt = (
                struct.pack("@qqHHi", 0, 0, 0x02, 0x08, val) +
                struct.pack("@qqHHi", 0, 0, 0x00, 0x00, 0)
            )
            self.sock.sendall(pkt)
            return True
        except Exception:
            self.close()
            return False

    def key_event(self, code: int, is_pressed: bool) -> bool:
        if not self.connect() or not self.sock:
            return False
        try:
            val = 1 if is_pressed else 0
            pkt = (
                struct.pack("@qqHHi", 0, 0, 0x01, code, val) +
                struct.pack("@qqHHi", 0, 0, 0x00, 0x00, 0)
            )
            self.sock.sendall(pkt)
            return True
        except Exception:
            self.close()
            return False


class LinuxInputInjector:
    """
    High-performance Linux input injector with 5 fallback tiers:
    1. evdev.UInput (Linux kernel virtual device - <0.2ms latency, recognized by KDE Wayland as real mouse)
    2. Pure-Python /dev/uinput ioctl device (zero external dependencies)
    3. ydotoold daemon via direct UNIX socket (1000Hz zero-overhead Wayland injection)
    4. ydotool CLI fallback
    5. xdotool & pynput (X11 / XWayland fallback)
    """

    def __init__(self, on_log: Optional[Callable[[str], None]] = None) -> None:
        self.mode = "none"
        self.on_log = on_log
        self.evdev_mouse = None
        self.evdev_kbd = None
        self.uinput_fd = None
        self.ydotool_client = YdotoolSocketClient()
        self._ensure_uinput_accessible()
        self._init_injector()

    @property
    def evdev_device(self):
        return self.evdev_mouse

    def _ensure_uinput_accessible(self) -> None:
        """Attempts non-interactive chmod on /dev/uinput if available."""
        if os.path.exists("/dev/uinput") and not os.access("/dev/uinput", os.W_OK):
            try:
                subprocess.run(["sudo", "-n", "chmod", "666", "/dev/uinput"], capture_output=True, timeout=1.0)
            except Exception:
                pass

    def _init_injector(self) -> None:
        # Tier 1: Try evdev module with separate dedicated mouse & keyboard devices
        # This is critical for Wayland (KWin / libinput) so mouse movements create a real pointer seat
        try:
            import evdev
            from evdev import UInput, ecodes

            mouse_cap = {
                ecodes.EV_REL: [ecodes.REL_X, ecodes.REL_Y, ecodes.REL_WHEEL, ecodes.REL_HWHEEL],
                ecodes.EV_KEY: [
                    ecodes.BTN_LEFT, ecodes.BTN_RIGHT, ecodes.BTN_MIDDLE,
                    ecodes.BTN_SIDE, ecodes.BTN_EXTRA
                ]
            }
            self.evdev_mouse = UInput(mouse_cap, name="Connect-Me-Mouse", version=0x1)

            kbd_keys = [
                ecodes.KEY_ESC, ecodes.KEY_ENTER, ecodes.KEY_BACKSPACE, ecodes.KEY_TAB,
                ecodes.KEY_SPACE, ecodes.KEY_LEFTSHIFT, ecodes.KEY_RIGHTSHIFT,
                ecodes.KEY_LEFTCTRL, ecodes.KEY_RIGHTCTRL, ecodes.KEY_LEFTALT, ecodes.KEY_RIGHTALT,
                ecodes.KEY_LEFTMETA, ecodes.KEY_RIGHTMETA, ecodes.KEY_UP, ecodes.KEY_DOWN,
                ecodes.KEY_LEFT, ecodes.KEY_RIGHT, ecodes.KEY_DELETE, ecodes.KEY_HOME,
                ecodes.KEY_END, ecodes.KEY_PAGEUP, ecodes.KEY_PAGEDOWN, ecodes.KEY_CAPSLOCK,
                ecodes.KEY_INSERT, ecodes.KEY_SCROLLLOCK, ecodes.KEY_PAUSE,
            ]
            for c in range(ord('A'), ord('Z') + 1):
                attr = f"KEY_{chr(c)}"
                if hasattr(ecodes, attr):
                    kbd_keys.append(getattr(ecodes, attr))
            for i in range(10):
                attr = f"KEY_{i}"
                if hasattr(ecodes, attr):
                    kbd_keys.append(getattr(ecodes, attr))
            for i in range(1, 13):
                attr = f"KEY_F{i}"
                if hasattr(ecodes, attr):
                    kbd_keys.append(getattr(ecodes, attr))

            kbd_cap = {
                ecodes.EV_KEY: list(set(kbd_keys))
            }
            try:
                self.evdev_kbd = UInput(kbd_cap, name="Connect-Me-Keyboard", version=0x1)
            except Exception:
                self.evdev_kbd = None

            self.mode = "evdev"
            return
        except Exception:
            self.evdev_mouse = None
            self.evdev_kbd = None

        # Tier 2: Try native /dev/uinput via fcntl
        try:
            if os.path.exists("/dev/uinput") and os.access("/dev/uinput", os.W_OK):
                self._init_raw_uinput()
                if self.uinput_fd is not None:
                    self.mode = "uinput_raw"
                    return
        except Exception:
            pass

        # Tier 3: Try ydotool with ydotoold daemon (socket connection for 1000Hz performance)
        if shutil.which("ydotool") or shutil.which("ydotoold"):
            self._ensure_ydotoold()
            self.mode = "ydotool"
            return

        # Tier 4: Fallback to xdotool
        if shutil.which("xdotool"):
            self.mode = "xdotool"
            return

        # Tier 5: Fallback to pynput
        try:
            from pynput.mouse import Controller as MouseController
            from pynput.keyboard import Controller as KbdController
            self.pynput_mouse = MouseController()
            self.pynput_kbd = KbdController()
            self.mode = "pynput"
            return
        except Exception:
            pass

        self.mode = "none"

    def _init_raw_uinput(self) -> None:
        try:
            import fcntl
            UI_SET_EVBIT = 0x40045564
            UI_SET_KEYBIT = 0x40045565
            UI_SET_RELBIT = 0x40045566
            UI_DEV_CREATE = 0x5501

            fd = os.open("/dev/uinput", os.O_WRONLY | os.O_NONBLOCK)
            fcntl.ioctl(fd, UI_SET_EVBIT, 0x01)  # EV_KEY
            fcntl.ioctl(fd, UI_SET_EVBIT, 0x02)  # EV_REL
            fcntl.ioctl(fd, UI_SET_RELBIT, 0x00)  # REL_X
            fcntl.ioctl(fd, UI_SET_RELBIT, 0x01)  # REL_Y
            fcntl.ioctl(fd, UI_SET_RELBIT, 0x08)  # REL_WHEEL
            fcntl.ioctl(fd, UI_SET_KEYBIT, 0x110)  # BTN_LEFT
            fcntl.ioctl(fd, UI_SET_KEYBIT, 0x111)  # BTN_RIGHT
            fcntl.ioctl(fd, UI_SET_KEYBIT, 0x112)  # BTN_MIDDLE

            name = b"Connect-Me-Mouse\x00".ljust(80, b"\x00")
            input_id = struct.pack("<HHHH", 0x03, 0x1234, 0x5678, 1)  # BUS_USB
            uinput_user_dev = name + input_id + b"\x00" * (4 + 64 * 4 * 4)
            os.write(fd, uinput_user_dev)
            fcntl.ioctl(fd, UI_DEV_CREATE)
            self.uinput_fd = fd
        except Exception:
            self.uinput_fd = None

    def _write_raw_event(self, ev_type: int, code: int, value: int) -> None:
        if self.uinput_fd is None:
            return
        try:
            # struct input_event: timeval (sec: 8B, usec: 8B), type: 2B, code: 2B, value: 4B
            pkt = struct.pack("@qqHHi", 0, 0, ev_type, code, value)
            os.write(self.uinput_fd, pkt)
        except Exception:
            pass

    def _ensure_ydotoold(self) -> None:
        res = subprocess.run(["pgrep", "-x", "ydotoold"], capture_output=True, text=True)
        if res.returncode != 0:
            try:
                subprocess.run(["systemctl", "--user", "start", "ydotoold"], capture_output=True, timeout=1.5)
            except Exception:
                pass
            res2 = subprocess.run(["pgrep", "-x", "ydotoold"], capture_output=True, text=True)
            if res2.returncode != 0 and shutil.which("ydotoold"):
                try:
                    sock_path = "/tmp/.ydotool_socket"
                    subprocess.Popen(["ydotoold", f"--socket-path={sock_path}"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                    os.environ["YDOTOOL_SOCKET"] = sock_path
                    time.sleep(0.15)
                except Exception:
                    pass

    def move_relative(self, dx: int, dy: int) -> None:
        dev = self.evdev_mouse or self.evdev_device
        if self.mode == "evdev" and dev:
            import evdev
            from evdev import ecodes
            dev.write(ecodes.EV_REL, ecodes.REL_X, dx)
            dev.write(ecodes.EV_REL, ecodes.REL_Y, dy)
            dev.syn()
        elif self.mode == "uinput_raw" and self.uinput_fd:
            self._write_raw_event(0x02, 0x00, dx)  # EV_REL, REL_X
            self._write_raw_event(0x02, 0x01, dy)  # EV_REL, REL_Y
            self._write_raw_event(0x00, 0x00, 0)   # EV_SYN, SYN_REPORT
        elif self.mode == "ydotool":
            if self.ydotool_client.move_relative(dx, dy):
                return
            subprocess.run(["ydotool", "mousemove", "--", str(dx), str(dy)], check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        elif self.mode == "xdotool":
            subprocess.run(["xdotool", "mousemove_relative", "--", str(dx), str(dy)], check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        elif self.mode == "pynput" and hasattr(self, "pynput_mouse") and self.pynput_mouse:
            try:
                self.pynput_mouse.move(dx, dy)
            except Exception:
                pass

    def mouse_button(self, btn: int, is_pressed: bool) -> None:
        # btn: 1=Left, 2=Right, 3=Middle
        dev = self.evdev_mouse or self.evdev_device
        if self.mode == "evdev" and dev:
            import evdev
            from evdev import ecodes
            code = ecodes.BTN_LEFT if btn == BUTTON_LEFT else (ecodes.BTN_RIGHT if btn == BUTTON_RIGHT else ecodes.BTN_MIDDLE)
            dev.write(ecodes.EV_KEY, code, 1 if is_pressed else 0)
            dev.syn()
        elif self.mode == "uinput_raw" and self.uinput_fd:
            code = 0x110 if btn == BUTTON_LEFT else (0x111 if btn == BUTTON_RIGHT else 0x112)
            self._write_raw_event(0x01, code, 1 if is_pressed else 0)
            self._write_raw_event(0x00, 0x00, 0)
        elif self.mode == "ydotool":
            if self.ydotool_client.mouse_button(btn, is_pressed):
                return
            ydo_code = "0xC0" if btn == BUTTON_LEFT else ("0xC1" if btn == BUTTON_RIGHT else "0xC2")
            subprocess.run(["ydotool", "click", f"{ydo_code}{'d' if is_pressed else 'u'}"], check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        elif self.mode == "xdotool":
            subcmd = "mousedown" if is_pressed else "mouseup"
            subprocess.run(["xdotool", subcmd, str(btn)], check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        elif self.mode == "pynput" and hasattr(self, "pynput_mouse") and self.pynput_mouse:
            try:
                from pynput.mouse import Button
                btn_obj = Button.left if btn == BUTTON_LEFT else (Button.right if btn == BUTTON_RIGHT else Button.middle)
                if is_pressed:
                    self.pynput_mouse.press(btn_obj)
                else:
                    self.pynput_mouse.release(btn_obj)
            except Exception:
                pass

    def mouse_scroll(self, sx: int, sy: int) -> None:
        dev = self.evdev_mouse or self.evdev_device
        if self.mode == "evdev" and dev:
            import evdev
            from evdev import ecodes
            val = 1 if sy > 0 else (-1 if sy < 0 else 0)
            if val != 0:
                dev.write(ecodes.EV_REL, ecodes.REL_WHEEL, val)
                dev.syn()
        elif self.mode == "uinput_raw" and self.uinput_fd:
            val = 1 if sy > 0 else (-1 if sy < 0 else 0)
            if val != 0:
                self._write_raw_event(0x02, 0x08, val)  # REL_WHEEL
                self._write_raw_event(0x00, 0x00, 0)
        elif self.mode == "ydotool":
            if self.ydotool_client.mouse_scroll(sx, sy):
                return
            wheel_arg = f"-w{sy}"
            subprocess.run(["ydotool", "mousemove", wheel_arg], check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        elif self.mode == "xdotool":
            btn = "4" if sy > 0 else "5"
            clicks = max(1, abs(sy) // 40)
            subprocess.run(["xdotool", "click", "--repeat", str(clicks), btn], check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        elif self.mode == "pynput" and hasattr(self, "pynput_mouse") and self.pynput_mouse:
            try:
                self.pynput_mouse.scroll(sx, sy)
            except Exception:
                pass

    def _vk_to_evdev_code(self, vk: int) -> Optional[int]:
        try:
            import evdev
            from evdev import ecodes
            if 0x41 <= vk <= 0x5A:  # A-Z
                char = chr(vk)
                return getattr(ecodes, f"KEY_{char}", None)
            if 0x30 <= vk <= 0x39:  # 0-9
                num = chr(vk)
                return getattr(ecodes, f"KEY_{num}", None)
            if 0x70 <= vk <= 0x7B:  # F1-F12
                fnum = vk - 0x70 + 1
                return getattr(ecodes, f"KEY_F{fnum}", None)

            mapping = {
                0x08: ecodes.KEY_BACKSPACE,
                0x09: ecodes.KEY_TAB,
                0x0D: ecodes.KEY_ENTER,
                0x1B: ecodes.KEY_ESC,
                0x20: ecodes.KEY_SPACE,
                0x21: ecodes.KEY_PAGEUP,
                0x22: ecodes.KEY_PAGEDOWN,
                0x23: ecodes.KEY_END,
                0x24: ecodes.KEY_HOME,
                0x25: ecodes.KEY_LEFT,
                0x26: ecodes.KEY_UP,
                0x27: ecodes.KEY_RIGHT,
                0x28: ecodes.KEY_DOWN,
                0x2D: ecodes.KEY_INSERT,
                0x2E: ecodes.KEY_DELETE,
                0x5B: ecodes.KEY_LEFTMETA,
                0x5C: ecodes.KEY_RIGHTMETA,
                0xA0: ecodes.KEY_LEFTSHIFT,
                0xA1: ecodes.KEY_RIGHTSHIFT,
                0xA2: ecodes.KEY_LEFTCTRL,
                0xA3: ecodes.KEY_RIGHTCTRL,
                0xA4: ecodes.KEY_LEFTALT,
                0xA5: ecodes.KEY_RIGHTALT,
            }
            return mapping.get(vk)
        except Exception:
            return None

    def _evdev_code_to_vk(self, code: int) -> Optional[int]:
        try:
            import evdev
            from evdev import ecodes
            for c in range(ord('A'), ord('Z') + 1):
                if getattr(ecodes, f"KEY_{chr(c)}", None) == code:
                    return c
            for i in range(10):
                if getattr(ecodes, f"KEY_{i}", None) == code:
                    return 0x30 + i
            for i in range(1, 13):
                if getattr(ecodes, f"KEY_F{i}", None) == code:
                    return 0x6F + i

            reverse_map = {
                ecodes.KEY_BACKSPACE: 0x08,
                ecodes.KEY_TAB: 0x09,
                ecodes.KEY_ENTER: 0x0D,
                ecodes.KEY_ESC: 0x1B,
                ecodes.KEY_SPACE: 0x20,
                ecodes.KEY_PAGEUP: 0x21,
                ecodes.KEY_PAGEDOWN: 0x22,
                ecodes.KEY_END: 0x23,
                ecodes.KEY_HOME: 0x24,
                ecodes.KEY_LEFT: 0x25,
                ecodes.KEY_UP: 0x26,
                ecodes.KEY_RIGHT: 0x27,
                ecodes.KEY_DOWN: 0x28,
                ecodes.KEY_INSERT: 0x2D,
                ecodes.KEY_DELETE: 0x2E,
                ecodes.KEY_LEFTMETA: 0x5B,
                ecodes.KEY_RIGHTMETA: 0x5C,
                ecodes.KEY_LEFTSHIFT: 0xA0,
                ecodes.KEY_RIGHTSHIFT: 0xA1,
                ecodes.KEY_LEFTCTRL: 0xA2,
                ecodes.KEY_RIGHTCTRL: 0xA3,
                ecodes.KEY_LEFTALT: 0xA4,
                ecodes.KEY_RIGHTALT: 0xA5,
                ecodes.KEY_CAPSLOCK: 0x14,
                ecodes.KEY_SCROLLLOCK: 0x91,
            }
            return reverse_map.get(code)
        except Exception:
            return None

    def key_event(self, vk: int, is_pressed: bool, ch: str) -> None:
        dev = self.evdev_kbd or self.evdev_mouse or self.evdev_device
        if self.mode == "evdev" and dev:
            import evdev
            from evdev import ecodes
            code = self._vk_to_evdev_code(vk)
            if code is not None:
                dev.write(ecodes.EV_KEY, code, 1 if is_pressed else 0)
                dev.syn()
                return

        if self.mode == "ydotool":
            code = self._vk_to_evdev_code(vk)
            if code is not None and self.ydotool_client.key_event(code, is_pressed):
                return

        if not is_pressed:
            return

        if self.mode in ("xdotool", "ydotool"):
            if vk == 0x08:  # Backspace
                cmd = ["ydotool", "key", "14:1", "14:0"] if self.mode == "ydotool" else ["xdotool", "key", "BackSpace"]
            elif vk == 0x0D:  # Enter
                cmd = ["ydotool", "key", "28:1", "28:0"] if self.mode == "ydotool" else ["xdotool", "key", "Return"]
            elif vk == 0x1B:  # Escape
                cmd = ["ydotool", "key", "1:1", "1:0"] if self.mode == "ydotool" else ["xdotool", "key", "Escape"]
            elif vk == 0x09:  # Tab
                cmd = ["ydotool", "key", "15:1", "15:0"] if self.mode == "ydotool" else ["xdotool", "key", "Tab"]
            elif vk in (0x5B, 0x5C):  # Win / Super
                cmd = ["ydotool", "key", "125:1", "125:0"] if self.mode == "ydotool" else ["xdotool", "key", "Super_L"]
            elif vk == 0x25:  # Left Arrow
                cmd = ["ydotool", "key", "105:1", "105:0"] if self.mode == "ydotool" else ["xdotool", "key", "Left"]
            elif vk == 0x26:  # Up Arrow
                cmd = ["ydotool", "key", "103:1", "103:0"] if self.mode == "ydotool" else ["xdotool", "key", "Up"]
            elif vk == 0x27:  # Right Arrow
                cmd = ["ydotool", "key", "106:1", "106:0"] if self.mode == "ydotool" else ["xdotool", "key", "Right"]
            elif vk == 0x28:  # Down Arrow
                cmd = ["ydotool", "key", "108:1", "108:0"] if self.mode == "ydotool" else ["xdotool", "key", "Down"]
            elif ch and ord(ch) >= 32:
                cmd = ["ydotool", "type", ch] if self.mode == "ydotool" else ["xdotool", "type", ch]
            else:
                return
            subprocess.run(cmd, check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


class LinuxHardwareInputGrabber:
    """Hardware-level input capture and background edge-crossing tracker for Linux as Host KVM using evdev."""

    def __init__(self, node: 'ConnectMeLinuxNode'):
        self.node = node
        self.is_active = False
        self.active_peer: Optional[dict] = None
        self.exit_edge: int = EDGE_NONE
        self._pointer_devs: List[Any] = []
        self._kbd_devs: List[Any] = []
        self._threads: List[threading.Thread] = []
        self._stop_event = threading.Event()
        self.on_deactivated: Optional[Callable[[], None]] = None

        # Coordinate tracking & edge resistance
        self._coord_lock = threading.Lock()
        self.cur_x = 960
        self.cur_y = 540
        self._accum_push = 0.0
        self._push_edge = EDGE_NONE
        self._edge_cooldown_until = 0.0
        self._supervisor_thread: Optional[threading.Thread] = None

        # Hardware modifier states for remote typing
        self._shift_down = False
        self._ctrl_down = False
        self._alt_down = False
        self._meta_down = False
        self._caps_lock = False

        self._init_initial_coordinates()

    def _init_initial_coordinates(self) -> None:
        try:
            min_x, min_y, vw, vh = self.node.topology.virtual_desktop_bounds()
            primary = next((m for m in self.node.topology.monitors if m.isPrimary), self.node.topology.monitors[0] if self.node.topology.monitors else None)
            if primary:
                self.cur_x = primary.virtualX + primary.width // 2
                self.cur_y = primary.virtualY + primary.height // 2
            else:
                self.cur_x = min_x + vw // 2
                self.cur_y = min_y + vh // 2
        except Exception:
            self.cur_x = 960
            self.cur_y = 540

    def sync_cursor(self, x: int, y: int) -> None:
        """Called by GUI or sentinels to calibrate absolute pointer coordinates."""
        if self.is_active:
            return
        with self._coord_lock:
            self.cur_x = int(x)
            self.cur_y = int(y)

    def sync_cursor_at_edge(self, edge: int, norm_pos: float = 0.5, peer: Optional[dict] = None) -> None:
        """Sets cursor coordinates just inside the returned edge upon handoff back to Linux."""
        target_mon = None
        if peer and peer.get("attachedMonitorId"):
            mon_id = peer.get("attachedMonitorId")
            for m in self.node.topology.monitors:
                if m.monitorId == mon_id:
                    target_mon = m
                    break
        if not target_mon:
            target_mon = self.node.topology.get_outermost_monitor_for_edge(edge)
        if not target_mon and self.node.topology.monitors:
            target_mon = self.node.topology.monitors[0]

        seg_start = float(peer.get("edgeOffsetStart", 0.0)) if peer else 0.0
        seg_end = float(peer.get("edgeOffsetEnd", 1.0)) if peer else 1.0
        mapped_norm = seg_start + max(0.0, min(1.0, float(norm_pos))) * (seg_end - seg_start)
        norm = max(0.02, min(0.98, mapped_norm))

        inset = 35
        with self._coord_lock:
            if target_mon:
                if edge == EDGE_RIGHT:
                    self.cur_x = target_mon.virtualX + target_mon.width - inset
                    self.cur_y = target_mon.virtualY + int(target_mon.height * norm)
                elif edge == EDGE_LEFT:
                    self.cur_x = target_mon.virtualX + inset
                    self.cur_y = target_mon.virtualY + int(target_mon.height * norm)
                elif edge == EDGE_TOP:
                    self.cur_x = target_mon.virtualX + int(target_mon.width * norm)
                    self.cur_y = target_mon.virtualY + inset
                elif edge == EDGE_BOTTOM:
                    self.cur_x = target_mon.virtualX + int(target_mon.width * norm)
                    self.cur_y = target_mon.virtualY + target_mon.height - inset
                else:
                    self.cur_x = target_mon.virtualX + target_mon.width // 2
                    self.cur_y = target_mon.virtualY + target_mon.height // 2
            else:
                min_x, min_y, vw, vh = self.node.topology.virtual_desktop_bounds()
                self.cur_x = min_x + vw // 2
                self.cur_y = min_y + vh // 2
        self._edge_cooldown_until = time.time() + 1.2

        # Physically nudge OS cursor away from the boundary edge to prevent instant bounce back
        try:
            if edge == EDGE_RIGHT:
                self.node.injector.move_relative(-60, 0)
            elif edge == EDGE_LEFT:
                self.node.injector.move_relative(60, 0)
            elif edge == EDGE_TOP:
                self.node.injector.move_relative(0, 60)
            elif edge == EDGE_BOTTOM:
                self.node.injector.move_relative(0, -60)
        except Exception:
            pass

    def can_grab(self) -> bool:
        """Returns True if evdev is importable and at least one physical pointer is accessible."""
        try:
            import evdev
            for path in evdev.list_devices():
                try:
                    dev = evdev.InputDevice(path)
                    caps = dev.capabilities()
                    if evdev.ecodes.EV_REL in caps and evdev.ecodes.REL_X in caps[evdev.ecodes.EV_REL]:
                        if "Connect-Me" not in dev.name and "ydotoold" not in dev.name:
                            return True
                except Exception:
                    pass
            return False
        except Exception:
            return False

    def start_monitoring(self) -> None:
        """Starts background monitoring loop for hot-plugged devices and edge tracking."""
        if self._supervisor_thread is not None and self._supervisor_thread.is_alive():
            return
        self._stop_event.clear()
        self._supervisor_thread = threading.Thread(target=self._supervisor_loop, daemon=True)
        self._supervisor_thread.start()

    def _supervisor_loop(self) -> None:
        while not self._stop_event.is_set():
            if not self.is_active:
                self._refresh_and_start_device_threads()
            for _ in range(35):
                if self._stop_event.is_set():
                    break
                time.sleep(0.1)

    def _refresh_and_start_device_threads(self) -> None:
        try:
            import evdev
            from evdev import ecodes, InputDevice, list_devices
        except ImportError:
            return

        active_paths = {getattr(d, "path", None) for d in (self._pointer_devs + self._kbd_devs) if d is not None}
        new_pointers = []
        new_kbds = []

        try:
            for path in list_devices():
                if path in active_paths:
                    continue
                try:
                    dev = InputDevice(path)
                    caps = dev.capabilities()
                    if "Connect-Me" in dev.name or "ydotoold" in dev.name:
                        dev.close()
                        continue
                    if ecodes.EV_REL in caps and ecodes.REL_X in caps[evdev.ecodes.EV_REL]:
                        new_pointers.append(dev)
                    elif ecodes.EV_KEY in caps and ecodes.KEY_A in caps[ecodes.EV_KEY] and ecodes.KEY_ENTER in caps[ecodes.EV_KEY]:
                        new_kbds.append(dev)
                    else:
                        dev.close()
                except Exception:
                    pass
        except Exception:
            return

        for dev in new_pointers:
            self._pointer_devs.append(dev)
            t = threading.Thread(target=self._device_read_loop, args=(dev, True), daemon=True)
            self._threads.append(t)
            t.start()

        for dev in new_kbds:
            self._kbd_devs.append(dev)
            t = threading.Thread(target=self._device_read_loop, args=(dev, False), daemon=True)
            self._threads.append(t)
            t.start()

    def _evdev_to_key_and_char(self, code: int) -> Tuple[Optional[int], int]:
        try:
            from evdev import ecodes
        except ImportError:
            return None, 0

        # Letters A-Z
        if ecodes.KEY_A <= code <= ecodes.KEY_Z:
            offset = code - ecodes.KEY_A
            vk = 0x41 + offset
            is_upper = self._shift_down ^ self._caps_lock
            ch = ord(chr(ord('A') + offset) if is_upper else chr(ord('a') + offset))
            return vk, ch

        # Top row numbers 0-9
        if ecodes.KEY_1 <= code <= ecodes.KEY_9:
            digit = (code - ecodes.KEY_1) + 1
            vk = 0x30 + digit
            shift_chars = ")!@#$%^&*("
            ch = ord(shift_chars[digit]) if self._shift_down else ord(str(digit))
            return vk, ch
        if code == ecodes.KEY_0:
            vk = 0x30
            ch = ord(')') if self._shift_down else ord('0')
            return vk, ch

        # Keypad 0-9
        kp_map = {
            ecodes.KEY_KP0: (0x60, ord('0')),
            ecodes.KEY_KP1: (0x61, ord('1')),
            ecodes.KEY_KP2: (0x62, ord('2')),
            ecodes.KEY_KP3: (0x63, ord('3')),
            ecodes.KEY_KP4: (0x64, ord('4')),
            ecodes.KEY_KP5: (0x65, ord('5')),
            ecodes.KEY_KP6: (0x66, ord('6')),
            ecodes.KEY_KP7: (0x67, ord('7')),
            ecodes.KEY_KP8: (0x68, ord('8')),
            ecodes.KEY_KP9: (0x69, ord('9')),
            ecodes.KEY_KPDOT: (0x6E, ord('.')),
            ecodes.KEY_KPENTER: (0x0D, 13),
            ecodes.KEY_KPPLUS: (0x6B, ord('+')),
            ecodes.KEY_KPMINUS: (0x6D, ord('-')),
            ecodes.KEY_KPASTERISK: (0x6A, ord('*')),
            ecodes.KEY_KPSLASH: (0x6F, ord('/')),
        }
        if code in kp_map:
            return kp_map[code]

        # Punctuation & Symbols (with Shift awareness)
        punct_map = {
            ecodes.KEY_SPACE: (0x20, 32, 32),
            ecodes.KEY_MINUS: (0xBD, ord('-'), ord('_')),
            ecodes.KEY_EQUAL: (0xBB, ord('='), ord('+')),
            ecodes.KEY_LEFTBRACE: (0xDB, ord('['), ord('{')),
            ecodes.KEY_RIGHTBRACE: (0xDD, ord(']'), ord('}')),
            ecodes.KEY_BACKSLASH: (0xDC, ord('\\'), ord('|')),
            ecodes.KEY_SEMICOLON: (0xBA, ord(';'), ord(':')),
            ecodes.KEY_APOSTROPHE: (0xDE, ord("'"), ord('"')),
            ecodes.KEY_GRAVE: (0xC0, ord('`'), ord('~')),
            ecodes.KEY_COMMA: (0xBC, ord(','), ord('<')),
            ecodes.KEY_DOT: (0xBE, ord('.'), ord('>')),
            ecodes.KEY_SLASH: (0xBF, ord('/'), ord('?')),
        }
        if code in punct_map:
            vk, unshift_c, shift_c = punct_map[code]
            return vk, shift_c if self._shift_down else unshift_c

        # Control & Navigation Keys
        ctrl_map = {
            ecodes.KEY_BACKSPACE: (0x08, 0),
            ecodes.KEY_TAB: (0x09, 9),
            ecodes.KEY_ENTER: (0x0D, 13),
            ecodes.KEY_ESC: (0x1B, 0),
            ecodes.KEY_PAGEUP: (0x21, 0),
            ecodes.KEY_PAGEDOWN: (0x22, 0),
            ecodes.KEY_END: (0x23, 0),
            ecodes.KEY_HOME: (0x24, 0),
            ecodes.KEY_LEFT: (0x25, 0),
            ecodes.KEY_UP: (0x26, 0),
            ecodes.KEY_RIGHT: (0x27, 0),
            ecodes.KEY_DOWN: (0x28, 0),
            ecodes.KEY_INSERT: (0x2D, 0),
            ecodes.KEY_DELETE: (0x2E, 0),
            ecodes.KEY_LEFTMETA: (0x5B, 0),
            ecodes.KEY_RIGHTMETA: (0x5C, 0),
            ecodes.KEY_LEFTSHIFT: (0xA0, 0),
            ecodes.KEY_RIGHTSHIFT: (0xA1, 0),
            ecodes.KEY_LEFTCTRL: (0xA2, 0),
            ecodes.KEY_RIGHTCTRL: (0xA3, 0),
            ecodes.KEY_LEFTALT: (0xA4, 0),
            ecodes.KEY_RIGHTALT: (0xA5, 0),
            ecodes.KEY_CAPSLOCK: (0x14, 0),
            ecodes.KEY_SCROLLLOCK: (0x91, 0),
        }
        if code in ctrl_map:
            return ctrl_map[code]

        # Function Keys F1-F12
        for i in range(1, 13):
            if getattr(ecodes, f"KEY_F{i}", None) == code:
                return 0x6F + i, 0

        # Fallback to injector's mapping
        vk = self.node.injector._evdev_code_to_vk(code)
        return vk, 0

    def _device_read_loop(self, dev: Any, is_pointer: bool) -> None:
        try:
            import evdev
            from evdev import ecodes
        except ImportError:
            return

        acc_dx = 0
        acc_dy = 0

        try:
            for event in dev.read_loop():
                if self._stop_event.is_set():
                    break

                # Case A: Remote control active (Devices grabbed, forwarding to remote peer)
                if self.is_active:
                    if event.type == ecodes.EV_REL:
                        if event.code == ecodes.REL_X:
                            acc_dx += event.value
                        elif event.code == ecodes.REL_Y:
                            acc_dy += event.value
                        elif event.code == ecodes.REL_WHEEL:
                            if self.active_peer:
                                self.node.send_mouse_scroll(self.active_peer, 0, event.value * 120)
                        elif event.code == ecodes.REL_HWHEEL:
                            if self.active_peer:
                                self.node.send_mouse_scroll(self.active_peer, event.value * 120, 0)

                    elif event.type == ecodes.EV_KEY:
                        # Emergency return hotkey: Scroll Lock only (ESC is preserved for apps/games)
                        if event.code == ecodes.KEY_SCROLLLOCK and event.value == 1:
                            self.node.log("[Donanım Yakalama] ⬅️ [ScrollLock] tuşuna basıldı, kontrol Linux'a geri alınıyor.")
                            threading.Thread(target=self.deactivate, args=(True,), daemon=True).start()
                            continue

                        # Mouse buttons
                        if event.code == ecodes.BTN_LEFT:
                            if self.active_peer:
                                self.node.send_mouse_button(self.active_peer, BUTTON_LEFT, event.value == 1)
                        elif event.code == ecodes.BTN_RIGHT:
                            if self.active_peer:
                                self.node.send_mouse_button(self.active_peer, BUTTON_RIGHT, event.value == 1)
                        elif event.code == ecodes.BTN_MIDDLE:
                            if self.active_peer:
                                self.node.send_mouse_button(self.active_peer, BUTTON_MIDDLE, event.value == 1)
                        else:
                            # Keyboard key
                            is_down = event.value in (1, 2)
                            if event.code in (ecodes.KEY_LEFTSHIFT, ecodes.KEY_RIGHTSHIFT):
                                self._shift_down = is_down
                            elif event.code in (ecodes.KEY_LEFTCTRL, ecodes.KEY_RIGHTCTRL):
                                self._ctrl_down = is_down
                            elif event.code in (ecodes.KEY_LEFTALT, ecodes.KEY_RIGHTALT):
                                self._alt_down = is_down
                            elif event.code in (ecodes.KEY_LEFTMETA, ecodes.KEY_RIGHTMETA):
                                self._meta_down = is_down
                            elif event.code == ecodes.KEY_CAPSLOCK and event.value == 1:
                                self._caps_lock = not self._caps_lock

                            mods = 0
                            if self._shift_down:
                                mods |= 1
                            if self._ctrl_down:
                                mods |= 2
                            if self._alt_down:
                                mods |= 4
                            if self._meta_down:
                                mods |= 8

                            vk, char_code = self._evdev_to_key_and_char(event.code)
                            if vk and self.active_peer:
                                self.node.send_key_event(self.active_peer, vk, event.code, is_down, mods, char_code)

                    elif event.type == ecodes.EV_SYN:
                        if (acc_dx != 0 or acc_dy != 0) and self.active_peer:
                            self.node.send_mouse_move(self.active_peer, acc_dx, acc_dy)
                            acc_dx = 0
                            acc_dy = 0

                # Case B: Local Linux desktop mode (Passive background edge tracking)
                else:
                    if is_pointer and event.type == ecodes.EV_REL:
                        dx = event.value if event.code == ecodes.REL_X else 0
                        dy = event.value if event.code == ecodes.REL_Y else 0
                        if dx != 0 or dy != 0:
                            self._process_local_mouse_move(dx, dy)
        except Exception:
            pass
        finally:
            try:
                if dev in self._pointer_devs:
                    self._pointer_devs.remove(dev)
                if dev in self._kbd_devs:
                    self._kbd_devs.remove(dev)
                dev.close()
            except Exception:
                pass

    def _process_local_mouse_move(self, dx: int, dy: int) -> None:
        if self.is_active or time.time() < self._edge_cooldown_until:
            return

        if not any(p.get("isMutuallyPaired") for p in self.node.peers.values()):
            self._accum_push = 0.0
            return

        with self._coord_lock:
            self.cur_x += dx
            self.cur_y += dy

            min_x, min_y, vw, vh = self.node.topology.virtual_desktop_bounds()
            max_x = min_x + vw - 1
            max_y = min_y + vh - 1

            self.cur_x = max(min_x, min(max_x, self.cur_x))
            self.cur_y = max(min_y, min(max_y, self.cur_y))

            cur_x = self.cur_x
            cur_y = self.cur_y

        mon = self.node.topology.find_monitor_at(cur_x, cur_y)
        if not mon:
            return

        rel_x = cur_x - mon.virtualX
        rel_y = cur_y - mon.virtualY

        dead_zone = 24
        in_v_corner = rel_y < dead_zone or rel_y > (mon.height - dead_zone)
        in_h_corner = rel_x < dead_zone or rel_x > (mon.width - dead_zone)

        hit_edge = EDGE_NONE
        outward_push = 0.0

        if rel_x >= mon.width - 2 and dx > 0 and not in_v_corner:
            hit_edge = EDGE_RIGHT
            outward_push = dx
        elif rel_x <= 1 and dx < 0 and not in_v_corner:
            hit_edge = EDGE_LEFT
            outward_push = -dx
        elif rel_y <= 1 and dy < 0 and not in_h_corner:
            hit_edge = EDGE_TOP
            outward_push = -dy
        elif rel_y >= mon.height - 2 and dy > 0 and not in_h_corner:
            hit_edge = EDGE_BOTTOM
            outward_push = dy

        if hit_edge == EDGE_NONE:
            if rel_x > 10 and rel_x < mon.width - 10 and rel_y > 10 and rel_y < mon.height - 10:
                self._accum_push = 0.0
                self._push_edge = EDGE_NONE
            return

        if self.node.topology.is_internal_seam(mon, hit_edge, cur_x, cur_y):
            self._accum_push = 0.0
            self._push_edge = EDGE_NONE
            return

        if hit_edge in (EDGE_LEFT, EDGE_RIGHT):
            norm_pos = max(0.02, min(0.98, rel_y / float(max(1, mon.height))))
        else:
            norm_pos = max(0.02, min(0.98, rel_x / float(max(1, mon.width))))

        target_peer = self.node.resolve_peer_for_edge_and_norm(hit_edge, norm_pos, mon.monitorId)
        if not target_peer:
            self._accum_push = 0.0
            self._push_edge = EDGE_NONE
            return

        if self._push_edge != hit_edge:
            self._push_edge = hit_edge
            self._accum_push = 0.0

        self._accum_push += outward_push

        if self._accum_push >= 15.0:
            self._accum_push = 0.0
            self._push_edge = EDGE_NONE
            self._edge_cooldown_until = time.time() + 1.2

            peer_name = target_peer.get("deviceName", "Cihaz")
            self.node.log(f"[Otomatik Kenar Geçişi] 🎯 İmleç {hit_edge}. kenardan '{peer_name}' cihazına otomatik aktarılıyor (norm={norm_pos:.2f}).")

            threading.Thread(
                target=self.node.switch_control_to_peer,
                args=(target_peer, hit_edge, norm_pos),
                daemon=True
            ).start()

    def activate(self, peer: dict, exit_edge: int, norm_pos: float = 0.5) -> bool:
        if self.is_active:
            self.deactivate(notify_peer=False)

        if not self._pointer_devs:
            self._refresh_and_start_device_threads()

        if not self._pointer_devs:
            return False

        self.active_peer = peer
        self.exit_edge = exit_edge
        self.is_active = True
        self._accum_push = 0.0
        self._push_edge = EDGE_NONE

        grabbed_count = 0
        for dev in list(self._pointer_devs + self._kbd_devs):
            try:
                dev.grab()
                grabbed_count += 1
            except Exception:
                pass

        if grabbed_count == 0:
            self.is_active = False
            return False

        self.node.log(f"[Donanım Yakalama] 🎯 Fiziksel fare ve klavye ({grabbed_count} donanım aygıtı) çekirdek seviyesinde başarıyla yakalandı.")
        return True

    def deactivate(self, notify_peer: bool = True):
        if not self.is_active:
            return
        self.is_active = False
        self.active_peer = None
        self._accum_push = 0.0
        self._push_edge = EDGE_NONE
        self._edge_cooldown_until = time.time() + 1.2

        for dev in list(self._pointer_devs + self._kbd_devs):
            try:
                dev.ungrab()
            except Exception:
                pass

        if notify_peer:
            self.node.return_host_control_to_local(notify_peer=True)

        if self.on_deactivated:
            try:
                self.on_deactivated()
            except Exception:
                pass

    def stop(self) -> None:
        self._stop_event.set()
        self.deactivate(notify_peer=False)
        for dev in list(self._pointer_devs + self._kbd_devs):
            try:
                dev.close()
            except Exception:
                pass
        self._pointer_devs.clear()
        self._kbd_devs.clear()


class ConnectMeLinuxNode:
    """Nobara Linux / KDE Plasma Wayland Full-Featured Node for Connect Me."""

    def __init__(self, on_log: Optional[Callable[[str], None]] = None) -> None:
        self.device_id = f"linux-{socket.gethostname().lower()}"
        self.device_name = f"{socket.gethostname()} (Nobara KDE)"
        self.platform = "linux-nobara"
        self.local_pin = f"{random.randint(100000, 999999)}"
        self.topology = LinuxMultiMonitorTopology()
        self.injector = LinuxInputInjector()
        self.hardware_grabber = LinuxHardwareInputGrabber(self)
        self.hardware_grabber.start_monitoring()

        self.shelf_dir = Path.home() / "Downloads" / "ConnectMe-Shelf"
        self.shelf_dir.mkdir(parents=True, exist_ok=True)
        self.trust_file = Path.home() / ".config" / "connectme" / "trusted_devices.json"
        self.trust_file.parent.mkdir(parents=True, exist_ok=True)
        self.trusted_devices: Dict[str, dict] = self._load_trusted_devices()

        self.topology_file = Path.home() / ".config" / "connectme" / "peer_topology.json"
        self.topology_file.parent.mkdir(parents=True, exist_ok=True)
        self.peer_arrangements: Dict[str, dict] = self._load_peer_arrangements()

        self.peers: Dict[str, dict] = {}
        self.active_remote_peer: Optional[dict] = None
        self.entry_edge: int = EDGE_NONE
        self.cursor_x: float = 0.0
        self.cursor_y: float = 0.0
        self.return_edge_push_accum: float = 0.0

        # Host Control State (When Linux is host controlling Android or Windows)
        self.controlling_remote_peer: Optional[dict] = None
        self.controlling_exit_edge: int = EDGE_NONE
        self.on_host_control_switched: Optional[Callable[[dict, int, float], None]] = None
        self.on_host_control_returned: Optional[Callable[[], None]] = None
        self._host_seq: int = 0
        self._host_udp_sock: socket.socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

        # UI & Telemetry Callbacks
        self.on_log = on_log
        self.on_cursor_update: Optional[Callable[[bool, int, int], None]] = None
        self.on_pin_request_received: Optional[Callable[[str, str], None]] = None
        self.on_peer_discovered: Optional[Callable[[dict], None]] = None
        self.on_peer_updated: Optional[Callable[[dict], None]] = None
        self._last_auto_connect_attempt: Dict[str, float] = {}
        self.suppressed_autoconnect: Set[str] = set()

        self.running = True
        self.last_copied_clipboard = ""
        self.udp_sock: Optional[socket.socket] = None

        # Audio streaming (Transmitter: Linux -> Windows)
        self.audio_streaming = False
        self.audio_target_ip: Optional[str] = None
        self.audio_proc: Optional[subprocess.Popen] = None
        self.audio_thread: Optional[threading.Thread] = None
        self.audio_seq = 0

        # Audio Receiver (Sink: Android/Windows -> Linux Headphones/Speakers)
        self.audio_rx_sock: Optional[socket.socket] = None
        self.audio_rx_bytes: int = 0
        self.audio_rx_active: bool = False
        self.audio_rx_last_time: float = 0.0
        self.audio_rx_last_sender: str = ""
        self.audio_rx_last_player: str = ""

        # Audio Matrix & Cross-Device Routing
        self.local_audio_sinks: List[Dict[str, Any]] = []
        self.remote_audio_sinks: Dict[str, List[Dict[str, Any]]] = {}
        self.active_audio_route: Optional[Dict[str, Any]] = None
        self.audio_rx_target_sink: Optional[str] = None
        self.on_audio_sinks_updated: Optional[Any] = None
        self.on_audio_route_changed: Optional[Any] = None

    def log(self, text: str) -> None:
        line = f"[{time.strftime('%Y-%m-%d %H:%M:%S')}] {text}"
        try:
            print(line)
        except UnicodeEncodeError:
            try:
                print(line.encode("ascii", errors="replace").decode("ascii"))
            except Exception:
                pass
        try:
            log_dir = Path.home() / ".config" / "connectme"
            log_dir.mkdir(parents=True, exist_ok=True)
            log_file = log_dir / "connectme.log"
            if log_file.exists() and log_file.stat().st_size > 3 * 1024 * 1024:
                old_file = log_dir / "connectme.old.log"
                log_file.rename(old_file)
            with open(log_file, "a", encoding="utf-8") as f:
                f.write(f"{line}\n")
        except Exception:
            pass

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

    def _load_peer_arrangements(self) -> Dict[str, dict]:
        if not self.topology_file.exists():
            return {}
        try:
            with open(self.topology_file, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            return {}

    def _save_peer_arrangements(self) -> None:
        try:
            with open(self.topology_file, "w", encoding="utf-8") as f:
                json.dump(self.peer_arrangements, f, indent=2)
        except Exception:
            pass

    def get_local_ip(self) -> str:
        """Determines active local IPv4 address on LAN."""
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            s.connect(("8.8.8.8", 80))
            lip = s.getsockname()[0]
            s.close()
            return lip
        except Exception:
            return "127.0.0.1"

    def get_peer_arrangement(self, device_id: str) -> dict:
        return self.peer_arrangements.get(device_id, {
            "assignedEdge": EDGE_RIGHT,
            "edgeOffset": 0.5,
            "edgeOffsetStart": 0.0,
            "edgeOffsetEnd": 1.0,
            "attachedMonitorId": None,
            "canvasX": None,
            "canvasY": None
        })

    def set_peer_arrangement(
        self,
        device_id: str,
        edge: int,
        edge_offset: float = 0.5,
        canvas_x: Optional[float] = None,
        canvas_y: Optional[float] = None,
        edge_offset_start: float = 0.0,
        edge_offset_end: float = 1.0,
        attached_monitor_id: Optional[str] = None
    ) -> None:
        rec = self.peer_arrangements.setdefault(device_id, {})
        rec["assignedEdge"] = edge
        rec["edgeOffset"] = edge_offset
        rec["edgeOffsetStart"] = edge_offset_start
        rec["edgeOffsetEnd"] = edge_offset_end
        if attached_monitor_id is not None:
            rec["attachedMonitorId"] = attached_monitor_id
        if canvas_x is not None:
            rec["canvasX"] = canvas_x
        if canvas_y is not None:
            rec["canvasY"] = canvas_y
        self._save_peer_arrangements()
        if device_id in self.peers:
            self.peers[device_id]["assignedEdge"] = edge
            self.peers[device_id]["edgeOffset"] = edge_offset
            self.peers[device_id]["edgeOffsetStart"] = edge_offset_start
            self.peers[device_id]["edgeOffsetEnd"] = edge_offset_end
            if attached_monitor_id is not None:
                self.peers[device_id]["attachedMonitorId"] = attached_monitor_id
            if canvas_x is not None:
                self.peers[device_id]["canvasX"] = canvas_x
                self.peers[device_id]["canvasY"] = canvas_y
        edge_name = {EDGE_LEFT: "Sol", EDGE_RIGHT: "Sağ", EDGE_TOP: "Üst", EDGE_BOTTOM: "Alt"}.get(edge, "Kenar")
        self.log(f"[Ekran Düzeni] '{device_id}' {edge_name} kenara yerleştirildi (%{int(edge_offset_start*100)} - %{int(edge_offset_end*100)}).")
        if self.on_peer_updated and device_id in self.peers:
            try:
                self.on_peer_updated(self.peers[device_id])
            except Exception:
                pass

    def send_edge_config_to_peer(self, peer: dict) -> None:
        """Sends TCP EDGE_CONFIG to remote device to inform its return entrance edge."""
        if not peer.get("isMutuallyPaired"):
            return
        assigned_edge = peer.get("assignedEdge", EDGE_RIGHT)
        opposite_edge = {
            EDGE_LEFT: EDGE_RIGHT,
            EDGE_RIGHT: EDGE_LEFT,
            EDGE_TOP: EDGE_BOTTOM,
            EDGE_BOTTOM: EDGE_TOP
        }.get(assigned_edge, EDGE_LEFT)
        hdr = {
            "type": "EDGE_CONFIG",
            "senderId": self.device_id,
            "senderName": self.device_name,
            "senderPlatform": self.platform,
            "returnEdge": opposite_edge
        }
        self.send_tcp_control_to_peer(peer, hdr)

    def reset_peer_arrangements(self) -> None:
        """Clears all custom canvas coordinates, deletes topology cache file, and resets peers to default edge."""
        self.peer_arrangements.clear()
        try:
            if self.topology_file.exists():
                self.topology_file.unlink()
        except Exception:
            pass
        for peer in self.peers.values():
            peer["canvasX"] = None
            peer["canvasY"] = None
            peer["assignedEdge"] = EDGE_RIGHT
            peer["edgeOffset"] = 0.5
            peer["edgeOffsetStart"] = 0.0
            peer["edgeOffsetEnd"] = 1.0
            peer["attachedMonitorId"] = None
        self.log("[Ekran Düzeni] 📐 Ekran yerleşim düzeni varsayılana sıfırlandı.")

    def distribute_peers_on_edge(self, edge: int) -> None:
        """Evenly distributes multiple mutually paired peers on a single edge across non-overlapping fractional segments."""
        same_edge_peers = [
            p for p in self.peers.values()
            if p.get("isMutuallyPaired") and p.get("assignedEdge", EDGE_RIGHT) == edge
        ]
        if not same_edge_peers:
            return

        outermost = self.topology.get_outermost_monitor_for_edge(edge)
        mon_id = getattr(outermost, "monitorId", "") if outermost else ""

        step = 1.0 / len(same_edge_peers)
        for i, p in enumerate(same_edge_peers):
            seg_start = round(i * step, 4)
            seg_end = round((i + 1) * step, 4)
            p["edgeOffset"] = (seg_start + seg_end) / 2.0
            p["edgeOffsetStart"] = seg_start
            p["edgeOffsetEnd"] = seg_end
            if not p.get("attachedMonitorId"):
                p["attachedMonitorId"] = mon_id
            p["canvasX"] = None
            p["canvasY"] = None
            self.set_peer_arrangement(
                p["deviceId"],
                edge,
                edge_offset=p["edgeOffset"],
                edge_offset_start=seg_start,
                edge_offset_end=seg_end,
                attached_monitor_id=p.get("attachedMonitorId", mon_id)
            )
            self.send_edge_config_to_peer(p)

    def auto_arrange_peers(self) -> None:
        """Distributes all mutually paired peers across edges (Right, Left, Bottom, Top) and distributes segments evenly."""
        paired = [p for p in self.peers.values() if p.get("isMutuallyPaired")]
        if not paired:
            return

        edges_order = [EDGE_RIGHT, EDGE_LEFT, EDGE_BOTTOM, EDGE_TOP]
        for i, p in enumerate(paired):
            edge = edges_order[i % len(edges_order)]
            outermost = self.topology.get_outermost_monitor_for_edge(edge)
            mon_id = getattr(outermost, "monitorId", "") if outermost else ""
            p["assignedEdge"] = edge
            p["attachedMonitorId"] = mon_id
            p["canvasX"] = None
            p["canvasY"] = None

        for edge in edges_order:
            self.distribute_peers_on_edge(edge)

        self.log(f"[Ekran Düzeni] 📐 {len(paired)} cihaz kenarlara otomatik ve dengeli olarak dağıtıldı.")

    def resolve_peer_for_edge_and_norm(self, edge: int, norm_pos: float = 0.5, monitor_id: Optional[str] = None) -> Optional[dict]:
        """Resolves target peer for screen edge based on normalized cursor position and 2D arrangement."""
        candidates = [p for p in self.peers.values() if p.get("isMutuallyPaired") and p.get("assignedEdge", EDGE_RIGHT) == edge]
        if not candidates:
            return None

        # Filter by attached physical monitor if specified
        if monitor_id:
            on_this_mon = [p for p in candidates if not p.get("attachedMonitorId") or p.get("attachedMonitorId") in (monitor_id, "ALL")]
            if on_this_mon:
                candidates = on_this_mon

        # Check for segment match [edgeOffsetStart .. edgeOffsetEnd]
        exact_matches = [
            p for p in candidates
            if (p.get("edgeOffsetStart", 0.0) - 0.02 <= norm_pos <= p.get("edgeOffsetEnd", 1.0) + 0.02)
        ]
        if exact_matches:
            exact_matches.sort(key=lambda p: abs(norm_pos - (p.get("edgeOffsetStart", 0.0) + p.get("edgeOffsetEnd", 1.0)) / 2.0))
            return exact_matches[0]

        if len(candidates) == 1:
            return candidates[0]

        candidates.sort(key=lambda p: abs(norm_pos - (p.get("edgeOffsetStart", 0.0) + p.get("edgeOffsetEnd", 1.0)) / 2.0))
        return candidates[0]

    def switch_control_to_peer(self, peer: dict, local_exit_edge: int = EDGE_NONE, norm_pos: float = 0.5) -> bool:
        """Switches mouse/keyboard control from Linux host to remote peer (Android or Windows)."""
        if not peer:
            return False
        remote_ip = peer.get("ipAddress")
        if not remote_ip:
            return False

        assigned_edge = peer.get("assignedEdge", EDGE_RIGHT)
        if local_exit_edge == EDGE_NONE:
            local_exit_edge = assigned_edge

        target_entrance_edge = {
            EDGE_LEFT: EDGE_RIGHT,
            EDGE_RIGHT: EDGE_LEFT,
            EDGE_TOP: EDGE_BOTTOM,
            EDGE_BOTTOM: EDGE_TOP
        }.get(local_exit_edge, EDGE_LEFT)

        self.controlling_remote_peer = peer
        self.controlling_exit_edge = local_exit_edge

        # Map local normalized position to peer's relative offset segment
        seg_start = float(peer.get("edgeOffsetStart", 0.0))
        seg_end = float(peer.get("edgeOffsetEnd", 1.0))
        span = max(0.05, seg_end - seg_start)
        peer_rel_pos = max(0.0, min(1.0, (norm_pos - seg_start) / span))

        # Activate kernel-level hardware input grabber if supported
        self.hardware_grabber.activate(peer, local_exit_edge, norm_pos)

        # Send Fast UDP EDGE_HANDOFF packet
        self.send_edge_handoff(peer, target_entrance_edge, peer_rel_pos)

        peer_name = peer.get("deviceName", remote_ip)
        self.log(f"[Host Modu] 🚀 Fare ve klavye kontrolü '{peer_name}' cihazına aktarıldı (Çıkış: {local_exit_edge}, Giriş: {target_entrance_edge}).")

        if self.on_host_control_switched:
            try:
                self.on_host_control_switched(peer, local_exit_edge, norm_pos)
            except Exception:
                pass
        return True

    def return_host_control_to_local(self, notify_peer: bool = True, return_edge: int = EDGE_NONE, norm_pos: float = 0.5) -> None:
        """Returns mouse/keyboard control back from remote peer to Linux host."""
        if hasattr(self, "hardware_grabber") and self.hardware_grabber.is_active:
            self.hardware_grabber.deactivate(notify_peer=False)

        peer = self.controlling_remote_peer
        if hasattr(self, "hardware_grabber"):
            eff_edge = return_edge if return_edge != EDGE_NONE else getattr(self, "controlling_exit_edge", EDGE_RIGHT)
            self.hardware_grabber.sync_cursor_at_edge(eff_edge, norm_pos, peer)

        if not peer:
            return
        self.controlling_remote_peer = None
        peer_name = peer.get("deviceName", "Cihaz")
        remote_ip = peer.get("ipAddress")

        if notify_peer and remote_ip:
            return_edge = {
                EDGE_LEFT: EDGE_RIGHT,
                EDGE_RIGHT: EDGE_LEFT,
                EDGE_TOP: EDGE_BOTTOM,
                EDGE_BOTTOM: EDGE_TOP
            }.get(self.controlling_exit_edge, EDGE_LEFT)

            # 1. Fast UDP packet
            try:
                u_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                pkt = struct.pack("<BBBBBBf", MAGIC_0, MAGIC_1, PROTO_VER, PACKET_EDGE_HANDOFF, return_edge, 0, 0.5)
                u_sock.sendto(pkt, (remote_ip, peer.get("udpInputPort", FAST_INPUT_UDP_PORT)))
                u_sock.close()
            except Exception:
                pass

            # 2. Reliable TCP packet
            try:
                t_sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
                t_sock.settimeout(1.5)
                t_sock.connect((remote_ip, peer.get("tcpControlPort", DATA_CONTROL_TCP_PORT)))
                self._send_tcp_frame(t_sock, {
                    "type": "EDGE_RETURN",
                    "senderId": self.device_id,
                    "senderName": self.device_name,
                    "senderUdpPort": FAST_INPUT_UDP_PORT,
                    "returnEdge": return_edge,
                    "normalizedPosition": 0.5
                })
                t_sock.close()
            except Exception:
                pass

        self.log(f"[Host Modu] ⬅️ '{peer_name}' cihazından yerel Linux masaüstüne dönüş yapıldı.")
        if self.on_host_control_returned:
            try:
                self.on_host_control_returned()
            except Exception:
                pass

    def send_edge_handoff(self, peer: dict, target_entrance_edge: int, normalized_pos: float = 0.5, is_dragging: bool = False) -> None:
        try:
            remote_ip = peer.get("ipAddress")
            udp_port = peer.get("udpInputPort", FAST_INPUT_UDP_PORT)
            norm_clamped = max(0.0, min(1.0, float(normalized_pos)))
            pkt = struct.pack("<BBBBBBf", MAGIC_0, MAGIC_1, PROTO_VER, PACKET_EDGE_HANDOFF, target_entrance_edge, 1 if is_dragging else 0, norm_clamped)
            self._host_udp_sock.sendto(pkt, (remote_ip, udp_port))
        except Exception as e:
            self.log(f"[Host Uyarı] send_edge_handoff hatası: {e}")

    def send_mouse_move(self, peer: dict, dx: int, dy: int) -> None:
        try:
            remote_ip = peer.get("ipAddress")
            udp_port = peer.get("udpInputPort", FAST_INPUT_UDP_PORT)
            self._host_seq = (self._host_seq + 1) & 0xFFFF
            dx_s = max(-32768, min(32767, int(dx)))
            dy_s = max(-32768, min(32767, int(dy)))
            pkt = struct.pack("<BBBBHhh", MAGIC_0, MAGIC_1, PROTO_VER, PACKET_MOUSE_MOVE, self._host_seq, dx_s, dy_s)
            self._host_udp_sock.sendto(pkt, (remote_ip, udp_port))
        except Exception:
            pass

    def send_mouse_button(self, peer: dict, btn: int, is_pressed: bool) -> None:
        try:
            remote_ip = peer.get("ipAddress")
            udp_port = peer.get("udpInputPort", FAST_INPUT_UDP_PORT)
            pkt = struct.pack("<BBBBBB", MAGIC_0, MAGIC_1, PROTO_VER, PACKET_MOUSE_BUTTON, btn, 1 if is_pressed else 0)
            self._host_udp_sock.sendto(pkt, (remote_ip, udp_port))
        except Exception:
            pass

    def send_mouse_scroll(self, peer: dict, sx: int, sy: int) -> None:
        try:
            remote_ip = peer.get("ipAddress")
            udp_port = peer.get("udpInputPort", FAST_INPUT_UDP_PORT)
            pkt = struct.pack("<BBBBhh", MAGIC_0, MAGIC_1, PROTO_VER, PACKET_MOUSE_SCROLL, max(-32768, min(32767, int(sx))), max(-32768, min(32767, int(sy))))
            self._host_udp_sock.sendto(pkt, (remote_ip, udp_port))
        except Exception:
            pass

    def send_key_event(self, peer: dict, vk: int, sc: int, is_pressed: bool, mods: int = 0, char_code: int = 0) -> None:
        try:
            remote_ip = peer.get("ipAddress")
            udp_port = peer.get("udpInputPort", FAST_INPUT_UDP_PORT)
            pkt = struct.pack("<BBBBHHBBH", MAGIC_0, MAGIC_1, PROTO_VER, PACKET_KEY_EVENT, vk & 0xFFFF, sc & 0xFFFF, 1 if is_pressed else 0, mods & 0xFF, char_code & 0xFFFF)
            self._host_udp_sock.sendto(pkt, (remote_ip, udp_port))
        except Exception:
            pass

    def disconnect_peer(self, device_id: str) -> bool:
        """Disconnects an active peer, returns mouse locally if active, suppresses auto-reconnect, and sends TCP DISCONNECT."""
        peer = self.peers.get(device_id)
        if not peer:
            for p in self.peers.values():
                if p.get("ipAddress") == device_id or p.get("deviceId") == device_id:
                    peer = p
                    break
        if not peer:
            return False

        dev_id = peer.get("deviceId", device_id)
        self.suppressed_autoconnect.add(dev_id)

        peer["in_ok"] = False
        peer["out_ok"] = False
        peer["isMutuallyPaired"] = False

        self.remote_audio_sinks.pop(dev_id, None)
        if self.active_audio_route and self.active_audio_route.get("targetDeviceId") == dev_id:
            self.set_and_broadcast_audio_route(None)
        if self.on_audio_sinks_updated:
            try:
                self.on_audio_sinks_updated()
            except Exception:
                pass

        if self.active_remote_peer and (self.active_remote_peer.get("deviceId") == dev_id or self.active_remote_peer.get("ipAddress") == peer.get("ipAddress")):
            self.return_control_to_local(0, 0.5)

        remote_ip = peer.get("ipAddress")
        tcp_port = peer.get("tcpControlPort", DATA_CONTROL_TCP_PORT)

        def _send_disconnect():
            try:
                sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
                sock.settimeout(2.5)
                sock.connect((remote_ip, tcp_port))
                header = {
                    "type": "DISCONNECT",
                    "senderId": self.device_id,
                    "senderName": self.device_name,
                    "senderPlatform": "linux-nobara"
                }
                self._send_tcp_frame(sock, header)
                sock.close()
            except Exception:
                pass

        if remote_ip:
            threading.Thread(target=_send_disconnect, daemon=True).start()

        self.log(f"[Bağlantı] 🔌 '{peer.get('deviceName', dev_id)}' ile olan bağlantı sonlandırıldı.")
        if self.on_peer_updated:
            try:
                self.on_peer_updated(peer)
            except Exception:
                pass
        return True

    def accept_inbound_pairing(self, device_id: str, remember: bool = True) -> bool:
        """Accepts an inbound pairing attempt in 1-click when remote peer already entered our PIN, sends PAIR_ACCEPT over TCP."""
        peer = self.peers.get(device_id)
        if not peer:
            for p in self.peers.values():
                if p.get("ipAddress") == device_id or p.get("deviceId") == device_id:
                    peer = p
                    break
        if not peer:
            return False

        dev_id = peer.get("deviceId", device_id)
        self.suppressed_autoconnect.discard(dev_id)
        peer["in_ok"] = True
        peer["out_ok"] = True
        peer["isMutuallyPaired"] = True
        self.on_peer_mutually_paired(peer)

        trust_token = f"trust-{self.device_id}-{os.urandom(8).hex()}" if remember else ""
        if remember and trust_token:
            self.trusted_devices[dev_id] = {
                "deviceId": dev_id,
                "deviceName": peer.get("deviceName", dev_id),
                "platform": peer.get("platform", "android"),
                "trustToken": trust_token,
                "autoConnect": True
            }
            self._save_trusted_devices()
            peer["is_trusted"] = True
            self.log(f"[Güvenlik] ⭐ '{peer.get('deviceName')}' güvenilir cihaz olarak kaydedildi.")

        # Send fast-path UDP PAIR_ACCEPT
        try:
            remote_ip = peer.get("ipAddress")
            if remote_ip:
                u_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                req_udp = {
                    "type": "PAIR_ACCEPT",
                    "senderId": self.device_id,
                    "senderName": self.device_name,
                    "senderPlatform": "linux-nobara",
                    "senderUdpPort": FAST_INPUT_UDP_PORT,
                    "senderTcpPort": DATA_CONTROL_TCP_PORT,
                    "senderScreenWidth": self.topology.total_width,
                    "senderScreenHeight": self.topology.total_height,
                    "senderMonitors": [asdict(m) for m in self.topology.monitors],
                    "requestTrust": remember,
                    "trustToken": trust_token if remember else None,
                    "isMutualComplete": True
                }
                u_sock.sendto(json.dumps(req_udp).encode("utf-8"), (remote_ip, DISCOVERY_UDP_PORT))
                u_sock.close()
        except Exception:
            pass

        # Send PAIR_ACCEPT over TCP to peer
        try:
            remote_ip = peer.get("ipAddress")
            tcp_port = peer.get("tcpControlPort", DATA_CONTROL_TCP_PORT)
            sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            sock.settimeout(4.0)
            sock.connect((remote_ip, tcp_port))
            req = {
                "type": "PAIR_ACCEPT",
                "senderId": self.device_id,
                "senderName": self.device_name,
                "senderPlatform": "linux-nobara",
                "senderUdpPort": FAST_INPUT_UDP_PORT,
                "senderTcpPort": DATA_CONTROL_TCP_PORT,
                "senderScreenWidth": self.topology.total_width,
                "senderScreenHeight": self.topology.total_height,
                "senderMonitors": [asdict(m) for m in self.topology.monitors],
                "requestTrust": remember,
                "trustToken": trust_token if remember else None,
                "isMutualComplete": True
            }
            self._send_tcp_frame(sock, req)
            sock.close()
            self.log(f"[Çift Taraflı Eşleşme] ✅ '{peer.get('deviceName')}' ({remote_ip}) bağlantı isteği tek tıkla onaylandı!")
            if self.on_peer_updated:
                try:
                    self.on_peer_updated(peer)
                except Exception:
                    pass
            return True
        except Exception as e:
            self.log(f"[Bağlantı Uyarı] PAIR_ACCEPT iletim hatası: {e}")
            return False

    def revoke_trust(self, device_id: str) -> None:
        if device_id in self.trusted_devices:
            del self.trusted_devices[device_id]
            self._save_trusted_devices()
        self.disconnect_peer(device_id)
        self.log(f"[Güvenlik] 🗑️ '{device_id}' için güvenilirlik kaydı silindi.")

    def fix_uinput_permissions(self) -> Tuple[bool, str]:
        """Attempts to setup uinput permissions via pkexec or sudo."""
        cmd = (
            "echo 'KERNEL==\"uinput\", GROUP=\"input\", MODE=\"0660\", TAG+=\"uaccess\", OPTIONS+=\"static_node=uinput\"' > /etc/udev/rules.d/99-connectme-uinput.rules "
            "&& groupadd -f input "
            "&& usermod -aG input \"$USER\" 2>/dev/null || true "
            "&& chmod 666 /dev/uinput 2>/dev/null || true "
            "&& udevadm control --reload-rules 2>/dev/null || true "
            "&& udevadm trigger 2>/dev/null || true"
        )
        try:
            if shutil.which("pkexec"):
                res = subprocess.run(["pkexec", "bash", "-c", cmd], capture_output=True, text=True, timeout=30.0)
                if res.returncode == 0:
                    self.injector._init_injector()
                    self.log(f"[Sürücü] ✅ /dev/uinput izinleri ayarlandı. Yeni mod: {self.injector.mode.upper()}")
                    return True, "İzinler başarıyla ayarlandı! Sanal fare aktif."
            res2 = subprocess.run(["sudo", "bash", "-c", cmd], capture_output=True, text=True, timeout=15.0)
            if res2.returncode == 0:
                self.injector._init_injector()
                self.log(f"[Sürücü] ✅ /dev/uinput izinleri ayarlandı. Yeni mod: {self.injector.mode.upper()}")
                return True, "İzinler başarıyla ayarlandı! Sanal fare aktif."
        except Exception as e:
            return False, f"İzin ayarlama hatası: {e}"
        return False, "Süper kullanıcı izni (pkexec/sudo) verilemedi."

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

        # Enumerate and cache local audio sinks
        self.local_audio_sinks = self.enumerate_local_audio_sinks()
        self.log(f" Algılanan Ses Çıkışları : {len(self.local_audio_sinks)} adet")
        for s in self.local_audio_sinks:
            def_str = " [VARSAYILAN]" if s.get("isDefault") else ""
            self.log(f"   • Ses Çıkışı: {s.get('sinkName')} ({s.get('sinkId')}){def_str}")

        self.log("=" * 72)

        # Start Discovery Receiver & Broadcaster
        threading.Thread(target=self._discovery_receiver_loop, daemon=True).start()
        threading.Thread(target=self._discovery_broadcast_loop, daemon=True).start()
        threading.Thread(target=self._tcp_server_loop, daemon=True).start()
        threading.Thread(target=self._udp_input_loop, daemon=True).start()
        threading.Thread(target=self._clipboard_monitor_loop, daemon=True).start()
        threading.Thread(target=self._audio_receiver_loop, daemon=True).start()

    def stop(self) -> None:
        """Stops the daemon background loops, releases sockets, and cleans up."""
        self.running = False
        if hasattr(self, "hardware_grabber"):
            self.hardware_grabber.stop()
        for s in (
            getattr(self, "discovery_sock", None),
            getattr(self, "tcp_srv", None),
            self.udp_sock,
            getattr(self, "_host_udp_sock", None),
            getattr(self, "audio_rx_sock", None),
        ):
            if s:
                try:
                    s.close()
                except Exception:
                    pass
        self.stop_audio_streaming()

    def build_beacon_json(self) -> bytes:
        _, _, vw, vh = self.topology.virtual_desktop_bounds()
        payload = {
            "type": "DISCOVER_BEACON",
            "deviceId": self.device_id,
            "deviceName": self.device_name,
            "platform": self.platform,
            "udpInputPort": FAST_INPUT_UDP_PORT,
            "tcpControlPort": DATA_CONTROL_TCP_PORT,
            "audioStreamUdpPort": AUDIO_STREAM_UDP_PORT,
            "screenWidth": vw,
            "screenHeight": vh,
            "monitors": [asdict(m) for m in self.topology.monitors],
            "timestamp": int(time.time() * 1000),
        }
        return json.dumps(payload).encode("utf-8")

    def _get_broadcast_addresses(self) -> List[str]:
        addrs = ["255.255.255.255"]
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            s.connect(("8.8.8.8", 80))
            local_ip = s.getsockname()[0]
            s.close()
            parts = local_ip.split(".")
            if len(parts) == 4:
                subnet_bc = f"{parts[0]}.{parts[1]}.{parts[2]}.255"
                if subnet_bc not in addrs:
                    addrs.append(subnet_bc)
        except Exception:
            pass
        return addrs

    def _discovery_receiver_loop(self) -> None:
        """Continuously receives UDP discovery beacons on port 42849 from Windows, Android and Linux."""
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        if hasattr(socket, "SO_REUSEPORT"):
            try:
                sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEPORT, 1)
            except OSError:
                pass
        try:
            sock.bind(("0.0.0.0", DISCOVERY_UDP_PORT))
            self.discovery_sock = sock
        except OSError as ex:
            self.log(f"[Keşif Hata] UDP {DISCOVERY_UDP_PORT} dinlenemedi: {ex}")
            return

        while self.running:
            try:
                data, addr = sock.recvfrom(4096)
                if not data:
                    continue
                payload = json.loads(data.decode("utf-8"))
                msg_type = payload.get("type", "")

                if msg_type == "PAIR_REQUEST":
                    target_pin = str(payload.get("targetPin", "")).strip()
                    sender_id = payload.get("senderId", addr[0])
                    sender_name = payload.get("senderName", addr[0])
                    peer = self.peers.setdefault(sender_id, {
                        "deviceId": sender_id,
                        "deviceName": sender_name,
                        "platform": payload.get("senderPlatform", "windows"),
                        "ipAddress": addr[0],
                        "udpInputPort": payload.get("senderUdpPort", FAST_INPUT_UDP_PORT),
                        "tcpControlPort": payload.get("senderTcpPort", DATA_CONTROL_TCP_PORT),
                        "in_ok": False,
                        "out_ok": False,
                        "isMutuallyPaired": False
                    })
                    peer["ipAddress"] = addr[0]
                    peer["deviceName"] = sender_name
                    peer["platform"] = payload.get("senderPlatform", peer.get("platform", "windows"))
                    if payload.get("senderMonitors"):
                        peer["monitors"] = payload.get("senderMonitors")
                    peer["lastSeen"] = time.time()

                    if target_pin == self.local_pin:
                        peer["in_ok"] = True
                        req_trust = bool(payload.get("requestTrust", False))
                        trust_token = payload.get("trustToken", "")
                        is_mut = bool(peer.get("out_ok", False))
                        peer["isMutuallyPaired"] = is_mut

                        if is_mut:
                            if req_trust and trust_token:
                                self.trusted_devices[sender_id] = {
                                    "deviceId": sender_id,
                                    "deviceName": sender_name,
                                    "platform": peer.get("platform", "windows"),
                                    "trustToken": trust_token,
                                    "autoConnect": True
                                }
                                self._save_trusted_devices()
                                peer["is_trusted"] = True
                                self.log(f"[Güvenlik] ⭐ '{sender_name}' karşılıklı doğrulandı ve güvenilir cihaz olarak kaydedildi.")
                            self.on_peer_mutually_paired(peer)
                        else:
                            peer["pending_trust_token"] = trust_token if req_trust else ""
                            peer["pending_req_trust"] = req_trust
                            self.log(f"[PIN İsteği - UDP] 🔔 '{sender_name}' ({addr[0]}) sizin 6 haneli kodunuzu ({self.local_pin}) doğru girdi! Eşleşmeyi tamamlamak için siz de onun 6 haneli kodunu girip onaylayın.")
                            if self.on_pin_request_received:
                                try:
                                    self.on_pin_request_received(sender_id, sender_name)
                                except Exception:
                                    pass

                        ack = {
                            "type": "PAIR_VERIFY_ACK",
                            "senderId": self.device_id,
                            "senderName": self.device_name,
                            "senderMonitors": [asdict(m) for m in self.topology.monitors],
                            "trustToken": trust_token if req_trust else None,
                            "isMutualComplete": is_mut,
                        }
                        ack_bytes = json.dumps(ack).encode("utf-8")
                        sock.sendto(ack_bytes, addr)
                        if self.on_peer_updated:
                            try:
                                self.on_peer_updated(peer)
                            except Exception:
                                pass
                    else:
                        rej = {"type": "PAIR_REJECT", "senderId": self.device_id, "senderName": self.device_name}
                        sock.sendto(json.dumps(rej).encode("utf-8"), addr)
                        self.log(f"[Güvenlik] ⚠️ '{sender_name}' hatalı kod denedi ({target_pin}).")
                    continue

                elif msg_type == "PAIR_VERIFY_ACK":
                    sender_id = payload.get("senderId", addr[0])
                    peer = self.peers.get(sender_id)
                    if not peer:
                        for p in self.peers.values():
                            if p.get("ipAddress") == addr[0]:
                                peer = p
                                break
                    if peer:
                        peer["out_ok"] = True
                        if payload.get("senderMonitors"):
                            peer["monitors"] = payload.get("senderMonitors")
                        if payload.get("senderName"):
                            peer["deviceName"] = payload.get("senderName")
                        if payload.get("isMutualComplete") or peer.get("in_ok"):
                            peer["isMutuallyPaired"] = True
                            if payload.get("trustToken"):
                                self.trusted_devices[peer["deviceId"]] = {
                                    "deviceId": peer["deviceId"],
                                    "deviceName": peer.get("deviceName", peer["deviceId"]),
                                    "platform": peer.get("platform", "windows"),
                                    "trustToken": payload.get("trustToken"),
                                    "autoConnect": True
                                }
                                self._save_trusted_devices()
                                peer["is_trusted"] = True
                            self.on_peer_mutually_paired(peer)
                            self.log(f"[Hızlı Eşleşme - UDP] ✅ '{peer.get('deviceName')}' ile karşılıklı eşleşme tamamlandı!")
                        else:
                            self.log(f"[PIN Onayı - UDP] 🔔 '{peer.get('deviceName')}' kodunuzu onayladı! Bağlantıyı tamamlamak için o da sizin kodunuzu ({self.local_pin}) girmeli.")
                        if self.on_peer_updated:
                            try:
                                self.on_peer_updated(peer)
                            except Exception:
                                pass
                    continue

                elif msg_type == "PAIR_ACCEPT":
                    sender_id = payload.get("senderId", addr[0])
                    sender_name = payload.get("senderName", addr[0])
                    peer = self.peers.setdefault(sender_id, {
                        "deviceId": sender_id,
                        "deviceName": sender_name,
                        "platform": payload.get("senderPlatform", "android"),
                        "ipAddress": addr[0],
                        "udpInputPort": payload.get("senderUdpPort", FAST_INPUT_UDP_PORT),
                        "tcpControlPort": payload.get("senderTcpPort", DATA_CONTROL_TCP_PORT),
                    })
                    peer["deviceName"] = sender_name
                    peer["platform"] = payload.get("senderPlatform", peer.get("platform", "android"))
                    peer["ipAddress"] = addr[0]
                    peer["in_ok"] = True
                    peer["out_ok"] = True
                    peer["isMutuallyPaired"] = True
                    self.on_peer_mutually_paired(peer)
                    self.suppressed_autoconnect.discard(sender_id)

                    req_trust = bool(payload.get("requestTrust", False))
                    trust_token = payload.get("trustToken", "")
                    if req_trust and trust_token:
                        self.trusted_devices[sender_id] = {
                            "deviceId": sender_id,
                            "deviceName": sender_name,
                            "platform": peer.get("platform", "android"),
                            "trustToken": trust_token,
                            "autoConnect": True
                        }
                        self._save_trusted_devices()
                        peer["is_trusted"] = True

                    ack = {
                        "type": "PAIR_ACCEPT_ACK",
                        "senderId": self.device_id,
                        "senderName": self.device_name,
                        "senderPlatform": "linux-nobara",
                        "senderUdpPort": FAST_INPUT_UDP_PORT,
                        "senderTcpPort": DATA_CONTROL_TCP_PORT,
                        "senderScreenWidth": self.topology.total_width,
                        "senderScreenHeight": self.topology.total_height,
                        "senderMonitors": [asdict(m) for m in self.topology.monitors],
                        "isMutualComplete": True
                    }
                    sock.sendto(json.dumps(ack).encode("utf-8"), addr)
                    self.log(f"[Çift Taraflı Eşleşme - UDP] ✅ '{sender_name}' ({addr[0]}) bağlantı isteğinizi onayladı!")
                    if self.on_peer_updated:
                        try:
                            self.on_peer_updated(peer)
                        except Exception:
                            pass
                    continue

                elif msg_type == "PAIR_ACCEPT_ACK":
                    sender_id = payload.get("senderId", addr[0])
                    sender_name = payload.get("senderName", addr[0])
                    peer = self.peers.get(sender_id)
                    if not peer:
                        for p in self.peers.values():
                            if p.get("ipAddress") == addr[0]:
                                peer = p
                                break
                    if peer:
                        peer["in_ok"] = True
                        peer["out_ok"] = True
                        peer["isMutuallyPaired"] = True
                        self.on_peer_mutually_paired(peer)
                        if self.on_peer_updated:
                            try:
                                self.on_peer_updated(peer)
                            except Exception:
                                pass
                    self.log(f"[Bağlantı Onayı - UDP] ✅ '{sender_name}' ({addr[0]}) PAIR_ACCEPT_ACK teyit etti.")
                    continue

                elif msg_type == "PAIR_REJECT":
                    sender_name = payload.get("senderName", addr[0])
                    self.log(f"[PIN Reddi - UDP] ⚠️ '{sender_name}' ({addr[0]}) girilen PIN kodunu reddetti.")
                    continue

                if msg_type != "DISCOVER_BEACON":
                    continue

                dev_id = payload.get("deviceId", "")
                if not dev_id or dev_id == self.device_id:
                    continue

                sender_name = payload.get("deviceName", dev_id)
                sender_plat = payload.get("platform", "unknown")
                sender_ip = addr[0]

                is_new = dev_id not in self.peers
                peer = self.peers.setdefault(dev_id, {
                    "deviceId": dev_id,
                    "deviceName": sender_name,
                    "platform": sender_plat,
                    "ipAddress": sender_ip,
                    "udpInputPort": payload.get("udpInputPort", FAST_INPUT_UDP_PORT),
                    "tcpControlPort": payload.get("tcpControlPort", DATA_CONTROL_TCP_PORT),
                    "screenWidth": payload.get("screenWidth", 1920),
                    "screenHeight": payload.get("screenHeight", 1080),
                    "monitors": payload.get("monitors", []),
                    "in_ok": False,
                    "out_ok": False,
                    "isMutuallyPaired": False,
                    "is_trusted": dev_id in self.trusted_devices
                })

                peer["ipAddress"] = sender_ip
                peer["deviceName"] = sender_name
                peer["platform"] = sender_plat
                peer["monitors"] = payload.get("monitors", peer.get("monitors", []))
                peer["screenWidth"] = payload.get("screenWidth", peer.get("screenWidth", 1920))
                peer["screenHeight"] = payload.get("screenHeight", peer.get("screenHeight", 1080))
                peer["lastSeen"] = time.time()

                arr = self.get_peer_arrangement(dev_id)
                peer["assignedEdge"] = arr.get("assignedEdge", EDGE_RIGHT)
                peer["edgeOffset"] = arr.get("edgeOffset", 0.5)
                peer["edgeOffsetStart"] = arr.get("edgeOffsetStart", 0.0)
                peer["edgeOffsetEnd"] = arr.get("edgeOffsetEnd", 1.0)
                peer["attachedMonitorId"] = arr.get("attachedMonitorId", None)
                if arr.get("canvasX") is not None:
                    peer["canvasX"] = arr.get("canvasX")
                if arr.get("canvasY") is not None:
                    peer["canvasY"] = arr.get("canvasY")

                is_trusted = dev_id in self.trusted_devices
                peer["is_trusted"] = is_trusted

                if is_trusted and not peer.get("isMutuallyPaired", False) and dev_id not in self.suppressed_autoconnect:
                    threading.Thread(target=self.try_auto_reconnect, args=(peer,), daemon=True).start()

                if is_new:
                    trust_tag = " [⭐ GÜVENİLİR - Otomatik Bağlanıyor...]" if is_trusted else " — Eşleşmek için 6 haneli kod girin."
                    self.log(f"[Keşif] Cihaz bulundu: {sender_name} ({sender_plat}) @ {sender_ip}{trust_tag}")
                    if self.on_peer_discovered:
                        try:
                            self.on_peer_discovered(peer)
                        except Exception:
                            pass
                elif self.on_peer_updated:
                    try:
                        self.on_peer_updated(peer)
                    except Exception:
                        pass
            except Exception:
                pass

    def try_auto_reconnect(self, peer: dict) -> bool:
        """Sends TRUSTED_RECONNECT over TCP using persistent pre-shared trust token."""
        if peer.get("isMutuallyPaired"):
            return True
        dev_id = peer.get("deviceId", "")
        if dev_id in self.suppressed_autoconnect:
            return False
        saved_rec = self.trusted_devices.get(dev_id)
        if not saved_rec or not saved_rec.get("autoConnect", True):
            return False
        token = saved_rec.get("trustToken", "")
        if not token:
            return False

        now = time.time()
        if now - self._last_auto_connect_attempt.get(dev_id, 0) < 5.0:
            return False
        self._last_auto_connect_attempt[dev_id] = now

        try:
            remote_ip = peer.get("ipAddress")
            tcp_port = peer.get("tcpControlPort", DATA_CONTROL_TCP_PORT)
            sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            sock.settimeout(3.5)
            sock.connect((remote_ip, tcp_port))
            req = {
                "type": "TRUSTED_RECONNECT",
                "senderId": self.device_id,
                "senderName": self.device_name,
                "senderPlatform": "linux-nobara",
                "senderUdpPort": FAST_INPUT_UDP_PORT,
                "senderTcpPort": DATA_CONTROL_TCP_PORT,
                "senderScreenWidth": self.topology.total_width,
                "senderScreenHeight": self.topology.total_height,
                "senderMonitors": [asdict(m) for m in self.topology.monitors],
                "trustToken": token
            }
            self._send_tcp_frame(sock, req)
            prefix = self._recv_exact(sock, 12)
            if len(prefix) < 12:
                sock.close()
                return False
            json_len, _ = struct.unpack("<iq", prefix)
            ack_bytes = self._recv_exact(sock, json_len)
            ack = json.loads(ack_bytes.decode("utf-8"))
            sock.close()

            if ack.get("type") == "TRUSTED_RECONNECT_ACK" and ack.get("isMutualComplete"):
                peer["in_ok"] = True
                peer["out_ok"] = True
                peer["is_trusted"] = True
                peer["isMutuallyPaired"] = True
                peer["monitors"] = ack.get("senderMonitors", peer.get("monitors", []))
                self.log(f"[Otomatik Bağlantı] ⭐ Güvenilir cihaz '{peer.get('deviceName')}' ({remote_ip}) PIN'siz otomatik bağlandı!")
                if self.on_peer_updated:
                    try:
                        self.on_peer_updated(peer)
                    except Exception:
                        pass
                return True
        except Exception:
            pass
        return False

    def send_discovery_broadcast(self, target_ip: Optional[str] = None) -> None:
        """Sends an immediate discovery beacon broadcast or targeted unicast beacon."""
        beacon_data = self.build_beacon_json()
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        try:
            if target_ip:
                sock.sendto(beacon_data, (target_ip, DISCOVERY_UDP_PORT))
            else:
                for bc_addr in self._get_broadcast_addresses():
                    try:
                        sock.sendto(beacon_data, (bc_addr, DISCOVERY_UDP_PORT))
                    except OSError:
                        pass
        finally:
            sock.close()

    def register_manual_peer(self, ip: str, device_name: str = "", platform: str = "windows") -> dict:
        """Registers a manual peer by IP address and sends an immediate targeted discovery beacon."""
        dev_id = f"peer-{ip}"
        for existing_id, p in self.peers.items():
            if p.get("ipAddress") == ip:
                dev_id = existing_id
                break

        self.suppressed_autoconnect.discard(dev_id)

        peer = self.peers.setdefault(dev_id, {
            "deviceId": dev_id,
            "deviceName": device_name or f"Cihaz ({ip})",
            "platform": platform,
            "ipAddress": ip,
            "udpInputPort": FAST_INPUT_UDP_PORT,
            "tcpControlPort": DATA_CONTROL_TCP_PORT,
            "screenWidth": 1920,
            "screenHeight": 1080,
            "monitors": [],
            "in_ok": False,
            "out_ok": False,
            "isMutuallyPaired": False,
            "is_trusted": dev_id in self.trusted_devices,
            "lastSeen": time.time()
        })
        peer["ipAddress"] = ip
        peer["lastSeen"] = time.time()
        arr = self.get_peer_arrangement(dev_id)
        peer["assignedEdge"] = arr.get("assignedEdge", EDGE_RIGHT)
        peer["edgeOffset"] = arr.get("edgeOffset", 0.5)
        peer["edgeOffsetStart"] = arr.get("edgeOffsetStart", 0.0)
        peer["edgeOffsetEnd"] = arr.get("edgeOffsetEnd", 1.0)
        peer["attachedMonitorId"] = arr.get("attachedMonitorId", None)
        if arr.get("canvasX") is not None:
            peer["canvasX"] = arr.get("canvasX")
        if arr.get("canvasY") is not None:
            peer["canvasY"] = arr.get("canvasY")

        # Hedefe anında doğrudan UDP keşif paketi gönder
        try:
            self.send_discovery_broadcast(target_ip=ip)
        except Exception:
            pass

        self.log(f"[Manuel IP] 🌐 '{ip}' hedefli cihaz eklendi ve doğrudan UDP keşif sinyali gönderildi.")
        return peer

    def _discovery_broadcast_loop(self) -> None:
        """Periodically broadcasts local discovery beacon to the network."""
        while self.running:
            self.send_discovery_broadcast()
            time.sleep(2.5)

    def _tcp_server_loop(self) -> None:
        try:
            srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            srv.bind(("0.0.0.0", DATA_CONTROL_TCP_PORT))
            srv.listen(8)
            self.tcp_srv = srv
            while self.running:
                try:
                    conn, addr = srv.accept()
                    threading.Thread(target=self._handle_tcp_client, args=(conn, addr[0]), daemon=True).start()
                except Exception:
                    if not self.running:
                        break
        except Exception as e:
            self.log(f"[TCP Sunucu Hatası] Port {DATA_CONTROL_TCP_PORT} başlatılamadı: {e}")

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
                    "platform": header.get("senderPlatform", "windows"),
                    "ipAddress": remote_ip,
                    "udpInputPort": header.get("senderUdpPort", FAST_INPUT_UDP_PORT),
                    "tcpControlPort": header.get("senderTcpPort", DATA_CONTROL_TCP_PORT),
                    "in_ok": False,
                    "out_ok": False,
                    "isMutuallyPaired": False
                })
                peer["monitors"] = header.get("senderMonitors", peer.get("monitors", []))

                if target_pin == self.local_pin:
                    peer["in_ok"] = True
                    req_trust = bool(header.get("requestTrust", False))
                    trust_token = header.get("trustToken", "")

                    is_mut = bool(peer.get("out_ok", False))
                    peer["isMutuallyPaired"] = is_mut

                    if is_mut:
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
                            self.log(f"[Güvenlik] ⭐ '{sender_name}' karşılıklı doğrulandı ve güvenilir cihaz olarak kaydedildi.")
                        self.on_peer_mutually_paired(peer)
                    else:
                        peer["pending_trust_token"] = trust_token if req_trust else ""
                        peer["pending_req_trust"] = req_trust
                        self.log(f"[PIN İsteği] 🔔 '{sender_name}' ({remote_ip}) sizin 6 haneli kodunuzu ({self.local_pin}) doğru girdi! Eşleşmeyi tamamlamak için siz de onun 6 haneli kodunu girip onaylayın.")
                        if self.on_pin_request_received:
                            try:
                                self.on_pin_request_received(sender_id, sender_name)
                            except Exception:
                                pass

                    ack = {
                        "type": "PAIR_VERIFY_ACK",
                        "senderId": self.device_id,
                        "senderName": self.device_name,
                        "senderMonitors": [asdict(m) for m in self.topology.monitors],
                        "trustToken": trust_token if req_trust else None,
                        "isMutualComplete": is_mut,
                    }
                    self._send_tcp_frame(conn, ack)
                else:
                    self._send_tcp_frame(conn, {"type": "PAIR_REJECT", "senderId": self.device_id, "senderName": self.device_name})
                    self.log(f"[Güvenlik] ⚠️ '{sender_name}' hatalı kod denedi ({target_pin}).")

            elif msg_type == "PAIR_ACCEPT":
                sender_id = header.get("senderId", remote_ip)
                sender_name = header.get("senderName", remote_ip)
                peer = self.peers.setdefault(sender_id, {
                    "deviceId": sender_id,
                    "deviceName": sender_name,
                    "platform": header.get("senderPlatform", "android"),
                    "ipAddress": remote_ip,
                    "udpInputPort": header.get("senderUdpPort", FAST_INPUT_UDP_PORT),
                    "tcpControlPort": header.get("senderTcpPort", DATA_CONTROL_TCP_PORT),
                })
                peer["deviceName"] = sender_name
                peer["platform"] = header.get("senderPlatform", peer.get("platform", "android"))
                peer["ipAddress"] = remote_ip
                peer["udpInputPort"] = header.get("senderUdpPort", peer.get("udpInputPort", FAST_INPUT_UDP_PORT))
                peer["tcpControlPort"] = header.get("senderTcpPort", peer.get("tcpControlPort", DATA_CONTROL_TCP_PORT))
                peer["monitors"] = header.get("senderMonitors", peer.get("monitors", []))
                peer["in_ok"] = True
                peer["out_ok"] = True
                peer["isMutuallyPaired"] = True
                self.suppressed_autoconnect.discard(sender_id)

                req_trust = bool(header.get("requestTrust", False))
                trust_token = header.get("trustToken", "")
                if req_trust and trust_token:
                    self.trusted_devices[sender_id] = {
                        "deviceId": sender_id,
                        "deviceName": sender_name,
                        "platform": peer.get("platform", "android"),
                        "trustToken": trust_token,
                        "autoConnect": True
                    }
                    self._save_trusted_devices()
                    peer["is_trusted"] = True
                    self.log(f"[Güvenlik] ⭐ '{sender_name}' güvenilir cihaz olarak kaydedildi.")

                ack = {
                    "type": "PAIR_ACCEPT_ACK",
                    "senderId": self.device_id,
                    "senderName": self.device_name,
                    "senderPlatform": "linux-nobara",
                    "senderUdpPort": FAST_INPUT_UDP_PORT,
                    "senderTcpPort": DATA_CONTROL_TCP_PORT,
                    "senderScreenWidth": self.topology.total_width,
                    "senderScreenHeight": self.topology.total_height,
                    "senderMonitors": [asdict(m) for m in self.topology.monitors],
                    "isMutualComplete": True
                }
                self._send_tcp_frame(conn, ack)
                self.log(f"[Çift Taraflı Eşleşme] ✅ '{sender_name}' ({remote_ip}) bağlantı isteğinizi tek tıkla kabul etti! Karşılıklı bağlantı aktif.")
                self.on_peer_mutually_paired(peer)
                if self.on_peer_updated:
                    try:
                        self.on_peer_updated(peer)
                    except Exception:
                        pass

            elif msg_type == "PAIR_ACCEPT_ACK":
                sender_id = header.get("senderId", remote_ip)
                sender_name = header.get("senderName", remote_ip)
                peer = self.peers.get(sender_id)
                if not peer:
                    for p in self.peers.values():
                        if p.get("ipAddress") == remote_ip:
                            peer = p
                            break
                if peer:
                    peer["in_ok"] = True
                    peer["out_ok"] = True
                    peer["isMutuallyPaired"] = True
                    self.on_peer_mutually_paired(peer)
                    if self.on_peer_updated:
                        try:
                            self.on_peer_updated(peer)
                        except Exception:
                            pass
                self.log(f"[Bağlantı Onayı] ✅ '{sender_name}' ({remote_ip}) PAIR_ACCEPT_ACK teyit etti.")

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
                    peer["monitors"] = header.get("senderMonitors", peer.get("monitors", []))
                    self.on_peer_mutually_paired(peer)
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

            elif msg_type == "EDGE_RETURN":
                edge = header.get("returnEdge", 0)
                norm_pos = float(header.get("normalizedPosition", 0.5))
                self.log(f"[Kenar Dönüşü] '{header.get('senderName')}' ({remote_ip}) TCP sinyaliyle yerel masaüstüne dönüş yaptı.")
                if self.controlling_remote_peer is not None:
                    self.return_host_control_to_local(notify_peer=False, return_edge=edge, norm_pos=norm_pos)
                else:
                    self.return_control_to_local(edge, norm_pos)

            elif msg_type == "DISCONNECT":
                sender_id = header.get("senderId", remote_ip)
                sender_name = header.get("senderName", remote_ip)
                self.suppressed_autoconnect.add(sender_id)
                peer = self.peers.get(sender_id)
                if not peer:
                    for p in self.peers.values():
                        if p.get("ipAddress") == remote_ip:
                            peer = p
                            break
                if peer:
                    peer["in_ok"] = False
                    peer["out_ok"] = False
                    peer["isMutuallyPaired"] = False
                    if self.active_remote_peer and (self.active_remote_peer.get("deviceId") == peer.get("deviceId") or self.active_remote_peer.get("ipAddress") == remote_ip):
                        self.return_control_to_local(0, 0.5)
                    self.remote_audio_sinks.pop(sender_id, None)
                    if self.active_audio_route and self.active_audio_route.get("targetDeviceId") == sender_id:
                        self.set_and_broadcast_audio_route(None)
                    if self.on_audio_sinks_updated:
                        try:
                            self.on_audio_sinks_updated()
                        except Exception:
                            pass
                    if self.on_peer_updated:
                        try:
                            self.on_peer_updated(peer)
                        except Exception:
                            pass
                self.log(f"[Bağlantı] 🔌 '{sender_name}' ({remote_ip}) bağlantıyı sonlandırdı.")

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

            elif msg_type == "AUDIO_SINKS_ANNOUNCE":
                sender_id = header.get("senderId", remote_ip)
                sender_name = header.get("senderName", remote_ip)
                sinks = header.get("audioSinks", [])
                if isinstance(sinks, list):
                    self.remote_audio_sinks[sender_id] = sinks
                    self.log(f"[Ses Matrisi] 🔍 '{sender_name}' ({len(sinks)} ses çıkış aygıtı) duyuruldu.")
                    if self.on_audio_sinks_updated:
                        try:
                            self.on_audio_sinks_updated()
                        except Exception:
                            pass

            elif msg_type == "AUDIO_ROUTE_CHANGE":
                sender_name = header.get("senderName", remote_ip)
                route = header.get("audioRoute")
                self.active_audio_route = route
                self._apply_audio_route(route)
                if self.on_audio_route_changed:
                    try:
                        self.on_audio_route_changed(route)
                    except Exception:
                        pass

    def _udp_input_loop(self) -> None:
        try:
            self.udp_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            self.udp_sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            if hasattr(socket, "SO_REUSEPORT"):
                try:
                    self.udp_sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEPORT, 1)
                except OSError:
                    pass
            self.udp_sock.bind(("0.0.0.0", FAST_INPUT_UDP_PORT))
        except OSError as ex:
            self.log(f"[UDP Girdi Hatası] Port {FAST_INPUT_UDP_PORT} dinlenemedi: {ex}")
            return
        min_x, min_y, vw, vh = self.topology.virtual_desktop_bounds()

        while self.running:
            try:
                data, addr = self.udp_sock.recvfrom(2048)
                if len(data) < 4 or data[0] != MAGIC_0 or data[1] != MAGIC_1 or data[2] != PROTO_VER:
                    continue

                pkt_type = data[3]
                if pkt_type == PACKET_HEARTBEAT_PING and len(data) >= 12:
                    pong = bytearray(data[:12])
                    pong[3] = PACKET_HEARTBEAT_PONG
                    self.udp_sock.sendto(bytes(pong), addr)

                elif pkt_type == PACKET_EDGE_HANDOFF and len(data) >= 10:
                    edge, dragging, norm_pos = struct.unpack("<BBf", data[4:10])
                    if self.controlling_remote_peer is not None and (self.controlling_remote_peer.get("ipAddress") == addr[0] or self.controlling_remote_peer.get("deviceId") == addr[0]):
                        self.log(f"[Kenar Dönüşü] '{self.controlling_remote_peer.get('deviceName')}' imleci Linux'a geri bıraktı.")
                        self.return_host_control_to_local(notify_peer=False, return_edge=edge, norm_pos=norm_pos)
                        continue

                    min_x, min_y, vw, vh = self.topology.virtual_desktop_bounds()

                    peer = None
                    for p in self.peers.values():
                        if p.get("ipAddress") == addr[0]:
                            peer = p
                            break
                    if not peer:
                        peer = {"deviceId": addr[0], "deviceName": addr[0], "ipAddress": addr[0], "udpInputPort": FAST_INPUT_UDP_PORT, "tcpControlPort": DATA_CONTROL_TCP_PORT}

                    self.active_remote_peer = peer
                    self.entry_edge = edge

                    # Calculate entrance coordinate on Linux (edge is targetEntranceEdge)
                    if edge == EDGE_LEFT:
                        # Cursor entered through Linux's LEFT edge
                        self.cursor_x = min_x + 18
                        self.cursor_y = min_y + int(vh * norm_pos)
                    elif edge == EDGE_RIGHT:
                        # Cursor entered through Linux's RIGHT edge
                        self.cursor_x = min_x + vw - 18
                        self.cursor_y = min_y + int(vh * norm_pos)
                    elif edge == EDGE_TOP:
                        # Cursor entered through Linux's TOP edge
                        self.cursor_x = min_x + int(vw * norm_pos)
                        self.cursor_y = min_y + 18
                    elif edge == EDGE_BOTTOM:
                        # Cursor entered through Linux's BOTTOM edge
                        self.cursor_x = min_x + int(vw * norm_pos)
                        self.cursor_y = min_y + vh - 18
                    else:
                        self.cursor_x = min_x + int(vw * 0.5)
                        self.cursor_y = min_y + int(vh * 0.5)

                    self.return_edge_push_accum = 0.0

                    self.log(f"[Kenar Geçişi] ➡️ İmleç '{peer.get('deviceName')}' ekranından Linux'a geçti ({int(self.cursor_x)}, {int(self.cursor_y)}).")
                    # Nudge hardware cursor to wake up and display pointer immediately on Wayland
                    self.injector.move_relative(1, 0)
                    self.injector.move_relative(-1, 0)
                    if self.on_cursor_update:
                        try:
                            self.on_cursor_update(True, int(self.cursor_x), int(self.cursor_y))
                        except Exception:
                            pass

                elif pkt_type == PACKET_MOUSE_MOVE and len(data) >= 10:
                    seq, dx, dy = struct.unpack("<Hhh", data[4:10])
                    self.injector.move_relative(dx, dy)

                    min_x, min_y, vw, vh = self.topology.virtual_desktop_bounds()
                    self.cursor_x = max(min_x, min(min_x + vw, self.cursor_x + dx))
                    self.cursor_y = max(min_y, min(min_y + vh, self.cursor_y + dy))

                    if self.on_cursor_update:
                        try:
                            self.on_cursor_update(True, int(self.cursor_x), int(self.cursor_y))
                        except Exception:
                            pass

                    # Check if cursor hits boundary to return to Windows/remote with push resistance
                    if self.active_remote_peer is not None:
                        return_triggered = False
                        ret_edge = EDGE_NONE
                        ret_norm = 0.5
                        RESISTANCE_THRESHOLD = 24.0

                        # If cursor entered through LEFT edge, pushing against LEFT edge returns to remote's RIGHT edge
                        if self.entry_edge == EDGE_LEFT:
                            if self.cursor_x <= min_x and dx < 0:
                                self.return_edge_push_accum += abs(dx)
                                if self.return_edge_push_accum >= RESISTANCE_THRESHOLD:
                                    return_triggered = True
                                    ret_edge = EDGE_RIGHT
                                    ret_norm = max(0.0, min(1.0, (self.cursor_y - min_y) / float(max(1, vh))))
                            elif dx > 3:
                                self.return_edge_push_accum = 0.0

                        # If cursor entered through RIGHT edge, pushing against RIGHT edge returns to remote's LEFT edge
                        elif self.entry_edge == EDGE_RIGHT:
                            if self.cursor_x >= min_x + vw and dx > 0:
                                self.return_edge_push_accum += abs(dx)
                                if self.return_edge_push_accum >= RESISTANCE_THRESHOLD:
                                    return_triggered = True
                                    ret_edge = EDGE_LEFT
                                    ret_norm = max(0.0, min(1.0, (self.cursor_y - min_y) / float(max(1, vh))))
                            elif dx < -3:
                                self.return_edge_push_accum = 0.0

                        # If cursor entered through TOP edge, pushing against TOP edge returns to remote's BOTTOM edge
                        elif self.entry_edge == EDGE_TOP:
                            if self.cursor_y <= min_y and dy < 0:
                                self.return_edge_push_accum += abs(dy)
                                if self.return_edge_push_accum >= RESISTANCE_THRESHOLD:
                                    return_triggered = True
                                    ret_edge = EDGE_BOTTOM
                                    ret_norm = max(0.0, min(1.0, (self.cursor_x - min_x) / float(max(1, vw))))
                            elif dy > 3:
                                self.return_edge_push_accum = 0.0

                        # If cursor entered through BOTTOM edge, pushing against BOTTOM edge returns to remote's TOP edge
                        elif self.entry_edge == EDGE_BOTTOM:
                            if self.cursor_y >= min_y + vh and dy > 0:
                                self.return_edge_push_accum += abs(dy)
                                if self.return_edge_push_accum >= RESISTANCE_THRESHOLD:
                                    return_triggered = True
                                    ret_edge = EDGE_TOP
                                    ret_norm = max(0.0, min(1.0, (self.cursor_x - min_x) / float(max(1, vw))))
                            elif dy < -3:
                                self.return_edge_push_accum = 0.0

                        if return_triggered:
                            target = self.active_remote_peer
                            self.active_remote_peer = None
                            self.return_edge_push_accum = 0.0
                            if self.on_cursor_update:
                                try:
                                    self.on_cursor_update(False, 0, 0)
                                except Exception:
                                    pass
                            threading.Thread(target=self.return_control_to_peer, args=(target, ret_edge, ret_norm), daemon=True).start()

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
            except Exception:
                pass

    def return_control_to_peer(self, peer: dict, return_edge: int, norm_pos: float) -> None:
        """Sends framed TCP EDGE_RETURN and UDP EDGE_HANDOFF packet back to remote device."""
        norm_pos = max(0.0, min(1.0, norm_pos))
        peer_ip = peer.get("ipAddress")
        peer_name = peer.get("deviceName", peer_ip)
        if not peer_ip:
            return

        # 1. Fast UDP packet
        try:
            u_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            pkt = struct.pack("<BBBBBBf", MAGIC_0, MAGIC_1, PROTO_VER, PACKET_EDGE_HANDOFF, return_edge, 0, norm_pos)
            u_sock.sendto(pkt, (peer_ip, peer.get("udpInputPort", FAST_INPUT_UDP_PORT)))
            u_sock.close()
        except Exception:
            pass

        # 2. Reliable framed TCP packet
        try:
            t_sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            t_sock.settimeout(2.0)
            t_sock.connect((peer_ip, peer.get("tcpControlPort", DATA_CONTROL_TCP_PORT)))
            hdr = {
                "type": "EDGE_RETURN",
                "senderId": self.device_id,
                "senderName": self.device_name,
                "senderUdpPort": FAST_INPUT_UDP_PORT,
                "returnEdge": return_edge,
                "normalizedPosition": norm_pos,
            }
            self._send_tcp_frame(t_sock, hdr)
            t_sock.close()
        except Exception:
            pass

        self.log(f"[Kenar Geçişi] ⬅️ İmleç ve klavye '{peer_name}' ekranına geri döndü (Kenar: {return_edge}, %{int(norm_pos * 100)}).")

    def return_control_to_local(self, edge: int, norm_pos: float) -> None:
        """Called when remote releases cursor back to Linux."""
        self.active_remote_peer = None
        if self.on_cursor_update:
            try:
                self.on_cursor_update(False, 0, 0)
            except Exception:
                pass
        self.log(f"[Kenar Dönüşü] Yerel Linux masaüstüne dönüş yapıldı (Kenar: {edge}, %{int(norm_pos * 100)}).")

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

    def broadcast_tcp_control(self, hdr: Dict[str, Any]) -> None:
        """Broadcasts a framed TCP control header to all mutually paired peers."""
        targets = [p for p in self.peers.values() if p.get("isMutuallyPaired")]
        jb = json.dumps(hdr).encode("utf-8")
        prefix = struct.pack("<iq", len(jb), 0)
        for peer in targets:
            try:
                s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
                s.settimeout(2.5)
                s.connect((peer["ipAddress"], peer.get("tcpControlPort", DATA_CONTROL_TCP_PORT)))
                s.sendall(prefix + jb)
                s.close()
            except Exception:
                pass

    def send_tcp_control_to_peer(self, peer: Dict[str, Any], hdr: Dict[str, Any]) -> None:
        """Sends a framed TCP control header to a specific peer."""
        try:
            jb = json.dumps(hdr).encode("utf-8")
            prefix = struct.pack("<iq", len(jb), 0)
            s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            s.settimeout(2.5)
            s.connect((peer["ipAddress"], peer.get("tcpControlPort", DATA_CONTROL_TCP_PORT)))
            s.sendall(prefix + jb)
            s.close()
        except Exception:
            pass

    def enumerate_local_audio_sinks(self) -> List[Dict[str, Any]]:
        """Queries PipeWire / PulseAudio to discover all physical and virtual audio output devices."""
        sinks: List[Dict[str, Any]] = [
            {
                "deviceId": self.device_id,
                "deviceName": self.device_name,
                "platform": "linux",
                "sinkId": "default",
                "sinkName": "Varsayılan Linux Ses Çıkışı",
                "isDefault": True
            }
        ]

        if shutil.which("pactl"):
            try:
                res = subprocess.run(["pactl", "-f", "json", "list", "sinks"], capture_output=True, text=True, timeout=2.0)
                if res.returncode == 0 and res.stdout.strip():
                    data = json.loads(res.stdout)
                    if isinstance(data, list):
                        for s in data:
                            name = s.get("name", "")
                            desc = s.get("description", "")
                            if not desc and "properties" in s:
                                desc = s["properties"].get("device.description", s["properties"].get("node.description", name))
                            if not desc:
                                desc = name
                            if name:
                                sinks.append({
                                    "deviceId": self.device_id,
                                    "deviceName": self.device_name,
                                    "platform": "linux",
                                    "sinkId": name,
                                    "sinkName": desc.strip(),
                                    "isDefault": False
                                })
            except Exception:
                pass

            if len(sinks) == 1:
                try:
                    res = subprocess.run(["pactl", "list", "sinks", "short"], capture_output=True, text=True, timeout=2.0)
                    if res.returncode == 0 and res.stdout.strip():
                        for line in res.stdout.strip().splitlines():
                            parts = line.split()
                            if len(parts) >= 2:
                                s_id = parts[1]
                                sinks.append({
                                    "deviceId": self.device_id,
                                    "deviceName": self.device_name,
                                    "platform": "linux",
                                    "sinkId": s_id,
                                    "sinkName": s_id,
                                    "isDefault": False
                                })
                except Exception:
                    pass

        return sinks

    def broadcast_local_audio_sinks(self) -> None:
        """Broadcasts this machine's audio output devices to all paired peers."""
        if not self.local_audio_sinks:
            self.local_audio_sinks = self.enumerate_local_audio_sinks()
        hdr = {
            "type": "AUDIO_SINKS_ANNOUNCE",
            "senderId": self.device_id,
            "senderName": self.device_name,
            "senderPlatform": "linux",
            "audioSinks": self.local_audio_sinks
        }
        self.broadcast_tcp_control(hdr)

    def set_and_broadcast_audio_route(self, route: Optional[Dict[str, Any]]) -> None:
        """Sets the ecosystem-wide active audio route and broadcasts to all peers."""
        self.active_audio_route = route
        hdr = {
            "type": "AUDIO_ROUTE_CHANGE",
            "senderId": self.device_id,
            "senderName": self.device_name,
            "senderPlatform": "linux",
            "audioRoute": route
        }
        self.broadcast_tcp_control(hdr)
        self._apply_audio_route(route)

    def _apply_audio_route(self, route: Optional[Dict[str, Any]]) -> None:
        """Applies an audio route locally: either becomes a transmitter, receiver, or idles."""
        my_ip = self.get_local_ip()
        if not route or route.get("targetDeviceId") in ("NONE", None, ""):
            if self.audio_streaming:
                self.stop_audio_streaming()
            self.audio_rx_target_sink = None
            self.log("[Ses Matrisi] ⚪ Ses yönlendirme devre dışı. Her cihaz kendi yerel sesini çalıyor.")
        elif route.get("targetDeviceId") == self.device_id or (route.get("targetIp") and route.get("targetIp") == my_ip):
            # This Linux PC is the selected audio sink
            if self.audio_streaming:
                self.stop_audio_streaming()
            sink_id = route.get("targetSinkId")
            self.audio_rx_target_sink = None if sink_id == "default" else sink_id
            self.log(f"[Ses Matrisi] 🟢 Aktif Ses Alıcısı: {route.get('targetSinkName', 'Varsayılan')} (Diğer cihazların sesleri buraya yönlendiriliyor).")
        else:
            # Remote machine is the selected audio sink
            target_ip = route.get("targetIp")
            target_dev_id = route.get("targetDeviceId")
            if not target_ip or target_ip in ("127.0.0.1", "0.0.0.0", my_ip):
                peer = self.peers.get(target_dev_id)
                if not peer:
                    for p in self.peers.values():
                        if p.get("deviceId") == target_dev_id:
                            peer = p
                            break
                if peer:
                    target_ip = peer.get("ipAddress")
            if target_ip and target_ip != my_ip:
                self.audio_rx_target_sink = None
                self.start_audio_streaming(target_ip)
                self.log(f"[Ses Matrisi] 📡 Ses Akışı Başlatıldı -> {route.get('targetDeviceName')} ({target_ip}:{AUDIO_STREAM_UDP_PORT})")

    def on_peer_mutually_paired(self, peer: Dict[str, Any]) -> None:
        """Invoked when a peer completes mutual pairing; exchanges audio sinks and current route."""
        if not self.local_audio_sinks:
            self.local_audio_sinks = self.enumerate_local_audio_sinks()
        if self.local_audio_sinks:
            hdr_sinks = {
                "type": "AUDIO_SINKS_ANNOUNCE",
                "senderId": self.device_id,
                "senderName": self.device_name,
                "senderPlatform": "linux",
                "audioSinks": self.local_audio_sinks
            }
            self.send_tcp_control_to_peer(peer, hdr_sinks)
        if self.active_audio_route:
            hdr_route = {
                "type": "AUDIO_ROUTE_CHANGE",
                "senderId": self.device_id,
                "senderName": self.device_name,
                "senderPlatform": "linux",
                "audioRoute": self.active_audio_route
            }
            self.send_tcp_control_to_peer(peer, hdr_route)

    def start_audio_streaming(self, target_ip: str) -> bool:
        """Starts real-time PipeWire / PulseAudio recording and streams 48kHz S16LE stereo PCM to target Windows PC."""
        if self.audio_streaming:
            self.stop_audio_streaming()

        rec_cmd = None
        def_sink = ""
        if shutil.which("pactl"):
            try:
                res = subprocess.run(["pactl", "get-default-sink"], capture_output=True, text=True, timeout=1.0)
                if res.returncode == 0:
                    def_sink = res.stdout.strip()
            except Exception:
                pass

        monitor_device = f"{def_sink}.monitor" if def_sink else "@DEFAULT_MONITOR@"

        if shutil.which("parec"):
            rec_cmd = ["parec", "-d", monitor_device, "--format=s16le", "--rate=48000", "--channels=2"]
        elif shutil.which("pw-record"):
            if def_sink:
                rec_cmd = ["pw-record", "--raw", f"--target={def_sink}.monitor", "--format=s16", "--rate=48000", "--channels=2", "-"]
            else:
                rec_cmd = ["pw-record", "--raw", "--format=s16", "--rate=48000", "--channels=2", "-"]
        elif shutil.which("ffmpeg"):
            rec_cmd = ["ffmpeg", "-loglevel", "quiet", "-f", "pulse", "-i", "default", "-f", "s16le", "-ar", "48000", "-ac", "2", "-"]

        if not rec_cmd:
            self.log("[Ses Hatası] ❌ pw-record veya parec bulunamadı (PipeWire/PulseAudio gereklidir).")
            return False

        try:
            self.audio_proc = subprocess.Popen(
                rec_cmd,
                stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL,
                bufsize=3840,
            )
            time.sleep(0.05)
            if self.audio_proc.poll() is not None:
                if shutil.which("parec"):
                    self.audio_proc = subprocess.Popen(
                        ["parec", "--format=s16le", "--rate=48000", "--channels=2"],
                        stdout=subprocess.PIPE,
                        stderr=subprocess.DEVNULL,
                        bufsize=3840,
                    )
                elif shutil.which("pw-record"):
                    self.audio_proc = subprocess.Popen(
                        ["pw-record", "--raw", "--format=s16", "--rate=48000", "--channels=2", "-"],
                        stdout=subprocess.PIPE,
                        stderr=subprocess.DEVNULL,
                        bufsize=3840,
                    )
            self.audio_streaming = True
            self.audio_target_ip = target_ip
            self.audio_seq = 0
            self.audio_thread = threading.Thread(
                target=self._audio_streaming_worker, args=(target_ip,), daemon=True
            )
            self.audio_thread.start()
            self.log(f"[Ses Akışı] 🎧 Ses yakalama başlatıldı -> {target_ip}:{AUDIO_STREAM_UDP_PORT} (48kHz Stereo S16LE)")
            return True
        except Exception as ex:
            self.log(f"[Ses Hatası] ❌ Ses akışı başlatılamadı: {ex}")
            return False

    def stop_audio_streaming(self) -> None:
        """Stops the audio recording subprocess and UDP transmission."""
        self.audio_streaming = False
        if self.audio_proc:
            try:
                self.audio_proc.terminate()
                self.audio_proc.wait(timeout=1.0)
            except Exception:
                pass
            self.audio_proc = None
        self.log("[Ses Akışı] ⏹️ Ses akışı durduruldu.")

    def _audio_streaming_worker(self, target_ip: str) -> None:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        chunk_size = 960  # 240 samples * 2 ch * 2 bytes = 5ms @ 48kHz (MTU-safe 974B UDP packet)
        while self.audio_streaming and self.audio_proc and self.audio_proc.stdout:
            try:
                raw = self.audio_proc.stdout.read(chunk_size)
                if not raw:
                    break
                self.audio_seq = (self.audio_seq + 1) & 0xFFFF
                # MAGIC_0, MAGIC_1, PROTO_VER, PACKET_AUDIO_CHUNK, channels(1B), sample_rate(4B), bits_per_sample(1B), seq(2B), pcm_len(2B)
                hdr = struct.pack("<BBBBBIBHH", MAGIC_0, MAGIC_1, PROTO_VER, PACKET_AUDIO_CHUNK, 2, 48000, 16, self.audio_seq, len(raw))
                sock.sendto(hdr + raw, (target_ip, AUDIO_STREAM_UDP_PORT))
            except Exception:
                break
        sock.close()
        self.audio_streaming = False

    def _audio_receiver_loop(self) -> None:
        """Listens on UDP port 42852 for incoming PCM audio chunks (e.g. from Android/Windows) and plays them through Linux speakers/headphones."""
        try:
            sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            if hasattr(socket, "SO_REUSEPORT"):
                try:
                    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEPORT, 1)
                except OSError:
                    pass
            sock.bind(("0.0.0.0", AUDIO_STREAM_UDP_PORT))
            self.audio_rx_sock = sock
            self.log(f"[Ses Alıcısı] 🎧 UDP {AUDIO_STREAM_UDP_PORT} portu dinleniyor (Merkezi Kulaklık/Hoparlör Modu).")
        except OSError as ex:
            self.log(f"[Ses Alıcısı Hatası] UDP {AUDIO_STREAM_UDP_PORT} dinlenemedi: {ex}")
            return

        last_packet_time = 0.0
        active_playback_proc: Optional[subprocess.Popen] = None
        current_playback_sr = 48000
        current_playback_ch = 2

        while self.running:
            try:
                sock.settimeout(1.0)
                try:
                    data, addr = sock.recvfrom(8192)
                except socket.timeout:
                    if active_playback_proc and (time.time() - last_packet_time > 2.5):
                        try:
                            if active_playback_proc.stdin:
                                active_playback_proc.stdin.close()
                            active_playback_proc.terminate()
                            active_playback_proc.wait(timeout=0.5)
                        except Exception:
                            pass
                        active_playback_proc = None
                        self.audio_rx_active = False
                    continue

                if len(data) < 14 or data[0] != MAGIC_0 or data[1] != MAGIC_1 or data[2] != PROTO_VER:
                    continue
                if data[3] != PACKET_AUDIO_CHUNK:
                    continue

                ch, sr, bits, seq, payload_len = struct.unpack("<BIBHH", data[4:14])
                pcm_data = data[14:14 + payload_len] if len(data) >= 14 + payload_len else data[14:]
                if not pcm_data:
                    continue

                now = time.time()
                last_packet_time = now
                self.audio_rx_bytes += len(pcm_data)
                self.audio_rx_last_time = now
                self.audio_rx_last_sender = addr[0]
                self.audio_rx_active = True

                if (
                    active_playback_proc is None
                    or active_playback_proc.poll() is not None
                    or current_playback_sr != sr
                    or current_playback_ch != ch
                ):
                    if active_playback_proc:
                        try:
                            if active_playback_proc.stdin:
                                active_playback_proc.stdin.close()
                            active_playback_proc.terminate()
                        except Exception:
                            pass

                    play_cmd = None
                    target_arg = []
                    if self.audio_rx_target_sink:
                        if shutil.which("pw-play"):
                            target_arg = [f"--target={self.audio_rx_target_sink}"]
                        elif shutil.which("paplay"):
                            target_arg = ["-d", self.audio_rx_target_sink]
                        elif shutil.which("aplay"):
                            target_arg = ["-D", self.audio_rx_target_sink]

                    if shutil.which("pw-play"):
                        play_cmd = ["pw-play", "--raw"] + target_arg + ["--format=s16", f"--rate={sr}", f"--channels={ch}", "-"]
                    elif shutil.which("paplay"):
                        play_cmd = ["paplay"] + target_arg + ["--raw", "--format=s16le", f"--rate={sr}", f"--channels={ch}", "-"]
                    elif shutil.which("aplay"):
                        play_cmd = ["aplay"] + target_arg + ["-t", "raw", "-f", "S16_LE", "-r", str(sr), "-c", str(ch), "-q", "-"]

                    if play_cmd:
                        try:
                            active_playback_proc = subprocess.Popen(
                                play_cmd,
                                stdin=subprocess.PIPE,
                                stdout=subprocess.DEVNULL,
                                stderr=subprocess.DEVNULL,
                                bufsize=0,
                            )
                            current_playback_sr = sr
                            current_playback_ch = ch
                            self.audio_rx_last_player = play_cmd[0]
                            self.log(f"[Ses Çalma] 🔊 Gelen ses akışı çalınıyor ({play_cmd[0]} - {sr}Hz {ch} kanal S16LE, Kaynak: {addr[0]}).")
                        except Exception as p_err:
                            self.log(f"[Ses Çalma Hatası] Oynatma süreci başlatılamadı: {p_err}")
                            active_playback_proc = None

                if active_playback_proc and active_playback_proc.stdin:
                    try:
                        active_playback_proc.stdin.write(pcm_data)
                        active_playback_proc.stdin.flush()
                    except BrokenPipeError:
                        active_playback_proc = None
                    except Exception:
                        pass
            except Exception:
                pass

        if active_playback_proc:
            try:
                if active_playback_proc.stdin:
                    active_playback_proc.stdin.close()
                active_playback_proc.terminate()
            except Exception:
                pass

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
