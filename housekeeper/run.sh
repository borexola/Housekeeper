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
export HOUSEKEEPER_DATA_DIR=/data
export HOUSEKEEPER__Storage__Path=/data/housekeeper.db

# Ingress reaches the container on this port. Only the Supervisor can, and it has already checked who you are.
export HOUSEKEEPER__Api__BindAddress=0.0.0.0
export HOUSEKEEPER__Api__Port=5080
export HOUSEKEEPER__Api__IngressAddress=172.30.32.2

# Tells the application it is running as an add-on, where the three settings above are a contract with the
# Supervisor rather than preferences. Without this the settings page could change the listen address, the
# Supervisor would no longer find the port declared in config.yaml, and the page needed to undo it would be
# unreachable -- with no way in to fix it short of deleting /data/settings.json from a terminal add-on.
export HOUSEKEEPER_MANAGED=addon

token="$(option home_assistant_token)"

if [ -n "${token}" ]; then
    # A long-lived token was supplied, so talk to Home Assistant directly.
    export HOUSEKEEPER_HA_TOKEN="${token}"
    export HOUSEKEEPER__HomeAssistant__BaseUrl="${HOUSEKEEPER__HomeAssistant__BaseUrl:-http://homeassistant:8123}"
elif [ -n "${SUPERVISOR_TOKEN:-}" ]; then
    # Nothing supplied: borrow the Supervisor's credentials through its Core proxy.
    export HOUSEKEEPER_HA_TOKEN="${SUPERVISOR_TOKEN}"
    export HOUSEKEEPER__HomeAssistant__BaseUrl="${HOUSEKEEPER__HomeAssistant__BaseUrl:-http://supervisor/core}"
fi

case "$(option log_level)" in
    trace)   level=Trace ;;
    debug)   level=Debug ;;
    warning) level=Warning ;;
    error)   level=Error ;;
    *)       level=Information ;;
esac
export Logging__LogLevel__Default="${level}"

exec dotnet /app/Housekeeper.Api.dll
