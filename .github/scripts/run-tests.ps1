<#
.SYNOPSIS
  Runs DropWheel's tests on a build machine, which has no testdata\ (the sample files are kept out of git).

  Every test runs. A failure counts only as "needs the sample files" when its error says a file of
  testdata\ is missing; a test that fails otherwise runs again alone (timing tests slow down on a busy
  machine); anything that still fails stops the build. The results are written to test-results\.
#>
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$results = Join-Path $root 'test-results'
New-Item -ItemType Directory -Force $results | Out-Null
if (-not $env:PETAL_SETTINGS_DIR) { $env:PETAL_SETTINGS_DIR = Join-Path $results 'settings-test' }
New-Item -ItemType Directory -Force $env:PETAL_SETTINGS_DIR | Out-Null
$project = Join-Path $root 'tests\Petal.Tests'
# One test loads File Explorer's thumbnail handler from its Debug build, as it is on a developer's PC.
dotnet build (Join-Path $root 'src\Petal.Shell') -c Debug --nologo
if ($LASTEXITCODE -ne 0) { throw "File Explorer's thumbnail handler didn't build." }

dotnet test $project -c Release --nologo --logger "trx;LogFileName=all.trx" --results-directory $results
$firstExit = $LASTEXITCODE

$trx = Get-ChildItem $results -Recurse -Filter 'all.trx' | Select-Object -First 1
if (-not $trx) { throw "The tests didn't run (dotnet test exited with $firstExit)." }
[xml]$doc = Get-Content $trx.FullName -Raw
$all = @($doc.TestRun.Results.UnitTestResult)
$failed = @($all | Where-Object { $_.outcome -eq 'Failed' })
Write-Host "Tests: $($all.Count) run, $(@($all | Where-Object { $_.outcome -eq 'Passed' }).Count) passed, $($failed.Count) failed"
if ($firstExit -ne 0 -and $failed.Count -eq 0) { throw "dotnet test exited with $firstExit without a failed test: the run broke." }

$needSamples = @()
$stillFailing = @()
foreach ($test in $failed) {
    $message = "$($test.Output.ErrorInfo.Message)"
    $name = "$($test.testName)"
    if ($message -match 'testdata' -and $message -match 'FileNotFound|DirectoryNotFound|Could not find (file|a part of the path)') {
        $needSamples += $name
        continue
    }
    # Theories carry their arguments in the name; the filter wants the method.
    $method = ($name -replace '\(.*$', '').Trim()
    Write-Host "Running again alone: $method"
    dotnet test $project -c Release --nologo --no-build --filter "FullyQualifiedName=$method" --logger "trx;LogFileName=again-$($stillFailing.Count + $needSamples.Count).trx" --results-directory $results
    if ($LASTEXITCODE -ne 0) {
        $stillFailing += "$name`n    " + ($message.Trim() -replace "`r?`n", "`n    ")
    }
}

if ($needSamples.Count -gt 0) {
    Write-Host "`nNeed the sample files of testdata\ (run them on the PC that has them):"
    $needSamples | ForEach-Object { Write-Host "  $_" }
}
if ($stillFailing.Count -gt 0) {
    Write-Host "`nFailed:"
    $stillFailing | ForEach-Object { Write-Host "  $_" }
    throw "$($stillFailing.Count) test(s) failed."
}
Write-Host "`nEvery test that can run here passed."
