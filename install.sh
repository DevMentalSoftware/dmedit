#!/bin/bash
set -e

echo "Installing DMEdit..."

# Install prerequisites
sudo apt install -y libgtk-3-0 libx11-6 libxcb1 libxcursor1 \
    libxrandr2 libxi6 libfontconfig1 libfreetype6 libgl1

# Install app
mkdir -p /opt/dmedit
cp -r "$(dirname "$0")"/* /opt/dmedit/
chmod +x /opt/dmedit/dmedit

# Create desktop entry (system-wide)
cat > /usr/local/share/applications/dmedit.desktop << 'DESKTOP'
[Desktop Entry]
Name=DMEdit
Exec=/opt/dmedit/dmedit %F
Icon=/opt/dmedit/text_editor.svg
Type=Application
Categories=TextEditor;Development;
MimeType=text/plain;text/markdown;
Terminal=false
DESKTOP

# Symlink to PATH
sudo ln -sf /opt/dmedit/dmedit /usr/local/bin/dmedit

echo "Done! Run 'dmedit' or find it in your application menu."
