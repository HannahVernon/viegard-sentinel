# Deploying Viegard

This runbook covers deploying the Viegard stack with Docker Compose and verifying it, including the post-deployment backup restore drill.  It reflects a verified deployment (2026-08-25) on a Debian 13 Docker host.  All addresses and names below are placeholders; substitute your own.

## Prerequisites

- Docker Engine with the Compose plugin
- A clone of this repository on the host

## 1. Prepare secrets and directories

Each secret is one file in `deploy/secrets/`; the file name is the secret name (D-0006).  These directories are gitignored and must never be committed.

```bash
cd deploy
mkdir -p secrets data/postgres backups/postgres
chmod 700 backups/postgres      # dumps will contain full email bodies and security history
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

## Operational notes

- `docker compose logs` keeps the container's full history; use `--since`/`-t` to separate fresh entries from old ones after a fix.
- Keep `Logging__LogLevel__Microsoft.EntityFrameworkCore: Warning` (in the example) so SQL statement logging stays quiet; noisy always-on errors train operators to ignore logs.
- Foreground `docker compose up` stops the stack on Ctrl-C; use `-d` for anything you want to survive the terminal.
