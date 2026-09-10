#!/usr/bin/env bash
# Entry point for the Home Assistant add-on. Everything here is bootstrap: which Home Assistant to talk to
# and how to log. Every other setting lives on the add-on's own settings page, which survives restarts.
set -euo pipefail

OPTIONS_FILE=/data/options.json

option() {
    [ -f "${OPTIONS_FILE}" ] || return 0
    jq -r --arg key "$1" '.[$key] // empty' "${OPTIONS_FILE}"
}

# /data is the add-on's persistent volume: database, saved settings and saved secrets all live there.
export HEARTHSENSE_DATA_DIR=/data
export HEARTHSENSE__Storage__Path=/data/hearthsense.db

# Ingress reaches the container on this port. Only the Supervisor can, and it has already checked who you are.
export HEARTHSENSE__Api__BindAddress=0.0.0.0
export HEARTHSENSE__Api__Port=5080
export HEARTHSENSE__Api__IngressAddress=172.30.32.2

token="$(option home_assistant_token)"

if [ -n "${token}" ]; then
    # A long-lived token was supplied, so talk to Home Assistant directly.
    export HEARTHSENSE_HA_TOKEN="${token}"
    export HEARTHSENSE__HomeAssistant__BaseUrl="${HEARTHSENSE__HomeAssistant__BaseUrl:-http://homeassistant:8123}"
elif [ -n "${SUPERVISOR_TOKEN:-}" ]; then
    # Nothing supplied: borrow the Supervisor's credentials through its Core proxy.
    export HEARTHSENSE_HA_TOKEN="${SUPERVISOR_TOKEN}"
    export HEARTHSENSE__HomeAssistant__BaseUrl="${HEARTHSENSE__HomeAssistant__BaseUrl:-http://supervisor/core}"
fi

case "$(option log_level)" in
    trace)   level=Trace ;;
    debug)   level=Debug ;;
    warning) level=Warning ;;
    error)   level=Error ;;
    *)       level=Information ;;
esac
export Logging__LogLevel__Default="${level}"

exec dotnet /app/HearthSense.Api.dll
