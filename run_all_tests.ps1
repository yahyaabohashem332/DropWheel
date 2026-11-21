$env:PETAL_SETTINGS_DIR = "D:\Tangerine\tests\settings-test"
cd D:\Tangerine
& "C:\Users\YAHYA ABO HASHEM\dotnet\dotnet.exe" test tests\Petal.Tests\Petal.Tests.csproj -c Release --no-build
& "C:\Users\YAHYA ABO HASHEM\dotnet\dotnet.exe" test tests\Petal.Engines.Media.Tests\Petal.Engines.Media.Tests.csproj -c Release --no-build
& "C:\Users\YAHYA ABO HASHEM\dotnet\dotnet.exe" test tests\Petal.Engines.Imaging.Tests\Petal.Engines.Imaging.Tests.csproj -c Release --no-build
& "C:\Users\YAHYA ABO HASHEM\dotnet\dotnet.exe" test tests\Petal.Engines.Documents.Tests\Petal.Engines.Documents.Tests.csproj -c Release --no-build