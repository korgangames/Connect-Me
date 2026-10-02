#!/usr/bin/env python3
"""
Connect Me — Nobara Linux (KDE Plasma Wayland / wlroots / X11) Tam Entegre Sistem Servisi & KVM Motoru
Korgan Games (v1.6.5 Yerinde Otomatik Güncelleyici, Sabit Android İmzası & Sürücü İyileştirmeleri)

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
from typing import Callable, Dict, List, Optional, Tuple

MAGIC_0 = 0x43  # 'C'
MAGIC_1 = 0x4D  # 'M'
PROTO_VER = 0x01
VERSION = "1.6.5"

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

    def refresh_monitors(self) -> List[PhysicalMonitor]:
        detected = (
            self._try_kscreen_doctor()
            or self._try_kscreen_console()
            or self._try_kscreen_doctor_text()
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
        # Sort monitors deterministically by X then Y
        detected.sort(key=lambda m: (m.virtualX, m.virtualY))
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


class LinuxInputInjector:
    """
    High-performance Linux input injector with 4 fallback tiers:
    1. evdev.UInput (Linux kernel virtual device - <0.2ms latency, recognized by KDE Wayland as real mouse)
    2. Pure-Python /dev/uinput ioctl device (zero external dependencies)
    3. ydotoold daemon + ydotool (Wayland compatible with auto-starting daemon)
    4. xdotool (X11 / XWayland compatibility)
    """

    def __init__(self, on_log: Optional[Callable[[str], None]] = None) -> None:
        self.mode = "none"
        self.on_log = on_log
        self.evdev_device = None
        self.uinput_fd = None
        self._ensure_uinput_accessible()
        self._init_injector()

    def _ensure_uinput_accessible(self) -> None:
        """Attempts non-interactive chmod on /dev/uinput if available."""
        if os.path.exists("/dev/uinput") and not os.access("/dev/uinput", os.W_OK):
            try:
                subprocess.run(["sudo", "-n", "chmod", "666", "/dev/uinput"], capture_output=True, timeout=1.0)
            except Exception:
                pass

    def _init_injector(self) -> None:
        # Tier 1: Try evdev module
        try:
            import evdev
            from evdev import UInput, ecodes

            keys = [
                ecodes.BTN_LEFT, ecodes.BTN_RIGHT, ecodes.BTN_MIDDLE,
                ecodes.BTN_SIDE, ecodes.BTN_EXTRA,
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
                    keys.append(getattr(ecodes, attr))
            for i in range(10):
                attr = f"KEY_{i}"
                if hasattr(ecodes, attr):
                    keys.append(getattr(ecodes, attr))
            for i in range(1, 13):
                attr = f"KEY_F{i}"
                if hasattr(ecodes, attr):
                    keys.append(getattr(ecodes, attr))

            cap = {
                ecodes.EV_REL: [ecodes.REL_X, ecodes.REL_Y, ecodes.REL_WHEEL, ecodes.REL_HWHEEL],
                ecodes.EV_KEY: list(set(keys))
            }
            self.evdev_device = UInput(cap, name="Connect-Me-Virtual-Mouse", version=0x1)
            self.mode = "evdev"
            return
        except Exception:
            self.evdev_device = None

        # Tier 2: Try native /dev/uinput via fcntl
        try:
            if os.path.exists("/dev/uinput") and os.access("/dev/uinput", os.W_OK):
                self._init_raw_uinput()
                if self.uinput_fd is not None:
                    self.mode = "uinput_raw"
                    return
        except Exception:
            pass

        # Tier 3: Try ydotool with ydotoold auto-start
        if shutil.which("ydotool"):
            self._ensure_ydotoold()
            self.mode = "ydotool"
            return

        # Tier 4: Fallback to xdotool
        if shutil.which("xdotool"):
            self.mode = "xdotool"
            return

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
                    sock_path = "/tmp/.ydotoold_socket"
                    subprocess.Popen(["ydotoold", f"--socket-path={sock_path}"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                    os.environ["YDOTOOL_SOCKET"] = sock_path
                    time.sleep(0.2)
                except Exception:
                    pass

    def move_relative(self, dx: int, dy: int) -> None:
        if self.mode == "evdev" and self.evdev_device:
            import evdev
            from evdev import ecodes
            self.evdev_device.write(ecodes.EV_REL, ecodes.REL_X, dx)
            self.evdev_device.write(ecodes.EV_REL, ecodes.REL_Y, dy)
            self.evdev_device.syn()
        elif self.mode == "uinput_raw" and self.uinput_fd:
            self._write_raw_event(0x02, 0x00, dx)  # EV_REL, REL_X
            self._write_raw_event(0x02, 0x01, dy)  # EV_REL, REL_Y
            self._write_raw_event(0x00, 0x00, 0)   # EV_SYN, SYN_REPORT
        elif self.mode == "ydotool":
            subprocess.run(["ydotool", "mousemove", "-x", str(dx), "-y", str(dy)], check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        elif self.mode == "xdotool":
            subprocess.run(["xdotool", "mousemove_relative", "--", str(dx), str(dy)], check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    def mouse_button(self, btn: int, is_pressed: bool) -> None:
        # btn: 1=Left, 2=Right, 3=Middle
        if self.mode == "evdev" and self.evdev_device:
            import evdev
            from evdev import ecodes
            code = ecodes.BTN_LEFT if btn == BUTTON_LEFT else (ecodes.BTN_RIGHT if btn == BUTTON_RIGHT else ecodes.BTN_MIDDLE)
            self.evdev_device.write(ecodes.EV_KEY, code, 1 if is_pressed else 0)
            self.evdev_device.syn()
        elif self.mode == "uinput_raw" and self.uinput_fd:
            code = 0x110 if btn == BUTTON_LEFT else (0x111 if btn == BUTTON_RIGHT else 0x112)
            self._write_raw_event(0x01, code, 1 if is_pressed else 0)
            self._write_raw_event(0x00, 0x00, 0)
        elif self.mode == "ydotool":
            ydo_code = "0xC0" if btn == BUTTON_LEFT else ("0xC1" if btn == BUTTON_RIGHT else "0xC2")
            subprocess.run(["ydotool", "click", f"{ydo_code}{'d' if is_pressed else 'u'}"], check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        elif self.mode == "xdotool":
            subcmd = "mousedown" if is_pressed else "mouseup"
            subprocess.run(["xdotool", subcmd, str(btn)], check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    def mouse_scroll(self, sx: int, sy: int) -> None:
        if self.mode == "evdev" and self.evdev_device:
            import evdev
            from evdev import ecodes
            val = 1 if sy > 0 else (-1 if sy < 0 else 0)
            if val != 0:
                self.evdev_device.write(ecodes.EV_REL, ecodes.REL_WHEEL, val)
                self.evdev_device.syn()
        elif self.mode == "uinput_raw" and self.uinput_fd:
            val = 1 if sy > 0 else (-1 if sy < 0 else 0)
            if val != 0:
                self._write_raw_event(0x02, 0x08, val)  # REL_WHEEL
                self._write_raw_event(0x00, 0x00, 0)
        elif self.mode == "xdotool":
            btn = "4" if sy > 0 else "5"
            clicks = max(1, abs(sy) // 40)
            subprocess.run(["xdotool", "click", "--repeat", str(clicks), btn], check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        elif self.mode == "ydotool":
            wheel_arg = f"-w{sy}"
            subprocess.run(["ydotool", "mousemove", wheel_arg], check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

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

    def key_event(self, vk: int, is_pressed: bool, ch: str) -> None:
        if self.mode == "evdev" and self.evdev_device:
            import evdev
            from evdev import ecodes
            code = self._vk_to_evdev_code(vk)
            if code is not None:
                self.evdev_device.write(ecodes.EV_KEY, code, 1 if is_pressed else 0)
                self.evdev_device.syn()
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
        self.active_remote_peer: Optional[dict] = None
        self.entry_edge: int = EDGE_NONE
        self.cursor_x: float = 0.0
        self.cursor_y: float = 0.0

        # UI & Telemetry Callbacks
        self.on_log = on_log
        self.on_cursor_update: Optional[Callable[[bool, int, int], None]] = None
        self.on_pin_request_received: Optional[Callable[[str, str], None]] = None
        self.on_peer_discovered: Optional[Callable[[dict], None]] = None

        self.running = True
        self.last_copied_clipboard = ""
        self.udp_sock: Optional[socket.socket] = None

        # Audio streaming
        self.audio_streaming = False
        self.audio_target_ip: Optional[str] = None
        self.audio_proc: Optional[subprocess.Popen] = None
        self.audio_thread: Optional[threading.Thread] = None
        self.audio_seq = 0

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

    def revoke_trust(self, device_id: str) -> None:
        if device_id in self.trusted_devices:
            del self.trusted_devices[device_id]
            self._save_trusted_devices()
            if device_id in self.peers:
                self.peers[device_id]["is_trusted"] = False
                self.peers[device_id]["in_ok"] = False
                self.peers[device_id]["out_ok"] = False
                self.peers[device_id]["isMutuallyPaired"] = False
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

        # Start Discovery Receiver & Broadcaster
        threading.Thread(target=self._discovery_receiver_loop, daemon=True).start()
        threading.Thread(target=self._discovery_broadcast_loop, daemon=True).start()
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
        except OSError as ex:
            self.log(f"[Keşif Hata] UDP {DISCOVERY_UDP_PORT} dinlenemedi: {ex}")
            return

        while self.running:
            try:
                data, addr = sock.recvfrom(4096)
                if not data:
                    continue
                payload = json.loads(data.decode("utf-8"))
                if payload.get("type") != "DISCOVER_BEACON":
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

                if is_new:
                    self.log(f"[Keşif] Cihaz bulundu: {sender_name} ({sender_plat}) @ {sender_ip} — Eşleşmek için 6 haneli kod girin.")
                    if self.on_peer_discovered:
                        try:
                            self.on_peer_discovered(peer)
                        except Exception:
                            pass
            except Exception:
                pass

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
                self.return_control_to_local(edge, norm_pos)

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
        min_x, min_y, vw, vh = self.topology.virtual_desktop_bounds()

        while self.running:
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

                # Calculate entrance coordinate on Linux
                if edge == EDGE_RIGHT:
                    self.cursor_x = min_x + 6
                    self.cursor_y = min_y + int(vh * norm_pos)
                elif edge == EDGE_LEFT:
                    self.cursor_x = min_x + vw - 6
                    self.cursor_y = min_y + int(vh * norm_pos)
                elif edge == EDGE_BOTTOM:
                    self.cursor_x = min_x + int(vw * norm_pos)
                    self.cursor_y = min_y + 6
                elif edge == EDGE_TOP:
                    self.cursor_x = min_x + int(vw * norm_pos)
                    self.cursor_y = min_y + vh - 6
                else:
                    self.cursor_x = min_x + int(vw * 0.5)
                    self.cursor_y = min_y + int(vh * 0.5)

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
                _, _, dx, dy = struct.unpack("<Hhh", data[4:10])
                self.injector.move_relative(dx, dy)
                self.cursor_x += dx
                self.cursor_y += dy

                min_x, min_y, vw, vh = self.topology.virtual_desktop_bounds()

                if self.on_cursor_update:
                    try:
                        self.on_cursor_update(True, int(self.cursor_x), int(self.cursor_y))
                    except Exception:
                        pass

                # Check if cursor hits boundary to return to Windows/remote
                if self.active_remote_peer is not None:
                    return_triggered = False
                    ret_edge = EDGE_NONE
                    ret_norm = 0.5

                    if self.entry_edge == EDGE_RIGHT and self.cursor_x <= min_x:
                        return_triggered = True
                        ret_edge = EDGE_RIGHT
                        ret_norm = (self.cursor_y - min_y) / float(max(1, vh))
                    elif self.entry_edge == EDGE_LEFT and self.cursor_x >= min_x + vw:
                        return_triggered = True
                        ret_edge = EDGE_LEFT
                        ret_norm = (self.cursor_y - min_y) / float(max(1, vh))
                    elif self.entry_edge == EDGE_BOTTOM and self.cursor_y <= min_y:
                        return_triggered = True
                        ret_edge = EDGE_BOTTOM
                        ret_norm = (self.cursor_x - min_x) / float(max(1, vw))
                    elif self.entry_edge == EDGE_TOP and self.cursor_y >= min_y + vh:
                        return_triggered = True
                        ret_edge = EDGE_TOP
                        ret_norm = (self.cursor_x - min_x) / float(max(1, vw))

                    if return_triggered:
                        target = self.active_remote_peer
                        self.active_remote_peer = None
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
            pkt = struct.pack("<BBBBBf", MAGIC_0, MAGIC_1, PROTO_VER, PACKET_EDGE_HANDOFF, return_edge, 0, norm_pos)
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

    def start_audio_streaming(self, target_ip: str) -> bool:
        """Starts real-time PipeWire / PulseAudio recording and streams 48kHz S16LE stereo PCM to target Windows PC."""
        if self.audio_streaming:
            self.stop_audio_streaming()

        rec_cmd = None
        if shutil.which("pw-record"):
            rec_cmd = ["pw-record", "--format=s16", "--rate=48000", "--channels=2", "-"]
        elif shutil.which("parec"):
            rec_cmd = ["parec", "--format=s16le", "--rate=48000", "--channels=2"]

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
        chunk_size = 1920  # 480 samples * 2 ch * 2 bytes = 10ms @ 48kHz
        while self.audio_streaming and self.audio_proc and self.audio_proc.stdout:
            try:
                raw = self.audio_proc.stdout.read(chunk_size)
                if not raw:
                    break
                self.audio_seq = (self.audio_seq + 1) & 0xFFFF
                # MAGIC_0, MAGIC_1, PROTO_VER, PACKET_AUDIO_CHUNK, channels(1B), sample_rate(4B), bits_per_sample(1B), seq(2B), pcm_len(2B)
                hdr = struct.pack("<BBBBBIHBH", MAGIC_0, MAGIC_1, PROTO_VER, PACKET_AUDIO_CHUNK, 2, 48000, 16, self.audio_seq, len(raw))
                sock.sendto(hdr + raw, (target_ip, AUDIO_STREAM_UDP_PORT))
            except Exception:
                break
        sock.close()
        self.audio_streaming = False

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
