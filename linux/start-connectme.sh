#!/usr/bin/env bash
# Connect Me — Linux Doğrudan GUI Başlatıcı
# Konsol veya terminal açmadan doğrudan grafik kontrol panelini başlatır.

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DIR"

LOG_DIR="$HOME/.config/connectme"
mkdir -p "$LOG_DIR"
LOG_FILE="$LOG_DIR/gui.log"

# 1. Eğer /dev/uinput veya /dev/input/event* henüz yazılabilir değilse sessiz izin vermeyi dene
sudo -n chmod 666 /dev/uinput /dev/input/event* 2>/dev/null || true

# 2. ydotoold kuruluysa ve çalışmıyorsa arka planda soket ile başlat
if command -v ydotoold >/dev/null 2>&1 && ! pgrep -x ydotoold >/dev/null 2>&1; then
    nohup ydotoold --socket-path=/tmp/.ydotool_socket >/dev/null 2>&1 &
    export YDOTOOL_SOCKET="/tmp/.ydotool_socket"
fi

# 3. Eğer eski askıda kalmış bağımsız daemon oturumu varsa sonlandır
pkill -f "python3.*connectme_linux_daemon.py" 2>/dev/null || true

# 4. Arka planda GUI'yi başlat ve çıktıları log dosyasına yaz
nohup python3 "$DIR/connectme_linux_gui.py" > "$LOG_FILE" 2>&1 &
