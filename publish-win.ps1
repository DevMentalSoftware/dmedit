dotnet publish src/DMEdit.App/DMEdit.App.csproj -c Release -r win-x64 --self-contained true -o publish/dmedit-win-x64 -p:PublishReadyToRun=true
:: -p:PublishSingleFile=true