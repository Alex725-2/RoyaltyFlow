#!/usr/bin/env bash
set +x
set -euo pipefail
umask 077

release=$1
revision=$2
mode=$3
env_file=/opt/royaltyflow/deploy/.env
compose_file="$release/deploy/docker-compose.yml"
postgres=securities-repository-postgres-1
local_api=http://127.0.0.1:18080
public_api=https://royalty.investment-tracker.ru

fail() {
    printf '%s\n' "$*" >&2
    exit 1
}

[[ $revision =~ ^[a-f0-9]{40}$ && -d $release && ! -L $release ]] ||
    fail 'Invalid release directory or revision.'
case "$mode:$release" in
    deploy:/opt/royaltyflow-releases/release.*|preflight:/tmp/royaltyflow-check.*|diagnose:/tmp/royaltyflow-check.*) ;;
    *) fail 'Invalid mode or staging directory.' ;;
esac
[[ -s $env_file && -f $env_file && ! -L $env_file && $(stat -c %a -- "$env_file") == 600 ]] ||
    fail 'Expected an existing regular deploy/.env with mode 600. Configure it on the VPS.'

exec 9</opt
flock -n 9 || fail 'Another deployment holds the /opt lock.'
docker info --format 'Docker server {{.ServerVersion}}'
docker compose version
df -h /opt /root "$(docker info --format '{{.DockerRootDir}}')"
docker network inspect securities-repository_default >/dev/null
docker exec "$postgres" pg_isready -q -U royaltyflow_user -d royaltyflow

compose() {
    local file=$1 tag=$2
    shift 2
    env -i PATH="$PATH" HOME=/root ROYALTYFLOW_TAG="$tag" docker compose \
        --project-name royaltyflow --env-file "$env_file" -f "$file" "$@"
}

checked_config() {
    local file=$1 tag=$2 mode=$3
    # Compose expands .env internally. Only the requested credential is sent to a private pipe.
    compose "$file" "$tag" config --format json 2>/dev/null | python3 -I -c '
import json
import re
import sys

tag, mode = sys.argv[1:]
config = json.load(sys.stdin)
service, = config["services"].values()
assert "royaltyflow-api" in config["services"] and service["image"] == f"royaltyflow-api:{tag}"
assert not service.get("volumes") and not config.get("volumes")
assert not service.get("privileged") and not service.get("network_mode")
assert set(service["networks"]) == {"postgres-network"}
assert config["networks"]["postgres-network"]["name"] == "securities-repository_default"
assert config["networks"]["postgres-network"]["external"] is True
port, = service["ports"]
assert (port["host_ip"], str(port["published"]), port["target"], port["protocol"]) == ("127.0.0.1", "18080", 8080, "tcp")
settings = service["environment"]
assert (settings["Postgres__Host"], settings["Postgres__Database"], settings["Postgres__User"]) == (
    "securities-repository-postgres-1", "royaltyflow", "royaltyflow_user")
password = settings["Postgres__Password"]
assert isinstance(password, str) and password.strip() and not any(c in password for c in "\r\n")
if mode != "rollback":
    demo = settings["DemoAccess__Password"]
    assert isinstance(demo, str) and demo.strip() and not any(c in demo for c in "\r\n")
    token = settings["DemoAccess__WriteToken"]
    assert re.fullmatch(r"[A-Za-z0-9_-]{32,128}", token) and token != demo
    if mode == "demo":
        print(demo)
    elif mode == "backup":
        print(password)
' "$tag" "$mode" 2>/dev/null
}

api_container() {
    docker ps -q --filter label=com.docker.compose.project=royaltyflow \
        --filter label=com.docker.compose.service=royaltyflow-api \
        --filter label=com.docker.compose.oneoff=False
}

wait_health() {
    local expected=$1 container state attempt
    for attempt in {1..40}; do
        container=$(api_container) || return 1
        if [[ -n $container ]]; then
            state=$(docker inspect --format '{{.Image}} {{if .State.Health}}{{.State.Health.Status}}{{end}}' "$container") || return 1
            if [[ $state == "$expected healthy" ]] &&
                curl --noproxy '*' -fsS --max-time 5 "$local_api/health/live" >/dev/null &&
                curl --noproxy '*' -fsS --max-time 5 "$local_api/health/ready" >/dev/null; then
                return 0
            fi
        fi
        sleep 2
    done
    return 1
}

checked_config "$compose_file" "$revision" check ||
    fail 'New Compose configuration is invalid. Check PostgreSQL and both authentication settings in deploy/.env.'
if [[ $mode == diagnose ]]; then
    if docker image inspect "royaltyflow-api:$revision" >/dev/null 2>&1; then
        printf 'Release image %s exists.\n' "$revision"
    else
        printf 'Release image %s is absent.\n' "$revision"
    fi
    report=''
    for candidate in /opt/royaltyflow-releases/release.*/migration-summary.txt; do
        if [[ -f $candidate && ! -L $candidate && ( -z $report || $candidate -nt $report ) ]]; then
            report=$candidate
        fi
    done
    if [[ -n $report ]]; then cat -- "$report"; else printf 'No migration summary has been saved.\n'; fi
    checked_config "$compose_file" "$revision" backup |
        docker exec -i "$postgres" sh -c '
            IFS= read -r PGPASSWORD || exit 1
            export PGPASSWORD PGCONNECT_TIMEOUT=5 PGOPTIONS="-c default_transaction_read_only=on -c statement_timeout=5000"
            exec psql -XwAt -v ON_ERROR_STOP=1 -h securities-repository-postgres-1 -U royaltyflow_user -d royaltyflow \
                -c "SELECT current_database(), EXISTS (SELECT FROM pg_catalog.pg_extension WHERE extname = '\''btree_gist'\''), pg_catalog.to_regclass('\''public.\"__EFMigrationsHistory\"'\'') IS NOT NULL"
        ' 2>/dev/null || fail 'Could not read PostgreSQL 16 metadata with the deployment credentials.'
    printf 'Fields: database | btree_gist installed | EF migration history exists.\n'
    exit 0
fi
current=$(api_container)
[[ $current =~ ^[a-f0-9]{12,64}$ ]] || fail 'Expected exactly one running RoyaltyFlow API for rollback.'
previous_image=$(docker inspect --format '{{.Image}}' "$current")
[[ $previous_image =~ ^sha256:[a-f0-9]{64}$ ]] || fail 'Could not identify the previous API image.'
previous_compose=$(docker inspect --format '{{index .Config.Labels "com.docker.compose.project.config_files"}}' "$current")
case "$previous_compose" in
    /opt/royaltyflow/deploy/docker-compose.yml|/opt/royaltyflow-releases/release.*/deploy/docker-compose.yml) ;;
    *) fail 'Rollback Compose file cannot be located from the running service.' ;;
esac
[[ -f $previous_compose && ! -L $previous_compose ]] || fail 'Rollback Compose file is unavailable.'
rollback_tag="rollback-${previous_image#sha256:}"
checked_config "$previous_compose" "$rollback_tag" rollback ||
    fail 'Rollback Compose configuration is invalid.'
wait_health "$previous_image" || fail 'Current API did not pass health checks. Deployment stopped.'
if [[ $mode == preflight ]]; then
    printf 'Preflight passed. The current container, Compose file and image are available for rollback.\n'
    exit 0
fi
docker image tag "$previous_image" "royaltyflow-api:$rollback_tag"

schema_touched=false
switch_attempted=false
on_exit() {
    local result=$?
    if ((result == 0)) || [[ $switch_attempted == false ]]; then
        return "$result"
    fi
    if [[ $schema_touched == true ]]; then
        printf 'Automatic application rollback requires the unchanged schema. Backup: %s\n' "$backup" >&2
    elif compose "$previous_compose" "$rollback_tag" up -d --no-deps --no-build --pull never royaltyflow-api >/dev/null 2>&1 &&
        wait_health "$previous_image"; then
        printf 'Previous API image restored and healthy. The release failed.\n' >&2
    else
        printf 'Automatic application rollback failed. Retained image: royaltyflow-api:%s\n' "$rollback_tag" >&2
    fi
    return "$result"
}
trap on_exit EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

printf 'Building RoyaltyFlow image from commit %s.\n' "$revision"
compose "$compose_file" "$revision" build royaltyflow-api ||
    fail 'Compose failed to build the release image.'
release_image=$(docker image inspect --format '{{.Id}}' "royaltyflow-api:$revision")

run_schema_command() {
    local argument=$1 failures=(0 0)
    compose "$compose_file" "$revision" run -T --rm --no-deps --pull never royaltyflow-api "$argument" </dev/null 2>&1 |
        awk '/^RoyaltyFlow (has pending migrations|schema is up to date|migrations applied)\.$/ ||
             /^Migration failed \([A-Za-z0-9_]+\)\. Check the royaltyflow target, PostgreSQL 16, schema permissions and concurrent migrations\.$/ {
                 print
             }' >> "$release/migration-summary.txt" || failures=("${PIPESTATUS[@]}")
    (( failures[1] == 0 )) || return 1
    printf '%s: Compose exit code %s\n' "$argument" "${failures[0]}" >> "$release/migration-summary.txt" || return 1
    return "${failures[0]}"
}

migration_status=0
run_schema_command --check-migrations || migration_status=$?
case "$migration_status" in
    0|10) ;;
    *) cat -- "$release/migration-summary.txt"; fail 'Could not verify migration state.' ;;
esac

printf 'Creating a verified backup of royaltyflow.\n'
backup_dir=$(mktemp -d /root/royaltyflow-backup.XXXXXXXX)
partial="$backup_dir/royaltyflow.dump.partial"
if ! checked_config "$compose_file" "$revision" backup |
    docker exec -i "$postgres" sh -c '
        IFS= read -r PGPASSWORD || exit 1
        export PGPASSWORD PGCONNECT_TIMEOUT=5
        exec pg_dump -w -h securities-repository-postgres-1 -U royaltyflow_user -d royaltyflow -Fc
    ' >"$partial" 2>/dev/null; then
    fail 'pg_dump failed. The current API is still running.'
fi
[[ -s $partial ]] &&
    docker exec -i "$postgres" pg_restore --list <"$partial" >/dev/null 2>&1 &&
    docker exec -i "$postgres" pg_restore --file=/dev/null <"$partial" >/dev/null 2>&1 ||
    fail 'Backup verification failed. The current API is still running.'
backup="$backup_dir/royaltyflow.dump"
mv -- "$partial" "$backup"
printf 'Verified backup: %s\n' "$backup"

case "$migration_status" in
    0) printf 'Schema matches the release. Migration skipped.\n' ;;
    10)
        schema_touched=true
        if ! run_schema_command --migrate; then
            cat -- "$release/migration-summary.txt"
            fail "Migration failed. Existing API retained. Verified backup: $backup."
        fi
        ;;
esac

switch_attempted=true
compose "$compose_file" "$revision" up -d --no-deps --no-build --pull never royaltyflow-api >/dev/null 2>&1 ||
    fail 'Compose failed to start the release API.'
wait_health "$release_image" || fail 'Release container did not become healthy and ready.'
status=$(curl --noproxy '*' -sS --max-time 10 -o /dev/null -w '%{http_code}' "$public_api/swagger/index.html") ||
    fail 'Public Swagger did not respond.'
[[ $status == 200 ]] || fail "Public Swagger returned HTTP $status."
status=$(curl --noproxy '*' -sS --max-time 10 -o /dev/null -w '%{http_code}' "$public_api/api/usage") ||
    fail 'Public API did not respond.'
[[ $status == 401 ]] || fail "Public API should challenge anonymous reads (HTTP $status)."
demo_password=$(checked_config "$compose_file" "$revision" demo) ||
    fail 'Could not load the demo credential for the smoke test.'
ROYALTYFLOW_DEMO_PASSWORD="$demo_password" python3 "$release/scripts/smoke_test.py" "$public_api" --read-only ||
    fail 'Public read-only smoke test failed.'
printf 'RoyaltyFlow release %s is healthy.\n' "$revision"
