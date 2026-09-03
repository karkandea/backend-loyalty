#!/usr/bin/env bash
set -euo pipefail

ENV_FILE="${ENV_FILE:-.env.vps}"
COMPOSE_FILE="${COMPOSE_FILE:-docker-compose.vps.yml}"
API_CONTAINER="${API_CONTAINER:-backend-loyalty}"
DB_CONTAINER="${DB_CONTAINER:-loyalty-postgres}"

if ! command -v docker >/dev/null 2>&1; then
  echo "ERROR: docker is required"
  exit 1
fi

if ! docker compose version >/dev/null 2>&1; then
  echo "ERROR: Docker Compose v2 is required (docker compose ...)"
  exit 1
fi

if ! command -v curl >/dev/null 2>&1; then
  echo "ERROR: curl is required"
  exit 1
fi

if [[ ! -f "$ENV_FILE" ]]; then
  echo "ERROR: $ENV_FILE not found"
  echo "Run scripts/migrate-supabase-to-vps.sh first or create the server-only env file."
  exit 1
fi

PORT="${PORT:-$(grep -m1 '^LOYALTY_API_PORT=' "$ENV_FILE" | cut -d= -f2- || true)}"
PORT="${PORT:-5092}"

echo "==> Building and starting isolated Loyalty API + PostgreSQL"
docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" up -d --build api

cleanup_on_failure() {
  echo
  echo "==> API logs"
  docker logs --tail 150 "$API_CONTAINER" 2>&1 || true
  echo
  echo "==> PostgreSQL logs"
  docker logs --tail 100 "$DB_CONTAINER" 2>&1 || true
}
trap cleanup_on_failure ERR

echo "==> Waiting for API startup"
for attempt in $(seq 1 30); do
  if curl --fail --silent "http://127.0.0.1:${PORT}/health" >/dev/null; then
    break
  fi

  if ! docker ps --format '{{.Names}}' | grep -qx "$API_CONTAINER"; then
    echo "ERROR: API container exited during startup"
    exit 1
  fi

  if [[ "$attempt" -eq 30 ]]; then
    echo "ERROR: API did not become healthy"
    exit 1
  fi

  sleep 2
done

echo "==> GET /health"
curl --fail --show-error --silent "http://127.0.0.1:${PORT}/health"
echo

echo "==> GET /health/db"
curl --fail --show-error --silent "http://127.0.0.1:${PORT}/health/db"
echo

echo "==> Runtime snapshot"
docker stats --no-stream "$API_CONTAINER" "$DB_CONTAINER"

echo
echo "PASS: backend built, started, and connected to isolated Loyalty PostgreSQL."
echo "API is bound locally at http://127.0.0.1:${PORT}"
echo "PostgreSQL has no host port mapping."
