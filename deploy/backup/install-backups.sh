#!/bin/bash
# Installs the Çalış Bakalım Enik backup job, and later connects it to Google
# Drive. Run from the deploy directory on the server:
#
#   sudo bash install-backups.sh age1...                        # step 1
#   sudo bash install-backups.sh --drive <shared-drive-id> <service-account.json>
#                                                               # step 2
#
# Step 1 alone already gives encrypted local backups every six hours. Step 2
# adds the off-site copy; until then each run says so and exits non-zero.
#
# SAFE TO RE-RUN. It touches only calis_bakalim_enik paths and the two
# enik-backup units. Nothing of the other projects on this box is read or
# changed, and the database is only ever read.

set -euo pipefail

[ "$(id -u)" -eq 0 ] || { echo "run with sudo" >&2; exit 1; }

HERE="$(cd "$(dirname "$0")" && pwd)"
ETC=/etc/calis_bakalim_enik
CONF="$ETC/backup.conf"
OUT=/var/backups/calis_bakalim_enik
LIB=/usr/local/lib/calis_bakalim_enik
RUN_AS=servrinuse

set_conf() {  # key value
    touch "$CONF"
    if grep -q "^$1=" "$CONF"; then
        sed -i "s|^$1=.*|$1=\"$2\"|" "$CONF"
    else
        echo "$1=\"$2\"" >> "$CONF"
    fi
    chown root:"$RUN_AS" "$CONF"
    chmod 0640 "$CONF"
}

run_once() {
    echo
    echo "==> running one backup now"
    systemctl start enik-backup.service || true
    journalctl -u enik-backup.service -n 25 --no-pager -o cat
    echo
    systemctl --no-pager list-timers enik-backup.timer | head -2
}

# ── step 2: Google Drive ─────────────────────────────────────────────────
if [ "${1:-}" = "--drive" ]; then
    DRIVE_ID="${2:?shared drive id missing}"
    SA_JSON="${3:?service account json path missing}"
    [ -f "$CONF" ] || { echo "run step 1 first" >&2; exit 1; }
    [ -f "$SA_JSON" ] || { echo "$SA_JSON not found" >&2; exit 1; }
    grep -q '"type": *"service_account"' "$SA_JSON" \
        || { echo "$SA_JSON is not a service account key" >&2; exit 1; }

    install -m 0640 -o root -g "$RUN_AS" "$SA_JSON" "$ETC/drive-sa.json"

    rclone config create enikdrive drive \
        scope drive \
        service_account_file "$ETC/drive-sa.json" \
        team_drive "$DRIVE_ID" \
        --non-interactive \
        --config "$ETC/rclone.conf" > /dev/null
    chown root:"$RUN_AS" "$ETC/rclone.conf"
    chmod 0640 "$ETC/rclone.conf"

    echo "==> checking the service account can reach the shared drive"
    sudo -u "$RUN_AS" rclone --config "$ETC/rclone.conf" mkdir enikdrive:calis_bakalim_enik
    sudo -u "$RUN_AS" rclone --config "$ETC/rclone.conf" lsd enikdrive: | grep -q calis_bakalim_enik \
        || { echo "!! created the folder but cannot list it; check the drive id and the member role" >&2; exit 1; }
    echo "==> reachable"

    set_conf RCLONE_REMOTE "enikdrive:calis_bakalim_enik"
    echo
    echo "The key is now at $ETC/drive-sa.json. Delete the copy you uploaded:"
    echo "    shred -u $SA_JSON"
    run_once
    exit 0
fi

# ── step 1: the job itself ───────────────────────────────────────────────
RECIPIENT="${1:-}"
if ! [[ "$RECIPIENT" =~ ^age1[0-9a-z]{58}$ ]]; then
    echo "usage: sudo bash install-backups.sh age1...   (the PUBLIC key from age-keygen)" >&2
    exit 2
fi

echo "==> packages"
DEBIAN_FRONTEND=noninteractive apt-get install -y -qq age rclone zstd > /dev/null
echo "    age $(age --version), $(rclone version | head -1)"

install -d -m 0750 -o root -g "$RUN_AS" "$ETC"
set_conf AGE_RECIPIENT "$RECIPIENT"
grep -q '^BACKUP_ENVS=' "$CONF" || set_conf BACKUP_ENVS "prod qa"
grep -q '^KEEP_DAYS=' "$CONF"   || set_conf KEEP_DAYS "14"

install -d -m 0700 -o "$RUN_AS" -g "$RUN_AS" "$OUT"
install -d -m 0755 "$LIB"
install -m 0755 -o root -g root "$HERE/enik-backup.sh" "$LIB/enik-backup.sh"

cat > /etc/systemd/system/enik-backup.service <<UNIT
[Unit]
Description=Çalış Bakalım Enik backup: database, key ring and config, encrypted, to Drive
Wants=network-online.target
After=network-online.target postgresql.service

[Service]
Type=oneshot
User=$RUN_AS
Group=$RUN_AS
ExecStart=$LIB/enik-backup.sh
Environment=RCLONE_CONFIG=$ETC/rclone.conf
Environment=XDG_CACHE_HOME=$OUT/.cache
Nice=10
IOSchedulingClass=idle
TimeoutStartSec=1h
ProtectSystem=strict
ReadWritePaths=$OUT
ProtectHome=yes
PrivateTmp=yes
NoNewPrivileges=yes
UNIT

# Every six hours. Persistent: a run missed while the power was off happens at
# boot instead of waiting for the next slot.
cat > /etc/systemd/system/enik-backup.timer <<UNIT
[Unit]
Description=Çalış Bakalım Enik backup, every six hours

[Timer]
OnCalendar=*-*-* 00/6:15:00
Persistent=true
RandomizedDelaySec=5min

[Install]
WantedBy=timers.target
UNIT

systemctl daemon-reload
systemctl enable --now enik-backup.timer > /dev/null
echo "==> enik-backup.timer enabled"
run_once
