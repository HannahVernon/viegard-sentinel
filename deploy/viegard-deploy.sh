#!/usr/bin/env bash
#
# viegard-deploy.sh - stand up, upgrade, and check a Viegard deployment.
#
# Commands:
#   install   First-time setup on a fresh Debian host: prerequisites, clone,
#             secrets, optional TLS certificate + renewal hook, build, start.
#   upgrade   Pull the configured branch and rebuild/restart only when new
#             commits arrived (use --force to rebuild regardless).
#   status    Show container state, admin liveness, cert expiry, last backup.
#
# Common options:
#   --dir <path>      Deployment root            (default /opt/viegard-sentinel)
#   --branch <name>   Git branch to track        (default dev; main is the
#                     future release branch and currently lags dev)
#   --yes             Skip confirmation prompts
#
# install options:
#   --repo-url <url>  Clone source (default the public Forgejo repository)
#   --domain <host>   Admin host name for direct TLS.  Issues a Let's
#                     Encrypt certificate via host certbot and installs a
#                     renewal hook that copies each renewed certificate
#                     into the stack and restarts the admin service
#                     (decision record D-0034 in DECISIONS.md).  Omit for
#                     loopback-only exposure (default).
#   --email <addr>    ACME registration email (required with --domain)
#
# Secrets are generated only when missing and are never overwritten or
# printed.  docker-compose.yml is copied from the example only when missing;
# operator edits are never touched.  Re-running install is safe.
#
set -euo pipefail

DEFAULT_REPO_URL="https://code.hannahvernon.com/hannah-vernon/viegard-sentinel.git"
APP_UID=1654   # non-root 'app' user in the .NET base images (see TODO: per-service UIDs)

DIR=/opt/viegard-sentinel
BRANCH=dev
REPO_URL="$DEFAULT_REPO_URL"
DOMAIN=""
EMAIL=""
ASSUME_YES=0
FORCE=0
COMMAND="${1:-}"
[ $# -gt 0 ] && shift

usage() { awk 'NR > 1 && !/^#/ { exit } NR > 1 { sub(/^# ?/, ""); print }' "$0"; exit "${1:-1}"; }
log()   { printf '\033[1;36m[viegard]\033[0m %s\n' "$*"; }
warn()  { printf '\033[1;33m[viegard]\033[0m %s\n' "$*" >&2; }
die()   { printf '\033[1;31m[viegard]\033[0m %s\n' "$*" >&2; exit 1; }

while [ $# -gt 0 ]; do
    case "$1" in
        --dir)      DIR="$2"; shift 2 ;;
        --branch)   BRANCH="$2"; shift 2 ;;
        --repo-url) REPO_URL="$2"; shift 2 ;;
        --domain)   DOMAIN="$2"; shift 2 ;;
        --email)    EMAIL="$2"; shift 2 ;;
        --yes)      ASSUME_YES=1; shift ;;
        --force)    FORCE=1; shift ;;
        -h|--help)  usage 0 ;;
        *)          die "Unknown option: $1 (try --help)" ;;
    esac
done

case "$BRANCH" in
    dev|main) ;;
    *) warn "Branch '$BRANCH' is not dev or main; proceeding, but only dev and main are supported branches." ;;
esac

confirm() {
    [ "$ASSUME_YES" -eq 1 ] && return 0
    printf '%s [y/N] ' "$1"
    read -r reply
    case "$reply" in y|Y|yes|YES) return 0 ;; *) die "Aborted." ;; esac
}

require_root() {
    [ "$(id -u)" -eq 0 ] || die "This command must run as root (docker, chown $APP_UID, certbot)."
}

compose_dir() { printf '%s/deploy' "$DIR"; }

# ---------------------------------------------------------------- install --

install_prerequisites() {
    log "Installing prerequisites (git, docker, compose plugin, openssl${DOMAIN:+, certbot})..."
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -qq
    # Debian package names; docker compose v2 ships as docker-compose-v2 on
    # current Debian and as docker-compose-plugin from Docker's own apt repo.
    local pkgs="git openssl docker.io"
    if apt-cache show docker-compose-v2 >/dev/null 2>&1; then
        pkgs="$pkgs docker-compose-v2"
    elif apt-cache show docker-compose-plugin >/dev/null 2>&1; then
        pkgs="$pkgs docker-compose-plugin"
    fi
    [ -n "$DOMAIN" ] && pkgs="$pkgs certbot"
    # shellcheck disable=SC2086
    apt-get install -y -qq $pkgs
    systemctl enable --now docker >/dev/null 2>&1 || true
    docker compose version >/dev/null 2>&1 \
        || die "docker compose v2 is not available after install; install the compose plugin manually and re-run."
}

clone_or_update_repo() {
    if [ -d "$DIR/.git" ]; then
        log "Repository already present at $DIR; fetching and switching to '$BRANCH'."
        git -C "$DIR" fetch origin
        git -C "$DIR" switch "$BRANCH"
        git -C "$DIR" pull --ff-only origin "$BRANCH"
    else
        log "Cloning $REPO_URL (branch $BRANCH) into $DIR..."
        git clone --branch "$BRANCH" "$REPO_URL" "$DIR"
    fi
}

create_secret() {
    # create_secret <file> <byte-count>  - generate only when missing; never print.
    local file="$1" bytes="$2"
    if [ -f "$file" ]; then
        log "Secret $(basename "$file") already exists; keeping it."
    else
        (umask 077 && openssl rand -base64 "$bytes" | tr -d '\n' > "$file")
        log "Generated secret $(basename "$file")."
    fi
    chown "$APP_UID" "$file"
    chmod 400 "$file"
}

prepare_secrets_and_dirs() {
    local d; d="$(compose_dir)"
    log "Preparing directories and secrets under $d..."
    mkdir -p "$d/secrets" "$d/data/postgres" "$d/backups/postgres" "$d/data/dataprotection-keys"
    chmod 700 "$d/backups/postgres"                 # dumps hold mail bodies + security history
    chown "$APP_UID" "$d/data/dataprotection-keys"
    chmod 700 "$d/data/dataprotection-keys"
    create_secret "$d/secrets/viegard-db-password" 24
    create_secret "$d/secrets/viegard-admin-bootstrap-password" 32
    chmod 755 "$d/secrets"                          # container user must traverse
}

prepare_compose_file() {
    local d; d="$(compose_dir)"
    if [ -f "$d/docker-compose.yml" ]; then
        log "docker-compose.yml already exists; not touching it."
    else
        cp "$d/docker-compose.example.yml" "$d/docker-compose.yml"
        log "Created docker-compose.yml from the example (stays out of git)."
    fi
}

issue_certificate() {
    [ -n "$DOMAIN" ] || return 0
    [ -n "$EMAIL" ] || die "--domain requires --email for ACME registration."
    local d hook; d="$(compose_dir)"
    hook=/etc/letsencrypt/renewal-hooks/deploy/viegard.sh

    mkdir -p "$d/certs" && chmod 755 "$d/certs"

    if [ ! -f "$hook" ]; then
        log "Writing certbot deploy hook $hook..."
        cat > "$hook" <<HOOK
#!/bin/sh
set -e
D=/etc/letsencrypt/live/$DOMAIN
T=$d/certs
cp -L "\$D/fullchain.pem" "\$T/admin.crt"
cp -L "\$D/privkey.pem"  "\$T/admin.key"
chown $APP_UID:$APP_UID "\$T/admin.crt" "\$T/admin.key"
chmod 400 "\$T/admin.crt" "\$T/admin.key"
cd $d && docker compose restart viegard-admin
HOOK
        chmod 755 "$hook"
    else
        log "Certbot deploy hook already exists; keeping it."
    fi

    if [ -d "/etc/letsencrypt/live/$DOMAIN" ]; then
        log "Certificate for $DOMAIN already exists; skipping issuance."
    else
        confirm "Request a Let's Encrypt certificate for $DOMAIN now (port 80 must reach this host)?"
        certbot certonly --standalone --non-interactive --agree-tos \
            --email "$EMAIL" -d "$DOMAIN"
    fi

    # Place the current PEMs without waiting for the first renewal.  The
    # compose restart inside the hook is harmless before first start.
    log "Copying certificate into $d/certs..."
    sh "$hook" || true
}

start_stack() {
    local d; d="$(compose_dir)"
    log "Building and starting the stack (first start applies EF Core migrations)..."
    (cd "$d" && docker compose up -d --build)
}

verify_stack() {
    local d ok=1; d="$(compose_dir)"
    log "Waiting for the admin liveness endpoint..."
    for _ in $(seq 1 30); do
        if curl -fsS --max-time 2 http://127.0.0.1:8080/healthz >/dev/null 2>&1; then
            ok=0; break
        fi
        sleep 2
    done
    (cd "$d" && docker compose ps)
    if [ "$ok" -eq 0 ]; then
        log "Admin liveness check passed."
    else
        warn "Admin liveness check did not pass within 60s.  Inspect: (cd $d && docker compose logs -t --tail 50)"
        return 1
    fi
}

post_install_checklist() {
    cat <<'CHECK'

------------------------------------------------------------------------
Post-install checklist (edit deploy/docker-compose.yml, then
`docker compose up -d` from the deploy directory to apply):

 1. First login: user 'admin', password = contents of
    deploy/secrets/viegard-admin-bootstrap-password (root-readable only;
    forced password change + TOTP enrollment on first login; store the
    recovery codes OUTSIDE Viegard).
 2. Direct TLS exposure: set Viegard__Admin__Exposure=direct, the
    Tls__CertificatePath/KeyPath mounts, WebAuthn RelyingPartyId +
    Origins__0 for your domain, and Viegard__Admin__AllowedSources__*.
 3. Data sources ship disabled; enable deliberately, one at a time
    (docs/swag-syslog-setup.md for SWAG; firewall UDP 5514 via the
    DOCKER-USER chain per docs/deployment.md).
 4. Retention is fail-safe OFF: configure Viegard__Retention__* and add
    the singleton 'maintenance' role to enable purging (example values
    in the Data retention section of docs/deployment.md).
 5. Run the backup restore drill (docs/deployment.md section 4) once the
    first dump appears in deploy/backups/postgres/.
------------------------------------------------------------------------
CHECK
}

cmd_install() {
    require_root
    command -v curl >/dev/null 2>&1 || apt-get install -y -qq curl
    install_prerequisites
    clone_or_update_repo
    prepare_secrets_and_dirs
    prepare_compose_file
    issue_certificate
    start_stack
    verify_stack || true
    post_install_checklist
    log "Install complete.  Deployment root: $DIR (branch $BRANCH)."
}

# ---------------------------------------------------------------- upgrade --

cmd_upgrade() {
    require_root
    [ -d "$DIR/.git" ] || die "$DIR is not a Viegard clone; run install first."
    local d current before after; d="$(compose_dir)"

    current="$(git -C "$DIR" branch --show-current)"
    if [ "$current" != "$BRANCH" ]; then
        log "Switching from branch '$current' to '$BRANCH'..."
        git -C "$DIR" fetch origin
        git -C "$DIR" switch "$BRANCH"
    fi

    before="$(git -C "$DIR" rev-parse HEAD)"
    git -C "$DIR" fetch origin
    git -C "$DIR" pull --ff-only origin "$BRANCH"
    after="$(git -C "$DIR" rev-parse HEAD)"

    if [ "$before" = "$after" ] && [ "$FORCE" -eq 0 ]; then
        log "Already up to date on '$BRANCH' ($(git -C "$DIR" rev-parse --short HEAD)); nothing to do.  Use --force to rebuild anyway."
        return 0
    fi

    if [ "$before" != "$after" ]; then
        log "Updated $(git -C "$DIR" rev-parse --short "$before")..$(git -C "$DIR" rev-parse --short "$after"):"
        git -C "$DIR" --no-pager log --oneline "$before..$after" | sed 's/^/    /'
    fi

    confirm "Rebuild and restart the stack now (migrations apply automatically)?"
    (cd "$d" && docker compose up -d --build)
    verify_stack
    log "Upgrade complete on branch '$BRANCH' at $(git -C "$DIR" rev-parse --short HEAD)."
}

# ----------------------------------------------------------------- status --

cmd_status() {
    [ -d "$DIR/.git" ] || die "$DIR is not a Viegard clone."
    local d; d="$(compose_dir)"
    log "Branch: $(git -C "$DIR" branch --show-current) @ $(git -C "$DIR" rev-parse --short HEAD)"
    (cd "$d" && docker compose ps) || true
    if curl -fsS --max-time 2 http://127.0.0.1:8080/healthz >/dev/null 2>&1; then
        log "Admin liveness: OK"
    else
        warn "Admin liveness: NOT RESPONDING on 127.0.0.1:8080"
    fi
    if [ -f "$d/certs/admin.crt" ]; then
        log "Admin certificate: $(openssl x509 -in "$d/certs/admin.crt" -noout -enddate | sed 's/notAfter=/expires /')"
    fi
    local last_dump
    last_dump="$(find "$d/backups/postgres" -maxdepth 1 -name '*.dump' -printf '%T@ %p\n' 2>/dev/null | sort -rn | head -n 1 | cut -d' ' -f2- || true)"
    if [ -n "$last_dump" ]; then
        log "Latest backup: $(basename "$last_dump") ($(date -r "$last_dump" '+%Y-%m-%d %H:%M %Z'))"
    else
        warn "No database dumps found in $d/backups/postgres/."
    fi
}

# ------------------------------------------------------------------- main --

case "$COMMAND" in
    install) cmd_install ;;
    upgrade) cmd_upgrade ;;
    status)  cmd_status ;;
    -h|--help|help) usage 0 ;;
    *) usage 1 ;;
esac
