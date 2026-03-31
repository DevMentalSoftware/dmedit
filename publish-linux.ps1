dotnet publish src/DMEdit.App/DMEdit.App.csproj -c Release -r linux-x64 --self-contained true -o publish/dmedit-linux-x64
copy resources/text_editor.svg publish/dmedit-linux-x64
scp -r publish/dmedit-linux-x64/* justin@justin-linux.local:~/dmedit/
