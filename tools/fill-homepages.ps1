<#
fill-homepages.ps1 - adds a "homepage" to every winget app in an apps.json that doesn't have one yet,
using the Homepage (or Publisher Url) that winget lists for the package. The app uses it to fetch the
vendor's own site icon for apps that aren't installed yet.

    .\tools\fill-homepages.ps1 src\WinWipeHelper\apps.json

Only lines like  { "id": "X", ... "kind": "winget" }  are touched; the file's layout is kept.
#>
param([Parameter(Mandatory)][string[]]$Path)
$ErrorActionPreference = 'Stop'

function Get-Homepage([string]$id) {
    $out = winget show --id $id --exact --accept-source-agreements --disable-interactivity 2>$null | Out-String
    foreach ($label in 'Homepage', 'Publisher Url') {
        $m = [regex]::Match($out, "(?m)^\s*${label}:\s*(https?://\S+)")
        if ($m.Success) { return $m.Groups[1].Value.TrimEnd('/') }
    }
    return $null
}

foreach ($p in $Path) {
    $full = (Resolve-Path $p).Path
    $raw = [IO.File]::ReadAllText($full)
    $nl = if ($raw.Contains("`r`n")) { "`r`n" } else { "`n" }
    $lines = $raw.TrimEnd("`r", "`n") -split "`r?`n"
    $changed = 0
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        if ($line -match '"homepage"' -or $line -notmatch '"kind":\s*"winget"') { continue }
        $m = [regex]::Match($line, '"id":\s*"([^"]+)"')
        if (-not $m.Success) { continue }
        $id = $m.Groups[1].Value
        $hp = Get-Homepage $id
        if (-not $hp) { Write-Host "  (no homepage) $id" -ForegroundColor Yellow; continue }
        $lines[$i] = $line -replace '(,\s*)"kind":\s*"winget"', ('$1"homepage": "' + $hp.Replace('\', '\\').Replace('"', '\"') + '"$1"kind": "winget"')
        $changed++
        Write-Host "  $id -> $hp"
    }
    [IO.File]::WriteAllText($full, ($lines -join $nl) + $nl, (New-Object Text.UTF8Encoding $false))
    Write-Host "$full : $changed homepage(s) added" -ForegroundColor Green
}
