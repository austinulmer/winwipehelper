# build.ps1 - builds every app project under src\ into .\artifacts (one EXE each), plus the self-test if present.
#   .\build.ps1            Release build
#   .\build.ps1 -Test      also runs the offline self-tests
param([switch]$Test, [string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet -or -not (& $dotnet --list-sdks)) {
    $local = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if (Test-Path $local) { $dotnet = $local } else { throw 'The .NET SDK is needed to build (https://dotnet.microsoft.com/download).' }
}
$env:DOTNET_NOLOGO = '1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$out = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force $out | Out-Null

foreach ($proj in Get-ChildItem (Join-Path $root 'src') -Filter *.csproj -Recurse) {
    $name = ([xml](Get-Content $proj.FullName -Raw)).Project.PropertyGroup.AssemblyName | Where-Object { $_ } | Select-Object -First 1
    if (-not $name) { $name = $proj.BaseName }
    & $dotnet build $proj.FullName -c $Configuration --nologo -v quiet
    if ($LASTEXITCODE) { throw "Build failed: $($proj.Name)" }
    $bin = Join-Path $proj.DirectoryName "bin\$Configuration"
    $exe = "$name.exe"
    $extra = Get-ChildItem $bin -File | Where-Object Name -ne $exe
    if ($extra) { throw "Expected a single EXE in $bin, but also found: $($extra.Name -join ', ')" }
    Copy-Item (Join-Path $bin $exe) $out -Force
    Write-Host "Built $exe"
}

$selftest = Join-Path $root 'tests\SelfTest\SelfTest.csproj'
if (Test-Path $selftest) {
    & $dotnet build $selftest -c $Configuration --nologo -v quiet
    if ($LASTEXITCODE) { throw 'Build failed: SelfTest' }
    $st = Join-Path $out 'selftest'
    New-Item -ItemType Directory -Force $st | Out-Null
    Copy-Item (Join-Path $root "tests\SelfTest\bin\$Configuration\WinWipeHelper.SelfTest.exe") $st -Force
    if ($Test) {
        & (Join-Path $st 'WinWipeHelper.SelfTest.exe') offline
        if ($LASTEXITCODE) { throw "$LASTEXITCODE self-test check(s) failed" }
    }
}
Get-ChildItem $out -Recurse -File | Select-Object @{ n = 'File'; e = { $_.FullName.Substring($out.Length + 1) } }, Length | Format-Table -AutoSize
