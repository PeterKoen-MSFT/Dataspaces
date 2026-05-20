# Verify catalog + negotiation + HttpData-PULL transfer end-to-end.
# Participant B is the consumer, Participant A the provider, asset 'resources'.

$ErrorActionPreference = 'Stop'

$CP_B = 'http://localhost:28081/api/mgmt'
$CP_A_DSP = 'http://cp-a:8082/api/dsp/2025-1'
$DID_A = 'did:web:ih-a%3A7083:participant-a'

function PostJson($Url, $Body) {
    Invoke-RestMethod -Uri $Url -Method POST -ContentType 'application/json' -Body $Body -TimeoutSec 60
}

function GetJson($Url) {
    Invoke-RestMethod -Uri $Url -Method GET -TimeoutSec 60
}

Write-Host "==> 1. Catalog request B -> A"
$catBody = @{
    '@context' = @{ '@vocab' = 'https://w3id.org/edc/v0.0.1/ns/' }
    '@type' = 'CatalogRequest'
    counterPartyAddress = $CP_A_DSP
    counterPartyId = $DID_A
    protocol = 'dataspace-protocol-http:2025-1'
} | ConvertTo-Json -Depth 5
$catalog = PostJson "$CP_B/v4/catalog/request" $catBody
$dataset = $catalog.dataset | Where-Object { $_.'@id' -eq 'resources' } | Select-Object -First 1
if (-not $dataset) { throw "no 'resources' dataset in catalog" }
$offer = if ($dataset.hasPolicy -is [array]) { $dataset.hasPolicy[0] } else { $dataset.hasPolicy }
Write-Host "    offerId = $($offer.'@id')"

Write-Host "==> 2. Initiate contract negotiation"
$negBody = @{
    '@context' = @('https://w3id.org/edc/connector/management/v2')
    '@type' = 'ContractRequest'
    counterPartyAddress = $CP_A_DSP
    counterPartyId = $DID_A
    protocol = 'dataspace-protocol-http:2025-1'
    policy = @{
        '@type' = 'Offer'
        '@id' = $offer.'@id'
        assigner = $DID_A
        target = 'resources'
        permission = @(@{
            action = 'use'
            constraint = @{
                leftOperand = 'MembershipCredential'
                operator = 'eq'
                rightOperand = 'active'
            }
        })
        prohibition = @()
        obligation = @()
    }
    callbackAddresses = @()
} | ConvertTo-Json -Depth 10
$neg = PostJson "$CP_B/v4/contractnegotiations" $negBody
$negId = $neg.'@id'
Write-Host "    negotiationId = $negId"

Write-Host "==> 3. Poll negotiation until FINALIZED"
$contractAgreementId = $null
for ($i=1; $i -le 40; $i++) {
    $n = GetJson "$CP_B/v4/contractnegotiations/$negId"
    Write-Host "    [$i/40] state=$($n.state)"
    if ($n.state -eq 'FINALIZED') { $contractAgreementId = $n.contractAgreementId; break }
    if ($n.state -eq 'TERMINATED') { throw "negotiation TERMINATED: $($n.errorDetail)" }
    Start-Sleep -Seconds 3
}
if (-not $contractAgreementId) { throw "negotiation did not FINALIZE" }
Write-Host "    contractAgreementId = $contractAgreementId"

Write-Host "==> 4. Initiate transfer (HttpData-PULL)"
$tBody = @{
    '@context' = @{ '@vocab' = 'https://w3id.org/edc/v0.0.1/ns/' }
    '@type' = 'TransferRequest'
    counterPartyAddress = $CP_A_DSP
    protocol = 'dataspace-protocol-http:2025-1'
    contractId = $contractAgreementId
    transferType = 'HttpData-PULL'
} | ConvertTo-Json -Depth 5
$tr = PostJson "$CP_B/v4/transferprocesses" $tBody
$tpId = $tr.'@id'
Write-Host "    transferId = $tpId"

Write-Host "==> 5. Poll transfer until STARTED"
for ($i=1; $i -le 40; $i++) {
    $t = GetJson "$CP_B/v4/transferprocesses/$tpId"
    Write-Host "    [$i/40] state=$($t.state)"
    if ($t.state -in 'STARTED','COMPLETED') { break }
    if ($t.state -eq 'TERMINATED') { throw "transfer TERMINATED: $($t.errorDetail)" }
    Start-Sleep -Seconds 3
}

Write-Host "==> 6. Fetch EDR DataAddress"
$edr = GetJson "$CP_B/v3/edrs/$tpId/dataaddress"
$endpoint = $edr.endpoint
$auth = $edr.authorization
Write-Host "    endpoint = $endpoint"

Write-Host "==> 7. Pull data through dataplane"
# Translate dp-a hostname to localhost dataplane public port
$endpointHost = $endpoint -replace 'http://dp-a:11002', 'http://localhost:11102'
$response = Invoke-WebRequest -Uri $endpointHost -Headers @{ Authorization = $auth } -UseBasicParsing -TimeoutSec 30
Write-Host "    HTTP $($response.StatusCode), $($response.Content.Length) bytes"
Write-Host "--- BEGIN DATA ---"
Write-Host $response.Content
Write-Host "--- END DATA ---"
Write-Host ""
Write-Host "ROUND-TRIP OK"
