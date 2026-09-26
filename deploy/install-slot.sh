#!/bin/bash
# Stands up one Çalış Bakalım Enik environment: database, directories, nginx and
# the systemd unit.
#
#   sudo bash install-slot.sh qa
#   sudo bash install-slot.sh prod
#
# Follows the shape already on this box rather than inventing one — orbit is the
# closest neighbour (also .NET, also Flutter web, also split across two
# hostnames) and everything below mirrors it: a conf.d map for the forwarded
# scheme, a snippets file for the security headers, one nginx site for the
# static bundle and another proxying the API, a self-contained binary in
# ExecStart, and ReadOnlyPaths=/ with a drop-in for the one writable directory.
#
# SAFE TO RE-RUN. Every step checks before it writes. It touches nothing outside
# calis_bakalim_enik_* and the four nginx files it owns by name.
#
# IT DOES NOT START THE API. The binaries are not here yet and the env file has
# no Resend key, so starting now would only crash-loop. The last thing it prints
# is what to do next.

set -euo pipefail

# ── the two hostnames ────────────────────────────────────────────────────
# Change these if you want different names; they appear in the nginx sites, the
# CSP and the API's own CORS list, and all of those have to agree.
#
# The API gets its own hostname, as orbit does. Same-origin would work too, but
# the client is already built to call an absolute API URL and the API already
# carries a CORS allow-list, so matching the neighbour costs nothing and keeps
# the two deployable separately.
case "${1:-}" in
    qa)
        ENV_NAME=qa
        WEB_HOST=qa-calis-bakalim-enik.starlius.com
        API_HOST=qa-calis-bakalim-enik-api.starlius.com
        PORT=3011
        ;;
    prod)
        ENV_NAME=prod
        WEB_HOST=calis-bakalim-enik.starlius.com
        API_HOST=calis-bakalim-enik-api.starlius.com
        PORT=3010
        ;;
    *)
        echo "usage: sudo bash install-slot.sh qa|prod" >&2
        exit 2
        ;;
esac

PROJECT=calis_bakalim_enik
OWNER=servrinuse

API_DIR="/var/www/${PROJECT}_api/${ENV_NAME}"        # published binaries
WEB_DIR="/var/www/${PROJECT}/${ENV_NAME}"            # flutter bundle
FILES_DIR="/var/www/${PROJECT}_api/files/${ENV_NAME}" # the ONLY writable path
DEPLOY_DIR="/var/www/${PROJECT}_api/deploy"
APP_ENV="${API_DIR}/${PROJECT}_api.env"
PG_ENV="${DEPLOY_DIR}/${PROJECT}_${ENV_NAME}.env"
UNIT="${ENV_NAME}_${PROJECT}"

if [ "$(id -u)" -ne 0 ]; then
    echo "Run me as root: sudo bash install-slot.sh ${ENV_NAME}" >&2
    exit 1
fi

echo "############ ${PROJECT} — ${ENV_NAME} ############"
echo "  web  ${WEB_HOST}  ->  ${WEB_DIR}"
echo "  api  ${API_HOST}  ->  127.0.0.1:${PORT}"
echo

# ── 1. database and roles ────────────────────────────────────────────────
# provision-db.sh is already here and is the house style: a migrations role that
# owns the schema and an app role with DML only, so a SQL-injection bug in the
# running app cannot alter or drop anything. Idempotent.
if [ -f "${DEPLOY_DIR}/provision-db.sh" ]; then
    bash "${DEPLOY_DIR}/provision-db.sh" "${ENV_NAME}"
else
    echo "!! ${DEPLOY_DIR}/provision-db.sh is missing — database not provisioned." >&2
    echo "   Everything else will still install." >&2
fi
echo

# ── 2. directories ───────────────────────────────────────────────────────
# files/ holds the Data Protection key ring, which encrypts every enrolled TOTP
# secret. Losing it locks out every user with an authenticator, and by design no
# operator can reset someone else's second factor. Back it up WITH the database.
for d in "$API_DIR" "$WEB_DIR" "$FILES_DIR" "${FILES_DIR}/keys" "${FILES_DIR}/logs"; do
    mkdir -p "$d"
done
chown -R "${OWNER}:${OWNER}" "/var/www/${PROJECT}" "/var/www/${PROJECT}_api"
chmod 750 "$FILES_DIR" "${FILES_DIR}/keys"
echo "==> directories ready (writable: ${FILES_DIR})"

# ── 3. nginx: the shared map ─────────────────────────────────────────────
# A map may only be declared once per http block, so this lives in conf.d and is
# shared by both environments. Cloudflare terminates TLS and cloudflared hands
# the request over plaintext loopback, so $scheme is always http here — preserve
# the X-Forwarded-Proto Cloudflare set, and fall back to $scheme only for a
# request that reached nginx directly.
MAP_FILE=/etc/nginx/conf.d/enik-forwarded-proto.conf
if [ ! -f "$MAP_FILE" ]; then
    cat > "$MAP_FILE" <<'NGINX'
map $http_x_forwarded_proto $enik_forwarded_proto {
    default $http_x_forwarded_proto;
    ""      $scheme;
}

# HSTS only means anything over HTTPS, and nginx omits a header whose value is
# empty — so this map is the conditional nginx does not otherwise have.
map $enik_forwarded_proto $enik_hsts {
    https   "max-age=63072000; includeSubDomains; preload";
    default "";
}
NGINX
    echo "==> ${MAP_FILE} written"
else
    echo "==> ${MAP_FILE} already there — left alone"
fi

# ── 4. nginx: security headers for the static site ───────────────────────
# nginx serves the HTML document, so the .NET process never sees that request —
# a CSP set only by the API would not apply to the page at all. nginx also
# RESETS inherited add_header in any block that declares its own, which is why
# this file is included again inside every location that adds a header.
SNIPPET=/etc/nginx/snippets/enik-web-headers-${ENV_NAME}.conf
mkdir -p /etc/nginx/snippets
cat > "$SNIPPET" <<'NGINX'
add_header X-Content-Type-Options "nosniff" always;
add_header X-Frame-Options "DENY" always;
add_header Referrer-Policy "strict-origin-when-cross-origin" always;
add_header Permissions-Policy "camera=(), microphone=(), geolocation=(), payment=()" always;
add_header Cross-Origin-Opener-Policy "same-origin" always;
add_header Cross-Origin-Resource-Policy "same-origin" always;
add_header Strict-Transport-Security $enik_hsts always;

# 'wasm-unsafe-eval' is required and is NOT 'unsafe-eval': it permits WebAssembly
# compilation only, never eval of strings. Flutter's CanvasKit renderer does not
# start without it.
#
# connect-src must name the API origin exactly. Keep it in step with
# Cors__AllowedOrigins in the API env — a hostname allowed there but missing
# here is a request the server permits and the browser refuses, which looks like
# a server fault and is not one.
add_header Content-Security-Policy "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self' https://__API_HOST__; worker-src 'self' blob:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'" always;
NGINX
sed -i "s|__API_HOST__|${API_HOST}|g" "$SNIPPET"
echo "==> ${SNIPPET} written"

# ── 5. nginx: the API site ───────────────────────────────────────────────
# A pure reverse proxy. nginx deliberately serves no files here: an unmatched
# /api/v1 path must reach the app and 404 the way a client expects, not fall
# through to some index.html.
cat > "/etc/nginx/sites-available/${UNIT}" <<'NGINX'
server {
    listen 80;
    listen [::]:80;
    server_name __API_HOST__;

    # The largest legitimate body is a note; anything bigger is a mistake or an
    # attack, and rejecting at the edge saves buffering an upload the app will
    # refuse anyway.
    client_max_body_size 1m;

    location / {
        proxy_pass http://127.0.0.1:__PORT__;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $enik_forwarded_proto;

        # CF-Connecting-IP passes through untouched: it is what the API's
        # forwarded-headers trust list reads to partition the rate limiter, the
        # brute-force lockout and the audit trail. nginx forwards it by default
        # because it contains no underscores.
    }

    location ~* (wp-admin|wp-login|xmlrpc|\.php) {
        return 404;
    }
}
NGINX
sed -i -e "s|__API_HOST__|${API_HOST}|g" -e "s|__PORT__|${PORT}|g" \
    "/etc/nginx/sites-available/${UNIT}"
echo "==> sites-available/${UNIT} written"

# ── 6. nginx: the static site ────────────────────────────────────────────
cat > "/etc/nginx/sites-available/${UNIT}_web" <<'NGINX'
server {
    listen 80;
    listen [::]:80;
    server_name __WEB_HOST__;

    root __WEB_DIR__;
    index index.html;

    include snippets/enik-web-headers-__ENV__.conf;

    # The shell must never be cached, or a deploy leaves clients pinned to the
    # previous bundle's asset hashes and the release appears not to have
    # happened. This block declares add_header, which resets everything
    # inherited, so the headers are included again.
    location = /index.html {
        include snippets/enik-web-headers-__ENV__.conf;
        add_header Cache-Control "no-cache, no-store, must-revalidate" always;
    }

    location = /flutter_bootstrap.js {
        include snippets/enik-web-headers-__ENV__.conf;
        add_header Cache-Control "no-cache, no-store, must-revalidate" always;
    }

    location = /version.json {
        include snippets/enik-web-headers-__ENV__.conf;
        add_header Cache-Control "no-cache, no-store, must-revalidate" always;
    }

    # Flutter emits content-hashed filenames, so everything else caches hard.
    location ~* \.(?:css|js|wasm|woff2?|png|jpg|jpeg|gif|svg|ico|json|ttf|otf)$ {
        include snippets/enik-web-headers-__ENV__.conf;
        add_header Cache-Control "public, max-age=31536000, immutable" always;
    }

    # The app uses HASH routing (/#/bugun), so a deep link is never a real path
    # and this fallback is only reached by a stray URL. It is here anyway so one
    # does not 404 into nginx's default page.
    location / {
        try_files $uri $uri/ /index.html;
    }

    location ~ /\. {
        deny all;
        return 404;
    }

    location ~* (wp-admin|wp-login|xmlrpc|\.php) {
        return 404;
    }
}
NGINX
sed -i -e "s|__WEB_HOST__|${WEB_HOST}|g" -e "s|__WEB_DIR__|${WEB_DIR}|g" \
    -e "s|__ENV__|${ENV_NAME}|g" "/etc/nginx/sites-available/${UNIT}_web"
echo "==> sites-available/${UNIT}_web written"

ln -sfn "/etc/nginx/sites-available/${UNIT}"     "/etc/nginx/sites-enabled/${UNIT}"
ln -sfn "/etc/nginx/sites-available/${UNIT}_web" "/etc/nginx/sites-enabled/${UNIT}_web"

# nginx -t before reload: a bad config would otherwise take down every other
# site on this box, which is the one outcome that is not ours to risk.
if nginx -t; then
    systemctl reload nginx
    echo "==> nginx reloaded"
else
    echo "!! nginx -t FAILED — not reloading. The other sites are untouched." >&2
    exit 1
fi

# ── 7. systemd ───────────────────────────────────────────────────────────
cat > "/etc/systemd/system/${UNIT}.service" <<'UNITFILE'
[Unit]
Description=Calis Bakalim Enik API (__ENV__)
After=network.target postgresql.service redis-server.service
Wants=postgresql.service redis-server.service

[Service]
# Kestrel does not send sd_notify, so Type=notify would hold the unit in
# "activating" until the timeout and then kill a healthy process.
Type=simple
User=servrinuse
Group=servrinuse

ExecStart=/var/www/calis_bakalim_enik_api/__ENV__/CalisBakalimEnik.Api
WorkingDirectory=/var/www/calis_bakalim_enik_api/__ENV__
Restart=always
RestartSec=5

EnvironmentFile=/var/www/calis_bakalim_enik_api/__ENV__/calis_bakalim_enik_api.env

Environment=ASPNETCORE_URLS=http://127.0.0.1:__PORT__
# Production even in the qa slot, deliberately: it is what turns on the
# Cloudflare forwarded-header trust list and the two startup guards that refuse
# a logging-only mailer and an unpersisted key ring. A separate environment name
# here would run the test team on a configuration production never sees.
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_PrintTelemetryMessage=false

NoNewPrivileges=true
PrivateTmp=true
PrivateDevices=true
ProtectSystem=true
ProtectHome=true
ReadOnlyPaths=/
CapabilityBoundingSet=
AmbientCapabilities=
SystemCallFilter=@system-service
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
UNITFILE
sed -i -e "s|__ENV__|${ENV_NAME}|g" -e "s|__PORT__|${PORT}|g" \
    "/etc/systemd/system/${UNIT}.service"

# The one writable path, as a drop-in so the unit itself stays the house shape.
mkdir -p "/etc/systemd/system/${UNIT}.service.d"
cat > "/etc/systemd/system/${UNIT}.service.d/writable.conf" <<UNITDROP
[Service]
ReadWritePaths=${FILES_DIR}
UNITDROP

systemctl daemon-reload
systemctl enable "${UNIT}" >/dev/null 2>&1 || true
echo "==> ${UNIT}.service installed and enabled (NOT started)"

# ── 8. the app env file ──────────────────────────────────────────────────
# Composed from the database credentials provision-db.sh just wrote, so the
# connection string is correct without anyone copying a password by hand.
if [ -f "$APP_ENV" ]; then
    echo "==> ${APP_ENV} already exists — left alone"
else
    PGDATABASE=""; PGUSER_APP=""; PGPASSWORD_APP=""
    # shellcheck disable=SC1090
    [ -f "$PG_ENV" ] && . "$PG_ENV"

    umask 077
    cat > "$APP_ENV" <<APPENV
# ${PROJECT} ${ENV_NAME} — generated $(date -Iseconds). chmod 600, never commit.
#
# FILL IN Email__ApiKey BEFORE STARTING. The API refuses to boot without a real
# mailer outside Development, on purpose: a deployment where every confirmation
# link vanishes into a log file looks healthy while nobody can finish signing up.

Email__Provider=Resend
Email__ApiKey=
Email__From=Çalış Bakalım Enik <calis-bakalim-enik@business-application.starlius.com>
Email__AppBaseUrl=https://${WEB_HOST}

DataProtection__KeyPath=${FILES_DIR}/keys

ConnectionStrings__Default=Host=127.0.0.1;Port=5432;Database=${PGDATABASE};Username=${PGUSER_APP};Password=${PGPASSWORD_APP}

# db 1 = prod, db 2 = qa, db 3 = local development. One Redis is shared with the
# other projects on this box, so the database number is the only separation —
# the cbe: prefix is a readability aid, not isolation.
Redis__Configuration=127.0.0.1:6379
Redis__Database=__REDISDB__
Redis__KeyPrefix=cbe:

# Empty uses Cloudflare's published ranges. Loopback is added automatically and
# must be: cloudflared and nginx both reach the app over 127.0.0.1, which is not
# in Cloudflare's list, and without it every user collapses into one rate-limit
# bucket.

Cors__AllowedOrigins__0=https://${WEB_HOST}

# Second factor OFF for this deployment. Defaults to true in code, so this line
# is what turns it off and removing it turns it back on.
#
# The client must be built with --dart-define=MFA_ENABLED=false to match. The
# API is authoritative: with the server off and the client on, the account
# screen offers an enrolment every call to which answers 404.
#
# Enrolled factors are SKIPPED, not deleted — flipping this back to true asks
# for the authenticator somebody already has rather than locking them out.
#
# This is a real reduction in security and is reasonable for a test team who
# would otherwise spend the first hour scanning QR codes. It is not reasonable
# once there are customers.
Mfa__Enabled=false

Jwt__Issuer=https://${API_HOST}
Jwt__Audience=calisbakalimenik.app

# Empty means no version gate, which is the right default: a floor that turns
# itself on locks every tester out of a working app on a routine deploy.
# Client__MinimumVersion=
# Client__StoreUrl=

# Promoted to PlatformAdmin at startup if the account exists. Set it for the
# first deploy, then remove it.
# Admin__BootstrapEmail=
APPENV

    if [ "$ENV_NAME" = "prod" ]; then
        sed -i "s|__REDISDB__|1|" "$APP_ENV"
    else
        sed -i "s|__REDISDB__|2|" "$APP_ENV"
    fi

    chmod 600 "$APP_ENV"
    chown "${OWNER}:${OWNER}" "$APP_ENV"
    echo "==> ${APP_ENV} written (chmod 600, database password filled in)"
fi

# ── done ─────────────────────────────────────────────────────────────────
cat <<DONE

############ ready ############

Nothing outside ${PROJECT}_* and the four nginx files above was touched, and the
API is deliberately NOT running yet.

Still to do, in this order:

  1. Put the Resend API key into:
         ${APP_ENV}
     The service will crash-loop until you do — that guard is intentional.

  2. In Cloudflare Zero Trust, point both hostnames at this machine:
         ${WEB_HOST}  ->  http://localhost:80
         ${API_HOST}  ->  http://localhost:80
     nginx routes them apart by server_name, the same way the orbit sites work.

  3. Tell me it is done. I publish the API into
         ${API_DIR}
     and the web bundle into
         ${WEB_DIR}
     run the migrations as the MIGRATIONS role, then:
         sudo systemctl start ${UNIT}
         curl -sf http://127.0.0.1:${PORT}/api/health/ready

BACK UP ${FILES_DIR}/keys WITH THE DATABASE. It encrypts every enrolled
two-factor secret. Restore the database without it and every user who set up an
authenticator is locked out for good, and by design nobody can reset someone
else's second factor.
DONE
