#!/usr/bin/env bash
#
# viegard-deploy.sh - stand up, upgrade, and check a Viegard deployment.
#
# Commands:
#   install    First-time setup on a fresh Debian host: prerequisites, clone,
#              secrets, optional TLS certificate + renewal hook, build, start.
#   configure  Write deploy/docker-compose.generated.yml from the options
#              below and wire it in via COMPOSE_FILE in deploy/.env.  The
#              generated file is applied FIRST and your docker-compose.yml
#              is applied AFTER it, so any setting you declare in your own
#              file wins.  Re-running configure regenerates the whole file:
#              pass the complete set of options you want each time.
#   upgrade    Pull the configured branch and rebuild/restart only when new
#              commits arrived (use --force to rebuild regardless).
#   install-agent
#              Install the privileged host-side agent that polls fixed-verb
#              upgrade requests from PostgreSQL through the db container.
#   status     Show container state, admin liveness, cert expiry, last backup.
#
# Common options:
#   --dir <path>      Deployment root            (default /opt/viegard-sentinel)
#   --branch <name>   Git branch to track        (default dev; main is the
#                     future release branch and currently lags dev)
#   --yes             Skip confirmation prompts
#
# install options:
#   --repo-url <url>  Clone source (default the public Forgejo repository)
#   --email <addr>    ACME registration email (required with --domain)
#
# install + configure options:
#   --domain <host>   Admin host name for direct TLS.  install: issues a
#                     Let's Encrypt certificate via host certbot and adds a
#                     renewal hook that copies each renewed certificate
#                     into the stack and restarts the admin service
#                     (decision record D-0034 in DECISIONS.md).  Both
#                     commands: writes the direct-TLS settings (exposure,
#                     certificate mounts, WebAuthn relying party/origin)
#                     into the generated compose file.  Omit for
#                     loopback-only exposure (default).  NOTE: port lists
#                     are appended across compose files; if your own
#                     docker-compose.yml already publishes an admin HTTPS
#                     port, keep TLS there and do not pass --domain to
#                     configure, or the two published ports will conflict.
#   --https-port <n>  Host port published for admin HTTPS (default 443)
#   --allowed-sources <cidr[,cidr...]>
#                     Admin allowlist entries (fail-closed; empty keeps
#                     loopback-only)
#   --enable-retention
#                     Adds the singleton 'maintenance' role and the
#                     reference retention periods (raw 30d, events 90d,
#                     decision chain 180d, audit 365d, dead-letters 30d,
#                     expired sessions 30d).  Override any single value by
#                     declaring it in your own docker-compose.yml.
#   --enable-syslog --syslog-sources <cidr[,cidr...]>
#                     Enables the UDP syslog listener, publishes 5514/udp,
#                     and sets its fail-closed source allowlist.
#
# install-agent options:
#   --agent-deploy-dir <path>
#                     Deploy directory containing docker-compose.yml
#                     (default <deployment root>/deploy)
#   --agent-target <name>
#                     Host upgrade target name (default vm)
#   --agent-schema <name>
#                     PostgreSQL schema for Viegard objects (default viegard)
#   --agent-poll-seconds <n>
#                     Host agent polling interval in seconds (default 30)
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
HTTPS_PORT=443
ALLOWED_SOURCES=""
ENABLE_RETENTION=0
ENABLE_SYSLOG=0
SYSLOG_SOURCES=""
ASSUME_YES=0
FORCE=0
AGENT_DEPLOY_DIR=""
AGENT_TARGET=vm
AGENT_SCHEMA=viegard
AGENT_POLL_SECONDS=30
COMMAND="${1:-}"
[ $# -gt 0 ] && shift
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

usage() { awk 'NR > 1 && !/^#/ { exit } NR > 1 { sub(/^# ?/, ""); print }' "$0"; exit "${1:-1}"; }
log()   { printf '\033[1;36m[viegard]\033[0m %s\n' "$*"; }
warn()  { printf '\033[1;33m[viegard]\033[0m %s\n' "$*" >&2; }
die()   { printf '\033[1;31m[viegard]\033[0m %s\n' "$*" >&2; exit 1; }

while [ $# -gt 0 ]; do
    case "$1" in
        --dir)              DIR="$2"; shift 2 ;;
        --branch)           BRANCH="$2"; shift 2 ;;
        --repo-url)         REPO_URL="$2"; shift 2 ;;
        --domain)           DOMAIN="$2"; shift 2 ;;
        --email)            EMAIL="$2"; shift 2 ;;
        --https-port)       HTTPS_PORT="$2"; shift 2 ;;
        --allowed-sources)  ALLOWED_SOURCES="$2"; shift 2 ;;
        --enable-retention) ENABLE_RETENTION=1; shift ;;
        --enable-syslog)    ENABLE_SYSLOG=1; shift ;;
        --syslog-sources)   SYSLOG_SOURCES="$2"; shift 2 ;;
        --agent-deploy-dir) AGENT_DEPLOY_DIR="$2"; shift 2 ;;
        --agent-target)     AGENT_TARGET="$2"; shift 2 ;;
        --agent-schema)     AGENT_SCHEMA="$2"; shift 2 ;;
        --agent-poll-seconds) AGENT_POLL_SECONDS="$2"; shift 2 ;;
        --yes)              ASSUME_YES=1; shift ;;
        --force)            FORCE=1; shift ;;
        -h|--help)          usage 0 ;;
        *)                  die "Unknown option: $1 (try --help)" ;;
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

admin_probe_urls() {
    # Build candidate liveness URLs from the actual published ports.  In
    # loopback mode 8080 is published on 127.0.0.1; in direct TLS mode 8443
    # is published (possibly bound to a specific address) and 8080 may not
    # respond, so probe both.  TLS probes use -k: the certificate names the
    # public host, not the IP being probed.
    local d mapping; d="$(compose_dir)"
    mapping="$( (cd "$d" && docker compose port viegard-admin 8080) 2>/dev/null | head -n 1)"
    [ -n "$mapping" ] && printf 'http://%s/healthz\n' "${mapping/0.0.0.0/127.0.0.1}"
    mapping="$( (cd "$d" && docker compose port viegard-admin 8443) 2>/dev/null | head -n 1)"
    [ -n "$mapping" ] && printf 'https://%s/healthz\n' "${mapping/0.0.0.0/127.0.0.1}"
}

admin_alive() {
    local url
    while IFS= read -r url; do
        [ -n "$url" ] || continue
        if curl -fsk --max-time 2 "$url" >/dev/null 2>&1; then
            return 0
        fi
    done <<EOF
$(admin_probe_urls)
EOF
    return 1
}

verify_stack() {
    local d ok=1; d="$(compose_dir)"
    log "Waiting for the admin liveness endpoint..."
    for _ in $(seq 1 30); do
        if admin_alive; then
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
 2. Direct TLS, admin allowlist, retention, and syslog can all be written
    for you by the configure command (generated file loses to any setting
    you declare in docker-compose.yml):
      viegard-deploy.sh configure --domain <host> \
        --allowed-sources <cidr,...> --enable-retention \
        --enable-syslog --syslog-sources <cidr,...>
 3. Data sources ship disabled; enable deliberately, one at a time
    (docs/swag-syslog-setup.md for SWAG; firewall UDP 5514 via the
    DOCKER-USER chain per docs/deployment.md).
 4. Retention is fail-safe OFF until configured (--enable-retention above
    writes the reference periods; example values in the Data retention
    section of docs/deployment.md).
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
    if any_configure_flags; then
        [ "$ENABLE_SYSLOG" -eq 1 ] && [ -z "$SYSLOG_SOURCES" ] && die "--enable-syslog requires --syslog-sources (the allowlist is fail-closed)."
        generate_compose_fragment
    fi
    start_stack
    verify_stack || true
    post_install_checklist
    log "Install complete.  Deployment root: $DIR (branch $BRANCH)."
}

# -------------------------------------------------------------- configure --

any_configure_flags() {
    [ -n "$DOMAIN" ] || [ -n "$ALLOWED_SOURCES" ] || [ "$ENABLE_RETENTION" -eq 1 ] || [ "$ENABLE_SYSLOG" -eq 1 ]
}

emit_csv_env() {
    # emit_csv_env <out-file> <env-prefix> <start-index> <csv>
    # Generated array entries start at a high index so they never collide
    # with operator-declared __0..__N entries in docker-compose.yml
    # (the .NET configuration binder orders sparse indices numerically).
    local out="$1" prefix="$2" index="$3" csv="$4" item
    local IFS=','
    for item in $csv; do
        item="$(printf '%s' "$item" | tr -d '[:space:]')"
        [ -n "$item" ] || continue
        printf '      %s__%s: "%s"\n' "$prefix" "$index" "$item" >> "$out"
        index=$((index + 1))
    done
}

generate_compose_fragment() {
    local d out tmp; d="$(compose_dir)"
    out="$d/docker-compose.generated.yml"
    tmp="$out.tmp"

    {
        printf '# GENERATED by viegard-deploy.sh - do not hand-edit.\n'
        printf '# Applied BEFORE docker-compose.yml (see COMPOSE_FILE in .env), so any\n'
        printf '# setting you declare in docker-compose.yml overrides this file.\n'
        printf '# Regenerate with: viegard-deploy.sh configure <options>\n'
        printf 'services:\n'
    } > "$tmp"

    if [ -n "$DOMAIN" ] || [ -n "$ALLOWED_SOURCES" ]; then
        printf '  viegard-admin:\n' >> "$tmp"
        printf '    environment:\n' >> "$tmp"
        if [ -n "$DOMAIN" ]; then
            cat >> "$tmp" <<TLS
      Viegard__Admin__Exposure: direct
      Viegard__Admin__Tls__CertificatePath: /run/viegard/certs/admin.crt
      Viegard__Admin__Tls__KeyPath: /run/viegard/certs/admin.key
      Viegard__Admin__Tls__Port: "8443"
      Viegard__Admin__WebAuthn__RelyingPartyId: $DOMAIN
      Viegard__Admin__WebAuthn__Origins__0: https://$DOMAIN
TLS
        fi
        [ -n "$ALLOWED_SOURCES" ] && emit_csv_env "$tmp" "Viegard__Admin__AllowedSources" 50 "$ALLOWED_SOURCES"
        if [ -n "$DOMAIN" ]; then
            cat >> "$tmp" <<TLSMOUNT
    volumes:
      - ./certs:/run/viegard/certs:ro
    ports:
      - "$HTTPS_PORT:8443"
TLSMOUNT
        fi
    fi

    if [ "$ENABLE_RETENTION" -eq 1 ] || [ "$ENABLE_SYSLOG" -eq 1 ]; then
        printf '  viegard-pipeline:\n' >> "$tmp"
        printf '    environment:\n' >> "$tmp"
        if [ "$ENABLE_RETENTION" -eq 1 ]; then
            cat >> "$tmp" <<'RETENTION'
      # Singleton maintenance role at a high index so it never collides
      # with the Roles__0..N your docker-compose.yml declares.
      Viegard__Host__Roles__9: maintenance
      Viegard__Retention__RawObservationsDays: "30"
      Viegard__Retention__EventsDays: "90"
      Viegard__Retention__IncidentsDays: "180"
      Viegard__Retention__ClassificationsDays: "180"
      Viegard__Retention__DecisionsDays: "180"
      Viegard__Retention__ActionsDays: "180"
      Viegard__Retention__AuditRecordsDays: "365"
      Viegard__Retention__DeadLetteredQueueMessagesDays: "30"
      Viegard__Retention__ExpiredAdminSessionsDays: "30"
RETENTION
        fi
        if [ "$ENABLE_SYSLOG" -eq 1 ]; then
            printf '      Viegard__Sources__Syslog__Enabled: "true"\n' >> "$tmp"
            [ -n "$SYSLOG_SOURCES" ] && emit_csv_env "$tmp" "Viegard__Sources__Syslog__AllowedSources" 50 "$SYSLOG_SOURCES"
            cat >> "$tmp" <<'SYSLOGPORT'
    ports:
      - "5514:5514/udp"
SYSLOGPORT
        fi
    fi

    mv "$tmp" "$out"
    log "Wrote $out."

    # Wire the layering into .env: generated file first, operator file last
    # (later files win per setting).
    local envfile="$d/.env" wanted="COMPOSE_FILE=docker-compose.generated.yml:docker-compose.yml"
    if [ -f "$envfile" ] && grep -q '^COMPOSE_FILE=' "$envfile"; then
        if ! grep -qxF "$wanted" "$envfile"; then
            sed -i "s|^COMPOSE_FILE=.*|$wanted|" "$envfile"
            log "Updated COMPOSE_FILE in $envfile."
        fi
    else
        printf '%s\n' "$wanted" >> "$envfile"
        log "Added COMPOSE_FILE to $envfile."
    fi

    if docker compose version >/dev/null 2>&1; then
        if (cd "$d" && docker compose config --quiet); then
            log "Merged compose configuration validated."
        else
            die "docker compose config rejected the merged configuration; inspect $out and docker-compose.yml."
        fi
    fi
}

cmd_configure() {
    require_root
    local d; d="$(compose_dir)"
    [ -f "$d/docker-compose.yml" ] || die "$d/docker-compose.yml not found; run install first."
    any_configure_flags || die "configure needs at least one option (--domain, --allowed-sources, --enable-retention, --enable-syslog); pass the complete set you want each run."
    [ "$ENABLE_SYSLOG" -eq 1 ] && [ -z "$SYSLOG_SOURCES" ] && die "--enable-syslog requires --syslog-sources (the allowlist is fail-closed)."
    if [ -n "$DOMAIN" ] && [ ! -f "$d/certs/admin.crt" ]; then
        warn "No certificate at $d/certs/admin.crt yet; direct TLS will not start until one exists (run install --domain --email, or place PEMs manually)."
    fi
    generate_compose_fragment
    log "Apply with: cd $d && docker compose up -d"
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

# ---------------------------------------------------------- install-agent --

read_default() {
    local prompt="$1" default="$2" reply
    if [ "$ASSUME_YES" -eq 1 ]; then
        printf '%s\n' "$default"
        return
    fi

    # This function's stdout is captured by command substitution, so the
    # prompt must go to stderr or it is silently swallowed and the script
    # appears to hang while read waits on stdin.
    printf '%s [%s] ' "$prompt" "$default" >&2
    read -r reply
    if [ -n "$reply" ]; then
        printf '%s\n' "$reply"
    else
        printf '%s\n' "$default"
    fi
}

validate_agent_name() {
    local label="$1" value="$2" max="$3" length i character
    length="${#value}"
    [ "$length" -gt 0 ] || die "$label is required."
    [ "$length" -le "$max" ] || die "$label must be $max characters or fewer."
    for ((i = 0; i < length; i++)); do
        character="${value:i:1}"
        case "$character" in
            [a-z]|[0-9]|-|_) ;;
            *) die "$label must use only lowercase letters, digits, dash, and underscore." ;;
        esac
    done
}

validate_positive_integer() {
    local label="$1" value="$2" length i character
    length="${#value}"
    [ "$length" -gt 0 ] || die "$label is required."
    for ((i = 0; i < length; i++)); do
        character="${value:i:1}"
        case "$character" in
            [0-9]) ;;
            *) die "$label must be a positive integer." ;;
        esac
    done
    [ "$value" -ge 1 ] || die "$label must be at least 1."
}

validate_agent_deploy_dir() {
    local deploy_dir="$1"
    case "$deploy_dir" in
        *[[:space:]]*) die "Agent DEPLOY_DIR cannot contain whitespace because systemd reads it from an EnvironmentFile." ;;
    esac
    [ -d "$deploy_dir" ] || die "Agent DEPLOY_DIR does not exist: $deploy_dir"
    [ -f "$deploy_dir/docker-compose.yml" ] || die "docker-compose.yml not found in agent DEPLOY_DIR: $deploy_dir"
    [ -f "$deploy_dir/viegard-host-agent.sh" ] || die "viegard-host-agent.sh not found in agent DEPLOY_DIR: $deploy_dir"
}

cmd_install_agent() {
    require_root
    local default_deploy_dir deploy_dir target schema poll_seconds conf tmp_conf unit_src unit_dest
    default_deploy_dir="${AGENT_DEPLOY_DIR:-$(compose_dir)}"
    deploy_dir="$(read_default "Host agent deploy directory" "$default_deploy_dir")"
    target="$(read_default "Host agent target name" "$AGENT_TARGET")"
    schema="$(read_default "Host agent PostgreSQL schema" "$AGENT_SCHEMA")"
    poll_seconds="$(read_default "Host agent poll interval seconds" "$AGENT_POLL_SECONDS")"

    validate_agent_name "AGENT_TARGET" "$target" 64
    validate_agent_name "AGENT_SCHEMA" "$schema" 63
    validate_positive_integer "AGENT_POLL_SECONDS" "$poll_seconds"
    validate_agent_deploy_dir "$deploy_dir"

    conf=/etc/viegard/host-agent.conf
    tmp_conf="$conf.tmp"
    unit_src="$SCRIPT_DIR/viegard-host-agent.service"
    unit_dest=/etc/systemd/system/viegard-host-agent.service
    [ -f "$unit_src" ] || die "Host agent unit not found: $unit_src"

    mkdir -p /etc/viegard
    {
        printf '# Viegard host upgrade agent configuration.\n'
        printf '# Do not store database credentials here; the agent uses psql through the viegard-db container.\n'
        printf 'DEPLOY_DIR=%s\n' "$deploy_dir"
        printf 'TARGET_NAME=%s\n' "$target"
        printf 'DB_SCHEMA=%s\n' "$schema"
        printf 'POLL_SECONDS=%s\n' "$poll_seconds"
    } > "$tmp_conf"
    chmod 600 "$tmp_conf"
    mv "$tmp_conf" "$conf"

    cp "$unit_src" "$unit_dest"
    chmod 644 "$unit_dest"
    chmod 755 "$deploy_dir/viegard-host-agent.sh"
    systemctl daemon-reload
    systemctl enable viegard-host-agent.service >/dev/null
    systemctl restart viegard-host-agent.service

    if systemctl is-active --quiet viegard-host-agent.service; then
        log "Host agent installed and running for target '$target'."
    else
        die "Host agent service did not start; inspect: systemctl status viegard-host-agent.service --no-pager"
    fi
}

# ----------------------------------------------------------------- status --

cmd_status() {
    [ -d "$DIR/.git" ] || die "$DIR is not a Viegard clone."
    local d; d="$(compose_dir)"
    log "Branch: $(git -C "$DIR" branch --show-current) @ $(git -C "$DIR" rev-parse --short HEAD)"
    (cd "$d" && docker compose ps) || true
    if admin_alive; then
        log "Admin liveness: OK"
    else
        warn "Admin liveness: NOT RESPONDING on any published admin port"
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
    install)       cmd_install ;;
    configure)     cmd_configure ;;
    upgrade)       cmd_upgrade ;;
    install-agent) cmd_install_agent ;;
    status)        cmd_status ;;
    -h|--help|help) usage 0 ;;
    *) usage 1 ;;
esac
