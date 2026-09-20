#!/bin/bash
# Provisions one Çalış Bakalım Enik environment's PostgreSQL database and roles.
#
#   sudo bash provision-db.sh dev|qa|prod
#
# Follows the house style already used by orbit_api/deploy/install-prod-qa.sh:
# two roles per database — a migrations role that OWNS the schema and an app role
# with DML only — so a SQL-injection bug in the running app cannot alter or drop
# anything. See docs/DATABASE.md §9 and docs/DEPLOYMENT.md §0.
#
# Idempotent and safe to re-run: existing roles keep their passwords, an existing
# database is left alone. It touches nothing outside the calis_bakalim_enik_* names.

set -euo pipefail

ENV_NAME="${1:-}"
case "$ENV_NAME" in
    dev|qa|prod) ;;
    *) echo "usage: sudo bash provision-db.sh dev|qa|prod" >&2; exit 2 ;;
esac

PROJECT=calis_bakalim_enik
DB="${PROJECT}_${ENV_NAME}"
ROLE_MIG="${PROJECT}_${ENV_NAME}_migrations"
ROLE_APP="${PROJECT}_${ENV_NAME}_app"

DEPLOY_DIR="/var/www/${PROJECT}_api/deploy"
ENV_FILE="${DEPLOY_DIR}/${PROJECT}_${ENV_NAME}.env"
OWNER_USER="servrinuse"

if [ "$(id -u)" -ne 0 ]; then
    echo "This script must run as root (it uses sudo -u postgres)." >&2
    exit 1
fi

mkdir -p "$DEPLOY_DIR"

echo "############ ${DB} ############"

# ── roles ────────────────────────────────────────────────────────────────
# Passwords are generated here and never printed. If a role already exists its
# password is left untouched, so re-running cannot orphan a live service.
role_exists() {
    sudo -u postgres psql -tAc "SELECT 1 FROM pg_roles WHERE rolname='$1'" | grep -q 1
}

NEW_SECRETS=0

for role in "$ROLE_MIG" "$ROLE_APP"; do
    if role_exists "$role"; then
        echo "==> role $role already exists — password left alone"
    else
        pw="$(openssl rand -base64 33 | tr -d '\n/+=' | cut -c1-32)"
        sudo -u postgres psql -v ON_ERROR_STOP=1 -q \
            -c "CREATE ROLE \"$role\" LOGIN PASSWORD '$pw'"
        echo "==> role $role created"
        if [ "$role" = "$ROLE_MIG" ]; then PW_MIG="$pw"; else PW_APP="$pw"; fi
        NEW_SECRETS=1
    fi
done

# ── database ─────────────────────────────────────────────────────────────
if sudo -u postgres psql -tAc "SELECT 1 FROM pg_database WHERE datname='$DB'" | grep -q 1; then
    echo "==> database $DB already exists — left alone"
else
    sudo -u postgres createdb -O "$ROLE_MIG" -E UTF8 "$DB"
    echo "==> database $DB created, owned by $ROLE_MIG"
fi

# ── schema access ────────────────────────────────────────────────────────
# The app role gets USAGE only. Table-level grants are applied after the first
# migration run, because the tables do not exist yet.
sudo -u postgres psql -v ON_ERROR_STOP=1 -q -d "$DB" <<SQL
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT ALL   ON SCHEMA public TO "$ROLE_MIG";
GRANT USAGE ON SCHEMA public TO "$ROLE_APP";

-- Anything the migrations role creates from now on is automatically readable and
-- writable by the app role, so grants do not have to be re-run after every
-- migration. Without this, the first deploy after a new table 500s.
ALTER DEFAULT PRIVILEGES FOR ROLE "$ROLE_MIG" IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO "$ROLE_APP";
ALTER DEFAULT PRIVILEGES FOR ROLE "$ROLE_MIG" IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO "$ROLE_APP";
SQL
echo "==> schema grants applied"

# ── credentials file ─────────────────────────────────────────────────────
if [ "$NEW_SECRETS" -eq 1 ]; then
    umask 077
    {
        echo "# ${PROJECT} ${ENV_NAME} — generated $(date -Iseconds). chmod 600, never commit."
        echo "PGHOST=127.0.0.1"
        echo "PGPORT=5432"
        echo "PGDATABASE=${DB}"
        [ -n "${PW_MIG:-}" ] && echo "PGUSER_MIGRATIONS=${ROLE_MIG}" && echo "PGPASSWORD_MIGRATIONS=${PW_MIG}"
        [ -n "${PW_APP:-}" ] && echo "PGUSER_APP=${ROLE_APP}" && echo "PGPASSWORD_APP=${PW_APP}"
    } > "$ENV_FILE"
    chmod 600 "$ENV_FILE"
    chown "$OWNER_USER:$OWNER_USER" "$ENV_FILE"
    echo "==> credentials written to $ENV_FILE (chmod 600)"
else
    echo "==> no new roles, credentials file left alone"
fi

chown -R "$OWNER_USER:$OWNER_USER" "/var/www/${PROJECT}_api" 2>/dev/null || true

echo
echo "Done. $DB is ready."
echo "Nothing outside ${PROJECT}_* was touched."
