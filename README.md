# HearthSense

**Describe an automation. Review it. Then it goes live in Home Assistant.**

You type *"turn off the lights when no one is home"*. HearthSense finds the entities that request is
actually about, has a local LLM draft a real Home Assistant automation from them, checks it against
what you already have, and shows you the YAML. Nothing reaches your house until you press confirm.

It also watches your entities in the background and tells you when something looks off — *"the freezer
door has been open for 14 minutes; it's normally open for under a minute"* — which you can dismiss, or
turn into an automation with one click.

> HearthSense is not a life-safety system. Don't rely on it in place of smoke, CO, or leak alarms.

## Why it is shaped this way

Turning English into YAML is the easy part, and several projects already do it. The hard parts are the
ones that decide whether you trust it:

- **A model will invent `light.kitchen_ceiling` on a house that has no such entity.** Every entity id
  in a draft is checked against your real entities before you are ever shown a confirm button. A draft
  that references something imaginary is rejected with the names it made up.
- **`device_id` and `area_id` cannot be verified**, and a wrong one silently targets the wrong hardware.
  Drafts must target `entity_id`. The prompt says so and [the validator enforces it](src/HearthSense.Core/AutomationDrafting.cs).
- **Only the keys that were validated get written.** The config sent to Home Assistant is rebuilt from
  scratch, so anything extra the model added is dropped rather than passed through.
- **You probably already automated this.** Before confirming, HearthSense compares the draft against
  your existing automations by shared entities, trigger kind, and name, and warns you.
- **Exactly one call writes to your home** — `POST /api/proposals/{id}/confirm`. Everything else reads.

## How it works

```
"turn off lights when no one is home"
        │
        ├─ 1. shortlist    rank your entities against the request, take the best ~40
        ├─ 2. draft        local LLM returns one JSON automation, entities only from that shortlist
        ├─ 3. validate     every entity must exist · no device/area targets · known keys only
        ├─ 4. deduplicate  compare against existing automations, warn on overlap
        └─ 5. confirm      you approve the YAML → POST to Home Assistant → live
```

Separately, every few minutes:

```
poll /api/states → store state changes → three detectors → a list you triage
                                          ├─ stuck state   (held far longer than it ever has)
                                          ├─ numeric outlier (robust MAD z-score)
                                          └─ unavailable   (a reliable sensor gone quiet)
```

A finding never notifies anyone. It sits in a list until you dismiss it or promote it into a draft
automation — which goes through the exact same validate-and-confirm path as anything you type.

## Installing

HearthSense ships as a Home Assistant add-on and as a Docker image. Both are Linux containers; there is no
Windows installer and none is planned. Building from source is for contributors, not for running it.

### As a Home Assistant add-on

The best option if you run Home Assistant OS or Supervised. It appears in the sidebar, needs no token, and
nothing on your network can reach it.

1. **Settings → Add-ons → Add-on store → ⋮ → Repositories**, and add this repository's URL.
2. Install **HearthSense** and start it.
3. Open it from the sidebar.

The add-on borrows the Supervisor's own credentials, so there is no long-lived token to create. If your
Home Assistant refuses the automation write, put an admin token in the add-on's `home_assistant_token`
option and it will talk to Home Assistant directly instead. See [the add-on docs](hearthsense/DOCS.md).

> Publishing note: the add-on installs a prebuilt image. Replace `OWNER` in
> [`hearthsense/config.yaml`](hearthsense/config.yaml) and [`repository.yaml`](repository.yaml) with your
> GitHub account, then push a version tag — the [release workflow](.github/workflows/release.yml) builds and
> publishes the per-architecture images the Supervisor expects.

### With Docker

For Home Assistant Container, or Home Assistant running anywhere else.

```bash
export HEARTHSENSE_HA_TOKEN=<admin long-lived token>
export HEARTHSENSE_API_TOKEN=$(openssl rand -hex 32)
export HA_URL=http://homeassistant.local:8123
docker compose -f deploy/compose.yaml --profile llm up --build
```

Then open <http://127.0.0.1:5080/dashboard> and paste the API token into the box at the top right.

You need an **admin** long-lived access token here: creating automations uses Home Assistant's config API,
which non-admin tokens cannot reach. See [SECURITY.md](SECURITY.md) for what that means.

### Then configure it

Everything else is on the settings page: which Home Assistant to talk to, the model and its limits, what
gets watched, and the tokens themselves. Most changes apply immediately; the three that need a restart say
so. Defaults are Ollama at `localhost:11434` running `qwen2.5:7b`, and nothing watched until you choose
something. See [docs/configuration.md](docs/configuration.md).

You need a model of your own either way — HearthSense does not bundle one. Anything that reliably returns a
single JSON object works, and **Test connection** on the settings page tells you whether the server answered
and whether your model is actually pulled.

## API

| Method | Route | What it does |
|---|---|---|
| `GET` | `/api/status` | Which Home Assistant and model this instance points at, and whether scanning is on. |
| `POST` | `/api/proposals` | Draft an automation from a sentence. Writes nothing. |
| `GET` | `/api/proposals` | List drafts, newest first. |
| `GET` | `/api/proposals/{id}` | One draft with YAML preview and duplicate warnings. |
| `POST` | `/api/proposals/{id}/confirm` | **Writes the automation to Home Assistant.** |
| `POST` | `/api/proposals/{id}/reject` | Discard a draft. |
| `POST` | `/api/proposals/{id}/refine` | "Not quite": re-draft with your feedback. The old draft is superseded. |
| `GET` | `/api/anomalies` | What the scanner noticed. |
| `POST` | `/api/anomalies/{id}/dismiss` | Silence a finding for the re-detect window. |
| `POST` | `/api/anomalies/{id}/automate` | Turn a finding into a draft automation. |
| `POST` | `/api/scan` | Scan now instead of waiting. |
| `GET` | `/api/settings` | Every setting, its value, and where that value came from. |
| `PUT` | `/api/settings` | Change settings. Applies without a restart wherever possible. |
| `POST` | `/api/settings/reset` | Put a section back to what it would inherit. |
| `PUT` | `/api/settings/secrets` | Store or clear a secret. Never readable back. |
| `POST` | `/api/settings/test` | Check the saved Home Assistant or model settings actually work. |

OpenAPI is at `/openapi/v1.json`. Health is at `/health`; `/ready` reports the Home Assistant link. The two
pages are `/dashboard` and `/settings`.

## Layout

Two projects and one test project, about 5,700 lines of source and 2,300 of tests.

| | |
|---|---|
| `src/HearthSense.Core` | All the logic: shortlisting, draft validation, duplicate detection, detectors, prompts, YAML. No HTTP, no SQL, no hosting — every piece is a pure function or takes its I/O through a port. |
| `src/HearthSense.Api` | The adapters and the host: Home Assistant REST, the LLM client, SQLite, endpoints, the two pages, the settings store, the scan worker. |
| `hearthsense/` | The Home Assistant add-on: manifest, entry point, image definition. |
| `deploy/` | The standalone Docker image and Compose stack. |
| `tests/HearthSense.Tests` | 159 tests, ~0.6s. |

## Development

```bash
dotnet build HearthSense.slnx -c Release
dotnet test HearthSense.slnx -c Release
```

Requires the .NET 10 SDK. The build and the tests run on Windows, macOS and Linux; the *deployment* targets
are the Home Assistant add-on and the Docker image, both Linux. See [CONTRIBUTING.md](CONTRIBUTING.md) and
[docs/architecture.md](docs/architecture.md).

## License

[MIT](LICENSE).
