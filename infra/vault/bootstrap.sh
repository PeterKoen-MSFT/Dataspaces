#!/bin/sh
# Vault bootstrap. Runs once after vault + keycloak are healthy.
# Configures JWT auth backed by Keycloak, creates participants KV mount,
# writes the AES encryption key required by EDC, and creates roles for
# provisioner + participant principals.
#
# Token issuer URL must match what Keycloak puts into the `iss` claim,
# which in our compose setup is http://keycloak:8080/realms/mvd.

set -eu

echo "[vault-bootstrap] Waiting for vault to respond..."
until vault status >/dev/null 2>&1; do sleep 1; done
echo "[vault-bootstrap] Vault is up."

# Idempotent enable
vault auth enable jwt 2>/dev/null || echo "[vault-bootstrap] jwt auth already enabled"
vault secrets enable -path=participants -version=2 kv 2>/dev/null || echo "[vault-bootstrap] participants kv already enabled"
vault secrets enable -path=secret -version=1 kv 2>/dev/null || echo "[vault-bootstrap] secret kv already enabled"

vault write auth/jwt/config \
  jwks_url="http://keycloak:8080/realms/mvd/protocol/openid-connect/certs" \
  bound_issuer="http://keycloak:8080/realms/mvd" \
  default_role="participant"

ACCESSOR=$(vault auth list -format=json | grep -o '"jwt_[a-f0-9]*"' | head -1 | tr -d '"' )
if [ -z "$ACCESSOR" ]; then
  ACCESSOR=$(vault auth list | awk '/^jwt\//{print $3; exit}')
fi
echo "[vault-bootstrap] JWT accessor: $ACCESSOR"

vault policy write participants-restricted - <<EOF
path "participants/data/{{identity.entity.aliases.${ACCESSOR}.name}}/*" {
  capabilities = ["create", "read", "update", "delete", "list"]
}
path "participants/metadata/{{identity.entity.aliases.${ACCESSOR}.name}}/*" {
  capabilities = ["list"]
}
EOF

vault policy write provisioner-policy - <<'EOF'
path "*"      { capabilities = ["create", "read", "update", "delete", "list", "sudo"] }
path "sys/*"  { capabilities = ["create", "read", "update", "delete", "list", "sudo"] }
EOF

vault write auth/jwt/role/participant - <<EOF
{
  "role_type": "jwt",
  "user_claim": "participant_context_id",
  "bound_issuer": "http://keycloak:8080/realms/mvd",
  "bound_claims": { "role": "participant" },
  "token_policies": ["participants-restricted"],
  "clock_skew_leeway": 60
}
EOF

vault write auth/jwt/role/provisioner - <<EOF
{
  "role_type": "jwt",
  "user_claim": "azp",
  "bound_issuer": "http://keycloak:8080/realms/mvd",
  "bound_claims": { "role": "provisioner" },
  "token_policies": ["provisioner-policy"],
  "clock_skew_leeway": 60
}
EOF

# AES key required by EDC services (alias = aes-key-alias).
vault kv put secret/aes-key-alias content="yHo9w6m2KOI3FE7vI+fcN6j86JDQ6V10lJPlv9lLWoE="

echo "[vault-bootstrap] Done."
