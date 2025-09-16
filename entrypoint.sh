#!/bin/bash
set -Eeuo pipefail

TARGET_USER=appuser
TARGET_UID=1654
TARGET_GID=1654
LOG_FILE="/app/application.log"

log() { echo "$(date '+%Y-%m-%d %H:%M:%S') - $1" | tee -a "$LOG_FILE"; }

if [ "$(id -u)" = "0" ]; then
  # ensure log + data dir exist and are owned
  touch "$LOG_FILE"
  chown "$TARGET_UID:$TARGET_GID" "$LOG_FILE"

  install -d -o "$TARGET_UID" -g "$TARGET_GID" /app/openssl
  # best-effort fixups (fast if already correct)
  chown -R "$TARGET_UID:$TARGET_GID" /app/openssl || true
  chmod -R u+rwX /app/openssl || true

  exec capsh --keep=1 \
    --user="$TARGET_USER" --gid="$TARGET_GID" \
    --caps="cap_net_raw,cap_net_admin,cap_net_bind_service+epi" \
    --addamb=cap_net_raw,cap_net_admin,cap_net_bind_service \
    -- -c "exec /app/entrypoint.sh run \"$@\""
fi

if [ "${1:-}" = "run" ]; then
  shift
  if command -v xvfb-run >/dev/null 2>&1; then
    log "Using xvfb-run for virtual display."
    export DISPLAY=:99
    exec xvfb-run --auto-servernum --server-args="-screen 0 1920x1080x24" \
      dotnet /app/NetworkMonitorProcessor-debian12.dll "$@" 2>&1 | tee -a "$LOG_FILE"
  else
    log "xvfb-run not found. Running directly."
    exec dotnet /app/NetworkMonitorProcessor-debian12.dll "$@" 2>&1 | tee -a "$LOG_FILE"
  fi
fi

