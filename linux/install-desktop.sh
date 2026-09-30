#!/usr/bin/env bash
# Connect Me — Linux Masaüstü ve Başlat Menüsü Kurucusu
# Connect Me uygulamasını Nobara / KDE Plasma / GNOME başlat menüsüne ekler.

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="$HOME/.local/share/applications"
mkdir -p "$APP_DIR"

DESKTOP_FILE="$APP_DIR/ConnectMe.desktop"

cat <<EOF > "$DESKTOP_FILE"
[Desktop Entry]
Version=1.0
Type=Application
Name=Connect Me
GenericName=Çapraz Cihaz KVM ve Pano Paylaşımı
Comment=Windows, Android ve Linux arasında tek mouse, klavye, pano ve dosya paylaşımı
Exec=python3 "$DIR/connectme_linux_gui.py"
Path=$DIR
Icon=preferences-desktop-display
Terminal=false
Categories=Utility;Network;HardwareSettings;
StartupNotify=true
Keywords=kvm;mouse;keyboard;clipboard;android;windows;nobara;kde;
EOF

chmod +x "$DESKTOP_FILE"
chmod +x "$DIR/start-connectme.sh"
chmod +x "$DIR/connectme_linux_gui.py"

echo "✅ Connect Me başarıyla başlat menünüze eklendi!"
echo "Artık KDE Plasma / Nobara menünüzden doğrudan 'Connect Me' yazarak konsolsuz başlatabilirsiniz."
