# Changelog

All notable changes to Housekeeper are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.3] - 2026-09-23

### Changed
- A finding says when an automation you already have fires on it — "You already have an automation for
  this: **High CO2 alert** (fires when it goes above 1000 ppm)" — and the build button steps back to "Make
  another anyway". Only a switched-on trigger that names the entity itself and would fire on what was found
  counts; templates, blueprints and device or area targets never do.
- Automation configs are read only while an open finding needs them, cached for up to an hour against each
  automation's id and last change, and not retried for an hour after a failed read.

## [0.1.2] - 2026-09-20

### Added
- `Llm.OnlyWhenAsked` ("Only ask the model when I do"): nothing contacts the model unless you press
  something. Off by default. `docs/configuration.md` lists every path that reaches the model.

### Changed
- States are read back in Home Assistant's own words — a door is "open", not "on" — on finding cards,
  concern rules and draft readbacks. Automations are still written against the raw state.

## [0.1.1] - 2026-09-19

### Added
- Routines: once an hour, things you do by hand often enough — a light after a motion sensor, a door or
  someone arriving, or at about the same time most days — are offered as `Habit` findings with a one-click
  draft. Tuned by `Scan.LearnHabits`, `Scan.HabitMinimumTimes` and `Scan.HabitConfidence`.
- Dismissals teach the detectors: each one raises the bar a finding must clear to return, and the third
  silences it for good.
- `GET /api/anomalies/summary` counts routines apart as `habits`; `GET /api/insight` reports the last
  routine search.
- When `Scan.MaxTrackedEntities` binds, the entities routines are made of are kept first, and `sun.sun` is
  always watched.
- Existing automations are recognised by the areas, devices and blueprint inputs they target.
- A Copy button on every proposal's YAML.

### Changed
- A numeric reading must stay out of range for `Scan.MinimumExcursion` (10 minutes) before it is a finding,
  and back in range as long before it closes.
- Routine mining is stricter: a same-time routine must beat chance, machine-punctual ones are set aside, and
  a scan that lists no automations waits instead of re-offering what they do.
- Card buttons hold their card against the background refresh; the Concerns page refreshes like the others.

### Fixed
- A concern the model could not read is saved matched by name, retried one at a time with backoff, and
  offers **Read again**. A reading the model already gave survives a later failed attempt.
- "Locked", "unlocked" and "contact" in a concern match locks and contact sensors.
- A failed read of the house's time zone is no longer cached for six hours.
- The background refresh no longer closes open sections, wipes typed text or steals focus.

## [0.1.0] - 2026-09-16

First release.

### Added
- Drafting: a request in plain English is shortlisted against your entities, drafted by a local LLM (Ollama
  or any OpenAI-compatible endpoint), validated — every entity and service must exist, no device, area,
  floor or label targets, known keys only — and retried with the reason up to `Llm.MaxAttempts` times. The
  draft is checked for duplicates, read back in plain words, can be refined, and is written only on confirm.
- Scanning: stuck states, numeric outliers and sensors gone quiet, judged against time-of-day and weekday
  baselines, fed by polling, a live WebSocket feed, and a backfill from the recorder. Findings never
  notify; they can be dismissed, ignored, or turned into a draft, and close themselves when the condition
  passes. Related findings fold into one card, with a severity comparable across detectors.
- Missing-entity findings for automations created here; one deleted in Home Assistant is marked `Removed`.
- Concerns: worries in plain words, read by the model into entities and a rule, checked every scan.
- Pages: dashboard, Noticed (with a menu badge), Concerns, Logs and Settings. Settings apply without a
  restart except the bind address, port and database path; secrets can be stored and are never returned.
- Every entity is watched by default; Ignore narrows it. Config and diagnostic entities, and hidden ones,
  are recognised from the entity registry.
- `GET /api/suggestions`, `GET /api/insight`, `/health`, `/ready` and OpenAPI at `/openapi/v1.json`.
- A Home Assistant add-on with ingress, and a multi-arch Docker image.

### Changed
- Renamed from HearthSense. Settings use the `HOUSEKEEPER__` prefix and the database is `housekeeper.db`;
  rename an existing local `hearthsense.db` by hand.

### Security
- Loopback-only by default; any other bind needs `HOUSEKEEPER_API_TOKEN` or a trusted ingress address.
- Cross-site writes are refused, and the Host header is checked while no token is required.
- Services that act on Home Assistant itself, such as `homeassistant.restart`, are refused and never offered
  to the model.
- A self-signed Home Assistant certificate can be pinned by its SHA-256 fingerprint.
- **Test connection** never sends a stored credential to an address the caller supplied.

[Unreleased]: https://github.com/borexola/Housekeeper/compare/v0.1.3...HEAD
[0.1.3]: https://github.com/borexola/Housekeeper/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/borexola/Housekeeper/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/borexola/Housekeeper/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/borexola/Housekeeper/releases/tag/v0.1.0
