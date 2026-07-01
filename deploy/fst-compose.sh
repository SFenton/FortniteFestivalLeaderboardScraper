#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd -P)"
ROUTING_KEY="FST_DEPLOY_COMPOSE_DIR"

trim() {
    local value="$1"
    value="${value#"${value%%[![:space:]]*}"}"
    value="${value%"${value##*[![:space:]]}"}"
    printf '%s' "$value"
}

read_env_value() {
    local file="$1"
    local key="$2"
    local line value

    [[ -f "$file" ]] || return 1

    while IFS= read -r line || [[ -n "$line" ]]; do
        line="$(trim "$line")"
        [[ -z "$line" || "$line" == \#* ]] && continue

        if [[ "$line" == export[[:space:]]* ]]; then
            line="$(trim "${line#export}")"
        fi

        [[ "$line" == "$key"* ]] || continue
        value="${line#"$key"}"
        value="$(trim "$value")"
        [[ "$value" == =* ]] || continue
        value="$(trim "${value#=}")"

        if [[ "$value" == \"*\" && "$value" == *\" ]]; then
            value="${value:1:${#value}-2}"
        elif [[ "$value" == \'*\' && "$value" == *\' ]]; then
            value="${value:1:${#value}-2}"
        fi

        printf '%s' "$value"
        return 0
    done < "$file"

    return 1
}

resolve_dir() {
    local dir="$1"

    if [[ "$dir" == "~" || "$dir" == "~/"* ]]; then
        dir="$HOME${dir#~}"
    elif [[ "$dir" != /* ]]; then
        dir="$REPO_ROOT/$dir"
    fi

    if [[ ! -d "$dir" ]]; then
        printf 'ERROR: %s points to a missing directory: %s\n' "$ROUTING_KEY" "$dir" >&2
        exit 1
    fi

    cd -- "$dir"
    pwd -P
}

compose_dir="${FST_DEPLOY_COMPOSE_DIR:-}"

if [[ -z "$compose_dir" ]]; then
    compose_dir="$(read_env_value "$REPO_ROOT/.env" "$ROUTING_KEY" || true)"
fi

if [[ -z "$compose_dir" ]]; then
    compose_dir="$(read_env_value "$SCRIPT_DIR/.env" "$ROUTING_KEY" || true)"
fi

if [[ -n "$compose_dir" ]]; then
    compose_dir="$(resolve_dir "$compose_dir")"
    printf 'Using %s=%s\n' "$ROUTING_KEY" "$compose_dir" >&2
else
    compose_dir="$SCRIPT_DIR"
    printf 'Using repo deploy compose directory: %s\n' "$compose_dir" >&2
fi

cd -- "$compose_dir"

if [[ "$#" -eq 0 ]]; then
    set -- up -d
fi

exec docker compose "$@"
