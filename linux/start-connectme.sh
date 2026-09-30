#!/usr/bin/env bash
# Connect Me — Linux Doğrudan GUI Başlatıcı
# Konsol veya terminal açmadan doğrudan grafik kontrol panelini başlatır.

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DIR"

# Arka planda konsol penceresi olmaksızın GUI'yi başlat
nohup python3 "$DIR/connectme_linux_gui.py" >/dev/null 2>&1 &
