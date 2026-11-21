#!/usr/bin/env bash
set +x
set -euo pipefail
umask 077

release=$1
revision=$2
env_file=/opt/royaltyflow/deploy/.env
compose_file="$release/deploy/docker-compose.yml"
postgres=securities-repository-postgres-1
local_api=http://127.0.0.1:18080
public_api=https://royalty.investment-tracker.ru

fail() {
    printf '%s\n' "$*" >&2
    exit 1
}

[[ $revision =~ ^[a-f0-9]{40}$ && $release == /opt/royaltyflow-releases/release.* && -d $release && ! -L $release ]] ||
    fail 'Invalid release directory or revision.'
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

if ! docker load --input "$release/image.tar.gz" >/dev/null 2>&1; then
    fail 'Docker could not load the release image.'
fi
rm -- "$release/image.tar.gz"
release_image=$(docker image inspect --format '{{.Id}}' "royaltyflow-api:$revision")

migration_status=0
compose "$compose_file" "$revision" run -T --rm --no-deps --pull never royaltyflow-api --check-migrations \
    </dev/null >/dev/null 2>&1 || migration_status=$?
case "$migration_status" in
    0) printf 'Schema matches the release. Migration skipped.\n' ;;
    10)
        printf 'Pending migrations. Creating a verified backup of royaltyflow.\n'
        backup_dir=$(mktemp -d /root/royaltyflow-backup.XXXXXXXX)
        partial="$backup_dir/royaltyflow.dump.partial"
        if ! checked_config "$compose_file" "$revision" backup |
            docker exec -i "$postgres" sh -c '
                IFS= read -r PGPASSWORD || exit 1
                export PGPASSWORD PGCONNECT_TIMEOUT=5
                exec pg_dump -w -h securities-repository-postgres-1 -U royaltyflow_user -d royaltyflow -Fc
            ' >"$partial" 2>/dev/null; then
            fail 'pg_dump failed. Migration was skipped.'
        fi
        [[ -s $partial ]] &&
            docker exec -i "$postgres" pg_restore --list <"$partial" >/dev/null 2>&1 &&
            docker exec -i "$postgres" pg_restore --file=/dev/null <"$partial" >/dev/null 2>&1 ||
            fail 'Backup verification failed. Migration was skipped.'
        backup="$backup_dir/royaltyflow.dump"
        mv -- "$partial" "$backup"
        printf 'Verified backup: %s\n' "$backup"
        schema_touched=true
        compose "$compose_file" "$revision" run -T --rm --no-deps --pull never royaltyflow-api --migrate \
            </dev/null >/dev/null 2>&1 || fail "Migration failed. Existing API retained. Verified backup: $backup."
        ;;
    *) fail "Could not verify migration state (exit $migration_status). Check connectivity, PostgreSQL 16 and migration history." ;;
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
