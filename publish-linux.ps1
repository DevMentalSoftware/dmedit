$ErrorActionPreference = 'Stop'

# Wipe the previous output so a partial file (e.g. an SVG accidentally
# written at the directory's own path by a failed run) can't block the
# next publish.
if (Test-Path publish/dmedit-linux-x64) {
    Remove-Item -Recurse -Force publish/dmedit-linux-x64
}

dotnet publish src/DMEdit.App/DMEdit.App.csproj -c Release -r linux-x64 --self-contained true -o publish/dmedit-linux-x64
Copy-Item resources/text_editor.svg -Destination publish/dmedit-linux-x64/
scp -r publish/dmedit-linux-x64/* justin@justin-linux.local:~/dmedit/
