$env:Path += ";C:\Program Files (x86)\Microsoft Visual Studio\Installer"
cd "C:\Users\YAHYA ABO HASHEM\AppData\Local\Temp\magick\Magick.Native\build\windows"
& cmd.exe /c 'call "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat" && build.cmd Release Q8 x64 noHdri noOpenMP' 2>&1