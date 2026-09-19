# Housekeeper for Home Assistant

[![CI](https://github.com/borexola/Housekeeper/actions/workflows/ci.yml/badge.svg)](https://github.com/borexola/Housekeeper/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/borexola/Housekeeper?include_prereleases&sort=semver)](https://github.com/borexola/Housekeeper/releases)
[![Docker image](https://img.shields.io/badge/ghcr.io-borexola%2Fhousekeeper-2496ED?logo=docker&logoColor=white)](https://github.com/borexola/Housekeeper/pkgs/container/housekeeper)
[![License: MIT](https://img.shields.io/github/license/borexola/Housekeeper)](LICENSE)

[![Add repository to my Home Assistant](https://my.home-assistant.io/badges/supervisor_add_addon_repository.svg)](https://my.home-assistant.io/redirect/supervisor_add_addon_repository/?repository_url=https%3A%2F%2Fgithub.com%2Fborexola%2FHousekeeper)

**Ask for an automation, or let it tell you when something is off. Nothing changes until you confirm.**

You type *"turn off the lights when no one is home"*. Housekeeper finds the entities that request is
actually about, has a local LLM draft a real Home Assistant automation from them, checks it against
what you already have, and reads it back to you in plain words — *when the hall motion turns on, only if it
is after sunset, then turn on the hall light at 60% brightness* — with the YAML alongside. Nothing reaches
your house until you press confirm.

It also watches your entities in the background and tells you when something looks off — *"the freezer
door has been open for 14 minutes; it's normally open for under a minute"* — which you can dismiss, or
turn into an automation with one click.

> Housekeeper is not a life-safety system. Don't rely on it in place of smoke, CO, or leak alarms.

## Why it is shaped this way

Turning English into YAML is the easy part, and several projects already do it. The hard parts are the
ones that decide whether you trust it:

- **A model will invent `light.kitchen_ceiling` on a house that has no such entity.** Every entity id
  in a draft is checked against your real entities, and every service call against the services your Home
  Assistant actually has, before you are ever shown a confirm button.
- **A small model does not have to be right first time.** A rejected draft goes straight back to it with the
  exact reason, up to three times. Being told "light.kitchen_ceiling does not exist here, you have
  light.kitchen" is worth more than a bigger model, and the validator is what decides either way.
- **`device_id`, `area_id`, `floor_id` and `label_id` cannot be verified**, and a wrong one silently targets
  the wrong hardware — a whole floor of it, in two of those cases. Drafts must target `entity_id`. The prompt
  says so and [the validator enforces it](src/Housekeeper.Core/AutomationDrafting.cs).
- **Only the keys that were validated get written.** The config sent to Home Assistant is rebuilt from
  scratch, so anything extra the model added is dropped rather than passed through.
- **You probably already automated this.** Before confirming, Housekeeper compares the draft against
  your existing automations by shared entities, trigger kind, and name, and warns you.
- **Exactly one call writes to your home** — `POST /api/proposals/{id}/confirm`. Everything else reads.

## How it works

```
"turn off lights when no one is home"
        │
        ├─ 1. shortlist    rank your entities against the request, take the best ~40
        ├─ 2. draft        local LLM returns one JSON automation, from that shortlist and your real services
        ├─ 3. validate     every entity and service must exist · no device/area targets · known keys only
        │                  ↑ rejected? hand back the reason and ask again, up to 3 times
        ├─ 4. deduplicate  compare against existing automations, warn on overlap
        ├─ 5. read back    the validated config said in words: when · only if · then, using your entity names
        └─ 6. confirm      you approve it → POST to Home Assistant → live
```

Not sure what to ask for? The composer offers a handful of example requests written from your own
entities — your hall light, your hall motion sensor, your back door — so the first draft is one that fits.

Separately, every few minutes (and, the first time it sees an entity, starting from what Home Assistant's
own recorder already holds, so it is not blind for its first day):

```
poll /api/states → store state changes → three detectors → a list you triage
                                          ├─ stuck state   (held far longer than it ever has)
                                          ├─ numeric outlier (robust MAD z-score, and it has to stay out)
                                          └─ unavailable   (a reliable sensor gone quiet)
                                    └──→ once an hour, routines → things you could automate
```

It also learns how you use the house. Once an hour it reads weeks of your own switching and looks for two
shapes: *the pantry light goes on within a minute of the pantry motion sensor, nearly every time after
dark*, and *the porch light goes off at about ten past eleven, most nights*. Each one is offered in your
own words with the numbers behind it — *seen 11 times over 6 days, 92% of the time* — and a one-click
draft, and whatever an automation already does is left out. The bar is deliberately high: a cue in another
room needs twice the evidence, only the strongest cue is offered for any one thing, a same-time routine has
to beat what random switching would produce, anything done with a machine's punctuality is taken to be an
automation you have not told it about, and a routine you put away is never suggested again. Findings learn
too: dismiss one and it has to be further over the line to come back; dismiss it three times and it stays
quiet for good.

You can also say what you worry about, in your own words: *"the freezer warming up"*, *"the garage door left
open at night"*, *"the kids' room getting too cold"*. The model reads each concern into the entities it is
about and, where the words imply a line, into a rule — above -15 °C for ten minutes, open for half an hour.
Those entities are judged more strictly, their findings come first, and the rule itself is checked every
scan and reported in your words when it fires. Without a model, a concern is matched by name and still
sharpens the detectors.

A finding never notifies anyone. It sits in a list until you dismiss it or promote it into a draft
automation — which goes through the exact same validate-and-confirm path as anything you type. It also
closes itself: every scan re-checks what is still true, so a door that was shut, a reading that came back,
or a stretch that has since become this entity's normal ends up `Resolved` with nothing asked of you. One
event is one card: a plug's power and current moving together, or a hub taking a dozen entities offline in
the same moment, is a single finding that names the rest.

## Installing

Housekeeper ships as a Home Assistant add-on and as a Docker image. Both are Linux containers; there is no
Windows installer and none is planned. Building from source is for contributors, not for running it.

### As a Home Assistant add-on

The best option if you run Home Assistant OS or Supervised. It appears in the sidebar, needs no token, and
nothing on your network can reach it.

1. Click the button below, or go to **Settings → Add-ons → Add-on store → ⋮ → Repositories** and add
   `https://github.com/borexola/Housekeeper`.

   [![Add repository to my Home Assistant](https://my.home-assistant.io/badges/supervisor_add_addon_repository.svg)](https://my.home-assistant.io/redirect/supervisor_add_addon_repository/?repository_url=https%3A%2F%2Fgithub.com%2Fborexola%2FHousekeeper)

2. Install **Housekeeper** and start it.
3. Open it from the sidebar.

The add-on borrows the Supervisor's own credentials, so there is no long-lived token to create. If your
Home Assistant refuses the automation write, put an admin token in the add-on's `home_assistant_token`
option and it will talk to Home Assistant directly instead. See [the add-on docs](housekeeper/DOCS.md).

> The add-on installs a prebuilt image, so publish one before installing: push the repository to GitHub and
> tag a version. The [release workflow](.github/workflows/release.yml) builds and pushes the
> per-architecture images that [`housekeeper/config.yaml`](housekeeper/config.yaml) points at.
>
> Housekeeper is not on HACS and cannot be: HACS distributes Python integrations, dashboard plugins, themes
> and scripts, not add-ons or containers. Add-on repositories are a separate channel built into Home
> Assistant, and between the add-on and the Docker image every Home Assistant installation type is covered.

### With Docker

For Home Assistant Container, or Home Assistant running anywhere else. A prebuilt multi-arch image
(amd64 and arm64) is published to `ghcr.io/borexola/housekeeper` on every release, with `:edge` tracking
`main`.

```bash
docker run -d --name housekeeper --restart unless-stopped   -p 127.0.0.1:5080:5080   -v housekeeper-data:/data   -e HOUSEKEEPER_HA_TOKEN=<admin long-lived token>   -e HOUSEKEEPER_API_TOKEN=$(openssl rand -hex 32)   -e HOUSEKEEPER__HomeAssistant__BaseUrl=http://homeassistant.local:8123   ghcr.io/borexola/housekeeper:latest
```

Or build it yourself with Compose, which also brings up an Ollama container for the model:

```bash
export HOUSEKEEPER_HA_TOKEN=<admin long-lived token>
export HOUSEKEEPER_API_TOKEN=$(openssl rand -hex 32)
export HA_URL=http://homeassistant.local:8123
docker compose -f deploy/compose.yaml --profile llm up --build
```

Then open <http://127.0.0.1:5080/dashboard> and paste the API token into the box at the top right.

You need an **admin** long-lived access token here: creating automations uses Home Assistant's config API,
which non-admin tokens cannot reach. See [SECURITY.md](SECURITY.md) for what that means.

### Then configure it

Everything else is on the settings page: which Home Assistant to talk to, the model and its limits, what
gets watched, and the tokens themselves. Most changes apply immediately; the three that need a restart say
so. Defaults are Ollama at `localhost:11434` with no model chosen yet, and nothing watched until you choose
something. See [docs/configuration.md](docs/configuration.md).

You need a model of your own either way — Housekeeper does not bundle one. Anything that reliably returns a
single JSON object works. **Test connection** on the settings page tells you whether the server answered,
lists the models it has, and works on values you have typed but not yet saved.

## API

| Method | Route | What it does |
|---|---|---|
| `GET` | `/api/status` | Which Home Assistant and model this instance points at, and whether scanning is on. |
| `POST` | `/api/proposals` | Draft an automation from a sentence. Writes nothing. |
| `GET` | `/api/proposals` | List drafts, newest first. |
| `GET` | `/api/proposals/{id}` | One draft with its plain-English story, YAML preview and duplicate warnings. |
| `GET` | `/api/suggestions` | Example requests written from the entities this house actually has. |
| `POST` | `/api/proposals/{id}/confirm` | **Writes the automation to Home Assistant.** |
| `POST` | `/api/proposals/{id}/reject` | Discard a draft. |
| `POST` | `/api/proposals/{id}/dismiss` | Hide a finished proposal. Changes nothing in Home Assistant. |
| `POST` | `/api/proposals/{id}/restore` | Bring a dismissed one back. |
| `POST` | `/api/proposals/{id}/refine` | "Not quite": re-draft with your feedback. The old draft is superseded. |
| `GET` | `/api/logs/states` | The newest state changes stored from Home Assistant, by scan, live feed or backfill, with a search over entity id, name, device, area and state. |
| `GET` | `/api/logs` | The app's recent log lines, filtered by level, flow (Scan, Home Assistant, Model…) and a search term. The Logs page. |
| `GET` | `/api/concerns` | What you asked to be watched, and what each concern resolved to. |
| `POST` | `/api/concerns` | Add a concern in plain words. The model reads it; name matching stands in without one. |
| `DELETE` | `/api/concerns/{id}` | Remove a concern. Anything it raised closes on the next scan. |
| `GET` | `/api/anomalies` | What the scanner noticed. The Noticed page. |
| `GET` | `/api/anomalies/summary` | How many findings are open and how many are serious or asked for. Drives the menu badge. |
| `POST` | `/api/anomalies/{id}/dismiss` | Silence a finding for the re-detect window. |
| `POST` | `/api/anomalies/{id}/ignore` | Stop watching the finding's entity (`?scope=device`: every entity on its device) until it is removed from Scan → Ignore. |
| `POST` | `/api/anomalies/{id}/automate` | Turn a finding into a draft automation. |
| `POST` | `/api/scan` | Scan now instead of waiting. |
| `GET` | `/api/insight` | What the scanner watches, what history it holds, and what its last run did. |
| `GET` | `/api/settings` | Every setting, its value, and where that value came from. |
| `PUT` | `/api/settings` | Change settings. Applies without a restart wherever possible. |
| `POST` | `/api/settings/reset` | Put a section back to what it would inherit. |
| `PUT` | `/api/settings/secrets` | Store or clear a secret. Never readable back. |
| `POST` | `/api/settings/test` | Check the saved Home Assistant or model settings actually work. |

OpenAPI is at `/openapi/v1.json`. Health is at `/health`; `/ready` reports the Home Assistant link. The two
pages are `/dashboard` and `/settings`.

## Layout

Two projects and one test project, about 8,800 lines of source and 5,500 of tests.

| | |
|---|---|
| `src/Housekeeper.Core` | All the logic: shortlisting, draft validation, duplicate detection, detectors, prompts, YAML. No HTTP, no SQL, no hosting — every piece is a pure function or takes its I/O through a port. |
| `src/Housekeeper.Api` | The adapters and the host: Home Assistant REST, the LLM client, SQLite, endpoints, the two pages, the settings store, the scan worker. |
| `housekeeper/` | The Home Assistant add-on: manifest, entry point, image definition. |
| `deploy/` | The standalone Docker image and Compose stack. |
| `tests/Housekeeper.Tests` | 376 tests, a couple of seconds. |

## Development

```bash
dotnet build Housekeeper.slnx -c Release
dotnet test Housekeeper.slnx -c Release
```

Requires the .NET 10 SDK. The build and the tests run on Windows, macOS and Linux; the *deployment* targets
are the Home Assistant add-on and the Docker image, both Linux. See [CONTRIBUTING.md](CONTRIBUTING.md) and
[docs/architecture.md](docs/architecture.md).

## License

Housekeeper is an independent project. It is not created, endorsed, sponsored by, or affiliated with the
Home Assistant project or the Open Home Foundation. "Home Assistant" is a trademark of the Open Home
Foundation and is used here only to describe what Housekeeper works with.


[MIT](LICENSE).
