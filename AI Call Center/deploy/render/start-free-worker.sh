#!/bin/sh
set -eu

dotnet /app/PurpleGlass.Integrations.Worker.dll &
worker_pid=$!
httpd -f -p "${PORT:-10000}" -h /health &
health_pid=$!

terminate() {
  kill -TERM "$worker_pid" "$health_pid" 2>/dev/null || true
  wait "$worker_pid" 2>/dev/null || true
  wait "$health_pid" 2>/dev/null || true
}

trap terminate TERM INT
wait "$worker_pid"
worker_status=$?
kill -TERM "$health_pid" 2>/dev/null || true
wait "$health_pid" 2>/dev/null || true
exit "$worker_status"
