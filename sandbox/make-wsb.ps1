# make-wsb.ps1 - writes sandbox\WinWipeHelper.local.wsb with this PC's build folder (artifacts\) mapped in,
# then opens it in Windows Sandbox. Build first with .\build.ps1.
#   .\sandbox\make-wsb.ps1            write and open
#   .\sandbox\make-wsb.ps1 -NoOpen    just write the file
param([switch]$NoOpen)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$build = Join-Path $root 'artifacts'
if (-not (Test-Path $build)) { throw "Build first: .\build.ps1 (no $build yet)." }
$template = Get-Content (Join-Path $PSScriptRoot 'WinWipeHelper.wsb') -Raw
$xml = $template.Replace('__BUILD_FOLDER__', [Security.SecurityElement]::Escape((Resolve-Path $build).Path))
$out = Join-Path $PSScriptRoot 'WinWipeHelper.local.wsb'
[IO.File]::WriteAllText($out, $xml, (New-Object Text.UTF8Encoding $false))
Write-Host "Wrote $out"
if (-not $NoOpen) {
    if (-not (Test-Path "$env:windir\System32\WindowsSandbox.exe")) {
        Write-Host @'
Windows Sandbox isn't turned on. In an admin PowerShell run:
    Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All
then restart Windows. (Needs Windows 10/11 Pro, Enterprise or Education.)
'@ -ForegroundColor Yellow
        exit 1
    }
    Start-Process $out
}
