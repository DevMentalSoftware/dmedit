dotnet publish src/DevMentalMd.App/DevMentalMd.App.csproj -c Release -r linux-x64 --self-contained true -o publish/linux-x64
copy resources/dev_mental_head.svg publish/linux-x64
scp -r publish/linux-x64/* justin@justin-linux.local:~/dmedit/
