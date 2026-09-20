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
2. an optional JSON file at `HOUSEKEEPER_CONFIG_FILE`
3. environment variables, `HOUSEKEEPER__Section__Key`
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

`settings.json` and `secrets.json` live in the data directory, which is `HOUSEKEEPER_DATA_DIR` if set, and
otherwise the folder holding the database. It is `/data` in the container.

This one is deliberately not settable from the UI: it is where the answer to "where are the settings?" has
to be knowable before any settings have been read.

## Secrets

| Secret | Needed for | Environment variable |
|---|---|---|
| Home Assistant token | Everything. Must come from an **admin** user. | `HOUSEKEEPER_HA_TOKEN` |
| API token | Required when not bound to loopback. | `HOUSEKEEPER_API_TOKEN` |
| Model API key | OpenAI-compatible endpoints. Ollama does not use one. | `HOUSEKEEPER_LLM_API_KEY` |

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

Each is `HOUSEKEEPER__Section__Key` as an environment variable, or nested under `Housekeeper` in JSON.

### Home Assistant

| Key | Default | |
|---|---|---|
| `BaseUrl` | `http://homeassistant.local:8123` | |
| `RequestTimeout` | `30s` | |
| `CertificateFingerprint` | *(empty)* | Only for an https Home Assistant using a certificate it signed itself. Paste the certificate's SHA-256 fingerprint; that exact certificate is then trusted and anything else is still refused. Punctuation and case do not matter, and the whole `sha256 Fingerprint=…` line from `openssl` can be pasted as-is. |
| `AcceptAnyCertificate` | `false` | Turns certificate checking off entirely. Housekeeper would then send your Home Assistant admin token to whatever answers at that address. Prefer the fingerprint. |
| `ResolveAreas` | `true` | One extra templated call per entity fetch to resolve areas. Improves drafting; turn off if you do not use areas. |
| `TokenEnvironmentVariable` | `HOUSEKEEPER_HA_TOKEN` | Which variable holds the token, when it is not stored in the UI. |

### Model (`Llm`)

| Key | Default | |
|---|---|---|
| `Provider` | `Ollama` | `Ollama`, or `OpenAI` for any OpenAI-compatible chat-completions endpoint. |
| `Endpoint` | `http://localhost:11434` | For an OpenAI-style server, `http://host:8080/v1` — with or without the `/v1`, both work. |
| `Model` | *(empty)* | Empty means whatever the server has loaded, which suits LM Studio, llama.cpp and similar. Ollama needs a name. |
| `OnlyWhenAsked` | `false` | Contact the model only when you pressed something. See below. |
| `Timeout` | `90s` | A 7B model on CPU is slow; this is generous on purpose. |
| `Temperature` | `0.1` | |
| `MaxOutputTokens` | `1200` | |
| `MaxCandidateEntities` | `40` | Entities shortlisted into the prompt. Raising it costs tokens and dilutes attention. |
| `MaxAttempts` | `3` | After a rejected draft the model is told exactly what the validator objected to and asked again. Small models usually get it on the second or third try. |
| `ContextTokens` | `8192` | Ollama only. How much context to ask for. Many community builds declare 2048 in their Modelfile, which is smaller than the instructions plus your entity list — and Ollama drops whatever does not fit rather than saying so, which makes the model look far worse than it is. Ignored by OpenAI-compatible servers. |
| `ApiKeyEnvironmentVariable` | `HOUSEKEEPER_LLM_API_KEY` | |

**Test connection** on the settings page asks the endpoint which models it has, so it can tell "server is
down" apart from "server is up but that model was never pulled", and lists what is there if you have not
chosen one. It tests the values on the page, saved or not, so you can try an address before keeping it.
For Ollama: `ollama pull qwen2.5:7b`.

A small model does not have to be right first time. Every draft is checked against your real entities and
your real services, and a rejected one goes back to the model with the exact sentence it was rejected with.
That is worth more than a larger model: raising `MaxAttempts` costs time, not accuracy, and the validator
never lets a wrong answer through regardless of how many attempts it took.

#### When Housekeeper talks to your model

Two things do, and one of them is you pressing a button:

- **Drafting**, when you ask for an automation, press *Make an automation* on a finding, or refine a draft.
  Up to `MaxAttempts` calls, fewer if the model repeats itself or says it cannot be done.
- **Reading a concern.** The model is asked when you add one. If it was down, or answered with something
  unusable, the scan's tick tries again — at most one concern per tick, least recently tried first, with the
  gap doubling per failure to a ceiling of an hour, and it gives up after three unusable answers. A concern
  the model has read is never re-read, so once they have all been read this is nothing at all.

Nothing else does. The scan itself never calls the model, nor does the dashboard's polling or
`GET /api/status`; *Test connection* asks which models the server has without running one.

`OnlyWhenAsked` turns off that second bullet's retry, and only that — drafting, refining and the
*Read again* button on a concern all work exactly as before. It trades a guarantee for some arithmetic: the
background traffic was already small and self-extinguishing, but "small" is a measurement and this is a
promise. A concern left unread while it is on says so on its card, and says to press *Read again*, rather
than promising a retry that is not coming.

### Watching (`Scan`)

| Key | Default | |
|---|---|---|
| `Enabled` | `true` | Can be turned on and off without a restart. |
| `Interval` | `5m` | One `/api/states` call per tick. |
| `History` | `28d` | How much state history to keep and analyse. Four weeks, so that past three weeks a Saturday is judged against other weekend days rather than the working week. |
| `BackfillFromRecorder` | `true` | While an entity's stored history reaches back less than three weeks, read what Home Assistant's recorder already holds for it (usually about ten days), a few hundred entities per scan and once per process, so the detectors judge from the first scan. Readings are thinned on the way in. |
| `IncludeAll` | `true` | Observe every entity. On by default so a new install notices things without being configured first. |
| `Include` | *(empty)* | Entity id globs, e.g. `binary_sensor.*`. Only consulted when `IncludeAll` is off, and then an empty list observes nothing. |
| `Exclude` | *(empty)* | Applied last, so it narrows `IncludeAll` as well as `Include`. The dashboard's Ignore buttons add entity ids here. |
| `MaxTrackedEntities` | `500` | Hard cap, applied after the globs. When it binds, the sun and the things a routine is made of (lights, switches, covers, locks, fans, media players, motion and door sensors, people) are kept first, then readings, then Home Assistant's own machinery, and by id within each — raise it or narrow with `Exclude`. `sun.sun` is watched whenever Home Assistant reports it, whatever `Include` says, unless it is excluded by name. |
| `MinimumStuckDuration` | `10m` | A stuck state is never reported below this. |
| `StuckMultiplier` | `3.0` | How many times its historical worst case before reporting. |
| `MinimumUnavailableDuration` | `30m` | |
| `OutlierThreshold` | `4.0` | Robust z-score for numeric readings. |
| `MinimumSamples` | `12` | Below this a detector stays quiet. |
| `MinimumNumericSamples` | `30` | Readings a numeric baseline needs before it is one. |
| `MinimumBaselineSpan` | `6h` | How far back a numeric baseline has to reach before a reading is judged against it. |
| `MinimumEffect` | `0.15` | The share of its own value a reading must move before the move is worth mentioning; per-device-class floors in real units apply as well. |
| `MinimumExcursion` | `10m` | How long a reading has to stay out of its range before it is reported. A kettle or a sensor glitch is a spike gone by the next scan. A sensor that reports rarely counts from its last change. Zero reports on the first reading. |
| `GroupByDevice` | `true` | One card per device rather than one per entity when several readings on it move together. |
| `RealtimeUpdates` | `true` | Keep a WebSocket open so non-numeric changes are recorded as they happen. |
| `RedetectAfter` | `7d` | How long a dismissal keeps a finding quiet. Each dismissal also raises the bar the same finding has to clear to come back (half a doubling per dismissal), and the third silences it for good. Routines put away stay away from the first; neither kind of "no" is ever pruned. |
| `LearnHabits` | `true` | Once an hour, look through the stored history for routines — things you do by hand at about the same time or right after the same event — and offer to automate them. |
| `HabitMinimumTimes` | `5` | How many times a routine has to have happened, on at least three different days, before it is offered. Doubled for a cue in another room. |
| `HabitConfidence` | `0.6` | How reliably a routine has to hold: the share of the times the cue happened, with the thing not already done, that you did it within a few minutes. At least 0.8 for a cue in another room. |

Out of the box everything is watched, so there is nothing to configure before Housekeeper starts noticing
things. Narrow it with **Ignore** rather than by listing what you want — that is what the dashboard's Ignore
buttons write to, and it keeps working as you add devices. If you would rather name what to watch, turn
**Watch everything** off and fill in **Watch**; the settings page warns if you leave both empty.

On a large install the `MaxTrackedEntities` cap matters. When it binds, the first entities in priority order are watched — the sun, then what a routine is made of (lights, switches, covers, locks, fans, media players, motion and door sensors, people), then readings, with Home Assistant's own machinery (automations, scripts, buttons, helpers) dropped first, and by id within each tier. Watching
everything on a house with more entities than the cap silently watches the alphabetically-first ones. The
dashboard's Watching tile shows the number actually observed, so compare it against what Home Assistant
reports and raise the cap or add Ignore globs if it is short.

Detectors need history before they say anything: a new install is quiet for a day or two while samples
accumulate. That is working correctly, not a fault.

### Service (`Api`) and `Storage`

| Key | Default | |
|---|---|---|
| `Api.BindAddress` | `127.0.0.1` | **restart** · Anything but loopback requires an API token. |
| `Api.Port` | `5080` | **restart** |
| `Api.TokenEnvironmentVariable` | `HOUSEKEEPER_API_TOKEN` | |
| `Api.IngressAddress` | *(empty)* | One IP allowed in without a token because something in front already authenticated the caller. The add-on sets it to the Supervisor, `172.30.32.2`. Leave empty otherwise. |
| `Storage.Path` | `housekeeper.db` | **restart** · `/data/housekeeper.db` in the container. |
| `Storage.KeepDecidedFor` | `30d` | How long rejected, failed, superseded and removed proposals are kept, and how long dismissed and resolved findings are kept — the latter measured from the longer of this and `Scan.RedetectAfter`, so a dismissal is never forgotten while it is still keeping something quiet. Drafts, live automations, routines you put away and findings dismissed three times are never pruned. |

The settings page refuses to bind to a network address unless an API token is set, and refuses to clear the
API token while it is already bound to one. Locking yourself out of your own automation writer should take
more than one careless save. A trusted ingress address counts as a way in, which is why the add-on can run
with no token at all.

## Durations

Anywhere a duration is asked for, type `45s`, `10m`, `2h`, `14d` or `1.5h`. The `hh:mm:ss` and `d.hh:mm:ss`
forms still parse, so an existing config file keeps working.

## Example file

A full file is at [`examples/appsettings.sample.json`](../examples/appsettings.sample.json). Copy it to
`deploy/config/housekeeper.json` for the Compose stack, which mounts it read-only. Anything you then change
on the settings page overrides it.
