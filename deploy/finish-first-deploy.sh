#!/bin/bash
# Finishes a deploy whose build is already uploaded to <env>.staging.
#
#   sudo bash /var/www/calis_bakalim_enik_api/deploy/finish-first-deploy.sh qa
#   sudo bash /var/www/calis_bakalim_enik_api/deploy/finish-first-deploy.sh prod
#
# Run ON THE SERVER. It does only the parts that need root: stop, migrate, swap,
# start. The build and upload happen from the dev machine and need no sudo.
#
# For a routine deploy use `bash deploy/publish-api.sh <env>` from the dev
# machine instead -- it does the build, the upload and all of this in one go,
# asking for sudo twice over ssh. This script exists for a first install, where
# the env file has to be filled in by hand between the two halves.

set -euo pipefail

case "${1:-}" in
    qa)   ENV_NAME=qa;   PORT=3011 ;;
    prod) ENV_NAME=prod; PORT=3010 ;;
    *) echo "usage: sudo bash finish-first-deploy.sh qa|prod" >&2; exit 2 ;;
esac

PROJECT=calis_bakalim_enik
UNIT="${ENV_NAME}_${PROJECT}"
BASE="/var/www/${PROJECT}_api"
LIVE="${BASE}/${ENV_NAME}"
STAGE="${BASE}/${ENV_NAME}.staging"
PGENV="${BASE}/deploy/${PROJECT}_${ENV_NAME}.env"
APP_ENV="${LIVE}/${PROJECT}_api.env"

[ "$(id -u)" -eq 0 ] || { echo "!! run me with sudo" >&2; exit 1; }

echo "############ ${UNIT} ############"

# ── 0. sanity, before anything is touched ────────────────────────────────
for f in "${STAGE}/CalisBakalimEnik.Api" "${STAGE}/migrate.sql" "$PGENV" "$APP_ENV"; do
    [ -f "$f" ] || { echo "!! missing: $f" >&2; exit 1; }
done

# The API throws at startup without a real mailer and without a signing key.
# Catch both here, where the message is plain, rather than in a restart loop.
grep -qE '^Email__ApiKey=.+' "$APP_ENV" || {
    echo "!! Email__ApiKey is empty in ${APP_ENV}." >&2
    echo "   The API refuses to boot without it. Fill it in, then re-run." >&2
    exit 1; }
grep -qE '^Jwt__PrivateKeyPath=.+' "$APP_ENV" || {
    echo "!! Jwt__PrivateKeyPath is not set in ${APP_ENV}." >&2
    echo "   Without it there is nothing to sign access tokens with." >&2
    exit 1; }
echo "==> staging, credentials and a filled-in env are all present"

# ── 1. stop ──────────────────────────────────────────────────────────────
# reset-failed too: a slot started before its first publish restart-loops on
# 203/EXEC, and once that trips the rate limit `start` refuses outright.
systemctl stop "$UNIT" 2>/dev/null || true
systemctl reset-failed "$UNIT" 2>/dev/null || true
echo "==> ${UNIT} stopped"

# ── 2. migrate ───────────────────────────────────────────────────────────
# As the MIGRATIONS role, never the app role: the app role holds no schema
# rights on purpose, so an injection bug in the running service cannot alter or
# drop anything. --idempotent, so a database already up to date is a no-op.
# shellcheck disable=SC1090
. "$PGENV"
PGPASSWORD="$PGPASSWORD_MIGRATIONS" psql \
    -h "$PGHOST" -p "$PGPORT" -U "$PGUSER_MIGRATIONS" -d "$PGDATABASE" \
    -v ON_ERROR_STOP=1 -q -f "${STAGE}/migrate.sql"
echo "==> migrations applied as ${PGUSER_MIGRATIONS}"

# ── 3. swap ──────────────────────────────────────────────────────────────
# --delete so a file dropped from the build stops existing, but never the env
# file or a backup of it: it holds the Resend key and the database password and
# is not part of the build.
rsync -a --delete --exclude="${PROJECT}_api.env*" "${STAGE}/" "${LIVE}/"
chmod +x "${LIVE}/CalisBakalimEnik.Api"
echo "==> swapped into ${LIVE}"

# ── 4. start ─────────────────────────────────────────────────────────────
systemctl start "$UNIT"
echo "==> start issued, waiting for /api/health/ready"

for _ in $(seq 1 30); do
    if curl -sf -o /dev/null "http://127.0.0.1:${PORT}/api/health/ready"; then
        echo
        echo "==> HEALTHY on 127.0.0.1:${PORT}"
        curl -s "http://127.0.0.1:${PORT}/api/version"; echo
        rm -rf "$STAGE"
        echo "==> staging cleaned up. Done."
        exit 0
    fi
    sleep 2
done

echo >&2
echo "!! not healthy after 60s. Last 40 log lines:" >&2
journalctl -u "$UNIT" -n 40 --no-pager >&2
echo "   Staging left at ${STAGE} so nothing is lost." >&2
exit 1
