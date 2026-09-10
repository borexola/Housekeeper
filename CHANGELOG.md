# Changelog

All notable changes to HearthSense are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Drafting path: a plain-English request is shortlisted against real entities, drafted by a local
  LLM (Ollama or any OpenAI-compatible endpoint), validated so every `entity_id` exists and no
  `device_id`/`area_id` slips through, compared against existing automations for duplicates, and
  shown as YAML. Only an explicit confirm writes it to Home Assistant.
- Anomaly scanning: a polling worker keeps a compact state history in SQLite and runs three
  detectors (stuck state, robust numeric outlier, reliable sensor gone unavailable). Findings never
  notify; they can be dismissed for a re-detect window or promoted into a draft through the same
  validate-and-confirm path.
- Single-page dashboard served from the API, with optional bearer-token entry for non-loopback binds.
- Health (`/health`) and readiness (`/ready`) endpoints, OpenAPI at `/openapi/v1.json`.
- Configuration from `appsettings.json`, an optional `HEARTHSENSE_CONFIG_FILE`, and environment
  variables, validated at startup with every problem listed at once. Secrets accept a `_FILE` variant.
- Loopback-only by default; binding elsewhere without `HEARTHSENSE_API_TOKEN` is a startup failure.
- Multi-stage non-root Dockerfile with a curl-free health check, Compose stack with an optional
  Ollama profile, CI running format, build with warnings as errors, tests, and a vulnerability scan.
- Refine a draft: `POST /api/proposals/{id}/refine` with feedback re-drafts with the previous draft
  and the objection in the prompt. The old draft is marked `Superseded` only when the new one passes
  validation. The dashboard has a "Refine" box on every draft.
- Shortlist padding: when a request matches fewer than a dozen entities by name, the shortlist is
  topped up from commonly automated domains so the model can judge rather than being told nothing
  matched.
- Missing-entity findings: each scan checks every automation HearthSense created against the live
  entity list and raises a `MissingEntity` finding when one has gone. Promoting it re-drafts the
  original request.
- Time-of-day baseline for stuck-state detection: when enough previous periods began in the same
  four-hour band, only those set the bar.
- Storage schema version 2 (`feedback`, `parent_id` on proposals), upgraded in place on start.
- A settings page at `/settings` covering every option: the Home Assistant address and timeout, the model
  provider, endpoint, model and limits, everything the scanner does, the bind address, port and database
  path, and the three secrets. Each field shows the value in force, where it came from, and what it would
  fall back to, with a per-field reset and a per-section reset.
- Settings API: `GET`/`PUT /api/settings`, `POST /api/settings/reset`, `PUT /api/settings/secrets`, and
  `POST /api/settings/test` to check the saved Home Assistant or model settings actually work.
- Settings written in the UI are stored in `settings.json` in the data directory and layered above every
  other configuration source. Only values that differ from what they would inherit are stored.
- Secrets can be set from the UI, stored in `secrets.json` (owner-only where the platform supports it) and
  never returned by any endpoint.
- Durations can be written the way people say them: `45s`, `10m`, `2h`, `14d`.
- `HEARTHSENSE_DATA_DIR` chooses where settings and secrets live; it defaults to the database's folder.

- A Home Assistant add-on: manifest, entry point and image definition under `hearthsense/`, with a release
  workflow that publishes the per-architecture images the Supervisor installs. It appears in the sidebar
  through ingress, borrows the Supervisor's credentials so no long-lived token has to be created, and does
  not publish a port to the network.
- `Api.IngressAddress`: one source address that may skip the bearer token because something in front has
  already authenticated the caller. Matched on the connection's own address, never on a header.

### Changed
- Both pages redesigned around one shared stylesheet, so the dashboard and the settings screen cannot drift
  apart: centred layout, cards with real hierarchy, entity and service names as chips, source badges, and a
  status line that no longer competes with the buttons for space. A proposal that has already been decided
  keeps its YAML folded away.
- Both pages address the API relatively, and the dashboard is served at the root as well as at `/dashboard`,
  so they work under the Home Assistant ingress path prefix.
- Binding to a network address with no API token is allowed when a trusted ingress address is set, which is
  how the add-on runs. Without one it remains a startup failure.
- Settings take effect without a restart. The Home Assistant address, timeout and token, the model endpoint,
  model, timeout and key, the scan interval and the whole watch list are all re-read as they are used, and
  the API token is resolved per request so it can be rotated live. Only the bind address, port and database
  path still need a restart, and the UI says so when you change one.
- A missing Home Assistant token is now a startup warning rather than a fatal error, so a fresh install can
  boot far enough to be configured in the browser. Binding to a network address with no API token is still
  fatal, and the settings API refuses to create either of those situations.
- `/api/status` reports whether the instance is configured yet, and the dashboard shows a setup banner
  linking to the settings page when it is not.

### Fixed
- Form controls no longer render from the browser's dark palette on a light page. Declaring
  `color-scheme: light dark` without giving inputs their own colours left the textarea and the token box as
  dark slabs in a light layout; every control now takes its colours from the same variables as the page, and
  there is a real dark theme rather than an accidental half of one.
- The page content is centred instead of pinned to the left edge on a wide window.
- A Visual Studio or `dotnet run` session keeps its database, settings and secrets in `.localdata/` at the
  repository root rather than writing them into `src/HearthSense.Api/`, where `secrets.json` was not ignored
  by git and could have been committed.
- The API entry point now returns an exit code on the normal path, which the `--healthcheck` branch
  already required; without it the project did not compile.
- Home Assistant and LLM transport failures during a request now surface as a JSON `502` with the
  reason rather than an unhandled exception page, and the dashboard shows that reason.
- A timeout while reading one existing automation's config no longer cancels the whole draft; it is
  skipped like any other unreadable automation.
- `PRAGMA synchronous=NORMAL` is applied per pooled connection instead of only on the first one, and
  the database directory is created on first run.
- The scan worker takes its timers from the injected `TimeProvider`.
- `launchSettings.json` opens the dashboard on the address the app actually binds.

[Unreleased]: https://github.com/HEARTHSENSE_OWNER/HearthSense/commits/main
