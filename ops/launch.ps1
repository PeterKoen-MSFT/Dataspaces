# launch.ps1 — bring up the dataspace stack and seed it.
#
# Steps:
#   1. Normalise line endings on shell scripts (Windows checkouts ship CRLF).
#   2. podman compose up -d  (postgres -> vault -> keycloak -> vault-bootstrap
#                             -> cp/ih/dp-a/b -> issuer)
#   3. Wait for vault-bootstrap to exit cleanly.
#   4. Wait for all EDC services to report /api/check/readiness.
#   5. Run seed.sh in an ephemeral curlimages/curl container on the dataspaces network.
#
# Idempotent: re-running is safe.

[CmdletBinding()]
param(
    [switch]$NoSeed
)

$ErrorActionPreference = 'Stop'
$Root  = Split-Path -Parent $PSScriptRoot
$Infra = Join-Path $Root 'infra'

function Write-Step($msg) { Write-Host ""; Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    [ok] $msg" -ForegroundColor Green }
function Write-Warn2($msg){ Write-Host "    [..] $msg" -ForegroundColor Yellow }
function Write-Err($msg)  { Write-Host "    [!!] $msg" -ForegroundColor Red }

# ---- 1. Normalise line endings on shell scripts -------------------------
Write-Step "Normalise shell-script line endings to LF"
Get-ChildItem -Path $Infra -Recurse -File -Filter '*.sh' | ForEach-Object {
    $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
    $text  = [System.Text.Encoding]::UTF8.GetString($bytes)
    $lf    = $text -replace "`r`n", "`n"
    [System.IO.File]::WriteAllText($_.FullName,
        $lf,
        (New-Object System.Text.UTF8Encoding $false))
    Write-Ok $_.FullName.Substring($Root.Length + 1)
}

# ---- 2. Compose up -------------------------------------------------------
Write-Step "podman compose up -d"
Push-Location $Infra
try {
    & podman compose -f compose.yaml up -d
    if ($LASTEXITCODE -ne 0) { throw "podman compose up failed (exit $LASTEXITCODE)" }
}
finally { Pop-Location }
Write-Ok "compose up returned"

# ---- 3. Wait for vault-bootstrap to exit cleanly -------------------------
Write-Step "Wait for vault-bootstrap to complete"
$bootstrapDone = $false
for ($i = 0; $i -lt 60; $i++) {
    $state = (& podman inspect ds-vault-bootstrap --format '{{.State.Status}} {{.State.ExitCode}}' 2>$null)
    if ($LASTEXITCODE -eq 0 -and $state) {
        Write-Warn2 "vault-bootstrap state: $state"
        if ($state -match '^exited 0') { $bootstrapDone = $true; break }
        if ($state -match '^exited [^0]') { throw "vault-bootstrap failed: $state" }
    }
    Start-Sleep -Seconds 2
}
if (-not $bootstrapDone) { throw "vault-bootstrap did not finish in time" }
Write-Ok "vault-bootstrap exited 0"

# ---- 4. Wait for EDC services to report ready ----------------------------
Write-Step "Wait for EDC service readiness"
$readinessUrls = @(
    'http://localhost:18080/api/check/readiness',     # cp-a
    'http://localhost:28080/api/check/readiness',     # cp-b
    'http://localhost:17080/api/check/readiness',     # ih-a
    'http://localhost:27080/api/check/readiness',     # ih-b
    'http://localhost:11100/api/check/readiness',     # dp-a
    'http://localhost:21100/api/check/readiness',     # dp-b
    'http://localhost:19010/api/check/readiness'      # issuer
)
foreach ($u in $readinessUrls) {
    $okThis = $false
    for ($i = 0; $i -lt 90; $i++) {
        try {
            $r = Invoke-WebRequest -Uri $u -UseBasicParsing -TimeoutSec 3
            if ($r.StatusCode -eq 200) { $okThis = $true; break }
        } catch { Start-Sleep -Seconds 2 }
    }
    if ($okThis) { Write-Ok "ready $u" } else { Write-Err "NOT ready $u"; throw "service did not become ready: $u" }
}

# ---- 5. Run seed in an ephemeral curl container --------------------------
if ($NoSeed) {
    Write-Step "Skipping seed (NoSeed flag)"
}
else {
    Write-Step "Run seed.sh in ephemeral curl container"
    $seedHostPath = Join-Path $Infra 'seed' | Resolve-Path
    # podman compose creates a network named "<project>_<network>"; here the
    # project is implicit from the directory name (infra) plus the explicit
    # 'name: dataspaces' top-level key, so the actual network is dataspaces_dataspaces.
    $networkName = (& podman network ls --format '{{.Name}}' | Where-Object { $_ -match 'dataspaces' } | Select-Object -First 1)
    if (-not $networkName) { throw "Could not find dataspaces network" }
    Write-Warn2 "Using network: $networkName"
    & podman run --rm --name ds-seed --network $networkName `
        -v "${seedHostPath}:/seed:ro" `
        --entrypoint sh `
        docker.io/curlimages/curl:8.10.1 `
        /seed/seed.sh
    if ($LASTEXITCODE -ne 0) { throw "seed.sh failed (exit $LASTEXITCODE)" }
    Write-Ok "seed completed"
}

# ---- Summary -------------------------------------------------------------
Write-Step "Final container status"
& podman ps --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}'
Write-Host ""
Write-Host "Stack ready." -ForegroundColor Green
Write-Host "  Participant A management : http://localhost:18081/api/mgmt"
Write-Host "  Participant B management : http://localhost:28081/api/mgmt"
Write-Host "  Issuer admin             : http://localhost:19013/api/admin"
Write-Host "  Keycloak                 : http://localhost:8888 (admin/admin)"
