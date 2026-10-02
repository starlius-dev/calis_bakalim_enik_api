#!/bin/bash
# The one command that ships Çalış Bakalım Enik (F28). From the dev machine:
#
#   bash deploy/deploy.sh qa                 # API, then web
#   bash deploy/deploy.sh prod               # the same; asks you to confirm the versions
#   bash deploy/deploy.sh qa api             # only the API (or: web)
#   bash deploy/deploy.sh rollback qa api    # back to the previous release (or: web)
#   bash deploy/deploy.sh status             # what is live, what is one step back
#
# Each half builds, signs the bundle with the laptop's SSH key and hands it to
# /usr/local/sbin/enik-deploy on the server, which installs it into its own
# release folder and switches over in one rename (H54). No sudo password is
# needed: see deploy/server/ for the server side and why it is safe.
#
# The web half runs from the Flutter repo, found next to this one unless
# ENIK_WEB_REPO says otherwise.

set -euo pipefail

SSH_HOST=servrinuse@192.168.1.101
SSH_PORT=1212
API_REPO="$(cd "$(dirname "$0")/.." && pwd)"
WEB_REPO="${ENIK_WEB_REPO:-$HOME/Flutter/calis_bakalim_enik}"

remote() { ssh -p "$SSH_PORT" "$SSH_HOST" "sudo -n /usr/local/sbin/enik-deploy $*"; }

case "${1:-}" in
    status)
        remote status
        exit 0 ;;
    rollback)
        [[ "${2:-}" =~ ^(qa|prod)$ && "${3:-}" =~ ^(api|web)$ ]] \
            || { echo "usage: bash deploy/deploy.sh rollback qa|prod api|web" >&2; exit 2; }
        if [ "$2" = prod ]; then
            read -r -p "Roll PROD $3 back to the previous release? Type prod to go on: " answer
            [ "$answer" = prod ] || { echo "not confirmed"; exit 1; }
        fi
        remote rollback "$3" "$2"
        exit 0 ;;
    qa|prod) ENV_NAME="$1" ;;
    *) echo "usage: bash deploy/deploy.sh qa|prod [api|web] | rollback qa|prod api|web | status" >&2; exit 2 ;;
esac

WHAT="${2:-all}"
[[ "$WHAT" =~ ^(all|api|web)$ ]] || { echo "ship what? api, web or nothing for both" >&2; exit 2; }
if [ "$WHAT" != api ] && [ ! -f "$WEB_REPO/deploy/publish-web.sh" ]; then
    echo "!! no Flutter repo at $WEB_REPO; set ENIK_WEB_REPO" >&2
    exit 1
fi

# Prod: one question for both halves, naming exactly what goes out. Prod ships
# the numbers qa tested, so nothing is bumped on the way.
if [ "$ENV_NAME" = prod ]; then
    api_build=$(grep -oE '<DefaultBuildNumber>[0-9]+' "$API_REPO/Directory.Build.props" | grep -oE '[0-9]+$')
    web_version=$(grep '^version:' "$WEB_REPO/pubspec.yaml" 2>/dev/null | head -1 | awk '{print $2}')
    echo "About to ship to PROD:"
    [ "$WHAT" = web ] || echo "  API build ${api_build}   ($(git -C "$API_REPO" rev-parse --short=8 HEAD), branch $(git -C "$API_REPO" branch --show-current))"
    [ "$WHAT" = api ] || echo "  web ${web_version}   ($(git -C "$WEB_REPO" rev-parse --short=8 HEAD), branch $(git -C "$WEB_REPO" branch --show-current))"
    expected=$([ "$WHAT" = web ] && echo "$web_version" || echo "$api_build")
    read -r -p "Type ${expected} to go on: " answer
    [ "$answer" = "$expected" ] || { echo "not confirmed; nothing was shipped"; exit 1; }
    export ENIK_PROD_CONFIRMED_API="api-${api_build}" ENIK_PROD_CONFIRMED_WEB="web-${web_version}"
fi

if [ "$WHAT" != web ]; then
    ENIK_PROD_CONFIRMED="${ENIK_PROD_CONFIRMED_API:-}" bash "$API_REPO/deploy/publish-api.sh" "$ENV_NAME"
fi
if [ "$WHAT" != api ]; then
    (cd "$WEB_REPO" && ENIK_PROD_CONFIRMED="${ENIK_PROD_CONFIRMED_WEB:-}" bash deploy/publish-web.sh "$ENV_NAME")
fi

echo
remote status
