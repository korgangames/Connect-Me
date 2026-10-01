#!/usr/bin/env bash
# Connect Me — Linux Sanal Fare & Klavye Sürücüsü (/dev/uinput) Kurulum Betiği
# Nobara / KDE Plasma / Fedora / Ubuntu / Arch üzerinde Wayland imlecinin serbestçe hareket etmesini sağlar.

set -e

echo "🔧 Connect Me /dev/uinput donanım sürücüsü izinleri ayarlanıyor..."

# 1. udev kuralı oluştur
RULE_FILE="/etc/udev/rules.d/99-connectme-uinput.rules"
echo 'KERNEL=="uinput", GROUP="input", MODE="0660", TAG+="uaccess", OPTIONS+="static_node=uinput"' | sudo tee "$RULE_FILE" >/dev/null
echo "✅ udev kuralı yazıldı: $RULE_FILE"

# 2. Kullanıcıyı 'input' grubuna ekle
sudo groupadd -f input
sudo usermod -aG input "$USER"
echo "✅ Kullanıcı '$USER' input grubuna eklendi."

# 3. Geçerli oturum için /dev/uinput izinlerini hemen ver
sudo chmod 666 /dev/uinput 2>/dev/null || true
sudo udevadm control --reload-rules
sudo udevadm trigger

echo ""
echo "🎉 Kurulum tamamlandı! Connect Me artık KDE Plasma Wayland üzerinde donanım seviyesinde sanal fare ve klavye kullanabilir."
