#!/bin/sh
# Seed script: creates the issuer tenant + attestation/credential defs in
# the IssuerService, then for each participant creates a holder in the
# issuer, a participant context in the IH, and requests credential issuance.
# Finally seeds Participant A's controlplane with one asset, two policies,
# and a contract definition so participant B can negotiate.
#
# All operations are idempotent: HTTP 409 (already exists) is treated as success.
#
# Required tools in container: curl, sh.

set -eu

KC=http://keycloak:8080
IH_A=http://ih-a:7081
IH_B=http://ih-b:7081
ISSUER_ADMIN=http://issuer:10013/api/admin/v1alpha
ISSUER_IDENTITY=http://issuer:10015/api/identity/v1alpha
CP_A_MGMT=http://cp-a:8081/api/mgmt

DID_A="did:web:ih-a%3A7083:participant-a"
DID_B="did:web:ih-b%3A7083:participant-b"
DID_ISSUER="did:web:issuer%3A10016:issuer"

http_status() { tail -n1 | tr -d '[:space:]'; }
http_body() { sed '$d'; }

ok_or_409() {
  status="$1"; label="$2"; body="$3"
  if [ "$status" -eq 200 ] || [ "$status" -eq 201 ] || [ "$status" -eq 204 ]; then
    echo "  OK   $label"
  elif [ "$status" -eq 409 ]; then
    echo "  SKIP $label (409 already exists)"
  else
    echo "  FAIL $label  status=$status body=$body"
    exit 1
  fi
}

token() {
  client_id="$1"; client_secret="$2"; scope="$3"
  curl -sf -X POST "$KC/realms/mvd/protocol/openid-connect/token" \
    -H "Content-Type: application/x-www-form-urlencoded" \
    -d "grant_type=client_credentials" \
    -d "client_id=$client_id" \
    -d "client_secret=$client_secret" \
    -d "scope=$scope" \
    | sed -n 's/.*"access_token":"\([^"]*\)".*/\1/p'
}

post_idem() {
  url="$1"; auth="$2"; body="$3"; label="$4"
  R=$(curl -sS -w "\n%{http_code}" -X POST "$url" \
        -H "Content-Type: application/json" \
        -H "Authorization: Bearer $auth" \
        -d "$body")
  status=$(echo "$R" | tail -n1)
  body=$(echo "$R" | sed '$d')
  ok_or_409 "$status" "$label" "$body"
}

post_unauth() {
  url="$1"; body="$2"; label="$3"
  R=$(curl -sS -w "\n%{http_code}" -X POST "$url" \
        -H "Content-Type: application/json" \
        -d "$body")
  status=$(echo "$R" | tail -n1)
  body=$(echo "$R" | sed '$d')
  ok_or_409 "$status" "$label" "$body"
}

echo "[seed] Wait for keycloak"
until curl -sf "$KC/realms/mvd/.well-known/openid-configuration" >/dev/null 2>&1; do sleep 2; done

echo "[seed] Wait for cp-a / cp-b / ih-a / ih-b / issuer ready"
for url in http://cp-a:8080/api/check/readiness \
           http://cp-b:8080/api/check/readiness \
           http://ih-a:7080/api/check/readiness \
           http://ih-b:7080/api/check/readiness \
           http://issuer:10010/api/check/readiness ; do
  echo "    waiting on $url"
  until curl -sf "$url" >/dev/null 2>&1; do sleep 3; done
done
echo "[seed] All services ready."

# ---- Step 1: Issuer tenant ----
echo "[seed] === issuer tenant ==="
PROV_TOKEN=$(token provisioner provisioner-secret "issuer-admin-api:write")
[ -z "$PROV_TOKEN" ] && { echo "FAIL: cannot get provisioner token"; exit 1; }
ADMIN_TOKEN=$(token admin edc-v-admin-secret "identity-api:write issuer-admin-api:write")
[ -z "$ADMIN_TOKEN" ] && { echo "FAIL: cannot get admin token"; exit 1; }

post_idem "$ISSUER_IDENTITY/participants" "$PROV_TOKEN" "$(cat <<EOF
{
  "roles": ["admin"],
  "serviceEndpoints": [
    {
      "type": "IssuerService",
      "serviceEndpoint": "http://issuer:10012/api/issuance/v1alpha/participants/issuer",
      "id": "issuer-service-1"
    }
  ],
  "active": true,
  "participantContextId": "issuer",
  "did": "$DID_ISSUER",
  "key": {
    "keyId": "$DID_ISSUER#key-1",
    "privateKeyAlias": "$DID_ISSUER#key-1",
    "keyGeneratorParams": { "algorithm": "EdDSA" }
  },
  "additionalProperties": {
    "edc.vault.hashicorp.config": {
      "credentials": {
        "clientId": "issuer",
        "clientSecret": "issuer-secret",
        "tokenUrl": "http://keycloak:8080/realms/mvd/protocol/openid-connect/token"
      },
      "config": {
        "secretPath": "v1/participants",
        "folderPath": "issuer",
        "vaultUrl": "http://vault:8200"
      }
    }
  }
}
EOF
)" "create issuer tenant"

# Activate the issuer tenant + publish its DID (publish requires admin role)
post_idem "$ISSUER_IDENTITY/participants/issuer/state?isActive=true" "$PROV_TOKEN" "" "activate issuer tenant"
post_idem "$ISSUER_IDENTITY/participants/issuer/dids/publish" "$ADMIN_TOKEN" "{\"did\": \"$DID_ISSUER\"}" "publish issuer DID"

ISSUER_TOKEN=$(token issuer issuer-secret "issuer-admin-api:write")

post_idem "$ISSUER_ADMIN/participants/issuer/attestations" "$ISSUER_TOKEN" \
  '{"attestationType": "membership", "configuration": {}, "id": "membership-attestation-def-1"}' \
  "membership attestation def"

post_idem "$ISSUER_ADMIN/participants/issuer/credentialdefinitions" "$ISSUER_TOKEN" \
  '{"attestations": ["membership-attestation-def-1"], "credentialType": "MembershipCredential",
    "id": "membership-credential-def", "jsonSchema": "{}",
    "jsonSchemaUrl": "https://example.com/schema/membership-credential.json",
    "mappings": [
      {"input": "membership", "output": "credentialSubject.membership", "required": true},
      {"input": "membershipType", "output": "credentialSubject.membershipType", "required": "true"},
      {"input": "membershipStartDate", "output": "credentialSubject.membershipStartDate", "required": true}
    ],
    "rules": [], "format": "VC1_0_JWT", "validity": "604800"}' \
  "membership credential def"

post_idem "$ISSUER_ADMIN/participants/issuer/attestations" "$ISSUER_TOKEN" \
  '{"attestationType": "membership", "configuration": {}, "id": "partner-attestation-def-1"}' \
  "partner attestation def"

post_idem "$ISSUER_ADMIN/participants/issuer/credentialdefinitions" "$ISSUER_TOKEN" \
  '{"attestations": ["partner-attestation-def-1"], "credentialType": "PartnerCredential",
    "id": "partner-credential-def", "jsonSchema": "{}",
    "jsonSchemaUrl": "https://example.com/schema/partner-credential.json",
    "mappings": [
      {"input": "membership", "output": "credentialSubject.membership", "required": true},
      {"input": "membershipType", "output": "credentialSubject.membershipType", "required": "true"},
      {"input": "membershipStartDate", "output": "credentialSubject.membershipStartDate", "required": true}
    ],
    "rules": [], "format": "VC1_0_JWT", "validity": "604800"}' \
  "partner credential def"

# ---- Step 2: Per-participant holder + IH context + credential request ----
seed_participant() {
  PNAME="$1"; PDID="$2"; IH="$3"; CS="$4"; CP_DSP="$5"

  echo "[seed] === participant $PNAME ==="

  post_idem "$ISSUER_ADMIN/participants/issuer/holders" "$ISSUER_TOKEN" \
    "{\"did\": \"$PDID\", \"holderId\": \"$PDID\", \"name\": \"MVD $PNAME Participant\"}" \
    "issuer holder for $PNAME"

  post_idem "$IH/api/identity/v1alpha/participants/" "$PROV_TOKEN" "$(cat <<EOF
{
  "roles": [],
  "serviceEndpoints": [
    {"type": "CredentialService",
     "serviceEndpoint": "$CS/api/credentials/v1/participants/$PNAME",
     "id": "$PNAME-credentialservice-1"},
    {"type": "ProtocolEndpoint",
     "serviceEndpoint": "$CP_DSP",
     "id": "$PNAME-dsp"}
  ],
  "active": true,
  "participantId": "$PDID",
  "participantContextId": "$PNAME",
  "did": "$PDID",
  "key": {
    "keyId": "$PDID#key-1",
    "privateKeyAlias": "$PDID#key-1",
    "keyGeneratorParams": { "algorithm": "EC" }
  }
}
EOF
)" "IH participant context $PNAME"

  # Activate context + publish DID so other participants and the issuer can resolve it
  post_idem "$IH/api/identity/v1alpha/participants/$PNAME/state?isActive=true" "$PROV_TOKEN" "" "activate $PNAME"
  post_idem "$IH/api/identity/v1alpha/participants/$PNAME/dids/publish" "$ADMIN_TOKEN" "{\"did\": \"$PDID\"}" "publish $PNAME DID"

  # Re-fetch admin token (per-iteration; cheap)
  ADMIN_TOKEN=$(token admin edc-v-admin-secret "identity-api:write issuer-admin-api:write")

  for credtype in MembershipCredential PartnerCredential; do
    case "$credtype" in
      MembershipCredential) credid=membership-credential-def ;;
      PartnerCredential)    credid=partner-credential-def ;;
    esac
    R=$(curl -sS -w "\n%{http_code}" -X POST "$IH/api/identity/v1alpha/participants/$PNAME/credentials/request" \
          -H "Authorization: Bearer $ADMIN_TOKEN" \
          -H "Content-Type: application/json" \
          -d "{\"issuerDid\": \"$DID_ISSUER\",
               \"holderPid\": \"credreq-$PNAME-$credtype\",
               \"credentials\": [{\"format\": \"VC1_0_JWT\",
                                  \"type\": \"$credtype\",
                                  \"id\": \"$credid\"}]}")
    status=$(echo "$R" | tail -n1)
    body=$(echo "$R" | sed '$d')
    ok_or_409 "$status" "credential request $PNAME $credtype" "$body"

    echo "    waiting for $PNAME $credtype to be ISSUED..."
    for i in $(seq 1 30); do
      R=$(curl -sS -w "\n%{http_code}" "$IH/api/identity/v1alpha/participants/$PNAME/credentials/request/credreq-$PNAME-$credtype" \
            -H "Authorization: Bearer $ADMIN_TOKEN")
      status=$(echo "$R" | tail -n1)
      body=$(echo "$R" | sed '$d')
      if [ "$status" -eq 200 ]; then
        st=$(echo "$body" | sed -n 's/.*"status":"\([^"]*\)".*/\1/p')
        echo "    [$i/30] status=$st"
        if [ "$st" = "ISSUED" ]; then echo "    OK $PNAME $credtype issued"; break; fi
      fi
      sleep 5
    done
  done
}

seed_participant participant-a "$DID_A" "$IH_A" "http://ih-a:7082" "http://cp-a:8082/api/dsp/2025-1"
seed_participant participant-b "$DID_B" "$IH_B" "http://ih-b:7082" "http://cp-b:8082/api/dsp/2025-1"

# ---- Step 3: Provider-side asset / policy / contract definition ----
echo "[seed] === provider catalog (cp-a) ==="

post_unauth "$CP_A_MGMT/v4/assets" "$(cat <<'EOF'
{
  "@context": ["https://w3id.org/edc/connector/management/v2"],
  "@id": "resources",
  "@type": "Asset",
  "properties": { "description": "PoC resources.txt asset shared by Participant A." },
  "dataAddress": {
    "@type": "DataAddress",
    "type": "HttpData",
    "baseUrl": "http://resources-a:80/resources.txt"
  }
}
EOF
)" "asset 'resources'"

# Bind the MembershipCredential left operand to scopes via a CelExpression.
# Without this, the controlplane rejects policy definitions that reference it.
post_unauth "$CP_A_MGMT/v5beta/celexpressions" "$(cat <<'EOF'
{
  "@context": ["https://w3id.org/edc/connector/management/v2"],
  "@type": "CelExpression",
  "@id": "membership-cel",
  "leftOperand": "MembershipCredential",
  "description": "Holder must present an active MembershipCredential.",
  "scopes": ["catalog", "contract.negotiation", "transfer.process"],
  "expression": "ctx.agent.claims.vc.filter(c, c.type.exists(t, t.contains('MembershipCredential'))).exists(c, c.credentialSubject.exists(cs, timestamp(cs.membershipStartDate) < now))"
}
EOF
)" "CEL expression membership-cel"

post_unauth "$CP_A_MGMT/v5beta/celexpressions" "$(cat <<'EOF'
{
  "@context": ["https://w3id.org/edc/connector/management/v2"],
  "@type": "CelExpression",
  "@id": "partner-cel",
  "leftOperand": "PartnerCredential",
  "description": "Holder must present an active PartnerCredential.",
  "scopes": ["catalog", "contract.negotiation", "transfer.process"],
  "expression": "ctx.agent.claims.vc.filter(c, c.type.exists(t, t.contains('PartnerCredential'))).exists(c, c.credentialSubject.exists(cs, timestamp(cs.membershipStartDate) < now))"
}
EOF
)" "CEL expression partner-cel"

post_unauth "$CP_A_MGMT/v4/policydefinitions" "$(cat <<'EOF'
{
  "@context": ["https://w3id.org/edc/connector/management/v2"],
  "@type": "PolicyDefinition",
  "@id": "require-membership",
  "policy": {
    "@type": "Set",
    "permission": [
      {"action": "use",
       "constraint": {"leftOperand": "MembershipCredential",
                      "operator": "eq", "rightOperand": "active"}}
    ]
  }
}
EOF
)" "policy require-membership"

post_unauth "$CP_A_MGMT/v4/contractdefinitions" "$(cat <<'EOF'
{
  "@context": ["https://w3id.org/edc/connector/management/v2"],
  "@id": "membership-only-def",
  "@type": "ContractDefinition",
  "accessPolicyId": "require-membership",
  "contractPolicyId": "require-membership",
  "assetsSelector": {
    "@type": "Criterion",
    "operandLeft": "https://w3id.org/edc/v0.0.1/ns/id",
    "operator": "=",
    "operandRight": "resources"
  }
}
EOF
)" "contract def membership-only"

# Register Participant A's dataplane with cp-a so HTTP-PULL transfers can flow.
post_unauth "$CP_A_MGMT/v4beta/dataplanes" "$(cat <<'EOF'
{
  "@context": ["https://w3id.org/edc/connector/management/v2"],
  "@id": "dp-a",
  "url": "http://dp-a:8083/api/control/v1/dataflows",
  "allowedSourceTypes": ["HttpData"],
  "allowedTransferTypes": ["HttpData-PULL"]
}
EOF
)" "register dataplane dp-a"

echo "[seed] DONE."
