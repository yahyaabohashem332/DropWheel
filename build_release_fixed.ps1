$env:PATH = "C:\Users\YAHYA ABO HASHEM\dotnet;" + $env:PATH
cd D:\Tangerine

# Test dotnet
& "C:\Users\YAHYA ABO HASHEM\dotnet\dotnet.exe" --version

# Run tests with explicit dotnet path
& "C:\Users\YAHYA ABO HASHEM\dotnet\dotnet.exe" test tests\Petal.Tests\Petal.Tests.csproj -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }

# Run the build script (but skip tests since we already ran them)
& .\tools\scripts\build.ps1 -Version 1.23.1 -Notes "DropWheel 1.23.1: PDF long/first page converters, auto chains, batch progress, date/recycle options, Arabic localization, Send To integration, Explorer previews for PDF/3D/EPUB/comics/SVG" -SkipTests