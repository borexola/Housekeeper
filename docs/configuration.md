# Configuration

Everything is configurable from the settings page: **Settings** in the add-on's sidebar panel, or
<http://127.0.0.1:5080/settings> under Docker. Nothing has to be set through files or environment variables,
and most changes take effect straight away.

Running as a Home Assistant add-on, the only things set outside that page are the two add-on options, which
have to exist before the app starts: an optional Home Assistant token, and the log level. Everything on this
page is reachable from the settings screen instead.

The page shows, for every setting, the value in force, where that value came from, and what it would fall
back to if you reset it. Bad values are refused with the reason instead of being written and breaking the
next scan.

## Where settings come from

Four sources, each beating the ones above it:

1. `appsettings.json` shipped with the app
2. an optional JSON file at `HEARTHSENSE_CONFIG_FILE`
3. environment variables, `HEARTHSENSE__Section__Key`
4. **the settings page**, stored in `settings.json` in the data directory

The settings page wins on purpose: a value you type into the UI is a value you expect to apply. Each field
is badged so you can see which of these it came from:

| Badge | Meaning |
|---|---|
| `set here` | Stored by the settings page. A **Reset** link puts it back. |
| `from environment` | Coming from an environment variable or the config file. |
| `default` | Nobody has set it; the built-in default applies. |

Only the values you actually change are stored. Everything else keeps inheriting, so setting a variable in
your Compose file still works for every field you have not touched in the UI. Saving a value that happens to
match what it would inherit anyway stores nothing.

## The data directory

`settings.json` and `secrets.json` live in the data directory, which is `HEARTHSENSE_DATA_DIR` if set, and
otherwise the folder holding the database. It is `/data` in the container.

This one is deliberately not settable from the UI: it is where the answer to "where are the settings?" has
to be knowable before any settings have been read.

## Secrets

| Secret | Needed for | Environment variable |
|---|---|---|
| Home Assistant token | Everything. Must come from an **admin** user. | `HEARTHSENSE_HA_TOKEN` |
| API token | Required when not bound to loopback. | `HEARTHSENSE_API_TOKEN` |
| Model API key | OpenAI-compatible endpoints. Ollama does not use one. | `HEARTHSENSE_LLM_API_KEY` |

Set them on the settings page, or leave them in the environment. A secret typed into the UI is written to
`secrets.json` and takes precedence over the environment, so a correction made in the UI actually applies.
Secrets are never returned by any endpoint; the page shows only whether one is set and where it came from.
Each environment variable also accepts a `_FILE` variant pointing at a file, for Docker secrets. See
[SECURITY.md](../SECURITY.md) for what storing them costs.

## What needs a restart

Almost nothing. The bind address, the port, and the database path are marked `restart` on the page, and
saving one tells you a restart is needed. Everything else — including the Home Assistant address, the model,
every timeout, the scan interval and the watch list — applies to the next request or the next scan.

## Settings

Each is `HEARTHSENSE__Section__Key` as an environment variable, or nested under `HearthSense` in JSON.

### Home Assistant

| Key | Default | |
|---|---|---|
| `BaseUrl` | `http://homeassistant.local:8123` | |
| `RequestTimeout` | `30s` | |
| `ResolveAreas` | `true` | One extra templated call per entity fetch to resolve areas. Improves drafting; turn off if you do not use areas. |
| `TokenEnvironmentVariable` | `HEARTHSENSE_HA_TOKEN` | Which variable holds the token, when it is not stored in the UI. |

### Model (`Llm`)

| Key | Default | |
|---|---|---|
| `Provider` | `Ollama` | `Ollama`, or `OpenAI` for any OpenAI-compatible chat-completions endpoint. |
| `Endpoint` | `http://localhost:11434` | |
| `Model` | `qwen2.5:7b` | Pick something that follows JSON instructions well. |
| `Timeout` | `90s` | A 7B model on CPU is slow; this is generous on purpose. |
| `Temperature` | `0.1` | |
| `MaxOutputTokens` | `1200` | |
| `MaxCandidateEntities` | `40` | Entities shortlisted into the prompt. Raising it costs tokens and dilutes attention. |
| `ApiKeyEnvironmentVariable` | `HEARTHSENSE_LLM_API_KEY` | |

**Test connection** on the settings page asks the endpoint which models it has, so it can tell "server is
down" apart from "server is up but that model was never pulled". For Ollama: `ollama pull qwen2.5:7b`.

### Watching (`Scan`)

| Key | Default | |
|---|---|---|
| `Enabled` | `true` | Can be turned on and off without a restart. |
| `Interval` | `5m` | One `/api/states` call per tick. |
| `History` | `14d` | How much state history to keep and analyse. |
| `Include` | *(empty)* | Entity id globs, e.g. `binary_sensor.*`. **Empty means nothing is observed.** |
| `Exclude` | *(empty)* | Applied after `Include`. |
| `IncludeAll` | `false` | Observe everything instead of a list. |
| `MaxTrackedEntities` | `500` | Hard cap. |
| `MinimumStuckDuration` | `10m` | A stuck state is never reported below this. |
| `StuckMultiplier` | `3.0` | How many times its historical worst case before reporting. |
| `MinimumUnavailableDuration` | `30m` | |
| `OutlierThreshold` | `4.0` | Robust z-score for numeric readings. |
| `MinimumSamples` | `12` | Below this a detector stays quiet. |
| `RedetectAfter` | `7d` | How long a dismissal keeps a finding quiet. |

Scanning observes nothing until you set `Include` or `IncludeAll`. That is deliberate, and the settings page
warns when scanning is on but nothing is selected. A good starting point is `binary_sensor.*` plus
`sensor.*_temperature`.

Detectors need history before they say anything: a new install is quiet for a day or two while samples
accumulate. That is working correctly, not a fault.

### Service (`Api`) and `Storage`

| Key | Default | |
|---|---|---|
| `Api.BindAddress` | `127.0.0.1` | **restart** · Anything but loopback requires an API token. |
| `Api.Port` | `5080` | **restart** |
| `Api.TokenEnvironmentVariable` | `HEARTHSENSE_API_TOKEN` | |
| `Api.IngressAddress` | *(empty)* | One IP allowed in without a token because something in front already authenticated the caller. The add-on sets it to the Supervisor, `172.30.32.2`. Leave empty otherwise. |
| `Storage.Path` | `hearthsense.db` | **restart** · `/data/hearthsense.db` in the container. |

The settings page refuses to bind to a network address unless an API token is set, and refuses to clear the
API token while it is already bound to one. Locking yourself out of your own automation writer should take
more than one careless save. A trusted ingress address counts as a way in, which is why the add-on can run
with no token at all.

## Durations

Anywhere a duration is asked for, type `45s`, `10m`, `2h`, `14d` or `1.5h`. The `hh:mm:ss` and `d.hh:mm:ss`
forms still parse, so an existing config file keeps working.

## Example file

A full file is at [`examples/appsettings.sample.json`](../examples/appsettings.sample.json). Copy it to
`deploy/config/hearthsense.json` for the Compose stack, which mounts it read-only. Anything you then change
on the settings page overrides it.
