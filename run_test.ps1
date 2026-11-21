$env:PATH = $env:PATH + ";C:\Users\YAHYA ABO HASHEM\dotnet"
$env:PETAL_SETTINGS_DIR = "D:\Tangerine\tests\settings-test"
cd D:\Tangerine
& "C:\Users\YAHYA ABO HASHEM\dotnet\dotnet.exe" test tests\Petal.Tests\Petal.Tests.csproj -c Release --filter "FullyQualifiedName~FolderTests.Converting_a_folder_fills_a_copy_of_its_tree_and_leaves_the_folder_alone" --no-build 2>&1