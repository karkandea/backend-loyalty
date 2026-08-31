#!/usr/bin/env bash
set -euo pipefail

IMAGE_NAME="${IMAGE_NAME:-backend-loyalty:verify}"
CONTAINER_NAME="${CONTAINER_NAME:-backend-loyalty-verify}"
PORT="${PORT:-5092}"
ENV_FILE="${ENV_FILE:-.env.vps}"

if ! command -v docker >/dev/null 2>&1; then
  echo "ERROR: docker is required"
  exit 1
fi

if ! command -v curl >/dev/null 2>&1; then
  echo "ERROR: curl is required"
  exit 1
fi

if [[ ! -f "$ENV_FILE" ]]; then
  echo "ERROR: $ENV_FILE not found"
  echo "Create it from .env.example and fill real server-side values."
  exit 1
fi

echo "==> Building $IMAGE_NAME"
docker build -t "$IMAGE_NAME" .

echo "==> Replacing validation container $CONTAINER_NAME"
docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true

docker run -d \
  --name "$CONTAINER_NAME" \
  --add-host=host.docker.internal:host-gateway \
  --env-file "$ENV_FILE" \
  -p "127.0.0.1:${PORT}:8080" \
  "$IMAGE_NAME" >/dev/null

cleanup_on_failure() {
  echo
  echo "==> Container logs"
  docker logs --tail 150 "$CONTAINER_NAME" 2>&1 || true
}
trap cleanup_on_failure ERR

echo "==> Waiting for API startup"
for attempt in $(seq 1 30); do
  if curl --fail --silent "http://127.0.0.1:${PORT}/health" >/dev/null; then
    break
  fi

  if ! docker ps --format '{{.Names}}' | grep -qx "$CONTAINER_NAME"; then
    echo "ERROR: container exited during startup"
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
docker stats --no-stream "$CONTAINER_NAME"

echo
echo=""
echo "PASS: backend built, started, and connected to the configured database."
echo "Validation container remains running as: $CONTAINER_NAME"
echo "Stop it with: docker rm -f $CONTAINER_NAME"
