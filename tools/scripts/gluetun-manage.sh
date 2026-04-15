#!/usr/bin/env bash
# gluetun-manage.sh — CLI for managing gluetun VPN proxy containers.
#
# Usage:
#   ./gluetun-manage.sh status                              # Show VPN status of all gluetun containers
#   ./gluetun-manage.sh ip [instance]                       # Show public IP (all or specific)
#   ./gluetun-manage.sh city <instance> <city>              # Switch to any server in a city
#   ./gluetun-manage.sh server <instance> <city> <name>     # Switch to a specific named server
#   ./gluetun-manage.sh cycle-all                           # Rotate all to diverse servers (partitioned)
#   ./gluetun-manage.sh update-servers [instance]           # Refresh VPN server list
#   ./gluetun-manage.sh restart [instance]                  # Docker restart container(s)
#   ./gluetun-manage.sh logs [instance] [--follow]          # Tail container logs
#
# Environment:
#   GLUETUN_CONTROL_PORT  — Control API port (default: 8000)
#   GLUETUN_PATTERN       — Container name glob (default: gluetun-*)

set -euo pipefail

# ── Configuration ──────────────────────────────────────────
CONTROL_PORT="${GLUETUN_CONTROL_PORT:-8000}"
PATTERN="${GLUETUN_PATTERN:-gluetun-*}"
POLL_INTERVAL=3
POLL_TIMEOUT=45

# ── Colors ─────────────────────────────────────────────────
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
CYAN='\033[0;36m'
BOLD='\033[1m'
NC='\033[0m' # No Color

# ── Server tiers (matches RoundRobinProxyHandler.ServerTiers) ──
# Each entry is "City|ServerName". Partitioned across slots so no server
# appears in more than one slot's list. 255 servers across 40 cities, 5 tiers.

# Tier 0: US (lowest latency to Epic CDN) — 48 servers
TIER0=(
    "Los Angeles|Maia" "Los Angeles|Revati" "Los Angeles|Sarin" "Los Angeles|Xamidimura"
    "Chicago Illinois|Fang" "Chicago Illinois|Kruger" "Chicago Illinois|Meridiana"
    "Chicago Illinois|Praecipua" "Chicago Illinois|Sadalsuud" "Chicago Illinois|Sneden" "Chicago Illinois|Superba"
    "New York City|Muliphein" "New York City|Paikauhale" "New York City|Sadalmelik"
    "New York City|Terebellum" "New York City|Unukalhai" "New York City|Unurgunite"
    "Dallas Texas|Chamaeleon" "Dallas Texas|Equuleus" "Dallas Texas|Helvetios"
    "Dallas Texas|Leo" "Dallas Texas|Mensa" "Dallas Texas|Pegasus"
    "Dallas Texas|Ran" "Dallas Texas|Scutum" "Dallas Texas|Volans" "Dallas Texas|Vulpecula"
    "Miami|Aladfar" "Miami|Ascella" "Miami|Chertan"
    "Miami|Dziban" "Miami|Elkurud" "Miami|Giausar" "Miami|Meleph"
    "Atlanta Georgia|Hercules" "Atlanta Georgia|Libra" "Atlanta Georgia|Musca"
    "Atlanta Georgia|Sculptor" "Atlanta Georgia|Ursa"
    "Denver Colorado|Sadachbia" "Denver Colorado|Torcular"
    "Phoenix Arizona|Guniibuu" "Phoenix Arizona|Khambalia" "Phoenix Arizona|Sheratan"
    "San Jose California|Bunda" "San Jose California|Imai"
    "Fremont California|Aquila"
    "Raleigh North Carolina|Polis"
)

# Tier 1: Canada — 38 servers
TIER1=(
    "Montreal|Lacerta" "Montreal|Ross"
    "Toronto Ontario|Agena" "Toronto Ontario|Alhena" "Toronto Ontario|Alkurhah"
    "Toronto Ontario|Aludra" "Toronto Ontario|Alwaid" "Toronto Ontario|Alya"
    "Toronto Ontario|Angetenar" "Toronto Ontario|Arkab" "Toronto Ontario|Avior"
    "Toronto Ontario|Castula" "Toronto Ontario|Cephei" "Toronto Ontario|Chamukuy"
    "Toronto Ontario|Chort" "Toronto Ontario|Elgafar" "Toronto Ontario|Enif"
    "Toronto Ontario|Gorgonea" "Toronto Ontario|Kornephoros" "Toronto Ontario|Lesath"
    "Toronto Ontario|Mintaka" "Toronto Ontario|Regulus" "Toronto Ontario|Rotanev"
    "Toronto Ontario|Sadalbari" "Toronto Ontario|Saiph" "Toronto Ontario|Sargas"
    "Toronto Ontario|Sharatan" "Toronto Ontario|Sualocin" "Toronto Ontario|Tegmen"
    "Toronto Ontario|Tejat" "Toronto Ontario|Tyl" "Toronto Ontario|Ukdah"
    "Vancouver|Ginan" "Vancouver|Nahn" "Vancouver|Pisces"
    "Vancouver|Sham" "Vancouver|Telescopium" "Vancouver|Titawin"
)

# Tier 2: Western Europe (moderate latency) — 102 servers
TIER2=(
    "London|Amansinaya" "London|Arber" "London|Baiduri"
    "Amsterdam|Taiyangshou" "Amsterdam|Vindemiatrix"
    "Dublin|Minchir"
    "Frankfurt|Adhara" "Frankfurt|Adhil" "Frankfurt|Alsephina"
    "Frankfurt|Ashlesha" "Frankfurt|Cervantes" "Frankfurt|Dubhe"
    "Frankfurt|Errai" "Frankfurt|Fuyue" "Frankfurt|Menkalinan"
    "Frankfurt|Mirfak" "Frankfurt|Mirzam" "Frankfurt|Ogma"
    "Brussels|Capricornus" "Brussels|Castor" "Brussels|Columba"
    "Brussels|Diadema" "Brussels|Mebsuta"
    "Madrid|Jishui" "Madrid|Mekbuda" "Madrid|Taurus"
    "Barcelona|Eridanus"
    "Manchester|Bubup" "Manchester|Ceibo" "Manchester|Chaophraya"
    "Alblasserdam|Alchiba" "Alblasserdam|Alcyone" "Alblasserdam|Aljanah"
    "Alblasserdam|Alphard" "Alblasserdam|Alphecca" "Alblasserdam|Alpheratz"
    "Alblasserdam|Alphirk" "Alblasserdam|Alrai" "Alblasserdam|Alshat"
    "Alblasserdam|Alterf" "Alblasserdam|Alzirr" "Alblasserdam|Ancha"
    "Alblasserdam|Andromeda" "Alblasserdam|Anser" "Alblasserdam|Asellus"
    "Alblasserdam|Aspidiske" "Alblasserdam|Atik" "Alblasserdam|Canis"
    "Alblasserdam|Capella" "Alblasserdam|Caph" "Alblasserdam|Celaeno"
    "Alblasserdam|Chara" "Alblasserdam|Comae" "Alblasserdam|Crater"
    "Alblasserdam|Cygnus" "Alblasserdam|Dalim" "Alblasserdam|Diphda"
    "Alblasserdam|Edasich" "Alblasserdam|Elnath" "Alblasserdam|Eltanin"
    "Alblasserdam|Garnet" "Alblasserdam|Gianfar" "Alblasserdam|Gienah"
    "Alblasserdam|Hassaleh" "Alblasserdam|Horologium" "Alblasserdam|Hyadum"
    "Alblasserdam|Hydrus" "Alblasserdam|Jabbah" "Alblasserdam|Kajam"
    "Alblasserdam|Kocab" "Alblasserdam|Larawag" "Alblasserdam|Luhman"
    "Alblasserdam|Maasym" "Alblasserdam|Matar" "Alblasserdam|Melnick"
    "Alblasserdam|Menkent" "Alblasserdam|Merga" "Alblasserdam|Mirach"
    "Alblasserdam|Miram" "Alblasserdam|Muhlifain" "Alblasserdam|Muscida"
    "Alblasserdam|Musica" "Alblasserdam|Nash" "Alblasserdam|Orion"
    "Alblasserdam|Phaet" "Alblasserdam|Piautos" "Alblasserdam|Piscium"
    "Alblasserdam|Pleione" "Alblasserdam|Pyxis" "Alblasserdam|Rukbat"
    "Alblasserdam|Sadr" "Alblasserdam|Salm" "Alblasserdam|Scuti"
    "Alblasserdam|Sheliak" "Alblasserdam|Situla" "Alblasserdam|Subra"
    "Alblasserdam|Suhail" "Alblasserdam|Talitha" "Alblasserdam|Tarazed"
    "Alblasserdam|Tiaki" "Alblasserdam|Tianyi" "Alblasserdam|Zibal"
)

# Tier 3: Northern/Eastern Europe — 46 servers
TIER3=(
    "Stockholm|Copernicus" "Stockholm|Lupus" "Stockholm|Norma" "Stockholm|Segin"
    "Oslo|Camelopardalis" "Oslo|Cepheus" "Oslo|Fomalhaut" "Oslo|Gemini" "Oslo|Ophiuchus"
    "Uppsala|Albali" "Uppsala|Algorab" "Uppsala|Alrami" "Uppsala|Alula"
    "Uppsala|Atria" "Uppsala|Azmidiske" "Uppsala|Benetnasch" "Uppsala|Menkab" "Uppsala|Muphrid"
    "Prague|Centaurus" "Prague|Markab" "Prague|Turais"
    "Vienna|Alderamin" "Vienna|Beemim" "Vienna|Caelum"
    "Berlin|Cujam" "Berlin|Taiyi"
    "Zurich|Achernar" "Zurich|Achird" "Zurich|Athebyne" "Zurich|Baiten"
    "Zurich|Dorado" "Zurich|Hamal" "Zurich|Sirrah" "Zurich|Toliman"
    "Riga|Felis" "Riga|Meissa" "Riga|Phact" "Riga|Schedir"
    "Tallinn|Alruba"
    "Sofia|Apus" "Sofia|Grus"
    "Belgrade|Alnitak" "Belgrade|Marsic"
    "Bucharest|Alamak" "Bucharest|Canes" "Bucharest|Nembus"
)

# Tier 4: Rest of world (highest latency, last resort) — 21 servers
TIER4=(
    "Sao Paulo|Fulu"
    "Tokyo|Ainalrami" "Tokyo|Albaldah" "Tokyo|Bharani" "Tokyo|Biham"
    "Tokyo|Fleed" "Tokyo|Iskandar" "Tokyo|Okab" "Tokyo|Taphao"
    "Singapore|Auriga" "Singapore|Azelfafage" "Singapore|Circinus"
    "Singapore|Delphinus" "Singapore|Hydra" "Singapore|Luyten" "Singapore|Triangulum"
    "Taipei|Sulafat"
    "Auckland|Fawaris" "Auckland|Mothallah" "Auckland|Theemin" "Auckland|Tianguan"
)

# ── Helpers ────────────────────────────────────────────────

# Discover running gluetun proxy containers (gluetun-1 through gluetun-N).
# Excludes the bare "gluetun" container which is not part of the proxy pool.
discover_containers() {
    docker ps --format '{{.Names}}' --filter "name=${PATTERN}" | grep -E 'gluetun-[0-9]+' | sort
}

# Get the control API URL for a container. Uses the container's IP on the
# Docker network so we don't need host port mappings.
control_url() {
    local name="$1"
    local ip
    ip=$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$name" 2>/dev/null || true)
    if [[ -z "$ip" ]]; then
        echo ""
        return
    fi
    echo "http://${ip}:${CONTROL_PORT}"
}

# Quiet curl with timeout, returns body. Exits non-zero on failure.
api_get() {
    curl -sf --max-time 5 "$1" 2>/dev/null
}

# PUT JSON to a control API endpoint. Returns body on success, prints error on failure.
api_put() {
    local http_code body
    body=$(curl -s --max-time 15 -X PUT -H "Content-Type: application/json" \
        -w '\n%{http_code}' -d "$2" "$1" 2>/dev/null) || { echo "connection failed"; return 1; }
    http_code=$(echo "$body" | tail -1)
    body=$(echo "$body" | sed '$d')
    if [[ "$http_code" -ge 200 && "$http_code" -lt 300 ]]; then
        echo "$body"
        return 0
    else
        echo "HTTP ${http_code}: ${body}" >&2
        return 1
    fi
}

print_header() {
    echo -e "\n${BOLD}${CYAN}$1${NC}"
    echo "────────────────────────────────────────"
}

# Extract just the IP address from gluetun's /v1/publicip/ip JSON response.
# Returns empty string if unavailable.
extract_public_ip() {
    local raw="$1"
    # Response is JSON like {"public_ip":"1.2.3.4",...} — extract the IP
    echo "$raw" | grep -oP '"public_ip"\s*:\s*"[^"]*"' | grep -oP '"[^"]*"$' | tr -d '"' || echo ""
}

# Get the current city for a gluetun container.
# Uses docker inspect (always reliable) as primary, control API as fallback.
get_current_city() {
    local name="$1"
    local url="${2:-}"  # control URL, may be empty

    # Primary: docker inspect — always works, even if VPN is crashed/looping
    local env_city
    env_city=$(docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$name" 2>/dev/null \
        | grep '^SERVER_CITIES=' | head -1 | cut -d= -f2- || echo "")
    if [[ -n "$env_city" ]]; then
        echo "$env_city"
        return
    fi

    # Fallback: control API (for containers not started with SERVER_CITIES env)
    if [[ -n "$url" ]]; then
        local settings
        settings=$(api_get "${url}/v1/vpn/settings" || echo "")
        local city
        city=$(echo "$settings" | grep -oP '"cities"\s*:\s*\[\s*"\K[^"]+' || echo "")
        echo "$city"
        return
    fi

    echo ""
}

# Get the current server name for a gluetun container.
# Uses docker inspect (always reliable) as primary, control API as fallback.
get_current_server() {
    local name="$1"
    local url="${2:-}"

    # Primary: docker inspect
    local env_server
    env_server=$(docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$name" 2>/dev/null \
        | grep '^SERVER_NAMES=' | head -1 | cut -d= -f2- || echo "")
    if [[ -n "$env_server" ]]; then
        echo "$env_server"
        return
    fi

    # Fallback: control API
    if [[ -n "$url" ]]; then
        local settings
        settings=$(api_get "${url}/v1/vpn/settings" || echo "")
        local server
        server=$(echo "$settings" | grep -oP '"names"\s*:\s*\[\s*"\K[^"]+' || echo "")
        echo "$server"
        return
    fi

    echo ""
}

# ── Commands ───────────────────────────────────────────────

cmd_status() {
    local containers
    containers=$(discover_containers)
    if [[ -z "$containers" ]]; then
        echo -e "${YELLOW}No gluetun containers found matching '${PATTERN}'.${NC}"
        exit 0
    fi

    print_header "Gluetun VPN Status"
    printf "%-14s %-12s %-18s %s\n" "CONTAINER" "DOCKER" "VPN" "DETAILS"
    echo "────────────────────────────────────────────────────────────────"

    while IFS= read -r name; do
        local docker_status vpn_status details color
        docker_status=$(docker inspect -f '{{.State.Status}}' "$name" 2>/dev/null || echo "unknown")

        local url
        url=$(control_url "$name")
        if [[ -z "$url" ]]; then
            vpn_status="unreachable"
            details="no IP"
            color="$RED"
        else
            local raw
            raw=$(api_get "${url}/v1/vpn/status" || echo "")
            if [[ -z "$raw" ]]; then
                vpn_status="unreachable"
                details="control API down"
                color="$RED"
            elif echo "$raw" | grep -q '"running"'; then
                vpn_status="running"
                local city server
                city=$(get_current_city "$name" "$url")
                server=$(get_current_server "$name" "$url")
                if [[ -n "$server" ]]; then
                    details="${server}@${city:-?}"
                else
                    details="city=${city:-?}"
                fi
                color="$GREEN"
            elif echo "$raw" | grep -q '"stopping"'; then
                vpn_status="stopping"
                details="VPN is restarting..."
                color="$YELLOW"
            else
                vpn_status="stopped"
                # Extract status value from JSON if possible
                local extracted
                extracted=$(echo "$raw" | grep -oP '"status"\s*:\s*"[^"]*"' | grep -oP '"[^"]*"$' | tr -d '"' || echo "")
                details="${extracted:-$raw}"
                color="$RED"
            fi
        fi

        printf "${color}%-14s${NC} %-12s ${color}%-18s${NC} %s\n" "$name" "$docker_status" "$vpn_status" "$details"
    done <<< "$containers"
    echo
}

cmd_ip() {
    local targets
    if [[ $# -gt 0 ]]; then
        targets="$1"
    else
        targets=$(discover_containers)
    fi

    if [[ -z "$targets" ]]; then
        echo -e "${YELLOW}No gluetun containers found.${NC}"
        exit 0
    fi

    print_header "Public IPs"
    printf "%-14s %s\n" "CONTAINER" "PUBLIC IP"
    echo "────────────────────────────────────────"

    while IFS= read -r name; do
        local url ip
        url=$(control_url "$name")
        if [[ -z "$url" ]]; then
            ip="${RED}unreachable${NC}"
        else
            ip=$(api_get "${url}/v1/publicip/ip" || echo "")
            local parsed
            parsed=$(extract_public_ip "$ip")
            if [[ -n "$parsed" ]]; then
                ip="${GREEN}${parsed}${NC}"
            elif [[ -n "$ip" ]]; then
                ip="${YELLOW}connected (no public IP yet)${NC}"
            else
                ip="${RED}unavailable${NC}"
            fi
        fi
        printf "%-14s " "$name"
        echo -e "$ip"
    done <<< "$targets"
    echo
}

# Recreate a container from its existing config with updated env vars.
# Extracts image, env, caps, devices, networks, restart policy from docker inspect,
# stops+removes the old container, then runs a new one with the same settings.
# Usage: recreate_with_env <container_name> <ENV_KEY=value> [ENV_KEY2=value2 ...]
recreate_with_env() {
    local name="$1"; shift
    local -a env_overrides=("$@")

    # Extract config from the running/stopped container
    local image network restart_policy
    image=$(docker inspect -f '{{.Config.Image}}' "$name")
    restart_policy=$(docker inspect -f '{{.HostConfig.RestartPolicy.Name}}' "$name")
    network=$(docker inspect -f '{{range $k, $v := .NetworkSettings.Networks}}{{$k}}{{end}}' "$name" | head -1)

    # Collect current env vars as a plain array, skipping any that are being overridden
    local -a override_keys=()
    for override in "${env_overrides[@]}"; do
        override_keys+=("${override%%=*}")
    done

    local -a env_flags=()
    while IFS= read -r line; do
        [[ -z "$line" ]] && continue
        local key="${line%%=*}"
        # Skip if this key is being overridden
        local skip=false
        for okey in "${override_keys[@]}"; do
            if [[ "$key" == "$okey" ]]; then
                skip=true
                break
            fi
        done
        if [[ "$skip" == false ]]; then
            env_flags+=("-e" "$line")
        fi
    done < <(docker inspect -f '{{range .Config.Env}}{{.}}{{"\n"}}{{end}}' "$name")

    # Add overrides
    for override in "${env_overrides[@]}"; do
        env_flags+=("-e" "$override")
    done

    # Extract capabilities
    local -a caps=()
    while IFS= read -r cap; do
        [[ -n "$cap" ]] && caps+=("--cap-add" "$cap")
    done < <(docker inspect -f '{{range .HostConfig.CapAdd}}{{.}}{{"\n"}}{{end}}' "$name")

    # Extract devices
    local -a devices=()
    while IFS= read -r dev; do
        [[ -n "$dev" ]] && devices+=("--device" "$dev")
    done < <(docker inspect -f '{{range .HostConfig.Devices}}{{.PathOnHost}}{{"\n"}}{{end}}' "$name")

    # Stop and remove the old container
    echo "  Stopping ${name}..."
    docker stop -t 15 "$name" >/dev/null 2>&1 || true
    docker rm "$name" >/dev/null 2>&1 || true

    # Run the new container
    echo "  Starting ${name} (image: ${image})..."
    docker run -d \
        --name "$name" \
        --restart "${restart_policy:-unless-stopped}" \
        ${network:+--network "$network"} \
        "${caps[@]}" \
        "${devices[@]}" \
        "${env_flags[@]}" \
        "$image" >/dev/null
}

cmd_city() {
    if [[ $# -lt 2 ]]; then
        echo "Usage: $0 city <instance> <city>"
        echo "Example: $0 city gluetun-1 \"New York City\""
        exit 1
    fi
    local name="$1"
    local city="$2"

    echo -e "Switching ${BOLD}${name}${NC} to ${CYAN}${city}${NC} (any server)..."

    # Check if already on the requested city — skip if so
    local current_city
    current_city=$(get_current_city "$name")
    local current_server
    current_server=$(get_current_server "$name")
    if [[ -n "$current_city" && "$current_city" == "$city" && -z "$current_server" ]]; then
        local url raw_ip ip
        url=$(control_url "$name")
        if [[ -n "$url" ]]; then
            raw_ip=$(api_get "${url}/v1/publicip/ip" || echo "")
            ip=$(extract_public_ip "$raw_ip")
        fi
        echo -e "${GREEN}Already on ${city}${NC} — public IP: ${BOLD}${ip:-unknown}${NC}"
        return
    fi

    # Recreate container with new SERVER_CITIES env var.
    # Clear SERVER_NAMES so gluetun picks any available server in the city.
    recreate_with_env "$name" "SERVER_CITIES=${city}" "SERVER_NAMES="

    # Poll until VPN is live (container needs time to start + connect)
    echo -n "Waiting for VPN to connect"
    local url elapsed=0
    while [[ $elapsed -lt $POLL_TIMEOUT ]]; do
        sleep "$POLL_INTERVAL"
        elapsed=$((elapsed + POLL_INTERVAL))
        echo -n "."
        url=$(control_url "$name")
        if [[ -n "$url" ]]; then
            local status
            status=$(api_get "${url}/v1/vpn/status" || echo "")
            if echo "$status" | grep -q '"running"'; then
                echo ""
                local raw_ip ip
                raw_ip=$(api_get "${url}/v1/publicip/ip" || echo "")
                ip=$(extract_public_ip "$raw_ip")
                echo -e "${GREEN}VPN live on ${city}${NC} — public IP: ${BOLD}${ip:-unknown}${NC}"
                return
            fi
        fi
    done
    echo ""
    echo -e "${RED}VPN did not come up on ${city} within ${POLL_TIMEOUT}s.${NC}"
    echo "Check logs: $0 logs ${name}"
    exit 1
}

cmd_server() {
    if [[ $# -lt 3 ]]; then
        echo "Usage: $0 server <instance> <city> <server_name>"
        echo "Example: $0 server gluetun-1 \"New York City\" Muliphein"
        exit 1
    fi
    local name="$1"
    local city="$2"
    local server="$3"

    echo -e "Switching ${BOLD}${name}${NC} to ${CYAN}${server}@${city}${NC}..."

    # Check if already on the requested server — skip if so
    local current_server
    current_server=$(get_current_server "$name")
    if [[ -n "$current_server" && "$current_server" == "$server" ]]; then
        local url raw_ip ip
        url=$(control_url "$name")
        if [[ -n "$url" ]]; then
            raw_ip=$(api_get "${url}/v1/publicip/ip" || echo "")
            ip=$(extract_public_ip "$raw_ip")
        fi
        echo -e "${GREEN}Already on ${server}@${city}${NC} — public IP: ${BOLD}${ip:-unknown}${NC}"
        return
    fi

    # Recreate container targeting specific city + server name
    recreate_with_env "$name" "SERVER_CITIES=${city}" "SERVER_NAMES=${server}"

    # Poll until VPN is live
    echo -n "Waiting for VPN to connect"
    local url elapsed=0
    while [[ $elapsed -lt $POLL_TIMEOUT ]]; do
        sleep "$POLL_INTERVAL"
        elapsed=$((elapsed + POLL_INTERVAL))
        echo -n "."
        url=$(control_url "$name")
        if [[ -n "$url" ]]; then
            local status
            status=$(api_get "${url}/v1/vpn/status" || echo "")
            if echo "$status" | grep -q '"running"'; then
                echo ""
                local raw_ip ip
                raw_ip=$(api_get "${url}/v1/publicip/ip" || echo "")
                ip=$(extract_public_ip "$raw_ip")
                echo -e "${GREEN}VPN live on ${server}@${city}${NC} — public IP: ${BOLD}${ip:-unknown}${NC}"
                return
            fi
        fi
    done
    echo ""
    echo -e "${RED}VPN did not come up on ${server}@${city} within ${POLL_TIMEOUT}s.${NC}"
    echo "Check logs: $0 logs ${name}"
    exit 1
}

cmd_cycle_all() {
    local containers
    containers=$(discover_containers)
    if [[ -z "$containers" ]]; then
        echo -e "${YELLOW}No gluetun containers found.${NC}"
        exit 0
    fi

    # Count slots for partitioning
    local slot_count
    slot_count=$(echo "$containers" | wc -l)

    # Build partitioned server list for a slot, matching RoundRobinProxyHandler.BuildPrioritizedServerList.
    # Slot i gets indices i, i+slotCount, i+2*slotCount, … from each tier. Zero overlap.
    # Returns newline-separated "City|ServerName" entries.
    build_slot_servers() {
        local slot_idx=$1 slot_cnt=$2
        local -a tiers=("TIER0" "TIER1" "TIER2" "TIER3" "TIER4")
        for tier_name in "${tiers[@]}"; do
            local -n tier_ref="$tier_name"
            local tier_len=${#tier_ref[@]}
            for (( idx=slot_idx; idx<tier_len; idx+=slot_cnt )); do
                echo "${tier_ref[$idx]}"
            done
        done
    }

    print_header "Cycling all gluetun containers (server-level)"

    # Phase 1: Assign servers and recreate all containers (no waiting)
    local -a names=()
    local -a target_cities=()
    local -a target_servers=()
    local -a needs_cycle=()
    local i=0
    while IFS= read -r name; do
        local current_server
        current_server=$(get_current_server "$name")

        # Get this slot's partitioned server list and pick the first one
        # that isn't the current server
        local pick_city="" pick_server=""
        while IFS= read -r candidate; do
            local c_city="${candidate%%|*}"
            local c_server="${candidate##*|}"
            if [[ "$c_server" != "$current_server" ]]; then
                pick_city="$c_city"
                pick_server="$c_server"
                break
            fi
        done < <(build_slot_servers "$i" "$slot_count")
        # Fallback: just use the first server in partition
        if [[ -z "$pick_server" ]]; then
            local first
            first=$(build_slot_servers "$i" "$slot_count" | head -1)
            pick_city="${first%%|*}"
            pick_server="${first##*|}"
        fi

        names+=("$name")
        target_cities+=("$pick_city")
        target_servers+=("$pick_server")

        if [[ "$pick_server" == "$current_server" ]]; then
            echo -e "${BOLD}[${name}]${NC} — already on ${GREEN}${current_server}${NC}, skipping"
            needs_cycle+=(false)
        else
            local current_city
            current_city=$(get_current_city "$name")
            echo -e "${BOLD}[${name}]${NC} ${current_server:-${current_city:-?}} → ${CYAN}${pick_server}@${pick_city}${NC}"
            recreate_with_env "$name" "SERVER_CITIES=${pick_city}" "SERVER_NAMES=${pick_server}"
            needs_cycle+=(true)
        fi
        i=$((i + 1))
    done <<< "$containers"

    # Phase 2: Poll all recreated containers in parallel until all are live
    local total=${#names[@]}
    local -a live=()
    for (( k=0; k<total; k++ )); do
        if [[ "${needs_cycle[$k]}" == false ]]; then
            live+=(true)
        else
            live+=(false)
        fi
    done

    local all_live=false
    echo ""
    echo -n "Waiting for all VPNs to connect"
    local elapsed=0
    while [[ $elapsed -lt $POLL_TIMEOUT ]]; do
        sleep "$POLL_INTERVAL"
        elapsed=$((elapsed + POLL_INTERVAL))
        echo -n "."

        all_live=true
        for (( k=0; k<total; k++ )); do
            [[ "${live[$k]}" == true ]] && continue
            local url
            url=$(control_url "${names[$k]}")
            if [[ -n "$url" ]]; then
                local status
                status=$(api_get "${url}/v1/vpn/status" || echo "")
                if echo "$status" | grep -q '"running"'; then
                    live[$k]=true
                    continue
                fi
            fi
            all_live=false
        done

        if [[ "$all_live" == true ]]; then
            break
        fi
    done
    echo ""

    # Phase 3: Report results
    print_header "Results"
    for (( k=0; k<total; k++ )); do
        local name="${names[$k]}"
        local city="${target_cities[$k]}"
        local server="${target_servers[$k]}"
        if [[ "${live[$k]}" == true ]]; then
            local url raw_ip ip
            url=$(control_url "$name")
            raw_ip=""
            if [[ -n "$url" ]]; then
                raw_ip=$(api_get "${url}/v1/publicip/ip" || echo "")
            fi
            ip=$(extract_public_ip "$raw_ip")
            echo -e "${GREEN}${name}${NC} — ${server}@${city} — IP: ${BOLD}${ip:-unknown}${NC}"
        else
            echo -e "${RED}${name}${NC} — ${server}@${city} — ${RED}did not come up${NC}"
        fi
    done
    echo ""
}

cmd_update_servers() {
    local targets
    if [[ $# -gt 0 ]]; then
        targets="$1"
    else
        targets=$(discover_containers)
    fi

    if [[ -z "$targets" ]]; then
        echo -e "${YELLOW}No gluetun containers found.${NC}"
        exit 0
    fi

    print_header "Updating VPN server lists"
    while IFS= read -r name; do
        local url
        url=$(control_url "$name")
        if [[ -z "$url" ]]; then
            echo -e "${RED}${name}: unreachable${NC}"
            continue
        fi
        echo -n "${name}: "
        local result
        result=$(api_put "${url}/v1/updater/servers" "{}" || echo "")
        if [[ -n "$result" ]]; then
            echo -e "${GREEN}updated${NC} — ${result}"
        else
            echo -e "${YELLOW}sent (check logs for result)${NC}"
        fi
    done <<< "$targets"
    echo
}

cmd_restart() {
    local targets
    if [[ $# -gt 0 ]]; then
        targets="$1"
    else
        targets=$(discover_containers)
    fi

    if [[ -z "$targets" ]]; then
        echo -e "${YELLOW}No gluetun containers found.${NC}"
        exit 0
    fi

    while IFS= read -r name; do
        echo -e "Restarting ${BOLD}${name}${NC}..."
        docker restart "$name"
        echo -e "${GREEN}${name} restarted.${NC}"
    done <<< "$targets"
}

cmd_logs() {
    local name="${1:-}"
    if [[ -z "$name" ]]; then
        # Default to first discovered container
        name=$(discover_containers | head -1)
        if [[ -z "$name" ]]; then
            echo -e "${YELLOW}No gluetun containers found.${NC}"
            exit 0
        fi
    fi
    shift || true
    local follow=""
    for arg in "$@"; do
        if [[ "$arg" == "--follow" || "$arg" == "-f" ]]; then
            follow="--follow"
        fi
    done
    docker logs --tail 100 $follow "$name"
}

cmd_help() {
    cat <<EOF
${BOLD}gluetun-manage.sh${NC} — Manage gluetun VPN proxy containers

${BOLD}USAGE${NC}
  $0 <command> [args...]

${BOLD}COMMANDS${NC}
  status                                Show VPN status of all gluetun containers
  ip [instance]                         Show public IP (all or specific instance)
  city <instance> <city>                Switch instance to any server in a city
  server <instance> <city> <name>       Switch instance to a specific named server
  cycle-all                             Rotate all instances to diverse servers (partitioned)
  update-servers [instance]             Refresh VPN server list (fixes stale IPs)
  restart [instance]                    Docker restart container(s)
  logs [instance] [--follow]            Tail container logs

${BOLD}EXAMPLES${NC}
  $0 status
  $0 city gluetun-1 "New York City"
  $0 server gluetun-1 "New York City" Muliphein
  $0 cycle-all
  $0 update-servers gluetun-1
  $0 ip
  $0 logs gluetun-2 --follow

${BOLD}ENVIRONMENT${NC}
  GLUETUN_CONTROL_PORT   Control API port (default: 8000)
  GLUETUN_PATTERN        Container name glob (default: gluetun-*)

${BOLD}QUICK FIX — VPN crash-loop${NC}
  If gluetun is crash-looping on a dead server:
    1. $0 update-servers            # Refresh server IPs
    2. $0 server gluetun-1 "Los Angeles" Maia   # Switch to a specific server
    3. $0 status                    # Verify it's running
EOF
}

# ── Main dispatch ──────────────────────────────────────────
case "${1:-help}" in
    status)         shift; cmd_status "$@" ;;
    ip)             shift; cmd_ip "$@" ;;
    city)           shift; cmd_city "$@" ;;
    server)         shift; cmd_server "$@" ;;
    cycle-all)      shift; cmd_cycle_all "$@" ;;
    update-servers) shift; cmd_update_servers "$@" ;;
    restart)        shift; cmd_restart "$@" ;;
    logs)           shift; cmd_logs "$@" ;;
    help|--help|-h) cmd_help ;;
    *)
        echo -e "${RED}Unknown command: $1${NC}"
        echo "Run '$0 help' for usage."
        exit 1
        ;;
esac
