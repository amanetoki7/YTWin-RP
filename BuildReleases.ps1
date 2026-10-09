# Run this to generate releases. You need to supply the publish profiles.

dotnet publish YTWin-RichPresence/YTWin-RichPresence.csproj -p:PublishProfile=NoRuntime-arm64
dotnet publish YTWin-RichPresence/YTWin-RichPresence.csproj -p:PublishProfile=NoRuntime-x64
dotnet publish YTWin-RichPresence/YTWin-RichPresence.csproj -p:PublishProfile=WithRuntime-arm64
dotnet publish YTWin-RichPresence/YTWin-RichPresence.csproj -p:PublishProfile=WithRuntime-x64

$projectPath = "YTWin-RichPresence\YTWin-RichPresence.csproj"
$xml = [xml](Get-Content $projectPath)
$fileVersion = $xml.Project.PropertyGroup.FileVersion

cd YTWin-RichPresence/bin/Publish

Compress-Archive -Force -Path "arm64-WithRuntime/*" -DestinationPath "YTWin-RichPresence-v$fileVersion-arm64.zip"
Compress-Archive -Force -Path "x64-WithRuntime/*" -DestinationPath "YTWin-RichPresence-v$fileVersion-x64.zip"
Compress-Archive -Force -Path "arm64-NoRuntime/YTWin-RichPresence.exe" -DestinationPath "YTWin-RichPresence-v$fileVersion-arm64-NoRuntime.zip"
Compress-Archive -Force -Path "x64-NoRuntime/YTWin-RichPresence.exe" -DestinationPath "YTWin-RichPresence-v$fileVersion-x64-NoRuntime.zip"

cd ../../..