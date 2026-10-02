#!/bin/bash
# One-time: moves Çalış Bakalım Enik to its own system user (I56) and to release
# folders with an atomic switch (H54), and installs enik-deploy, the program the
# laptop's one-command deploy calls (F28). Run on the server from the folder it
# was copied to:
#
#   sudo bash ~/enik-isolation/install-enik-isolation.sh
#
# What it does, per slot (qa first; prod only if qa came back healthy):
#   - the live folder becomes releases/<env>/<build>-<sha>/, root-owned, and
#     /var/www/calis_bakalim_enik_api/<env> becomes a symlink to it; the same
#     for the web build in /var/www/calis_bakalim_enik/<env>;
#   - the app's env file moves to /etc/calis_bakalim_enik/<env>/api.env and the
#     migrations role's to .../db.env, both root 0600: no app on the box can
#     read Enik's database passwords any more, and neither can servrinuse;
#   - files/<env> (key ring, JWT key) becomes owned by the new user `enik`;
#   - the unit runs as `enik`; restarted and health-checked. If it does not come
#     back within 60 s, everything for that slot is put back as it was.
# Plus, once:
#   - /var/www itself becomes root-owned (its project folders keep their owners;
#     only creating a NEW top-level project folder needs sudo from now on).
#     Without this, servrinuse could rename Enik's folder away and put another
#     in its place, and the unit would start that as `enik`;
#   - servrinuse may run /usr/local/sbin/enik-deploy without a password; the
#     program installs only bundles signed by the laptop's key (yk-laptop);
#   - the old deploy scripts (finish-first-deploy.sh etc.) and the *.bak env
#     copies move to the root-only backup folder below; nothing is deleted.
#
# Downtime: about 10 seconds per slot. Other projects are not touched beyond
# /var/www's own owner. Copies of everything changed: /root/enik-i56-<stamp>/.

set -euo pipefail
[ "$(id -u)" -eq 0 ] || { echo "run with sudo" >&2; exit 1; }

HERE="$(cd "$(dirname "$0")" && pwd)"
API=/var/www/calis_bakalim_enik_api
WEB=/var/www/calis_bakalim_enik
ETC=/etc/calis_bakalim_enik
LIB=/var/lib/enik-deploy
STAMP=$(date -u +%Y%m%dT%H%MZ)
SAVE=/root/enik-i56-$STAMP

for f in enik-deploy deploy-signers; do
    [ -f "$HERE/$f" ] || { echo "!! $HERE/$f missing" >&2; exit 1; }
done
grep -q '^yk-laptop namespaces="enik-deploy" ssh-ed25519 ' "$HERE/deploy-signers" \
    || { echo "!! deploy-signers does not look right" >&2; exit 1; }

say() { echo "==> $*"; }
# Anything unexpected stops the script at once. Say where, and how to get the
# slots serving again, since a unit may be stopped at that moment.
trap 'echo "!! stopped at line $LINENO. Copies of what changed: $SAVE." >&2
      echo "   If a slot is down: sudo systemctl start qa_calis_bakalim_enik prod_calis_bakalim_enik" >&2
      echo "   and send Claude this output." >&2' ERR
install -d -m 0700 "$SAVE"
say "copies of everything changed go to $SAVE"

# ── once: user, folders, program, sudo rule ──────────────────────────────
if ! id enik > /dev/null 2>&1; then
    useradd --system --user-group --home-dir /nonexistent --no-create-home \
        --shell /usr/sbin/nologin --comment "Calis Bakalim Enik API" enik
    say "user enik created"
fi

install -d -m 0755 -o root -g root "$ETC"
install -m 0644 -o root -g root "$HERE/deploy-signers" "$ETC/deploy-signers"
install -m 0755 -o root -g root "$HERE/enik-deploy" /usr/local/sbin/enik-deploy
install -d -m 0755 -o root -g root "$LIB"
install -d -m 0700 -o servrinuse -g servrinuse "$LIB/incoming"
install -d -m 0700 -o root -g root "$LIB/work"

SUDOERS=/etc/sudoers.d/enik-deploy
cat > "$SAVE/enik-deploy.sudoers" <<'RULE'
# Enik deploys from the laptop (F28). The program installs only bundles signed
# by the laptop's key, so this does not hand servrinuse the enik account.
servrinuse ALL=(root) NOPASSWD: /usr/local/sbin/enik-deploy
RULE
visudo -cqf "$SAVE/enik-deploy.sudoers" || { echo "!! sudoers rule invalid" >&2; exit 1; }
install -m 0440 -o root -g root "$SAVE/enik-deploy.sudoers" "$SUDOERS"
say "enik-deploy installed, sudo rule $SUDOERS"

stat -c '%U:%G %a %n' /var/www "$API" "$WEB" "$API/files" > "$SAVE/owners-before.txt"
chown root:root /var/www "$API" "$WEB" "$API/files"
chmod 0755 /var/www "$API" "$WEB" "$API/files"
say "/var/www and Enik's top folders are root-owned"

# ── per slot ─────────────────────────────────────────────────────────────
release_id() {  # build sha -> id
    if [[ "$2" =~ ^[0-9a-f]{8}$ ]]; then echo "$1-$2"; else echo "$1-migrated"; fi
}

healthy() {  # port
    for _ in $(seq 1 30); do
        curl -sf -o /dev/null "http://127.0.0.1:$1/api/health/ready" && return 0
        sleep 2
    done
    return 1
}

write_unit() {  # env port
    cat > "/etc/systemd/system/$1_calis_bakalim_enik.service" <<UNIT
[Unit]
Description=Calis Bakalim Enik API ($1)
After=network.target postgresql.service redis-server.service
Wants=postgresql.service redis-server.service

[Service]
Type=simple
# Its own user since $(date -u +%F) (I56): no other project's process can read
# its key ring, and it cannot read theirs.
User=enik
Group=enik
# A symlink to the live release (H54); enik-deploy switches it in one rename.
ExecStart=$API/$1/CalisBakalimEnik.Api
WorkingDirectory=$API/$1
Restart=always
RestartSec=5
# root 0600, read by systemd before it drops to enik.
EnvironmentFile=$ETC/$1/api.env
Environment=ASPNETCORE_URLS=http://127.0.0.1:$2
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_PrintTelemetryMessage=false
NoNewPrivileges=true
PrivateTmp=true
PrivateDevices=true
ProtectSystem=true
ProtectHome=true
ReadOnlyPaths=/
ReadWritePaths=$API/files/$1
CapabilityBoundingSet=
AmbientCapabilities=
SystemCallFilter=@system-service
LimitNOFILE=65536
UMask=0077

[Install]
WantedBy=multi-user.target
UNIT
}

migrate_slot() {  # env port
    local env="$1" port="$2" unit="$1_calis_bakalim_enik"
    echo
    echo "############ $env ############"
    if [ -L "$API/$env" ]; then say "$env API is already a release symlink; skipped"; return 0; fi

    local ver build sha id wid
    ver=$(curl -s -m 5 "http://127.0.0.1:$port/api/version" || true)
    build=$(grep -oE '"buildNumber":"[0-9]+"' <<< "$ver" | grep -oE '[0-9]+' || echo 0)
    sha=$(grep -oE '"gitSha":"[^"]*"' <<< "$ver" | cut -d'"' -f4 || true)
    id=$(release_id "${build:-0}" "$sha")
    wid="$(grep -oE '"build_number":"[0-9]+"' "$WEB/$env/version.json" 2>/dev/null | grep -oE '[0-9]+' || echo 0)-migrated"
    say "live API is $id, live web is $wid"

    # Save what is about to change.
    mkdir -p "$SAVE/$env"
    cp -a "/etc/systemd/system/$unit.service" "$SAVE/$env/"
    [ -d "/etc/systemd/system/$unit.service.d" ] && cp -a "/etc/systemd/system/$unit.service.d" "$SAVE/$env/"
    cp -a "$API/$env/calis_bakalim_enik_api.env" "$SAVE/$env/"
    cp -a "$API/deploy/calis_bakalim_enik_$env.env" "$SAVE/$env/"

    # Settings to /etc, root only.
    install -d -m 0700 -o root -g root "$ETC/$env"
    install -m 0600 -o root -g root "$API/$env/calis_bakalim_enik_api.env" "$ETC/$env/api.env"
    install -m 0600 -o root -g root "$API/deploy/calis_bakalim_enik_$env.env" "$ETC/$env/db.env"

    say "stopping $unit"
    systemctl stop "$unit"

    # The release folder.
    install -d -m 0755 -o root -g root "$API/releases" "$API/releases/$env"
    mv -T "$API/$env" "$API/releases/$env/$id"
    rm -f "$API/releases/$env/$id/calis_bakalim_enik_api.env"
    find "$API/releases/$env/$id" -maxdepth 1 -name 'calis_bakalim_enik_api.env.bak*' -exec mv -t "$SAVE/$env/" {} +
    chown -R root:root "$API/releases/$env/$id"
    chmod -R u=rwX,go=rX "$API/releases/$env/$id"
    chmod 0755 "$API/releases/$env/$id/CalisBakalimEnik.Api"
    ln -sfn "releases/$env/$id" "$API/$env"
    echo "$id" > "$API/releases/$env/.history"

    # Writable data to the new user.
    chown -R enik:enik "$API/files/$env"
    chmod 0750 "$API/files/$env"

    write_unit "$env" "$port"
    rm -rf "/etc/systemd/system/$unit.service.d"
    systemctl daemon-reload
    systemctl reset-failed "$unit" 2> /dev/null || true
    systemctl start "$unit"

    if healthy "$port"; then
        say "$env healthy as enik: $(curl -s "http://127.0.0.1:$port/api/version")"
        ps -o user= -p "$(systemctl show -p MainPID --value "$unit")" | sed 's/^/    runs as /'
    else
        echo "!! $env did not come back within 60 s. Last 30 log lines:" >&2
        journalctl -u "$unit" -n 30 --no-pager >&2 || true
        echo "!! putting $env back as it was" >&2
        systemctl stop "$unit" || true
        rm -f "$API/$env"
        mv -T "$API/releases/$env/$id" "$API/$env"
        cp -a "$SAVE/$env/calis_bakalim_enik_api.env" "$API/$env/"
        chown -R servrinuse:servrinuse "$API/$env" "$API/files/$env"
        cp -a "$SAVE/$env/$unit.service" /etc/systemd/system/
        [ -d "$SAVE/$env/$unit.service.d" ] && cp -a "$SAVE/$env/$unit.service.d" /etc/systemd/system/
        systemctl daemon-reload
        systemctl reset-failed "$unit" 2> /dev/null || true
        systemctl start "$unit"
        healthy "$port" && echo "   $env is serving again as before" >&2
        echo "   Nothing else was changed for $env. Send Claude the log above." >&2
        exit 1
    fi

    # The web build: static, nothing to restart. nginx follows the symlink.
    if [ -d "$WEB/$env" ] && [ ! -L "$WEB/$env" ]; then
        install -d -m 0755 -o root -g root "$WEB/releases" "$WEB/releases/$env"
        mv -T "$WEB/$env" "$WEB/releases/$env/$wid"
        chown -R root:root "$WEB/releases/$env/$wid"
        chmod -R u=rwX,go=rX "$WEB/releases/$env/$wid"
        ln -sfn "releases/$env/$wid" "$WEB/$env"
        echo "$wid" > "$WEB/releases/$env/.history"
        local host
        host=$([ "$env" = prod ] && echo calis-bakalim-enik.starlius.com || echo qa-calis-bakalim-enik.starlius.com)
        code=$(curl -s -o /dev/null -w '%{http_code}' -H "Host: $host" http://127.0.0.1/)
        say "$env web is releases/$env/$wid, nginx answers $code"
    fi

    # The migrations credentials now live only in /etc.
    rm -f "$API/deploy/calis_bakalim_enik_$env.env"
    rm -rf "$API/$env.staging" "$WEB/$env.staging"
}

migrate_slot qa 3011
migrate_slot prod 3010

# ── the old deploy folder ────────────────────────────────────────────────
# Scripts owned by servrinuse that Yiğit ran with sudo were a way up to root
# for anyone holding servrinuse. They are replaced by enik-deploy; kept here.
for f in finish-first-deploy.sh install-slot.sh provision-db.sh fix-web-cache.sh; do
    [ -f "$API/deploy/$f" ] && mv "$API/deploy/$f" "$SAVE/"
done
chown root:root "$API/deploy"
chmod 0755 "$API/deploy"
# calis_bakalim_enik_dev.env stays, servrinuse's own: the laptop's dev database.

echo
say "done. Releases:"
/usr/local/sbin/enik-deploy status
echo
echo "Saved copies: $SAVE"
