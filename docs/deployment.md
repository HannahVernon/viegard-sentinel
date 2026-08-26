# Deploying Viegard

This runbook covers deploying the Viegard stack with Docker Compose and verifying it, including the post-deployment backup restore drill.  It reflects a verified deployment (2026-08-25) on a Debian 13 Docker host.  All addresses and names below are placeholders; substitute your own.

## Prerequisites

- Docker Engine with the Compose plugin
- A clone of this repository on the host

## 1. Prepare secrets and directories

Each secret is one file in `deploy/secrets/`; the file name is the secret name (D-0006).  These directories are gitignored and must never be committed.

```bash
cd deploy
mkdir -p secrets data/postgres backups/postgres data/dataprotection-keys
chmod 700 backups/postgres      # dumps will contain full email bodies and security history
chown 1654 data/dataprotection-keys && chmod 700 data/dataprotection-keys   # admin's ASP.NET Data Protection keys
openssl rand -base64 24 | tr -d '\n' > secrets/viegard-db-password

# The viegard-pipeline and viegard-admin containers run as the non-root
# 'app' user (UID 1654) from the .NET base images.  Root-owned mode-600
# files are unreadable to them; the directory also needs traversal.
chown 1654 secrets/viegard-db-password
chmod 400 secrets/viegard-db-password
chmod 755 secrets
```

## 2. Configure and start

```bash
cp docker-compose.example.yml docker-compose.yml   # local copy stays out of git
docker compose up -d --build
```

First start applies EF Core migrations automatically (`Viegard:Database:AutoMigrate`).

### Database schema

Every Viegard object lives in a dedicated PostgreSQL schema rather than `public`, controlled by `Viegard__Database__Schema` in the compose file (default `viegard`).  The pipeline creates the schema on startup if it is missing; both services must use the same value.  Names are validated fail-closed: lowercase letters, digits, and underscores only, starting with a letter, at most 63 characters.

The schema is applied via the connection `search_path`, so the migrations and queue SQL are schema-agnostic.  Keeping application objects out of `public` means a `pg_dump --schema=viegard` captures exactly the application state, and other tooling added to the same database later cannot collide with Viegard tables.

**Upgrading an existing deployment** that already migrated into `public`: the simplest path while the data is still expendable is to reset.  If any rows are worth keeping (for example, captured bot-signature events), export them first:

```bash
# Optional: keep selected rows before the reset.
docker compose exec viegard-db pg_dump -U viegard -d viegard \
    --table=public.normalized_events --data-only > /root/keep-events.sql

docker compose stop viegard-pipeline viegard-admin
docker compose exec viegard-db psql -U viegard -d viegard \
    -c 'DROP SCHEMA public CASCADE; CREATE SCHEMA public;'
docker compose up -d          # migrations re-run into the configured schema
```

Rows exported this way can be replayed into the new schema with `psql` after editing the `SET search_path` / table references, or simply kept as an archive.

## 3. Verify

```bash
docker compose ps                       # four services up; viegard-db healthy
curl http://127.0.0.1:8080/healthz     # admin liveness
docker compose logs -t --tail 30 viegard-pipeline
```

A clean pipeline boot logs: roles (`sources, correlation, classification, policy, actions`), `Ingestion worker: no data sources configured; idle` (sources ship disabled), correlation/classification/policy workers started, and the queue telemetry publisher.  The shipped posture is dry-run ON, all action providers OFF (D-0027): the stack can observe and decide, but cannot act.

## 4. Prove the backup with a restore drill (do this first)

The `viegard-db-backup` sidecar writes a custom-format `pg_dump` to `backups/postgres/` at every container start and then every 24 hours (14-day retention).  **A backup that has never been restored is a hope, not a backup**: the time to discover a broken dump, a bad format flag, or a permissions problem is now, with a throwaway database, not during an incident with your audit history on the line.  Run this drill immediately after first deployment, and again once real data has accumulated:

```bash
ls -lh backups/postgres/                # dump exists and is non-trivial in size

docker compose exec viegard-db createdb -U viegard restore_drill
docker compose exec viegard-db pg_restore -U viegard -d restore_drill \
    /dev/stdin < backups/postgres/viegard-*.dump
docker compose exec viegard-db psql -U viegard -d restore_drill -c '\dt'   # tables present
docker compose exec viegard-db dropdb -U viegard restore_drill
```

If `pg_restore` completes and `\dt` lists the Viegard tables, the dump is provably restorable.  (With multiple dumps present, replace the glob with one specific file.)

Disaster-recovery posture (D-0024): the dumps live on the VM disk, so they ride the host's regular VM-image backups; worst-case data loss is bounded by the daily dump cadence.

**Backup file protection:** dumps carry everything the database holds, which once mail ingestion is live includes full message bodies and your security event history.  The sidecar writes them mode 600 (root-owned) via `umask 077`, and the directory should be `700` (step 1).  If you deployed before this hardening, tighten existing files: `chmod 700 backups/postgres && chmod 600 backups/postgres/*.dump`.

## 5. Enable data sources

Sources ship disabled; enable them deliberately, one at a time.

- **SWAG/nginx syslog:** see [swag-syslog-setup.md](swag-syslog-setup.md).  Set `Viegard__Sources__Syslog__Enabled`, the fail-closed `AllowedSources` list, and publish `5514/udp` in your compose copy; firewall the port to the SWAG host.
- **IMAP accounts:** add entries under `Viegard__Sources__Imap__Accounts__*` with a password secret file per account (`PasswordSecretName`).
- **MDaemon logs:** runs as a satellite pipeline instance on the mail host (D-0025); deployment guide pending.

## Admin interface exposure and authentication

The admin interface uses local accounts, cookie authentication, server-side revocable sessions, mandatory TOTP, and recovery codes.  WebAuthn hardware-key support is planned for the next increment and will require HTTPS because browsers require a secure context.

### Bootstrap flow

Create the bootstrap password secret before the first admin startup:

```bash
openssl rand -base64 32 | tr -d '\n' > secrets/viegard-admin-bootstrap-password
chown 1654 secrets/viegard-admin-bootstrap-password
chmod 400 secrets/viegard-admin-bootstrap-password
```

When no `admin_users` rows exist, the admin service creates `Viegard__Admin__Bootstrap__Username` (default `admin`) from that secret.  The first login forces a password change, then TOTP enrollment with a manual base32 secret and otpauth URI.  Recovery codes are shown once after enrollment or regeneration.  Store them outside Viegard.

If the bootstrap secret is missing and no users exist, the host stays up but the UI remains locked.  Add the secret and restart the admin service.

### Exposure modes

Mode | Configuration | Notes
-----|---------------|------
`loopback` | `Viegard__Admin__Exposure=loopback` | Default.  The operator must keep the published port bound to loopback only, for example `127.0.0.1:8080:8080`.
`direct` | `Viegard__Admin__Exposure=direct` plus `Viegard__Admin__Tls__CertificatePath` and `KeyPath` | Kestrel loads mounted PEM files and exposes HTTPS directly.  A dedicated public IP with router dst-nat of 80/443 to the Viegard host is the reference topology.
`proxy` | `Viegard__Admin__Exposure=proxy` plus `Viegard__Admin__Proxy__TrustedNetworks__*` | Use only with a trusted TLS-terminating proxy.  Trusted networks are fail-closed: an empty list is a startup error.

Prefer a self-hosted VPN such as WireGuard for routine access.  Opening `AllowedSources` to `0.0.0.0/0` exposes the admin login to the internet and should be a deliberate exception, not the default.

### AllowedSources

`Viegard__Admin__AllowedSources__*` accepts bare IP addresses or CIDR ranges and uses the same semantics as syslog CIDR matching.  Empty list means loopback only.  Requests outside loopback and the configured ranges receive HTTP 403 with no body details and a warning log entry with sanitized values.

### Session and IP binding options

Setting | Default | Meaning
--------|---------|--------
`Viegard__Admin__Auth__AbsoluteLifetime` | `14.00:00:00` | Maximum session age.  Validation rejects values above 30 days.
`Viegard__Admin__Auth__IdleTimeout` | `48:00:00` | Session expires when not seen for this interval.
`Viegard__Admin__Auth__IpBindingMode` | `strict` | `strict` requires exact IP match, `subnet` accepts `/24` IPv4 or `/64` IPv6 movement, and `log-only` records mismatches without rejecting.
`Viegard__Admin__Auth__StepUpValidity` | `00:05:00` | How long a TOTP step-up remains valid for sensitive account actions.

## Firewalling the syslog port

**Docker bypasses the host firewall for published ports.**  Traffic to a published container port flows through Docker's NAT/forward chains, not the `INPUT` chain that tools like ufw manage: a `ufw deny 5514` is silently ineffective.  Docker's sanctioned filtering hook is the `DOCKER-USER` chain, which Docker creates and never flushes.

Restrict UDP 5514 to a single sender:

```bash
sudo iptables -I DOCKER-USER -p udp --dport 5514 ! -s 192.0.2.10 -j DROP
```

Or to private ranges, if many LAN hosts will send syslog:

```bash
sudo iptables -I DOCKER-USER -p udp --dport 5514 -s 192.168.0.0/16 -j RETURN
sudo iptables -I DOCKER-USER -p udp --dport 5514 -s 172.16.0.0/12 -j RETURN
sudo iptables -I DOCKER-USER 3 -p udp --dport 5514 -j DROP
```

Persist across reboots:

```bash
sudo apt install iptables-persistent
sudo netfilter-persistent save        # after any rule change
```

Notes:

- This is defense-in-depth layer two.  Layer one is Viegard's own fail-closed source allowlist (`AllowedSources`), which drops and counts non-allowlisted datagrams before any parsing.  Skipping the firewall rule leaves integrity intact but exposes the socket to floods (kernel-buffer pressure can drop legitimate datagrams) and widens reachable surface.
- Neither layer defeats on-LAN source spoofing; both trust the claimed source address.  Anti-spoofing belongs to the network layer (router/switch controls).
- `172.16.0.0/12` includes Docker's own container networks; allowing it means containers on this host can send syslog too.  Decide deliberately.
- The admin port needs no rule here: it binds to `127.0.0.1` on the host and is not reachable through the Docker forward path.
- Host services that are not Docker-published (e.g., SSH) hit `INPUT` normally; ufw or plain nftables work fine for those.

## Operational notes

- `docker compose logs` keeps the container's full history; use `--since`/`-t` to separate fresh entries from old ones after a fix.
- Keep `Logging__LogLevel__Microsoft.EntityFrameworkCore: Warning` (in the example) so SQL statement logging stays quiet; noisy always-on errors train operators to ignore logs.
- Foreground `docker compose up` stops the stack on Ctrl-C; use `-d` for anything you want to survive the terminal.
- Benign startup warnings: `Overriding HTTP_PORTS ... Binding to values defined by URLS` (explicit `ASPNETCORE_URLS` supersedes the image default) and Postgres listening on IPv6 inside the compose network (nothing is published beyond admin's localhost 8080).
- The admin container's `No XML encryptor configured` warning is expected for now: keys persist to a permission-protected volume; encrypting them at rest is deliberately deferred to the admin-authentication work (TODO.md).
