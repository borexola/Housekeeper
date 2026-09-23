# Housekeeper for Home Assistant

[![CI](https://github.com/borexola/Housekeeper/actions/workflows/ci.yml/badge.svg)](https://github.com/borexola/Housekeeper/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/borexola/Housekeeper?include_prereleases&sort=semver)](https://github.com/borexola/Housekeeper/releases)
[![Docker image](https://img.shields.io/badge/ghcr.io-borexola%2Fhousekeeper-2496ED?logo=docker&logoColor=white)](https://github.com/borexola/Housekeeper/pkgs/container/housekeeper)
[![License: MIT](https://img.shields.io/github/license/borexola/Housekeeper)](LICENSE)

**Ask for an automation, or let it tell you when something is off. Nothing changes until you confirm.**

Type *"turn off the lights when no one is home"* and a local LLM drafts a real Home Assistant automation
from your own entities. Housekeeper checks it against your house and the automations you already have,
reads it back in plain words — *when the hall motion turns on, only if it is after sunset, turn on the
hall light at 60%* — and writes nothing until you confirm.

It also watches your entities and tells you when something looks off — *"the freezer door has been open
for 14 minutes; it's normally open for under a minute"* — for you to dismiss or turn into an automation.

> Housekeeper is not a life-safety system. Don't rely on it in place of smoke, CO, or leak alarms.

## Why you can trust it

- **Nothing invented gets through.** Every entity and service in a draft is checked against your Home
  Assistant before you see a confirm button. A rejected draft goes back to the model with the reason, up to
  three times.
- **Only targets that can be checked.** A wrong `device_id`, `area_id`, `floor_id` or `label_id` silently
  hits the wrong hardware, so drafts must target `entity_id` — and
  [the validator enforces it](src/Housekeeper.Core/AutomationDrafting.cs).
- **Only validated keys are written.** The config sent to Home Assistant is rebuilt from what passed.
- **It knows what you already have.** A finding says when an automation of yours already fires on it, and
  every draft is compared against your automations before you confirm.
- **Exactly one call writes to your home:** `POST /api/proposals/{id}/confirm`.

## How it works

```
"turn off lights when no one is home"
        │
        ├─ 1. shortlist    rank your entities against the request, take the best ~40
        ├─ 2. draft        local LLM returns one JSON automation, from that shortlist and your real services
        ├─ 3. validate     every entity and service must exist · no device/area targets · known keys only
        │                  ↑ rejected? hand back the reason and ask again, up to 3 times
        ├─ 4. deduplicate  compare against existing automations, warn on overlap
        ├─ 5. read back    the validated config in words: when · only if · then
        └─ 6. confirm      you approve it → POST to Home Assistant → live
```

In the background, every few minutes, starting from the history Home Assistant's recorder already holds:

```
poll /api/states → store state changes → three detectors → a list you triage
                                          ├─ stuck state     (held far longer than it ever has)
                                          ├─ numeric outlier (robust MAD z-score, and it has to stay out)
                                          └─ unavailable     (a reliable sensor gone quiet)
                                    └──→ once an hour, routines → things you could automate
```

- **Findings** never notify. Dismiss one, ignore its entity, or turn it into a draft. Each closes itself
  when its condition passes, one event is one card, and every dismissal raises the bar for it to return.
- **Routines** are things you do by hand often enough to automate — *the pantry light goes on within a
  minute of the pantry motion sensor, after dark* — offered with the numbers behind them.
- **Concerns** are worries in your own words — *"the freezer warming up"* — read by the model into the
  entities they are about and a rule that is checked every scan.

## Installing

Housekeeper runs as a Home Assistant add-on or as a Docker image.

### Home Assistant add-on

For Home Assistant OS or Supervised. It appears in the sidebar and needs no token.

1. Add this repository:

   [![Add repository to my Home Assistant](https://my.home-assistant.io/badges/supervisor_add_addon_repository.svg)](https://my.home-assistant.io/redirect/supervisor_add_addon_repository/?repository_url=https%3A%2F%2Fgithub.com%2Fborexola%2FHousekeeper)

   or go to **Settings → Add-ons → Add-on store → ⋮ → Repositories** and add
   `https://github.com/borexola/Housekeeper`.
2. Install **Housekeeper**, start it, and open it from the sidebar.

If Home Assistant refuses the automation write, put an admin token in the add-on's `home_assistant_token`
option. See [the add-on docs](housekeeper/DOCS.md). Housekeeper is not on HACS, which distributes
integrations rather than add-ons.

### Docker

For Home Assistant Container or any other install. Images for amd64 and arm64 are published to
`ghcr.io/borexola/housekeeper`, with `:edge` tracking `main`.

```bash
docker run -d --name housekeeper --restart unless-stopped \
  -p 127.0.0.1:5080:5080 \
  -v housekeeper-data:/data \
  -e HOUSEKEEPER_HA_TOKEN=<admin long-lived token> \
  -e HOUSEKEEPER_API_TOKEN=$(openssl rand -hex 32) \
  -e HOUSEKEEPER__HomeAssistant__BaseUrl=http://homeassistant.local:8123 \
  ghcr.io/borexola/housekeeper:latest
```

Or build it with Compose, which can also bring up Ollama:

```bash
export HOUSEKEEPER_HA_TOKEN=<admin long-lived token>
export HOUSEKEEPER_API_TOKEN=$(openssl rand -hex 32)
export HA_URL=http://homeassistant.local:8123
docker compose -f deploy/compose.yaml --profile llm up --build
```

Open <http://127.0.0.1:5080/dashboard> and enter the API token under the key button at the top right.
The Home Assistant token must belong to an **admin**, because creating automations uses the config API;
see [SECURITY.md](SECURITY.md).

### Configure

Everything else is on the settings page: which Home Assistant, which model, and what to watch (every entity,
by default). Most changes apply at once. Bring your own model — anything that reliably returns a JSON
object works, and **Test connection** checks it. See [docs/configuration.md](docs/configuration.md).

## API

| Method | Route | What it does |
|---|---|---|
| `GET` | `/api/status` | Which Home Assistant and model this instance uses, and whether scanning is on. |
| `POST` | `/api/proposals` | Draft an automation from a sentence. Writes nothing. |
| `GET` | `/api/proposals` | List drafts, newest first. |
| `GET` | `/api/proposals/{id}` | One draft with its plain-English story, YAML and duplicate warnings. |
| `POST` | `/api/proposals/{id}/confirm` | **Writes the automation to Home Assistant.** |
| `POST` | `/api/proposals/{id}/reject` | Discard a draft. |
| `POST` | `/api/proposals/{id}/refine` | Re-draft with your feedback. The old draft is superseded. |
| `POST` | `/api/proposals/{id}/dismiss` | Hide a finished proposal. Changes nothing in Home Assistant. |
| `POST` | `/api/proposals/{id}/restore` | Bring a dismissed one back. |
| `GET` | `/api/suggestions` | Example requests written from this house's entities. |
| `GET` | `/api/anomalies` | What the scanner noticed, and any automation that already fires on an open finding. |
| `GET` | `/api/anomalies/summary` | How many findings are open and how many are serious. Drives the menu badge. |
| `POST` | `/api/anomalies/{id}/dismiss` | Silence a finding for the re-detect window. |
| `POST` | `/api/anomalies/{id}/ignore` | Stop watching the finding's entity, or with `?scope=device` its whole device. |
| `POST` | `/api/anomalies/{id}/automate` | Turn a finding into a draft automation. |
| `POST` | `/api/scan` | Scan now instead of waiting. |
| `GET` | `/api/insight` | What the scanner watches, what history it holds, and what its last run did. |
| `GET` | `/api/concerns` | Your concerns, and what each resolved to. |
| `POST` | `/api/concerns` | Add a concern in plain words. |
| `DELETE` | `/api/concerns/{id}` | Remove a concern. |
| `GET` | `/api/logs` | Recent log lines, filterable by level, flow and text. |
| `GET` | `/api/logs/states` | Recent state changes stored from Home Assistant, searchable. |
| `GET` | `/api/settings` | Every setting, its value, and where the value came from. |
| `PUT` | `/api/settings` | Change settings. |
| `POST` | `/api/settings/reset` | Put a section back to what it would inherit. |
| `PUT` | `/api/settings/secrets` | Store or clear a secret. Never readable back. |
| `POST` | `/api/settings/test` | Check that the Home Assistant or model settings work. |

OpenAPI is at `/openapi/v1.json`, health at `/health`, readiness at `/ready`. The pages are `/dashboard`,
`/noticed`, `/concerns`, `/logs` and `/settings`.

## Development

| | |
|---|---|
| `src/Housekeeper.Core` | All the logic, as pure functions or behind ports: no HTTP, SQL or hosting. |
| `src/Housekeeper.Api` | Adapters and host: Home Assistant, the LLM client, SQLite, endpoints, pages, settings, the scan worker. |
| `housekeeper/` | The Home Assistant add-on. |
| `deploy/` | The Docker image and Compose stack. |
| `tests/Housekeeper.Tests` | Unit and integration tests. |

```bash
dotnet build Housekeeper.slnx -c Release
dotnet test Housekeeper.slnx -c Release
```

Requires the .NET 10 SDK, on Windows, macOS or Linux. See [CONTRIBUTING.md](CONTRIBUTING.md) and
[docs/architecture.md](docs/architecture.md).

## License

[MIT](LICENSE). Housekeeper is an independent project, not created, endorsed, sponsored by, or affiliated
with the Home Assistant project or the Open Home Foundation. "Home Assistant" is a trademark of the Open
Home Foundation, used here only to describe what Housekeeper works with.
