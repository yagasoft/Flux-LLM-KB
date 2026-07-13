# Local-only deployment design

## Goal

Production installation, update, and feature-closeout commands must use only
locally available packages and container images unless an operator explicitly
opts in to a refresh.

## Observed state

The running Ollama service uses the derived local image `flux-ollama:local`,
also tagged `flux-ollama:d427aa7`. It carries its revision and runtime
fingerprint labels. The upstream base tag `ollama/ollama:latest` is not local.
The previous update script treated that missing base tag as a reason to pull
from the network before it could decide whether the derived runtime needed a
rebuild.

## Chosen design

- `install-flux.ps1` and `update-flux.ps1` gain explicit `-AllowImagePull` and
  `-AllowPackageRefresh` switches. Both default to disabled.
- Missing Docker images fail with an actionable message when image pulling is
  not allowed. Existing image tags are reused and never refreshed implicitly.
- A local `flux-ollama:local` runtime is discovered before the upstream
  `ollama/ollama:latest` base is considered. When the base is absent and the
  local runtime exists, deployment reuses that runtime and retains its existing
  local and revision tags plus labels.
- The Docker build-base resolver refuses to fall back to `python:3.12-slim`
  unless image pulling was explicitly allowed, preventing BuildKit from making
  an implicit base-image download.
- Package refreshes require `-AllowPackageRefresh`; otherwise npm and pip stay
  offline. Feature closeout uses `npm ci --offline` by default and forwards
  either opt-in switch only when requested.

## Explicit refresh path

Operators who intentionally want to obtain missing images use
`-AllowImagePull`. Operators who intentionally want npm or pip network access
use `-AllowPackageRefresh` together with the existing offline controls as
needed. Neither flag is enabled by normal feature closeout.

## Non-goals

- Do not retag `flux-ollama:local` as `ollama/ollama:latest`; that would hide
  the distinction between the derived runtime and its upstream base.
- Do not delete cached images, layers, packages, or runtime labels.
- Do not change model pulls, crawler configuration, or live recovery scope.

## Verification

Regression tests assert the explicit opt-in contract in the production and
feature-closeout scripts. A local-only deployment is then run against the
already discovered `flux-ollama:local` runtime before the Office recovery
requeue proceeds.
