#!/bin/bash
# Builds the API, signs it and has the server install it: migrate, switch,
# restart, health check. Usually run through deploy/deploy.sh, which ships the
# web build too:
#
#   bash deploy/publish-api.sh qa
#   bash deploy/publish-api.sh prod      # asks you to type the build number
#
# Run this FROM THE DEV MACHINE. It needs the .NET SDK, ssh access to the box
# and the laptop's SSH key, which signs the bundle. No sudo password: the server
# side is /usr/local/sbin/enik-deploy, which servrinuse may run because it only
# installs bundles signed by this key (F28, H54; deploy/server/).
# It does not need the Postgres tunnel: migrations go up as a SQL file and are
# applied on the server.
#
# The service is stopped for the swap, a few seconds. There is no blue/green.

set -euo pipefail

case "${1:-}" in
    qa)   ENV_NAME=qa ;;
    prod) ENV_NAME=prod ;;
    *) echo "usage: bash deploy/publish-api.sh qa|prod" >&2; exit 2 ;;
esac

SSH_HOST=servrinuse@192.168.1.101
SSH_PORT=1212
SSH="ssh -p ${SSH_PORT} ${SSH_HOST}"
INCOMING=/var/lib/enik-deploy/incoming
SIGNING_KEY="${ENIK_SIGNING_KEY:-$HOME/.ssh/id_ed25519}"
PROJ_FILE="src/CalisBakalimEnik.Api/CalisBakalimEnik.Api.csproj"
OUT="artifacts/publish-${ENV_NAME}"

cd "$(dirname "$0")/.."

echo "############ API -> ${ENV_NAME} ############"

# ── 0. version ───────────────────────────────────────────────────────────
# The build number lives in Directory.Build.props (<DefaultBuildNumber>) and is
# bumped and committed on every qa publish, the same way publish-web.sh bumps
# pubspec.yaml. Prod ships the number qa tested. The short commit hash is
# stamped in too, with -dirty when uncommitted changes went into the build, so
# /api/version names the exact source it came from. See docs/VERSIONING.md.
PROPS=Directory.Build.props
BUILD=$(grep -oE '<DefaultBuildNumber>[0-9]+' "$PROPS" | grep -oE '[0-9]+$' || true)
[ -n "$BUILD" ] || { echo "!! no <DefaultBuildNumber> in $PROPS" >&2; exit 1; }

if [ "$ENV_NAME" = qa ]; then
    BUILD=$((BUILD + 1))
    sed -i -E "s|<DefaultBuildNumber>[0-9]+</DefaultBuildNumber>|<DefaultBuildNumber>${BUILD}</DefaultBuildNumber>|" "$PROPS"
    if git commit -q "$PROPS" -m "chore(version): api build ${BUILD}" 2>/dev/null; then
        echo "==> api build number -> ${BUILD} (committed, not pushed)"
    else
        echo "!! could not commit the build number bump" >&2; exit 1
    fi
fi

if [ "$ENV_NAME" = prod ] && ! git diff --quiet HEAD -- src "$PROPS"; then
    echo "!! uncommitted changes under src/. Prod ships committed code only." >&2
    exit 1
fi

# Prod is confirmed by typing the build number, so a slip of the up-arrow on
# a qa command line never ships to customers.
if [ "$ENV_NAME" = prod ] && [ "${ENIK_PROD_CONFIRMED:-}" != "api-${BUILD}" ]; then
    read -r -p "Ship API build ${BUILD} ($(git rev-parse --short=8 HEAD)) to PROD? Type ${BUILD} to go on: " answer
    [ "$answer" = "$BUILD" ] || { echo "not confirmed; nothing was shipped"; exit 1; }
fi

GIT_SHA=$(git rev-parse --short=8 HEAD)
git diff --quiet HEAD -- src "$PROPS" || GIT_SHA="${GIT_SHA}-dirty"
echo "==> version $(grep -oE '<VersionPrefix>[^<]+' "$PROPS" | cut -d'>' -f2)+${BUILD}.${GIT_SHA}"

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
    -p:BuildNumber="$BUILD" \
    -p:GitSha="$GIT_SHA" \
    -o "$OUT"
echo "==> built"

# ── 2. migration script ──────────────────────────────────────────────────
# --idempotent so it can be applied to a database at any migration, including
# one already up to date. Generated rather than run by EF against the server:
# the file can be read before it is applied, and the box needs no EF tooling.
# NOT --no-build. dotnet ef reads the DEFAULT (Debug) build output, which the
# Release publish above does not touch, so --no-build silently generates the
# script from whatever was last built in Debug. A migration edited and then
# published would ship a stale script, and it surfaces as SQL that does not
# match the code. Letting ef build costs seconds and removes a class of
# wrong-deploy.
dotnet ef migrations script \
    --idempotent \
    --project src/CalisBakalimEnik.Infrastructure \
    --startup-project src/CalisBakalimEnik.Api \
    --output "${OUT}/migrate.sql"
echo "==> migration script generated ($(wc -l < "${OUT}/migrate.sql") lines)"

# --idempotent wraps every migration in a DO block, which makes the migration's
# SQL PL/pgSQL. There a query whose result nobody reads is the error "query has
# no destination for result data" -- so a bare SELECT works when migrations are
# applied directly and fails only here, on deploy, halfway through a script.
# Catch it before the database is touched. The fix in the migration is to write
# it as a DO block using PERFORM instead of SELECT.
if grep -nE '^    (SELECT|WITH) ' "${OUT}/migrate.sql"; then
    echo "!! the lines above are bare queries inside an idempotent DO wrapper" >&2
    echo "   and will fail with 'query has no destination for result data'." >&2
    exit 1
fi
echo "==> no bare queries inside the idempotent wrappers"

# ── 3. bundle and sign ───────────────────────────────────────────────────
# One file per build, signed with the laptop's SSH key. On the server
# enik-deploy (root) installs only what this key signed, which is what makes it
# safe to let servrinuse start it without a password: that account can upload
# anything, but cannot sign. See deploy/server/enik-deploy.
ID="${BUILD}-${GIT_SHA}"
BUNDLE="api-${ENV_NAME}-${ID}.tar.gz"
PKG="artifacts/bundle-api-${ENV_NAME}"
rm -rf "$PKG" "artifacts/${BUNDLE}" "artifacts/${BUNDLE}.sig"
mkdir -p "$PKG"
mv "$OUT" "$PKG/payload"
{
    echo "kind=api"
    echo "env=${ENV_NAME}"
    echo "id=${ID}"
    echo "version=$(grep -oE '<VersionPrefix>[^<]+' "$PROPS" | cut -d'>' -f2)+${BUILD}"
    echo "built_at=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
} > "$PKG/manifest.txt"
tar -czf "artifacts/${BUNDLE}" -C "$PKG" manifest.txt payload
ssh-keygen -Y sign -f "${SIGNING_KEY}" -n enik-deploy "artifacts/${BUNDLE}" 2> /dev/null \
    || { echo "!! could not sign with ${SIGNING_KEY}" >&2; exit 1; }
echo "==> bundle ${BUNDLE} ($(du -h "artifacts/${BUNDLE}" | cut -f1)), signed"

# ── 4. ship and install ──────────────────────────────────────────────────
# enik-deploy stops the service, migrates as the migrations role, switches the
# release symlink in one rename, starts, and waits for /api/health/ready. If the
# new build is not healthy it switches back to the previous one by itself.
scp -q -P "${SSH_PORT}" "artifacts/${BUNDLE}" "artifacts/${BUNDLE}.sig" "${SSH_HOST}:${INCOMING}/"
echo "==> uploaded"
$SSH "sudo -n /usr/local/sbin/enik-deploy install ${BUNDLE}"
rm -rf "$PKG"
