#!/usr/bin/env bash
# Builds the api and workers images from this checkout, for anyone without access to
# the published ones (and for CI, which tests the compose bundle against the code in
# the branch rather than against a release).
#
# There is deliberately no Dockerfile: `dotnet publish -t:PublishContainer` produces the
# image from the SDK, so there is no second build definition to drift out of step. The
# api image includes the built SPA - see Aictiq.Api.csproj - which is why this needs
# Node and pnpm as well as the .NET SDK.
#
#   ./build-local.sh                 # tags aictiq-local/{api,workers}:local
#   AICTIQ_IMAGE_TAG=v1 ./build-local.sh
#
# Then run compose against them without triggering its default source build:
#   AICTIQ_IMAGE_PULL_POLICY=never AICTIQ_IMAGE_REGISTRY=aictiq-local AICTIQ_IMAGE_TAG=local docker compose up -d
# or put those three lines in .env, which is what this script prints at the end.

set -euo pipefail

registry="${AICTIQ_IMAGE_REGISTRY:-aictiq-local}"
tag="${AICTIQ_IMAGE_TAG:-local}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

missing=()
command -v dotnet >/dev/null || missing+=("dotnet (.NET 10 SDK)")
command -v docker >/dev/null || missing+=("docker")
if [ "${SKIP_WEB_BUILD:-false}" != "true" ]; then
  command -v pnpm >/dev/null || missing+=("pnpm (or set SKIP_WEB_BUILD=true)")
fi

if [ ${#missing[@]} -gt 0 ]; then
  printf 'Missing required tools:\n' >&2
  printf '  - %s\n' "${missing[@]}" >&2
  exit 1
fi

echo "Building ${registry}/api:${tag} and ${registry}/workers:${tag} from ${root}"

# SKIP_WEB_BUILD=true produces a backend-only api image - useful for a quick API
# iteration, useless for actually serving the app, so it is not the default.
publish() {
  local project=$1 name=$2
  dotnet publish "${root}/backend/src/${project}" \
    -c Release \
    -t:PublishContainer \
    -p:SkipWebBuild="${SKIP_WEB_BUILD:-false}" \
    -p:ContainerRegistry= \
    -p:ContainerRepository="${registry}/${name}" \
    -p:ContainerImageTag="${tag}"
}

publish Api api
publish Workers workers

cat <<EOF

Done. To run compose against these images, add to deploy/.env:

  AICTIQ_IMAGE_REGISTRY=${registry}
  AICTIQ_IMAGE_TAG=${tag}
  AICTIQ_IMAGE_PULL_POLICY=never

then:  docker compose up -d
EOF
