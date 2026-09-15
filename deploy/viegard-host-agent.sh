#!/usr/bin/env bash
#
# viegard-host-agent.sh - privileged host-side executor for fixed Viegard commands.
#
# The agent polls host_upgrade_commands through the adjacent PostgreSQL
# container, claims a Pending command for its configured target, and runs the
# fixed upgrade verb.  It intentionally reads no database credentials.  Access
# to Docker/root on the host is the trust boundary required to run upgrades.
#
# Configuration file: /etc/viegard/host-agent.conf
#   DEPLOY_DIR=/opt/viegard-sentinel/deploy
#   TARGET_NAME=vm
#   DB_SCHEMA=viegard
#   POLL_SECONDS=30
#
# Values are simple KEY=value pairs.  Do not store database credentials here.
#
set -euo pipefail

CONFIG_FILE=/etc/viegard/host-agent.conf
DEPLOY_DIR=/opt/viegard-sentinel/deploy
TARGET_NAME=vm
DB_SCHEMA=viegard
POLL_SECONDS=30
DETAIL_LIMIT=2000

log()  { printf '\033[1;36m[viegard-host-agent]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[viegard-host-agent]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31m[viegard-host-agent]\033[0m %s\n' "$*" >&2; exit 1; }

load_config() {
    if [ ! -f "$CONFIG_FILE" ]; then
        warn "$CONFIG_FILE not found; using defaults."
        return
    fi

    local raw_line key value
    while IFS='=' read -r key value || [ -n "${key:-}" ]; do
        raw_line="${key:-}${value:+=$value}"
        raw_line="${raw_line%$'\r'}"
        case "$raw_line" in
            ''|'#'*) continue ;;
        esac

        key="${raw_line%%=*}"
        value="${raw_line#*=}"
        [ "$key" != "$raw_line" ] || die "Invalid config line in $CONFIG_FILE: $raw_line"

        case "$key" in
            DEPLOY_DIR)    DEPLOY_DIR="$value" ;;
            TARGET_NAME)   TARGET_NAME="$value" ;;
            DB_SCHEMA)     DB_SCHEMA="$value" ;;
            POLL_SECONDS)  POLL_SECONDS="$value" ;;
            *)             warn "Ignoring unknown config key '$key'." ;;
        esac
    done < "$CONFIG_FILE"
}

validate_name() {
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

validate_poll_seconds() {
    local value="$1" length i character
    length="${#value}"
    [ "$length" -gt 0 ] || die "POLL_SECONDS is required."
    for ((i = 0; i < length; i++)); do
        character="${value:i:1}"
        case "$character" in
            [0-9]) ;;
            *) die "POLL_SECONDS must be a positive integer." ;;
        esac
    done
    [ "$value" -ge 1 ] || die "POLL_SECONDS must be at least 1."
}

validate_uuid() {
    local value="$1" i character
    [ "${#value}" -eq 36 ] || return 1
    for ((i = 0; i < 36; i++)); do
        character="${value:i:1}"
        case "$i:$character" in
            8:-|13:-|18:-|23:-) ;;
            *:[0-9]|*:[a-f]|*:[A-F]) ;;
            *) return 1 ;;
        esac
    done
}

quote_identifier() {
    printf '"%s"' "$1"
}

commands_table() {
    printf '%s.%s' "$(quote_identifier "$DB_SCHEMA")" "$(quote_identifier "host_upgrade_commands")"
}

psql_exec() {
    docker compose -f "$DEPLOY_DIR/docker-compose.yml" exec -T viegard-db \
        psql -q -U viegard -d viegard -v ON_ERROR_STOP=1 "$@"
}

# psql performs :'variable' interpolation only for SQL read from stdin or
# -f, never for -c command strings, so SQL with variables must be piped.
psql_exec_stdin() {
    local sql="$1"
    shift
    printf '%s\n' "$sql" | psql_exec "$@" -Atf -
}

claim_next_pending() {
    local table sql
    table="$(commands_table)"
    sql="
UPDATE $table AS command
SET status = 1,
    started_at = now(),
    detail = NULL
WHERE command.id = (
    SELECT pending.id
    FROM $table AS pending
    WHERE pending.target = :'target_name'
      AND pending.status = 0
    ORDER BY pending.requested_at, pending.id
    LIMIT 1
    FOR UPDATE SKIP LOCKED
)
RETURNING command.id;"
    psql_exec_stdin "$sql" -v "target_name=$TARGET_NAME" | head -n 1
}

# An agent restart can strand a command in Running (claimed but never
# completed, e.g. the agent died or, historically, discarded a claim it
# could not parse).  Exactly one agent serves a target, so at startup any
# Running command for this target is provably orphaned: requeue it.
requeue_orphaned_running() {
    local table sql requeued
    table="$(commands_table)"
    sql="
UPDATE $table
SET status = 0,
    started_at = NULL
WHERE target = :'target_name'
  AND status = 1
RETURNING id;"
    requeued="$(psql_exec_stdin "$sql" -v "target_name=$TARGET_NAME" | grep -c . || true)"
    if [ "${requeued:-0}" -gt 0 ]; then
        log "Requeued $requeued orphaned Running command(s) for target '$TARGET_NAME'."
    fi
}

complete_command() {
    local command_id="$1" status_value="$2" detail="$3" table sql
    table="$(commands_table)"
    sql="
UPDATE $table
SET status = :status_value::integer,
    finished_at = now(),
    detail = :'detail'
WHERE id = :'command_id'::uuid
  AND status = 1;"
    psql_exec_stdin "$sql" \
        -v "command_id=$command_id" \
        -v "status_value=$status_value" \
        -v "detail=$detail" >/dev/null
}

resolve_deploy_script() {
    local candidate
    candidate="$DEPLOY_DIR/viegard-deploy.sh"
    if [ -x "$candidate" ]; then
        printf '%s\n' "$candidate"
        return
    fi

    candidate="$DEPLOY_DIR/../deploy/viegard-deploy.sh"
    if [ -x "$candidate" ]; then
        printf '%s\n' "$candidate"
        return
    fi

    die "Could not find executable viegard-deploy.sh from DEPLOY_DIR=$DEPLOY_DIR."
}

tail_detail() {
    local text="$1" length
    length="${#text}"
    if [ "$length" -le "$DETAIL_LIMIT" ]; then
        printf '%s' "$text"
        return
    fi

    printf '%s' "${text:length-DETAIL_LIMIT:DETAIL_LIMIT}"
}

run_upgrade_command() {
    local command_id="$1" deploy_script output exit_code detail status_value
    deploy_script="$(resolve_deploy_script)"
    log "Claimed host upgrade command $command_id for target '$TARGET_NAME'."

    set +e
    output="$("$deploy_script" upgrade --yes 2>&1)"
    exit_code=$?
    set -e

    detail="$(tail_detail "$output")"
    if [ "$exit_code" -eq 0 ]; then
        status_value=2
        log "Upgrade command $command_id succeeded."
    else
        status_value=3
        warn "Upgrade command $command_id failed with exit code $exit_code."
    fi

    complete_command "$command_id" "$status_value" "$detail"
}

main() {
    load_config
    validate_name "TARGET_NAME" "$TARGET_NAME" 64
    validate_name "DB_SCHEMA" "$DB_SCHEMA" 63
    validate_poll_seconds "$POLL_SECONDS"
    [ -d "$DEPLOY_DIR" ] || die "DEPLOY_DIR does not exist: $DEPLOY_DIR"
    [ -f "$DEPLOY_DIR/docker-compose.yml" ] || die "docker-compose.yml not found in DEPLOY_DIR: $DEPLOY_DIR"

    log "Polling for target '$TARGET_NAME' every $POLL_SECONDS second(s); schema '$DB_SCHEMA'."
    requeue_orphaned_running
    while true; do
        local command_id
        if command_id="$(claim_next_pending)"; then
            if [ -n "$command_id" ]; then
                if ! validate_uuid "$command_id"; then
                    warn "Ignoring invalid command id returned by database: $command_id"
                else
                    run_upgrade_command "$command_id"
                    log "Re-executing host agent to pick up any upgraded script version."
                    exec "$0" "$@"
                fi
            fi
        else
            warn "Could not poll for host upgrade commands; retrying."
        fi

        sleep "$POLL_SECONDS"
    done
}

main "$@"
