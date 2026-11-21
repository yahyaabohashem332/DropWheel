$env:PATH = $env:PATH + ";C:\Users\YAHYA ABO HASHEM\dotnet"
$env:PETAL_SETTINGS_DIR = "D:\Tangerine\tests\settings-test"
cd D:\Tangerine

# Run tests
& "C:\Users\YAHYA ABO HASHEM\dotnet\dotnet.exe" test tests\Petal.Tests\Petal.Tests.csproj -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw "Tests failed" }

# Run fetch-tools
& powershell.exe -ExecutionPolicy Bypass -File "tools\scripts\fetch-tools.ps1"

# Publish app
$dist = "D:\Tangerine\dist"
$publish = Join-Path $dist "Petal"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
& "C:\Users\YAHYA ABO HASHEM\dotnet\dotnet.exe" publish src\Petal.App\Petal.App.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:Version=1.23.1 -p:DebugType=none -o $publish --nologo --disable-build-servers
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

# Publish CLI
$cliStage = Join-Path $dist "cli-staging"
if (Test-Path $cliStage) { Remove-Item $cliStage -Recurse -Force }
& "C:\Users\YAHYA ABO HASHEM\dotnet\dotnet.exe" publish src\Petal.Cli\Petal.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:Version=1.23.1 -p:DebugType=none -o $cliStage --nologo --disable-build-servers
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed" }

Write-Host "Build completed successfully"