# reset.ps1 — tear down the dataspace stack completely.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'
$Root  = Split-Path -Parent $PSScriptRoot
$Infra = Join-Path $Root 'infra'

Write-Host "==> podman compose down -v" -ForegroundColor Cyan
Push-Location $Infra
try {
    & podman compose -f compose.yaml down -v --remove-orphans 2>&1 | Out-Host
}
finally { Pop-Location }

Write-Host "==> remove dataspaces network if it lingers" -ForegroundColor Cyan
& podman network rm -f dataspaces 2>&1 | Out-Host

Write-Host "==> remove any leftover ds-* containers" -ForegroundColor Cyan
$leftovers = & podman ps -a --filter 'name=ds-' --format '{{.Names}}' 2>$null
if ($leftovers) {
    $leftovers | ForEach-Object {
        & podman rm -f -v $_ 2>&1 | Out-Host
    }
}

Write-Host "==> volume prune" -ForegroundColor Cyan
& podman volume prune -f 2>&1 | Out-Host

Write-Host "Done." -ForegroundColor Green
