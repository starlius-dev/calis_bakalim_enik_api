#!/bin/bash
# Builds the API, ships it, migrates the database and restarts the service.
#
#   bash deploy/publish-api.sh qa
#   bash deploy/publish-api.sh prod
#
# Run this FROM THE DEV MACHINE, in a terminal — it needs the .NET SDK, ssh
# access to the box, and it will ask for your sudo password twice (stop, start).
# It does not need the Postgres tunnel: migrations go up as a SQL file and are
# applied on the server.
#
# The service is stopped for the swap. There is no blue/green here and for a
# test slot there does not need to be; the window is a few seconds.

set -euo pipefail

case "${1:-}" in
    qa)   ENV_NAME=qa;   PORT=3011 ;;
    prod) ENV_NAME=prod; PORT=3010 ;;
    *) echo "usage: bash deploy/publish-api.sh qa|prod" >&2; exit 2 ;;
esac

PROJECT=calis_bakalim_enik
SSH_HOST=servrinuse@192.168.1.101
SSH_PORT=1212
SSH="ssh -p ${SSH_PORT} ${SSH_HOST}"
SSH_TTY="ssh -t -p ${SSH_PORT} ${SSH_HOST}"

REMOTE_BASE="/var/www/${PROJECT}_api"
REMOTE_LIVE="${REMOTE_BASE}/${ENV_NAME}"
REMOTE_STAGE="${REMOTE_BASE}/${ENV_NAME}.staging"
UNIT="${ENV_NAME}_${PROJECT}"
PROJ_FILE="src/CalisBakalimEnik.Api/CalisBakalimEnik.Api.csproj"
OUT="artifacts/publish-${ENV_NAME}"

cd "$(dirname "$0")/.."

echo "############ API -> ${ENV_NAME} ############"

# ── 1. build ─────────────────────────────────────────────────────────────
# Self-contained linux-x64, because the unit's ExecStart is the binary itself
# rather than `dotnet X.dll` — the house style here, and it means the box needs
# no .NET runtime at all.
rm -rf "$OUT"
dotnet publish "$PROJ_FILE" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=false \
    -o "$OUT"
echo "==> built"

# ── 2. migration script ──────────────────────────────────────────────────
# --idempotent so it can be applied to a database at any migration, including
# one already up to date. Generated rather than run by EF against the server:
# the file can be read before it is applied, and the box needs no EF tooling.
dotnet ef migrations script \
    --idempotent \
    --project src/CalisBakalimEnik.Infrastructure \
    --startup-project src/CalisBakalimEnik.Api \
    --output "${OUT}/migrate.sql" \
    --no-build 2>/dev/null \
  || dotnet ef migrations script --idempotent \
        --project src/CalisBakalimEnik.Infrastructure \
        --startup-project src/CalisBakalimEnik.Api \
        --output "${OUT}/migrate.sql"
echo "==> migration script generated ($(wc -l < "${OUT}/migrate.sql") lines)"

# ── 3. ship to a staging directory ───────────────────────────────────────
# Into staging rather than over the live directory, so a half-finished transfer
# never becomes the thing systemd tries to start.
$SSH "mkdir -p ${REMOTE_STAGE}"
rsync -az --delete -e "ssh -p ${SSH_PORT}" "${OUT}/" "${SSH_HOST}:${REMOTE_STAGE}/"
echo "==> uploaded to ${REMOTE_STAGE}"

# ── 4. stop, migrate, swap, start ────────────────────────────────────────
echo
echo "--- sudo needed to stop ${UNIT} ---"
$SSH_TTY "sudo systemctl stop ${UNIT}"

# Migrations run as the MIGRATIONS role, never as the app role. The app role
# holds no schema rights on purpose, so a SQL-injection bug in the running
# service cannot alter or drop anything. Credentials come from the file
# provision-db.sh wrote; they are never printed.
$SSH bash -s <<REMOTE
set -euo pipefail
PGENV="${REMOTE_BASE}/deploy/${PROJECT}_${ENV_NAME}.env"
if [ ! -f "\$PGENV" ]; then
    echo "!! \$PGENV missing — run provision-db.sh ${ENV_NAME} first" >&2
    exit 1
fi
# shellcheck disable=SC1090
. "\$PGENV"
PGPASSWORD="\$PGPASSWORD_MIGRATIONS" psql \
    -h "\$PGHOST" -p "\$PGPORT" -U "\$PGUSER_MIGRATIONS" -d "\$PGDATABASE" \
    -v ON_ERROR_STOP=1 -q -f "${REMOTE_STAGE}/migrate.sql"
echo "==> migrations applied as \$PGUSER_MIGRATIONS"

rsync -a --delete --exclude='${PROJECT}_api.env' \
    "${REMOTE_STAGE}/" "${REMOTE_LIVE}/"
chmod +x "${REMOTE_LIVE}/CalisBakalimEnik.Api"
echo "==> swapped into ${REMOTE_LIVE}"
REMOTE

echo
echo "--- sudo needed to start ${UNIT} ---"
$SSH_TTY "sudo systemctl start ${UNIT}"

# ── 5. did it actually come up ───────────────────────────────────────────
echo "==> waiting for /api/health/ready"
for i in $(seq 1 30); do
    if $SSH "curl -sf -o /dev/null http://127.0.0.1:${PORT}/api/health/ready"; then
        echo "==> healthy"
        $SSH "curl -s http://127.0.0.1:${PORT}/api/version"
        echo
        exit 0
    fi
    sleep 2
done

echo "!! did not become healthy in 60s. The service log:" >&2
$SSH "sudo journalctl -u ${UNIT} -n 40 --no-pager" 2>/dev/null || \
    echo "   run: sudo journalctl -u ${UNIT} -n 40 --no-pager" >&2
exit 1
