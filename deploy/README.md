# deploy/

Everything needed to stand the QA slot up, and nothing that holds a secret.

The **internal test release runs in the QA slot**, not a new one. `DEPLOYMENT.md` §0
already defines prod (`3010`) and qa (`3011`) with matching databases, roles, unit names
and nginx sites, all read off the live box. Inventing a third environment for the test
team would mean a third set of everything and a convention nobody has used yet.

| File | Goes to |
|---|---|
| `qa_calis_bakalim_enik.service` | `/etc/systemd/system/` |
| `qa_calis_bakalim_enik_web.nginx` | `/etc/nginx/sites-available/qa_calis_bakalim_enik_web` |
| `calis_bakalim_enik_api.env.example` | `/var/www/calis_bakalim_enik_api/qa/calis_bakalim_enik_api.env`, filled in, `0600` |

## Before any of it works

Four things are decisions rather than commands, and three of them are not mine to make:

1. **The hostnames.** The files assume `test.calisbakalimenik.app` for the app and
   `api-qa.calisbakalimenik.app` for the API. If you pick different ones, they appear in
   four places: the nginx `server_name`, `Cors__AllowedOrigins__0`, `Jwt__Issuer`, and the
   bundle's `connect-src`. Getting `Cors` wrong is the one that will waste your afternoon
   — the app comes up blank with console errors and the API log says nothing, because a
   rejected preflight never reaches a handler.
2. **Resend domain verification** for `calisbakalimenik.app`. Until it is done the API
   will not start at all: it refuses the logging-only mailer outside Development, on
   purpose, because a deployment where nobody can complete signup otherwise looks healthy.
3. **The database and its two roles.** `calis_bakalim_enik_qa`, owned by
   `calis_bakalim_enik_qa_migrations`, with `calis_bakalim_enik_qa_app` holding `USAGE`
   and no schema rights. `DEPLOYMENT.md` §3 has the shape; the neighbours' own
   `install-prod-qa.sh` has the exact SQL and is the house style.
4. **`DataProtection__KeyPath` on durable storage.** `/var/www/calis_bakalim_enik_api/files/keys`
   in the unit's `ReadWritePaths`. **Back it up with the database.** It encrypts every
   enrolled TOTP secret, so losing it locks out every user who set up an authenticator,
   and no operator can reset someone else's second factor by design.

## Order

```
1. create the database and the two roles
2. fill in the env file, chmod 600, chown root:servrinuse
3. publish:   dotnet publish -c Release -o /var/www/calis_bakalim_enik_api/qa
4. migrate:   as the MIGRATIONS role, not the app role
5. install the unit, systemctl daemon-reload && enable --now qa_calis_bakalim_enik
6. poll:      curl -sf http://127.0.0.1:3011/api/health/ready
7. build:     flutter build web --release --dart-define=FLAVOR=qa
              then rsync build/web/ to /var/www/calis_bakalim_enik_qa
8. install the nginx site, nginx -t, reload
9. add the cloudflared ingress rules for both hostnames
10. smoke:    GET /api/version, then one authenticated round trip
```

Step 6 is `/api/health/ready`, with the `/api` prefix. `DEPLOYMENT.md` §6 used to say
`/health/ready`, which does not exist — a deploy script polling it would have waited out
its timeout on a service that was already up.

> `FLAVOR=qa`, not `prod`. The flavour is what picks the API hostname compiled into the
> bundle: `prod` bakes in `api.calisbakalimenik.app`, `qa` bakes in
> `api-qa.calisbakalimenik.app`. Build the QA slot with the wrong one and you get an app
> that loads perfectly and talks to the wrong server — with CORS as the only thing that
> tells you, from the browser console rather than any log on the box.

## What is deliberately not here

- **No CI/CD.** `DEPLOYMENT.md` §7 describes four GitHub workflows. None exist, and for a
  manual internal release they are work that pays off in phase 2.
- **No `firebase-messaging-sw.js`.** §5 required it. Firebase is not in `pubspec.yaml` on
  purpose, so there is no service worker and no web push. In-app notifications work.
- **No TLS config.** It terminates at Cloudflare; nginx serves plain HTTP on port 80 and
  cloudflared reaches it locally.
