# Dataspace PoC

A minimal, runtime-decentralized dataspace demo on Windows 11 with **podman**, using **only stock Eclipse EDC 0.17.0 images** (no custom builds, no metaform/CFM).

- **Participant A** — provider, exposes `resources.txt` as an asset
- **Participant B** — consumer, holds an issued `MembershipCredential`, negotiates and pulls the asset
- **Issuer** — issues the `MembershipCredential` over DCP

A↔B speak **real DSP 2025-1 and DCP**. The supporting infra (Postgres, HashiCorp Vault, Keycloak) is hosted as stock containers so the EDC 0.17.0 JARs (which are wired against those backends) work unmodified.

## Stack

11 containers on a single `dataspaces` podman network:

| Service | Image | Purpose |
| --- | --- | --- |
| `ds-postgres` | `postgres:17-alpine` | Shared multi-database backend (one DB per EDC service) |
| `ds-vault` | `hashicorp/vault:1.18` | Dev-mode HashiCorp Vault — required by EDC's HashicorpVaultExtension |
| `ds-keycloak` | `quay.io/keycloak/keycloak:26.0` | Pre-imported `mvd` realm; OAuth2 + JWT for service-to-service auth |
| `ds-vault-bootstrap` | `hashicorp/vault:1.18` | One-shot job; enables JWT auth + KV mounts + writes the AES key |
| `ds-cp-a` / `ds-cp-b` | `ghcr.io/eclipse-edc/.../controlplane:0.17.0` | Participant control planes |
| `ds-ih-a` / `ds-ih-b` | `ghcr.io/eclipse-edc/.../identity-hub:0.17.0` | Per-participant identity hub (DCP holder, STS, DID:web publisher) |
| `ds-dp-a` / `ds-dp-b` | `ghcr.io/eclipse-edc/.../dataplane:0.17.0` | HTTP-PULL data planes |
| `ds-issuer` | `ghcr.io/eclipse-edc/.../issuerservice:0.17.0` | Credential issuer (DCP) |
| `ds-resources-a` | `nginx:1.27-alpine` | Static server hosting `resources.txt` (the actual data behind A's asset) |

DIDs (resolved on the internal network only):

- `did:web:ih-a%3A7083:participant-a`
- `did:web:ih-b%3A7083:participant-b`
- `did:web:issuer%3A10016:issuer`

## Layout

```
infra/
  compose.yaml             single compose file for the whole stack
  postgres/init.sh         creates the 8 databases inside the postgres container
  vault/bootstrap.sh       idempotent vault setup (jwt auth, kv mounts, aes key)
  keycloak/realm-mvd.json  pre-imported realm with admin/provisioner/issuer/participant-* clients
  seed/seed.sh             creates issuer tenant, attestation+credential defs,
                           per-participant holders + IH contexts + credential
                           issuance, then provider asset + CEL + policy + contract
                           def + dataplane registration
ops/
  launch.ps1               bring up the stack and run the seed
  reset.ps1                tear everything down (compose down -v + prune)
  test-roundtrip.ps1       end-to-end: catalog -> negotiation -> transfer -> pull
participantA/              WinForms client (provider-side admin UI)
participantB/              WinForms client (consumer-side admin UI)
resources.txt              the actual asset payload
```

## Quick start

```powershell
# Bring up the stack and seed it (idempotent)
powershell -NoProfile -ExecutionPolicy Bypass -File .\ops\launch.ps1

# Run the full happy path (catalog + negotiation + transfer + pull)
powershell -NoProfile -ExecutionPolicy Bypass -File .\ops\test-roundtrip.ps1

# Tear down
powershell -NoProfile -ExecutionPolicy Bypass -File .\ops\reset.ps1
```

A successful round-trip prints the contents of `resources.txt` after streaming through `cp-b → cp-a → dp-a (HttpData-PULL) → resources-a`.

## Host port allocation

| Port range | Service |
| --- | --- |
| `5432` | postgres |
| `8200` | vault |
| `8888` | keycloak (admin/admin) |
| `18080-18083` | participant A controlplane |
| `17080-17084` | participant A identity hub |
| `11100/11102/11186` | participant A dataplane |
| `28080-28083` | participant B controlplane |
| `27080-27084` | participant B identity hub |
| `21100/21102/21186` | participant B dataplane |
| `19010-19016, 19999` | issuer service |

Internal DSP and DCP traffic uses the container hostnames (`cp-a`, `cp-b`, `ih-a`, `ih-b`, `issuer`); only management surfaces are exposed to the host.

## Notes / gotchas

- After creating a `ParticipantContext`, you must `POST /participants/{id}/state?isActive=true` and then `POST /participants/{id}/dids/publish`. The publish call requires the `admin` Keycloak client (provisioner returns 403).
- `IdentityHub` ports inside the container: `7080` default/readiness, `7081` identity mgmt, `7082` CredentialService, `7083` did:web, `7084` STS. Register the `CredentialService` service endpoint on **port 7082**, otherwise issuance is stuck at `REQUESTED`.
- Before posting a `PolicyDefinition` that references a credential left-operand, post a `CelExpression` to `/api/mgmt/v5beta/celexpressions` binding that left-operand to scopes `[catalog, contract.negotiation, transfer.process]`.
- `podman compose` shells out to Docker Desktop's `docker-compose.exe` only as a YAML parser — the engine is podman. The compose project name prefixes the network, so the actual network is `dataspaces_dataspaces`.
