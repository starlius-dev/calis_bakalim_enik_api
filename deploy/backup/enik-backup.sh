#!/bin/bash
# One backup run for Çalış Bakalım Enik. Started by enik-backup.timer as
# servrinuse; install-backups.sh puts it in place. Not meant to be run by hand,
# though it is safe to: `sudo systemctl start enik-backup` does exactly that.
#
# Per environment it produces ONE file, encrypted before it touches the disk:
#
#   /var/backups/calis_bakalim_enik/<env>/cbe-<env>-<UTC stamp>.tar.zst.age
#     db.dump         pg_dump -Fc of the whole database
#     keys/           the Data Protection key ring. Without it every stored
#                     TOTP secret is undecryptable and every MFA user is locked
#                     out, even with a perfect database restore.
#     jwt-signing-key.pem
#     app.env         the unit's environment: connection strings, mail key.
#     manifest.txt    what is inside, the API version, row counts.
#
# Encrypted to an age PUBLIC key. The private key is not on this machine, so a
# stolen disk, a leaked Drive folder or an intruder on this box cannot read a
# backup. The price is that nothing here can restore one either: that happens
# on a machine holding the key. See docs/DEPLOYMENT.md, "Restoring".
#
# Then everything not yet off-site is copied to Google Drive. The power and the
# line both drop a few times a month, so the copy is a catch-up, not a mirror:
# a run that could not upload leaves its files for the next one, and a run
# missed while the box was off is started at boot (Persistent=true).

set -euo pipefail
umask 077

CONF=/etc/calis_bakalim_enik/backup.conf
# shellcheck disable=SC1090
. "$CONF"

: "${AGE_RECIPIENT:?AGE_RECIPIENT missing from $CONF}"
BACKUP_ENVS="${BACKUP_ENVS:-prod qa}"
KEEP_DAYS="${KEEP_DAYS:-14}"
RCLONE_REMOTE="${RCLONE_REMOTE:-}"

BASE=/var/www/calis_bakalim_enik_api
OUT=/var/backups/calis_bakalim_enik
STAMP=$(date -u +%Y-%m-%dT%H%MZ)

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

failed=0

for env in $BACKUP_ENVS; do
    echo "== ${env}"
    dir="$WORK/$env"
    mkdir -p "$dir" "$OUT/$env"

    # The migrations role owns the schema, so it can read every table. The
    # app role deliberately cannot see some of them.
    (
        # shellcheck disable=SC1090
        . "$BASE/deploy/calis_bakalim_enik_${env}.env"
        export PGPASSWORD="$PGPASSWORD_MIGRATIONS"
        pg_dump -h "$PGHOST" -p "$PGPORT" -U "$PGUSER_MIGRATIONS" \
            -d "$PGDATABASE" -Fc -f "$dir/db.dump"
        psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER_MIGRATIONS" -d "$PGDATABASE" \
            -At -F ' ' -c "select 'users', count(*) from users
                           union all select 'tasks', count(*) from tasks
                           union all select 'medications', count(*) from medications
                           union all select 'migrations', count(*) from \"__EFMigrationsHistory\";" \
            > "$dir/counts.txt"
    ) || { echo "!! ${env}: dump failed" >&2; failed=1; continue; }

    # A dump that pg_restore cannot list is not a backup. Checked here, while
    # it is still readable; after encryption nothing on this box can look.
    tables=$(pg_restore --list "$dir/db.dump" | grep -c " TABLE DATA " || true)
    if [ "$tables" -lt 10 ]; then
        echo "!! ${env}: dump lists only ${tables} tables" >&2
        failed=1
        continue
    fi

    files="$BASE/files/$env"
    cp -a "$files/keys" "$dir/keys"
    [ -f "$files/jwt-signing-key.pem" ] && cp -a "$files/jwt-signing-key.pem" "$dir/"
    cp -a "$BASE/$env/calis_bakalim_enik_api.env" "$dir/app.env"

    port=$([ "$env" = prod ] && echo 3010 || echo 3011)
    {
        echo "environment: $env"
        echo "taken_at:    $STAMP"
        echo "host:        $(hostname)"
        echo "api:         $(curl -s -m 5 "http://127.0.0.1:${port}/api/version" || echo unreachable)"
        echo "tables:      $tables"
        echo "rows:"
        sed 's/^/  /' "$dir/counts.txt"
        echo "sha256:"
        (cd "$dir" && find . -type f ! -name manifest.txt -exec sha256sum {} + | sed 's/^/  /')
    } > "$dir/manifest.txt"
    rm "$dir/counts.txt"

    name="cbe-${env}-${STAMP}.tar.zst.age"
    tar -C "$dir" -cf - . | zstd -q -10 | age -r "$AGE_RECIPIENT" > "$OUT/$env/.${name}.partial"
    mv "$OUT/$env/.${name}.partial" "$OUT/$env/${name}"
    echo "==> $(du -h "$OUT/$env/${name}" | cut -f1)  ${name}"
done

# ── off-site ─────────────────────────────────────────────────────────────
# --ignore-existing: a file is written once and never touched again. The
# service account is only a Contributor on the shared drive, which lets it add
# files but not delete or trash them, so an intruder here cannot wipe the
# off-site copies either. Nothing is ever pruned there by this job.
uploaded=""
if [ -n "$RCLONE_REMOTE" ]; then
    # Whether it all went is decided below, from what Drive actually lists,
    # not from this exit code.
    rclone copy "$OUT" "$RCLONE_REMOTE" \
        --include "*/cbe-*.tar.zst.age" \
        --ignore-existing \
        --retries 5 --low-level-retries 10 \
        --contimeout 30s --timeout 5m \
        -v 2>&1 | grep -E "Copied|ERROR" || true
    if ! uploaded=$(rclone lsf -R "$RCLONE_REMOTE" --files-only --include "*/cbe-*.tar.zst.age" 2>/dev/null); then
        echo "!! off-site copy unreachable; local files are kept until it is back" >&2
        failed=1
        uploaded=""
    fi
fi

# ── local retention ──────────────────────────────────────────────────────
# Older than KEEP_DAYS is removed locally ONLY once Drive has it, so a long
# outage never costs history. With no remote configured it is plain age.
find "$OUT" -name "cbe-*.tar.zst.age" -mtime +"$KEEP_DAYS" -print0 |
while IFS= read -r -d '' file; do
    rel="${file#"$OUT"/}"
    if [ -z "$RCLONE_REMOTE" ] || grep -qxF "$rel" <<< "$uploaded"; then
        rm -f "$file"
        echo "pruned $rel"
    fi
done

if [ -n "$RCLONE_REMOTE" ]; then
    pending=0
    while IFS= read -r -d '' file; do
        grep -qxF "${file#"$OUT"/}" <<< "$uploaded" || pending=$((pending + 1))
    done < <(find "$OUT" -name "cbe-*.tar.zst.age" -print0)
    echo "off-site: ${pending} file(s) not yet on Drive"
    [ "$pending" -eq 0 ] || failed=1
fi

use=$(df --output=pcent / | tail -1 | tr -dc 0-9)
[ "$use" -lt 85 ] || { echo "!! root filesystem is ${use}% full" >&2; failed=1; }

exit "$failed"
