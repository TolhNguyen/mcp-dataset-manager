#!/usr/bin/env bash
# Deploy helper. Initializes .env on first run, generates JWT_KEY if missing,
# then brings API + Postgres + remote MCP bridge + Caddy up via Docker Compose.

set -euo pipefail

cd "$(dirname "$0")/.."

if [[ ! -f .env ]]; then
    echo "Creating .env from .env.example…"
    cp .env.example .env
fi

# Generate a random JWT_KEY if the user left it blank.
if ! grep -q '^JWT_KEY=.\+' .env; then
    if command -v openssl >/dev/null 2>&1; then
        KEY="$(openssl rand -hex 32)"
    else
        KEY="$(head -c 32 /dev/urandom | xxd -p | tr -d '\n')"
    fi
    awk -v k="$KEY" '/^JWT_KEY=/{print "JWT_KEY=" k; next}1' .env > .env.tmp
    mv .env.tmp .env
    echo "Generated JWT_KEY (64 hex chars). Keep .env secret."
fi

# Keep a production tools.md available for the bridge volume mount.
if [[ ! -f mcp-bridge/tools.md ]]; then
    cp mcp-bridge/tools.example.md mcp-bridge/tools.md
fi

set -a
# shellcheck disable=SC1091
source .env
set +a

echo "Building and starting API + DB + bridge + Caddy…"
docker compose up -d --build

echo
echo "Web app:      https://${EDM_DOMAIN:-localhost}/"
echo "MCP endpoint: https://${EDM_DOMAIN:-localhost}/mcp"
echo "Local API:    http://localhost:${API_PORT:-5847}/"
