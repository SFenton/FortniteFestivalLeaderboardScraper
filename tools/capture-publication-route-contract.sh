#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
    printf 'Usage: %s OUTPUT_DIRECTORY\n' "$0" >&2
    exit 64
fi

required_base=/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence
evidence_root=${FST_ROUTE_EVIDENCE_ROOT:-}
account_id=${FST_ROUTE_SAMPLE_ACCOUNT_ID:-}
if [[ -z "$evidence_root" || -z "$account_id" ]]; then
    printf 'ERROR: FST_ROUTE_EVIDENCE_ROOT and FST_ROUTE_SAMPLE_ACCOUNT_ID are required.\n' >&2
    exit 64
fi

required_base=$(realpath -e -- "$required_base")
evidence_root=$(realpath -e -- "$evidence_root")
case "$evidence_root/" in
    "$required_base/"*) ;;
    *)
        printf 'ERROR: route evidence root must remain below %s.\n' \
            "$required_base" >&2
        exit 1
        ;;
esac

requested_output=$(realpath -m -- "$1")
output_parent=$(realpath -e -- "$(dirname -- "$requested_output")")
output_directory="$output_parent/$(basename -- "$requested_output")"
case "$output_directory/" in
    "$evidence_root/"*) ;;
    *)
        printf 'ERROR: route output must remain below %s.\n' \
            "$evidence_root" >&2
        exit 1
        ;;
esac
if [[ -e "$output_directory" || -L "$output_directory" ]]; then
    printf 'ERROR: route output already exists: %s\n' \
        "$output_directory" >&2
    exit 1
fi

base_url=${API_BASE:-http://127.0.0.1:3001}

service_info=$(curl -fsS --max-time 15 "$base_url/api/service-info")
if ! jq -e '
    .currentUpdate.status == "idle"
    and .publication.publicReadsFrozen == false
    and (.publishedScrapeId // 0) > 0
' <<<"$service_info" >/dev/null; then
    printf 'ERROR: route capture requires an idle, unfrozen publication.\n' >&2
    exit 1
fi

publication_bootstrap=$(
    curl -fsS --max-time 15 "$base_url/api/publication"
)
publication_id=$(jq -r '.publicationId // 0' <<<"$publication_bootstrap")
published_scrape_id=$(
    jq -r '.publishedScrapeId // 0' <<<"$publication_bootstrap"
)
if [[ "$publication_id" -le 0 || "$published_scrape_id" -le 0 ]]; then
    printf 'ERROR: current publication identity is incomplete.\n' >&2
    exit 1
fi

song_id=${FST_ROUTE_SAMPLE_SONG_ID:-1fcee9b4-dc49-41b1-8e7d-add244f556a2}
instrument=${FST_ROUTE_SAMPLE_INSTRUMENT:-Solo_Vocals}
band_type=${FST_ROUTE_SAMPLE_BAND_TYPE:-Band_Duets}
combo=${FST_ROUTE_SAMPLE_COMBO:-01}
difficulty=${FST_ROUTE_SAMPLE_DIFFICULTY:-Expert}

band_seed=$(
    curl -sS --max-time 15 \
        "$base_url/api/rankings/bands/$band_type?rankBy=adjusted&page=1&pageSize=1" \
        2>/dev/null \
        || printf '{}'
)
team_key=${FST_ROUTE_SAMPLE_TEAM_KEY:-}
if [[ -z "$team_key" ]]; then
    team_key=$(
        jq -r '
            (
                if type == "object"
                then (.entries // .rankings // [])
                elif type == "array"
                then .
                else []
                end
            )[0]
            | .teamKey // .TeamKey // empty
        ' <<<"$band_seed"
    )
fi
if [[ -z "$team_key" ]]; then
    printf 'ERROR: no team key was discovered; set FST_ROUTE_SAMPLE_TEAM_KEY.\n' >&2
    exit 1
fi

rivals_seed=$(
    curl -sS --max-time 15 \
        "$base_url/api/player/$account_id/leaderboard-rivals/$instrument" \
        2>/dev/null \
        || printf '{}'
)
rival_id=$(
    jq -r '
        [
            .. |
            objects |
            .rivalAccountId?
            // .rivalId?
            // .accountId?
            // empty
        ]
        | map(select(type == "string" and length > 0))
        | .[0] // empty
    ' <<<"$rivals_seed"
)
if [[ -z "$rival_id" ]]; then
    rival_id=$account_id
fi

band_id=${FST_ROUTE_SAMPLE_BAND_ID:-}
if [[ -z "$band_id" ]]; then
    band_search=$(
        curl -sS --max-time 60 \
            "$base_url/api/bands/search?q=a" \
            || printf '[]'
    )
    if jq -e . >/dev/null 2>&1 <<<"$band_search"; then
        band_id=$(
            jq -r '
                (
                    if type == "object"
                    then (.bands // .results // .entries // [])
                    elif type == "array"
                    then .
                    else []
                    end
                )[0]
                | .bandId // .id // .teamKey // empty
            ' <<<"$band_search"
        )
    fi
fi
if [[ -z "$band_id" ]]; then
    band_id=$team_key
fi

scope_id=${FST_ROUTE_SAMPLE_SCOPE_ID:-all}
mkdir "$output_directory"
mkdir "$output_directory/raw" "$output_directory/normalized"
routes=(
    "POST|account-name-refresh|/api/account/name-refresh|{}"
    "GET|account-search|/api/account/search?q=a|"
    "GET|shop|/api/shop|"
    "GET|songs|/api/songs|"
    "GET|member-score-filter|/api/songs/member-score-filter|"
    "GET|path-image|/api/paths/$song_id/$instrument/$difficulty|"
    "GET|path-data|/api/paths/$song_id/$instrument/$difficulty/data|"
    "GET|leaderboard-bands-all|/api/leaderboard/$song_id/bands/all|"
    "GET|leaderboard-band-type|/api/leaderboard/$song_id/bands/$band_type|"
    "GET|leaderboard-member-scores|/api/leaderboard/$song_id/members/scores|"
    "GET|leaderboard-instrument|/api/leaderboard/$song_id/$instrument?top=23&offset=11|"
    "GET|leaderboard-rank-offsets|/api/leaderboard-rank-offsets/$song_id/$instrument|"
    "GET|leaderboard-all|/api/leaderboard/$song_id/all|"
    "GET|player|/api/player/$account_id|"
    "GET|player-stats|/api/player/$account_id/stats|"
    "GET|player-bands|/api/player/$account_id/bands|"
    "GET|player-band-type|/api/player/$account_id/bands/$band_type|"
    "GET|player-history|/api/player/$account_id/history?days=3650|"
    "GET|player-export|/api/player/$account_id/export|"
    "GET|band-export|/api/bands/$band_type/$team_key/export|"
    "GET|leaderboard-rivals|/api/player/$account_id/leaderboard-rivals/$instrument|"
    "GET|leaderboard-rival|/api/player/$account_id/leaderboard-rivals/$instrument/$rival_id|"
    "GET|rivals|/api/player/$account_id/rivals|"
    "GET|rivals-suggestions|/api/player/$account_id/rivals/suggestions|"
    "GET|rivals-all|/api/player/$account_id/rivals/all|"
    "GET|rivals-combo|/api/player/$account_id/rivals/$combo|"
    "GET|rivals-combo-rival|/api/player/$account_id/rivals/$combo/$rival_id|"
    "GET|rival-songs|/api/player/$account_id/rivals/$rival_id/songs/$instrument|"
    "GET|player-notifications|/api/player/$account_id/notifications|"
    "GET|band-team-notifications|/api/rankings/bands/$band_type/$team_key/notifications|"
    "GET|band-id-notifications|/api/bands/$band_id/notifications|"
    "GET|selected-members|/api/rankings/selected-members|"
    "GET|family|/api/rankings/family/$scope_id|"
    "GET|family-account|/api/rankings/family/$scope_id/$account_id|"
    "GET|rankings-instrument|/api/rankings/$instrument?rankBy=totalscore&page=1&pageSize=10|"
    "GET|rankings-account|/api/rankings/$instrument/$account_id?rankBy=totalscore|"
    "GET|rankings-history|/api/rankings/$instrument/$account_id/history?days=3650|"
    "GET|rankings-composite|/api/rankings/composite?page=1&pageSize=10|"
    "GET|rankings-composite-account|/api/rankings/composite/$account_id|"
    "GET|rankings-combo|/api/rankings/combo?page=1&pageSize=10|"
    "GET|rankings-combo-account|/api/rankings/combo/$account_id|"
    "GET|band-combos|/api/rankings/bands/$band_type/combos|"
    "GET|band-rankings|/api/rankings/bands/$band_type?rankBy=adjusted&page=1&pageSize=10|"
    "GET|bands-search|/api/bands/search?q=a|"
    "GET|band-id|/api/bands/$band_id|"
    "GET|band-history|/api/rankings/bands/$band_type/$team_key/history?days=3650|"
    "GET|band-songs|/api/rankings/bands/$band_type/$team_key/songs?limit=20|"
    "GET|band-song-rows|/api/rankings/bands/$band_type/$team_key/song-rows|"
    "GET|band-ranking|/api/rankings/bands/$band_type/$team_key?rankBy=adjusted|"
    "GET|rankings-neighborhood|/api/rankings/$instrument/$account_id/neighborhood?radius=2|"
    "GET|composite-neighborhood|/api/rankings/composite/$account_id/neighborhood?radius=2|"
    "GET|rankings-overview|/api/rankings/overview|"
    "GET|first-seen|/api/firstseen|"
    "GET|leaderboard-population|/api/leaderboard-population|"
    "GET|websocket-http-admission|/api/ws|"
)

if [[ ${#routes[@]} -ne 55 ]]; then
    printf 'ERROR: expected exactly 55 route captures, found %s.\n' \
        "${#routes[@]}" >&2
    exit 1
fi

manifest_entries="$output_directory/entries.jsonl"
: >"$manifest_entries"
for route in "${routes[@]}"; do
    IFS='|' read -r method name path body <<<"$route"
    raw="$output_directory/raw/$name.body"
    headers="$output_directory/raw/$name.headers"
    metrics="$output_directory/raw/$name.metrics"
    : >"$raw"
    : >"$headers"
    route_timeout=30
    if [[ "$name" == "player-export"
        || "$name" == "band-export" ]]; then
        route_timeout=120
    elif [[ "$name" == "leaderboard-rivals"
        || "$name" == "leaderboard-rival" ]]; then
        route_timeout=15
    fi

    curl_args=(
        -sS
        --max-time "$route_timeout"
        -X "$method"
        -D "$headers"
        -o "$raw"
        -w '%{http_code}|%{time_total}|%{size_download}|%{content_type}'
    )
    if [[ "$method" == "POST" ]]; then
        curl_args+=(
            -H 'Content-Type: application/json'
            --data "$body"
        )
    fi

    set +e
    result=$(curl "${curl_args[@]}" "$base_url$path")
    curl_exit=$?
    set -e
    if [[ -z "$result" ]]; then
        result="000|$route_timeout|0|"
    fi
    printf '%s\n' "$result" >"$metrics"
    IFS='|' read -r status duration bytes content_type <<<"$result"

    normalized="$output_directory/normalized/$name.json"
    is_json=false
    semantic_hash=
    if jq -e . "$raw" >/dev/null 2>&1; then
        jq -S -c . "$raw" >"$normalized"
        semantic_hash=$(sha256sum "$normalized" | cut -d' ' -f1)
        is_json=true
    fi
    raw_hash=$(sha256sum "$raw" | cut -d' ' -f1)

    jq -nc \
        --arg method "$method" \
        --arg name "$name" \
        --arg path "$path" \
        --arg status "$status" \
        --arg duration "$duration" \
        --arg bytes "$bytes" \
        --arg contentType "$content_type" \
        --arg rawHash "$raw_hash" \
        --arg semanticHash "$semantic_hash" \
        --arg curlExit "$curl_exit" \
        --argjson isJson "$is_json" \
        '{
            method: $method,
            name: $name,
            path: $path,
            status: ($status | tonumber),
            curlExit: ($curlExit | tonumber),
            durationSeconds: ($duration | tonumber),
            bytes: ($bytes | tonumber),
            contentType: $contentType,
            rawSha256: $rawHash,
            semanticSha256: (
                if $semanticHash == ""
                then null
                else $semanticHash
                end
            ),
            isJson: $isJson
        }' >>"$manifest_entries"
done

publication_after=$(
    curl -fsS --max-time 15 "$base_url/api/publication"
)
service_info_after=$(
    curl -fsS --max-time 15 "$base_url/api/service-info"
)
if [[ "$(jq -r '.publicationId // 0' <<<"$publication_after")" \
        != "$publication_id" \
    || "$(jq -r '.publishedScrapeId // 0' <<<"$publication_after")" \
        != "$published_scrape_id" ]] \
    || ! jq -e '
        .currentUpdate.status == "idle"
        and .publication.publicReadsFrozen == false
    ' <<<"$service_info_after" >/dev/null; then
    printf 'ERROR: publication changed or became unsafe during route capture.\n' >&2
    exit 1
fi

jq -s \
    --arg capturedAtUtc "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
    --argjson publicationId "$publication_id" \
    --argjson publishedScrapeId "$published_scrape_id" \
    --arg songId "$song_id" \
    --arg instrument "$instrument" \
    --arg bandType "$band_type" \
    --arg accountIdHash "$(printf '%s' "$account_id" | sha256sum | cut -d' ' -f1)" \
    --arg rivalIdHash "$(printf '%s' "$rival_id" | sha256sum | cut -d' ' -f1)" \
    --arg teamKeyHash "$(printf '%s' "$team_key" | sha256sum | cut -d' ' -f1)" \
    '{
        capturedAtUtc: $capturedAtUtc,
        publicationId: $publicationId,
        publishedScrapeId: $publishedScrapeId,
        routeCount: length,
        samples: {
            songId: $songId,
            instrument: $instrument,
            bandType: $bandType,
            accountIdSha256: $accountIdHash,
            rivalIdSha256: $rivalIdHash,
            teamKeySha256: $teamKeyHash
        },
        statuses:
            group_by(.status)
            | map({
                key: (.[0].status | tostring),
                value: length
            })
            | from_entries,
        entries: .
    }' "$manifest_entries" >"$output_directory/manifest.json"

jq -e '.routeCount == 55' \
    "$output_directory/manifest.json" >/dev/null
find "$output_directory" -type f \
    ! -name SHA256SUMS \
    -printf '%P\0' \
    | sort -z \
    | while IFS= read -r -d '' relative; do
        printf '%s  %s\n' \
            "$(sha256sum "$output_directory/$relative" | cut -d' ' -f1)" \
            "$relative"
    done >"$output_directory/SHA256SUMS"

jq '{
    publicationId,
    publishedScrapeId,
    routeCount,
    statuses,
    maxDurationSeconds:
        ([.entries[].durationSeconds] | max)
}' "$output_directory/manifest.json"
