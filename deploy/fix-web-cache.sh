#!/bin/bash
# ONE-TIME: stop nginx pinning every visitor to the bundle they first loaded.
#
#   sudo bash /var/www/calis_bakalim_enik_api/deploy/fix-web-cache.sh
#
# The web sites were generated with
#   Cache-Control: public, max-age=31536000, immutable
# on every .js, on the premise that Flutter emits content-hashed filenames. It
# does not: main.dart.js keeps its name on every build and flutter_bootstrap.js
# requests it by that bare name. So a returning visitor keeps their first
# bundle for a year and no deploy ever reaches them.
#
# This swaps that one header for "no-cache", which still STORES the file and
# revalidates before use: a repeat load is a conditional request answered 304.
# install-slot.sh has been fixed too, so a future slot is built correctly.

set -euo pipefail
[ "$(id -u)" -eq 0 ] || { echo "!! run me with sudo" >&2; exit 1; }

OLD='add_header Cache-Control "public, max-age=31536000, immutable" always;'
NEW='add_header Cache-Control "no-cache" always;'
CHANGED=0

for SITE in /etc/nginx/sites-available/qa_calis_bakalim_enik_web \
            /etc/nginx/sites-available/prod_calis_bakalim_enik_web; do
    [ -f "$SITE" ] || { echo "==> $SITE not present, skipping"; continue; }

    if ! grep -qF "$OLD" "$SITE"; then
        echo "==> $(basename "$SITE") already fixed"
        continue
    fi

    cp -p "$SITE" "${SITE}.bak-$(date +%Y%m%d%H%M%S)"
    # Only this project's files are touched; other projects' sites are not read.
    sed -i "s|$OLD|$NEW|" "$SITE"
    # etag is nginx's default for static files, but say so where the caching
    # decision is made rather than relying on a default staying put.
    sed -i "/$NEW/a\        etag on;" "$SITE"
    echo "==> $(basename "$SITE") patched (backup alongside)"
    CHANGED=1
done

if [ "$CHANGED" -eq 0 ]; then
    echo "==> nothing to do"
    exit 0
fi

# Never reload on a bad config: this nginx serves other projects, and taking
# them down is the one outcome that is not ours to risk.
if nginx -t; then
    systemctl reload nginx
    echo "==> nginx reloaded"
else
    echo "!! nginx -t FAILED. Restoring the backups and NOT reloading." >&2
    for SITE in /etc/nginx/sites-available/qa_calis_bakalim_enik_web \
                /etc/nginx/sites-available/prod_calis_bakalim_enik_web; do
        LATEST=$(ls -1t "${SITE}".bak-* 2>/dev/null | head -1 || true)
        [ -n "$LATEST" ] && cp -p "$LATEST" "$SITE" && echo "   restored $(basename "$SITE")" >&2
    done
    exit 1
fi

echo
echo "Check it:"
echo "  curl -sI https://qa-calis-bakalim-enik.starlius.com/main.dart.js | grep -i cache-control"
echo "  expected: Cache-Control: no-cache"
