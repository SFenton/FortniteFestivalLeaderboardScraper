#!/bin/sh
set -eu

proc_root="${1:?proc root required}"
application_name="${2:?application name required}"
shift 2

is_excluded() {
    candidate="$1"
    shift
    [ "$candidate" = "$$" ] && return 0
    [ "$candidate" = "$PPID" ] && return 0
    for excluded in "$@"; do
        [ "$candidate" = "$excluded" ] && return 0
    done
    return 1
}

for process_dir in "$proc_root"/[0-9]*; do
    [ -d "$process_dir" ] || continue
    pid="${process_dir##*/}"
    is_excluded "$pid" "$@" && continue
    executable="$(readlink "$process_dir/exe" 2>/dev/null || true)"
    [ "${executable##*/}" = "psql" ] || continue
    [ -r "$process_dir/cmdline" ] || continue
    if tr '\000' '\n' < "$process_dir/cmdline" |
        awk -v needle="application_name=$application_name" '
            {
                count = split($0, fields, /[[:space:]]+/)
                for (field_index = 1; field_index <= count; field_index++) {
                    if (fields[field_index] == needle) {
                        found = 1
                    }
                }
            }
            END { exit found ? 0 : 1 }
        '; then
        printf '%s\n' "$pid"
    fi
done
