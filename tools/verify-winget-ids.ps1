# verify-winget-ids.ps1 - checks every winget id in an apps.json against the microsoft/winget-pkgs repo
# (and, if winget is installed, against `winget show`). Exit code = number of ids that failed.
#   .\tools\verify-winget-ids.ps1 -AppsJson src\WinWipeHelper\apps.json
param([Parameter(Mandatory)][string]$AppsJson)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$list = Get-Content $AppsJson -Raw | ConvertFrom-Json
$ids = @($list.apps | Where-Object { -not $_.kind -or $_.kind -eq 'winget' } | ForEach-Object id)
$haveWinget = [bool](Get-Command winget -ErrorAction SilentlyContinue)
$haveGh = [bool](Get-Command gh -ErrorAction SilentlyContinue)
$bad = 0
foreach ($id in $ids) {
    # winget-pkgs layout: manifests/<first letter>/<Publisher>/<Package>[/<more>]
    $path = 'manifests/' + $id.Substring(0, 1).ToLower() + '/' + ($id -replace '\.', '/')
    $inRepo = $false
    try {
        if ($haveGh) { gh api "repos/microsoft/winget-pkgs/contents/$path" --silent 2>$null; $inRepo = $LASTEXITCODE -eq 0 }
        else { Invoke-RestMethod "https://api.github.com/repos/microsoft/winget-pkgs/contents/$path" -Headers @{ 'User-Agent' = 'verify-winget-ids' } | Out-Null; $inRepo = $true }
    } catch { $inRepo = $false }
    $inWinget = $null
    if ($haveWinget) {
        $out = winget show --id $id --exact --source winget --accept-source-agreements --disable-interactivity 2>&1 | Out-String
        $inWinget = $out -match 'Found .+ \['
    }
    $ok = $inRepo -and ($inWinget -ne $false)
    if (-not $ok) { $bad++ }
    '{0,-45} winget-pkgs: {1,-5}  winget show: {2}' -f $id, $inRepo, $(if ($null -eq $inWinget) { 'n/a' } else { $inWinget })
}
"`n$($ids.Count - $bad) of $($ids.Count) ids verified."
exit $bad
