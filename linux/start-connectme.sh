#!/usr/bin/env bash
# Connect Me — Linux Doğrudan GUI Başlatıcı
# Konsol veya terminal açmadan doğrudan grafik kontrol panelini başlatır.

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DIR"

LOG_DIR="$HOME/.config/connectme"
mkdir -p "$LOG_DIR"
LOG_FILE="$LOG_DIR/gui.log"

# Eğer eski askıda kalmış bağımsız daemon oturumu varsa sonlandır
pkill -f "python3.*connectme_linux_daemon.py" 2>/dev/null || true

# Arka planda GUI'yi başlat ve çıktıları log dosyasına yaz
nohup python3 "$DIR/connectme_linux_gui.py" > "$LOG_FILE" 2>&1 &
