# Changelog

All notable changes to Housekeeper are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Routines. Once an hour the scanner reads weeks of stored transitions and looks for things you do by hand
  regularly enough that an automation could do them: a light that follows a motion sensor, a door, or a
  person arriving, under whichever condition makes it reliable (always, after dark, weekdays, a band of the
  day), and things done at about the same time most days. Each is a finding of the new kind `Habit`, listed
  last on the Noticed page under "Things you could automate" with a one-click draft, said in your own
  entities with the numbers behind it. A cue from another room needs twice the evidence, only the strongest
  cue is offered per thing, a light that is also a switch is one thing, whatever an existing automation
  already does is left out, and a routine you put away is never suggested again. The top three also lead
  the composer's example requests. `Scan.LearnHabits`, `Scan.HabitMinimumTimes` and
  `Scan.HabitConfidence` tune it. The house's time zone is read from Home Assistant so "about 06:45" is
  the house's 06:45.
- `GET /api/anomalies/summary` reports routines apart, as `habits`; `open` and `serious` count problems
  only, so the menu badge means "something is wrong". `GET /api/insight` reports the last search for
  routines (`routines`: when, over how many entities, how many held up, are on offer, were already
  automated, were machine-made, or why it was skipped) and the scan report's `routines` count, and the
  dashboard and Noticed page say so, so "none yet" comes with the reason.
- Dismissals teach the detectors. A finding remembers how many times you dismissed it (`dismissals` on
  every anomaly, `POST /api/anomalies/{id}/dismiss` answers with the count and what it did); after the quiet
  period it comes back only if it is further past its bar, half a doubling per dismissal, and the third
  dismissal silences it for good. Neither a silenced finding nor a put-away routine is ever pruned.
- The watch-list cap keeps what the house can learn from. When `Scan.MaxTrackedEntities` binds, the sun and
  the entities a routine could be about — lights, switches, covers, locks, fans, media players, motion and
  door sensors, people — are kept first, then readings, then Home Assistant's own machinery. Taken in id
  order, a 3,400-entity house filled its 500 slots with `automation.*`, `binary_sensor.*` and `button.*`
  and never recorded a light. `sun.sun` is always watched when Home Assistant reports it.
- Existing automations are recognised by the areas and devices they target and by the entity ids in their
  blueprint inputs, not only by `entity_id` keys, so an editor-built "turn off the living room at eleven" is
  seen to cover the living room's lights.
- A Copy button on the YAML of every proposal. It uses the clipboard API where the page is allowed to,
  and falls back to select-and-copy where it is not, such as Home Assistant reached over plain http.

### Changed
- A numeric reading has to stay out of its range for `Scan.MinimumExcursion` (ten minutes) before it is a
  finding, dated from the stored readings in between. One reading is a kettle, a microwave, a TV switching
  on or a sensor glitch, and every such spike was a card that opened on one scan and closed on the next: a
  real house watched its Noticed page go from seven findings to three between two refreshes. A sensor that
  reports rarely counts from its last change, so a thermometer stuck high is not made to wait. The card now
  says how long the reading has been out. Two spikes with a normal reading between them are two spikes, not
  one excursion, and a chatty sensor's fresh spike is never dated to an excursion hours earlier. Closing
  needs the same evidence in reverse: back inside the range and stayed there for the wait, so one poll that
  happened to read normal cannot close a card that the next poll would reopen as new.
- The routine miner learned from review: a clock routine has to beat what random switching would produce
  for a window chosen after the fact; a response within seconds to the second, or the same minute every
  day, is an automation Housekeeper cannot see and is set aside rather than offered; every cue transition
  inside the window counts, so a motion sensor that clears before the light goes on is still the cue for
  movement; a cue with more history than its effect is not drowned in misses from before the effect
  existed; a weekday routine still needs the full number of occurrences after narrowing; the last band of
  the day ends at midnight, not 24:00; a person is "Sam", not "the Sam"; "after dark" asks the drafter for
  "when it is dark", which spans sunset to sunrise as counted, and the drafting prompt now shows the shapes
  for that, for daylight, and for weekdays and weekends. The search returns everything that held up and the
  scanner applies the cap with the user's put-aways in hand, so a put-away routine takes no slot and a
  routine past the cap is never closed as "not held up"; a promoted routine closes once its automation
  exists; one whose effect or cue leaves the watch list closes saying so; and a scan on which Home
  Assistant lists no automations, on a house that had them, waits rather than re-offering everything they do.
  Local times are precomputed once per transition, so the hourly search stays well under a second on a
  large house.
- The pages share one `action()` helper: every card button holds its card against the background refresh,
  reloads the list once the hold is off, and only then reveals what it made, so a confirmed draft no longer
  flashes its old state. The Concerns page refreshes every thirty seconds like the others, and a blip never
  wipes the list. The Copy button restores keyboard focus after copying and announces "Copied" to screen
  readers.

### Fixed
- Concerns the model could not read are no longer stuck that way. Such a concern is saved matched by name
  and marked provisional, the scan's tick asks the model again until it answers, and the card offers
  **Read again** instead of telling you to remove it and retype. What was matched and why the model is
  missing are now two lines rather than one orange sentence, "Settings → Model" is a link, the rule label
  says what it is and is dropped when there is none, times read like the other pages, and the cards keep
  their state across refreshes. "The OpenAI (server's loaded model) endpoint" is now the server's address.
- "A door left unlocked at night" matched nothing, because "unlocked" is in no entity's name and a word
  that names nothing used to sink the whole concern. "Locked", "unlocked" and "contact" are kind words now
  — locks, door and contact sensors — and never looked for in a name. A particular name that matches
  nothing still matches nothing: "the attic light" in a house with no attic does not become every light.
- A concern the model has already read keeps that reading when the model cannot be asked again, so a Read
  again pressed while the model is down does not trade a rule for a name match. The scan's tick asks the
  model about pending concerns one at a time, never-tried first, with a backoff that doubles per failed try
  and stops after three unusable answers, so one concern the model cannot read neither starves the rest nor
  costs a call per tick. A Read again and the tick cannot read the same concern at once.
- A failed read of Home Assistant's time zone is no longer held for six hours, during which every routine
  was worked out in the container's zone rather than the house's.
- The dashboard and Noticed pages no longer undo what you were doing every thirty seconds. The background
  refresh rebuilt every card from scratch, which closed an opened YAML section, wiped a half-typed
  refinement, dropped focus, and could detach a button whose action was still running. Cards are now
  reused when their data has not changed, carry over open sections, typed text, and focus when it has, and
  are left alone while an action on them is in flight.

### Changed
- Renamed from HearthSense to Housekeeper. The environment prefix is now `HOUSEKEEPER__`, the token
  variables are `HOUSEKEEPER_API_TOKEN`, `HOUSEKEEPER_HA_TOKEN` and `HOUSEKEEPER_LLM_API_KEY`, the
  configuration section is `Housekeeper`, the default database file is `housekeeper.db`, the add-on slug is
  `housekeeper`, and the image is `ghcr.io/borexola/housekeeper`. Nothing was released under the old name,
  so there is no migration; an existing local `hearthsense.db` can be renamed by hand.

### Added
- Every draft is read back in words. `ProposalView.Story` carries three lists — when, only if, then — built
  deterministically from the validated config by `AutomationNarrator`, with entities named the way Home
  Assistant names them. The dashboard shows the story first and folds the YAML underneath.
- `GET /api/suggestions`: a handful of example requests written from the entities this house actually has,
  offered on the composer so the first draft is one that fits.
- Findings fold across scans and across devices. Entities that went unavailable within a few minutes of one
  another are one card that names the rest, and a newcomer joins the card the user already has rather than
  opening beside it. Rows it replaces are closed as covered.
- Findings retire themselves with a stated reason, kept in the evidence as `closed_because`: the entity
  left the watch list, Home Assistant now classes it as a setting or diagnostic, it is a running total,
  another card covers it, or a numeric sensor has reported a newer reading the detector could not judge.
  An unavailable finding closes the moment its entity reports again, whatever its history.
- Backfill from the recorder. While an entity's stored history reaches back less than three weeks, the scan
  reads what Home Assistant's recorder already holds for it (`Scan.BackfillFromRecorder`, on by default),
  a few hundred entities per scan and once per process, thinned to the density the detectors read at. A
  fresh install judges from its first scan instead of after a day or two, and an existing one gains the
  days before it started watching. The scan report and the dashboard say how much was read.
- Every finding says which device and area its entity belongs to.
- Weekly baselines. Once an entity's history reaches back three weeks, stuck-state and numeric findings are
  judged against the same part of the week, weekday or weekend in the house's own time zone, as well as the
  same time of day. Evidence carries `baseline: time_of_week` and `week_part`.
- Concerns. `POST /api/concerns` takes a worry in plain words; the model reads it into the entities it is
  about and a rule (above or below a value, a state held too long, gone offline), and name matching stands
  in when no model can be asked. Concerned entities are judged against bars a third lower, their findings
  are ranked first and tagged, and a rule is checked every scan and raised as a `Concern` finding in the
  user's words. Storage schema version 5 adds the `concerns` table. A Concerns page at `/concerns` holds them, one card
  each, with presets and free text.
- Findings moved to their own page at `/noticed`, second in the menu, with a badge on every page showing
  how many are open. The badge is red when any is serious: something asked for through a concern, an
  automation of the user's own that no longer works, or a finding eight or more times past its bar
  (`GET /api/anomalies/summary`). The dashboard keeps drafting, watching and proposals.
- A Logs page at `/logs`, backed by `GET /api/logs`: the app's recent lines held in memory, newest first,
  searchable and filterable by level and by flow (Scan, Home Assistant, Model, Live feed, Drafting, Concerns,
  Settings). Every Home Assistant request and every model call now logs its path, status and timing at
  Debug, which the page shows and the console does not. A second view, backed by `GET /api/logs/states`,
  lists the state changes themselves as they arrive from Home Assistant, each with its device and area, searchable by entity, device, area and state.
- A redesigned dashboard: a composer with example requests, findings grouped by kind with an icon and a
  gauge for how far past the bar each is, story-first proposal cards with drafts kept on top, relative
  times, and the API token behind a key button rather than a permanent box. The settings page shares the
  same header and styles.

### Changed
- Severity saturates. It is 1 at the bar and one more for every doubling past it, capped at 8, on every
  detector alike; missing-entity findings sit above the cap. A plain ratio grew without limit, so a sensor
  dead for a month scored sixty and outranked a boiler at twice its usual pressure.
- `AnomalyView` carries `severity`.
- `Scan.History` defaults to 28 days rather than 14: two weeks holds at most two of each weekday, which is
  not enough to tell a weekly habit from a coincidence.
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
- Configuration from `appsettings.json`, an optional `HOUSEKEEPER_CONFIG_FILE`, and environment
  variables, validated at startup with every problem listed at once. Secrets accept a `_FILE` variant.
- Loopback-only by default; binding elsewhere without `HOUSEKEEPER_API_TOKEN` is a startup failure.
- Multi-stage non-root Dockerfile with a curl-free health check, Compose stack with an optional
  Ollama profile, CI running format, build with warnings as errors, tests, and a vulnerability scan.
- Refine a draft: `POST /api/proposals/{id}/refine` with feedback re-drafts with the previous draft
  and the objection in the prompt. The old draft is marked `Superseded` only when the new one passes
  validation. The dashboard has a "Refine" box on every draft.
- Shortlist padding: when a request matches fewer than a dozen entities by name, the shortlist is
  topped up from commonly automated domains so the model can judge rather than being told nothing
  matched.
- Missing-entity findings: each scan checks every automation Housekeeper created against the live
  entity list and raises a `MissingEntity` finding when one has gone. Promoting it re-drafts the
  original request.
- Ignore from a finding: `POST /api/anomalies/{id}/ignore` adds the entity (or, with `?scope=device`, every
  entity on its device) to `Scan.Exclude` and dismisses its open findings, so nothing about it is suggested
  again until it is removed from Settings → Scan → Ignore. The dashboard shows both buttons on every finding;
  the device one appears when Home Assistant knows the device.
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
- `HOUSEKEEPER_DATA_DIR` chooses where settings and secrets live; it defaults to the database's folder.

- A Home Assistant add-on: manifest, entry point and image definition under `housekeeper/`, with a release
  workflow that publishes the per-architecture images the Supervisor installs. It appears in the sidebar
  through ingress, borrows the Supervisor's credentials so no long-lived token has to be created, and does
  not publish a port to the network.
- `Api.IngressAddress`: one source address that may skip the bearer token because something in front has
  already authenticated the caller. Matched on the connection's own address, never on a header.

- Drafting retries. A rejected draft goes back to the model with the validator's own sentence, up to
  `Llm.MaxAttempts` times (default 3). An unanswering endpoint, an `UNSUPPORTED` reply, or an answer
  identical to the one just rejected ends the loop early rather than costing another model call.
- Service calls are validated against the list Home Assistant reports, and the services belonging to the
  shortlisted domains are shown to the model so it picks from real ones. An unreadable service list skips
  the check rather than blocking the draft.
- The prompt carries two complete worked examples of the required output, which is what a small model
  copies from.

- Findings close themselves. Each scan re-checks every open finding and marks the ones whose condition has
  passed `Resolved`: the door was shut, the reading came back, or the stretch that looked unusual has since
  become that entity's normal. They reopen at once if the condition returns, and closed findings are deleted
  once they outlive both the re-detect window and the keep-decided window.

- Entity registry: one WebSocket call per half hour reads `entity_category` and `hidden_by`, which are not
  in the state API. Home Assistant's own word on which entities are settings or instrument readings, and
  which the user has hidden, so findings about a plug's auto-off checkbox or a sensor's link quality stay
  out of the way. Best effort — an older Home Assistant, a proxy that will not pass WebSockets, or
  `HomeAssistant.ReadEntityRegistry` turned off falls back to recognising those from device classes, units
  and naming. Through the Supervisor's Core proxy the socket is `/core/websocket`, not `/core/api/websocket`.
- Findings are ranked. Each carries a severity — how many times past its own bar it is, comparable across
  the three detectors — and the list is ordered by it, so a latched presence sensor is not buried among
  smart plugs. Storage schema version 4 adds the column, upgraded in place on start.
- One finding per device per kind. A smart plug reports power, current and energy, and a switched light is
  often both a `light.` and a `switch.` entity, so one event arrived as three or four identical cards. The
  most severe stands for the group and names the rest in its summary and evidence (`also_moved`).
  `Scan.GroupByDevice` turns it off.

- Live state changes. A WebSocket subscription records transitions as they happen, alongside the scan rather
  than instead of it. The scan reads every entity once an interval and so can never record more than one
  sample per entity per interval — a door opened and shut inside that window leaves two stored samples
  carrying the same state, which the stuck-state detector has to discard rather than count as a stretch it
  never saw the ends of, and a motion sensor's real on-durations are almost entirely in that blind spot.
  Only non-numeric states are stored this way: a numeric reading feeds the outlier detector, which is handed
  a deliberately thinned baseline anyway, and those are every chatty entity in the house — storing them live
  would multiply the sample table for readings nothing would ever look at. Because it is an addition, a feed
  that drops or never connects costs only the transitions it would have seen and the next poll closes the
  gap, so there is no resynchronisation to get wrong. `Scan.RealtimeUpdates` turns it off.

### Changed
- Sitting at rest is no longer judged for any entity, not just binary sensors. A light that is off, a lock
  that is locked, a cover that is closed, a vacuum docked, a media player idle, an automation enabled: that
  is where these live, and no length of it is news. A real house reported its backyard light twice — once as
  a `light.` and once as a `switch.` — for being off for two hours at night. The active side is still judged,
  so a porch light left on all day still reports.
- Stuck-state findings need their evidence to reach back, not merely add up. Four stretches in one evening
  said "across 4 earlier stretches" and read as settled fact about a daily rhythm; the completed stretches
  must now span `Scan.MinimumBaselineSpan`, the same bar a numeric baseline has to clear. The card also says
  how long the watching took — "across 4 earlier stretches seen over 6 days" — so thin evidence looks thin.
- The shortlist the model is shown leaves out what nobody automates. Link quality, signal strength, uptime,
  firmware, calibration and sensitivity knobs and LED indicators are no longer offered, and entities Home
  Assistant marks `config`, or that the user has hidden, are demoted with them. This matters most when the
  wording matches little and the shortlist is padded speculatively: on a house of a few thousand entities,
  "make the garage cosy in the evening" spent 23 of its 40 lines on radio readings and calibration knobs,
  and the prompt then tells the model that what it cannot see does not exist. It is a demotion rather than
  a ban, so "notify me when the bluetooth signal on the lock drops" still finds it, and an entity id
  written out in full is always honoured. Deliberately a narrower rule than the one the detectors use:
  battery, update and connectivity all look diagnostic and are all ordinary things to automate.
- Numeric outlier detection no longer reports things nobody can act on. A robust z-score measures how tight
  a baseline is, not whether anything happened, and on its own it reported energy meters, disk usage, link
  quality, battery voltages and every plug that had merely been switched on — nineteen findings in a day on
  a real house, of which two were real. It now also requires: the baseline not to be a running total, by
  `state_class` or by the shape of the numbers (energy, disk, a draining battery); the entity not to be a
  diagnostic or a setting; a move clearing both a share of the value (`Scan.MinimumEffect`, 15%) and a
  per-device-class floor in watts, degrees or percent; the reading not to sit where the entity already
  spends time, which is what a plug that is off or an HRV on a lower fan speed looks like; and a baseline
  of at least `Scan.MinimumNumericSamples` readings (30) spanning at least `Scan.MinimumBaselineSpan`
  (6 hours).
- Numeric baselines are compared against the same four-hour band of the day when there is enough history
  for one, as stuck-state detection already was. An outdoor thermometer is no longer reported for being
  cold at night.
- The suggested threshold is placed from the baseline rather than from the excursion. It used to sit
  between normal and the reading that had just been seen, so every numeric suggestion was already crossed
  at the moment it was offered — "notify me when the voltage goes below 3012.83" from a sensor reading
  3009. It now clears three spreads, the 98th percentile of everything the entity has actually done, and
  the smallest move worth a finding; where that leaves no room before the current reading, no finding is
  raised at all.
- Numeric history is read separately from the rest and thinned to a couple of readings an hour, so a
  baseline covers the retention window instead of crowding the end of it. Taking the newest 250 changes
  from a sensor that changes every minute was four hours of history, not a fortnight, which is why
  "judged over 250 readings" could know nothing about nights. Contiguous history is still what
  stuck-state detection reads, because it measures stretches between one sample and the next.
- "Ready to judge" on the dashboard counts the same bar the detectors actually apply, so it no longer
  claims every entity in a house is fully judged after a day.
- The resting side of a binary sensor is no longer judged for holding its state. Motion clear, a door closed,
  a link up: sitting there is what these entities do nearly all day, at any length. The active side still is
  judged, so a door left open still reports, and for device classes whose healthy state is on (connectivity,
  power, plug, running, light, battery charging) it is the off side that counts as active.
- Findings read as a heading and two or three short sentences: the entity's name is the card title with its id
  as a chip, and the summary starts with what happened instead of repeating the name. Evidence carries
  `entity_name` and `device`, and the registry lookup resolves each entity's device alongside its area.
- A single trigger, condition or action written as a bare object instead of a one-element array is now
  accepted and normalised rather than rejected. Home Assistant still only receives the validated array form.
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

- `Storage.KeepDecidedFor` (default 30 days): finished proposals — rejected, failed, superseded, removed —
  older than this are deleted on each scan. Drafts and live automations are never pruned.
- A `Removed` proposal status. When an automation created here is later deleted in Home Assistant's own
  editor, the scanner notices, marks the proposal removed and dismisses any finding still open against it.

### Changed
- Drafting makes far fewer calls to Home Assistant. Automation entities now carry their config id, so the
  entity list a draft already holds is enough to know which configs to read, instead of fetching every
  state a second time. The configs and the service list are each kept for five minutes; the automations
  cache is keyed on the set of ids so an automation added or removed in Home Assistant is noticed at once,
  and creating one here invalidates it.
- The container health check is a bash socket probe rather than a second .NET process every 30 seconds, and
  runs every 60 seconds. The `--healthcheck` entry point is gone.

- `GET /api/insight` and a **Watching** panel on the dashboard: how many entities are watched and against
  which patterns, how many changes are held and over what span, how many entities hold enough history to be
  judged, when the next scan is due, and what the last one did — including its error, if it failed. A meter
  shows how far the install is from being able to say anything at all. The empty findings list now explains
  itself rather than only reporting emptiness.
- The scanner remembers its last run, so a failed scan is visible instead of looking like a quiet one.
- The countdown to the next scan, and how long ago the last one ran, tick once a second from the cached
  payload rather than jumping only when the panel is refetched. A hidden tab has its timers throttled by the
  browser, which is right, so returning to one corrects the times and refetches immediately.

- A finished proposal can be dismissed from the dashboard, and brought back with **show dismissed**. It is
  only hidden: an automation created here stays live in Home Assistant and stays supervised for entities
  that disappear under it. A draft cannot be dismissed, because it is a decision waiting to be made rather
  than a record — create it or discard it.
- Storage schema version 3 (`dismissed_utc` on proposals), upgraded in place on start.
- A third worked example in the prompt covering a repeating reminder, plus a rule that a trigger fires once
  and "every N after that" needs a `repeat`/`while` with a `delay`. A request like "warn me after two hours
  and every thirty minutes thereafter" was producing an automation that only ever fired once.

### Changed
- The model field is optional. Empty means whatever the server has loaded, which an OpenAI-style server such
  as LM Studio or llama.cpp answers with; the request is sent without a `model` field. Ollama still needs a
  name, and both the settings page and a draft attempt say so and list what the server offers.
- **Test connection** tests the values on the page rather than the values on disk, including a secret typed
  but not yet saved, so an address or a token can be tried before it is kept. Nothing is written by a test.
- The dashboard and settings banners say what is still missing before a first draft, rather than only that
  something is.

### Changed
- **Watch everything is now on by default.** A new install watched nothing until someone worked out that
  entity globs were the missing step, which reads as broken rather than as unconfigured. Ignore still applies
  on top — it is applied last, so it narrows watch-everything exactly as it narrowed a watch list, and the
  dashboard's Ignore buttons keep working unchanged. Turn **Watch everything** off to go back to naming what
  you want in **Watch**; the settings page warns if you leave both empty. Existing installs that stored a
  watch list are unaffected, since a stored value still beats the default.
- The dashboard says when the tracked-entity cap is truncating. Watching everything makes that the ordinary
  case on a house with more entities than the cap, and entities are taken in name order — so it was quietly
  watching the alphabetically-first 500 and never mentioning the rest.

### Security
- A Home Assistant serving HTTPS with a certificate it signed itself can now be reached, without giving up
  on checking. Put the certificate's SHA-256 fingerprint in `HomeAssistant.CertificateFingerprint` and that
  exact certificate is trusted — something else on the same network offering its own self-signed certificate
  is still refused, which matters because every request carries the admin token. Paste it however your tool
  prints it; the whole `sha256 Fingerprint=…` line from `openssl` works. `HomeAssistant.AcceptAnyCertificate`
  turns checking off entirely for anyone who needs it, warns at startup, and says plainly what it costs.
- The Host header is checked while no API token is required. DNS rebinding defeats every cross-site signal
  there is — a page on an attacker's domain whose DNS is flipped to `127.0.0.1` mid-visit is genuinely
  same-origin with a loopback install, so the browser reports `same-origin` and means it, and it can read
  replies as well as write. Requests addressed to anything but a loopback name are now refused with 421,
  reads included. `Api.AllowedHosts` is there for your own reverse proxy.
- `Sec-Fetch-Site: same-site` is refused alongside `cross-site`. For a host with no registrable domain —
  every IP literal, and `localhost` — "same-site" is satisfied by an equal host alone, so any *other* local
  service qualified. Home Assistant ingress is same-*origin*, so nothing legitimate needed it.
- A service that acts on Home Assistant itself is refused, and never offered to the model. `homeassistant.stop`
  and `homeassistant.restart` exist, so the existence check happily accepted them — "verifiable" was being
  used as a proxy for "safe", and for that handful of services it is not one.
- A state trigger comparing against a number is refused. It is the one mistake the prompt singles out as
  always wrong, and nothing enforced it: `to: "25"` compares text, so it is a legal automation that can
  never fire, and Home Assistant accepts it happily.
- The `scene:` shorthand action carries an entity id under its own key, which the walker never visited — so
  an invented scene reached the written config unchecked and never appeared in the list of what it touches.
- The add-on's pinned settings are read from the environment rather than from the settings file they exist
  to overrule. Reading them afterwards meant the pin re-asserted whatever was stored, so an install upgraded
  from a build where the port was an ordinary setting carried its stored port straight through the guard.
- `Reset` takes the same guards as `Save`. Removing an override changes what is in force exactly as much as
  writing one, and "Reset this section" sits one click away on every section.
- Cross-site writes are refused. On the default loopback binding there is no token and no authentication —
  normal for a local tool, and exactly what made this necessary: every state-changing call is a plain POST,
  which a browser sends cross-origin without asking first, so any page the user happened to have open could
  post to `127.0.0.1` and confirm a draft into their home. Non-GET requests carrying a cross-site
  `Sec-Fetch-Site` or a foreign `Origin` are now refused; both are set by the browser and cannot be forged
  from script, and a non-browser client sends neither.
- **Test connection** no longer sends a stored credential to an address the caller supplied. Aimed at a
  different host from the saved one, the test uses only a secret typed into the same request, and says so
  when there is none — otherwise "test" was a way to make the server post a Home Assistant admin token
  anywhere it was told to.
- A templated service name is refused rather than skipped. It resolves only when the automation runs, so
  nothing here can say what it would call — the same unverifiable-target problem as `device_id`, given the
  same answer. Templates anywhere else in a draft are untouched.
- `floor_id` and `label_id` targets are refused alongside `device_id` and `area_id`. A wrong floor or label
  actuates a great many devices at once, and none of the four can be checked against anything.
- Format characters are stripped from an alias and a description. A right-to-left override reverses how the
  rest of the line is drawn, so the sentence on the confirmation screen could be made to read as something
  it was not.
- The add-on's listen address, port and database path are pinned by the add-on and refused by the settings
  API. A stored override left the Supervisor knocking at a port nothing was listening on, with the page
  needed to undo it behind that same ingress.
- The guard that refuses to clear the API token now judges by the address the process is really listening
  on, not the one just typed in. The bind address only takes effect on restart, so a single save could
  switch it to loopback and, in the same breath, clear the token protecting a socket open to the network.
  It also honours the documented `{NAME}_FILE` convention, which it previously ignored — refusing a clear
  that was perfectly safe.
- The release workflow passes its `workflow_dispatch` input through the environment and validates its shape
  rather than interpolating it into a shell script in a job holding `packages:write`. It also builds and
  tests before publishing, and the add-on image is built in CI so a broken Dockerfile fails on a pull
  request rather than at release time.

### Fixed
- Refining a draft no longer overwrites a confirm that landed while the model was thinking. `RefineAsync`
  read the proposal, spent ten to sixty seconds on a local model, then wrote that whole stale row back —
  and confirming stays possible the entire time. The automation ended up live in the house while the record
  said `Superseded` with its Home Assistant id nulled out, so nothing supervised it, nothing could find it,
  and the row was deleted after the retention window. Every decision about a proposal now goes through one
  atomic claim that writes only the status.
- Dismissing a proposal cannot erase the automation id of a confirm in flight. The claim marks the row
  `Created` before Home Assistant is written to, so a dismissal arriving in that window passed its own
  "not a draft" check and read a row whose id was not there yet — and writing that snapshot back afterwards
  blanked the id the confirm had just recorded, leaving an automation live in the house with nothing pointing
  at it. Confirming, dismissing and retiring now each write only their own columns.
- A write Home Assistant refuses leaves the draft ready to try again. A non-admin token or a restart mid-click
  used to burn the proposal permanently — Create, Refine and Discard all answered 409 and the only button
  left was Dismiss, so a minute of model time and a careful read of the YAML had to be redone for a problem
  that was never in the draft. A write whose outcome is *unknown* can also be retried now, reusing the same
  automation id so the retry edits that automation rather than adding a second one.
- A refusal is taken as one however the model worded it. `UNSUPPORTED` had to match exactly, so
  `"UNSUPPORTED: no area targeting"` was fed back as *"no 'triggers' array"* — under a rule telling the
  model to fix that one thing. Which is how "turn off everything downstairs" stopped being a refusal and
  became an automation that turns off an arbitrary set of lights.
- A malformed reply is reported as malformed. Every automation is full of objects that parse, so accepting
  the first one that merely parsed meant a broken outer object fell through to a nested one and the draft
  was refused for "no usable 'alias'" — the alias was fine, and the repair loop then spent every remaining
  attempt fixing the one part that had been right. A reply cut off by the output-token cap now says so.
- The prompt teaches branching. "On at sunset and off at midnight" is one automation with two triggers and
  two different actions, and there was no shape in the prompt for that — so a small model put both actions
  in one sequence and the light turned on and straight back off. The validator cannot tell that from a
  correct automation. The "at sunset" row also carried a thirty-minute offset nobody asked for.
- The shortlist reaches every kind of thing in the house. Padding was a strict sort by domain rank, which
  drains one domain before starting the next — so in a house with forty lights a vague request showed the
  model nothing but lights, and the prompt then told it that what it could not see did not exist.
- The unavailable detector no longer counts the outage against the sensor's own record. The scanner stores
  the current reading first, so the outage sat in the history being judged — in the denominator and not the
  numerator — capping a spotless record at (n-1)/n and silencing the detector entirely below ten recorded
  changes. Which is exactly the steady entity it exists for.
- Judgeability is answered per detector, and closing a finding is a different question from being able to
  judge one. A still-dead sensor and a still-abnormal reading stop producing samples by definition, so their
  history ages out, their detector falls silent — and that was being read as "back to normal".
- Promoting a finding silences it for the re-detect window, not for ever. `Promoted` had no exit at all: the
  dedup key is unique and permanently occupied, so an entity could report that kind of problem exactly once.
- `running` and `battery_charging` are no longer treated as resting states, which had inverted which side of
  an appliance sensor was judged and silenced the case that matters — something left running far too long.
- A stretch never seen to end no longer sets the bar. The scanner is a poller, so a whole transition can
  happen between two polls and leave two neighbours in the same state; counting the gap between them as one
  long stretch inflated the "longest before now" that every stuck-state finding is measured against.
- A suggested threshold always sits between normal and the reading that prompted it — beyond the reading it
  produces an automation that can never fire, which was reachable at any configured threshold under 3.
- A timeout past what a timer accepts is clamped rather than thrown. Above ~49.7 days `CancelAfter` throws
  before a byte leaves the process, so every call failed — including the one that writes an automation, which
  then recorded itself as possibly-live in the user's home.
- Caches are keyed on which Home Assistant they came from, so changing the base URL no longer leaves drafting
  validating against the previous instance for five minutes.
- The documented `HOUSEKEEPER_CONFIG_FILE` layer is actually read. The file provider was given a rooted path
  it re-combined under its own root and never found, and because the source was optional it loaded nothing
  and logged nothing — so an entire documented configuration layer was discarded in silence.
- Ollama's model check matches the way Ollama resolves names: a bare name means `:latest`, so "Test
  connection" no longer passes on a model every draft would then 404 against. When the model is pulled under
  a different tag, the message names that tag.
- A scan interval under a millisecond is clamped rather than thrown out of the background service.
- An entity whose last change predates the retention window is no longer re-inserted and re-pruned on every
  scan for ever, counted each time as a new change stored.
- The duplicate-detection failure no longer blames the token for an ordinary 404 from a YAML-defined
  automation, which is expected and means nothing is wrong.
- The settings page keeps a secret you have pasted but not saved, shows the add-on's pinned settings as fixed
  rather than editable, can hold a provider value its dropdown has no option for, clears secret boxes on
  Discard, and survives its own initial load failing.
- A failed "Draft it" is no longer reported as a success when the follow-up reload also fails, and every
  button on a draft card now excludes the others while any one of them is in flight.
- A numeric reading is judged against the other readings rather than against itself. The scanner stores the
  current sample before the detectors run, so the outlier was inflating the very scale it was measured by:
  on a near-flat baseline the score is capped at the square root of the sample count, which at twelve
  samples is 3.46 and can never clear the default threshold of 4. A boiler at more than double its usual
  pressure scored 3.5 and was never mentioned.
- A steady sensor going quiet is now reported. Samples are only stored when an entity *changes*, so a
  thermostat that has read the same number for a fortnight — exactly the kind whose silence matters most —
  never accumulated the twelve recorded changes the unavailable detector demanded. It now asks how long the
  entity has been on record instead of how much it fidgets.
- A suggested duration longer than a day rounds up rather than down. Past the end of the friendly-durations
  table any span came back as 24 hours, putting the threshold below the entity's ordinary behaviour and
  producing an automation that fires constantly.
- A threshold is never offered closer to normal than halfway to the reading that prompted the finding.
- The `light` binary-sensor device class is no longer treated as healthy-when-on, which inverted which side
  of a light sensor was judged. A value that rounds to negative zero is written as `0`, and no longer slips
  past the guard that drops a move too small to see.
- Confirming claims the draft in a single conditional update, so two confirms at once cannot write the
  automation to Home Assistant twice; rejecting takes the same claim, so it cannot overwrite a confirm
  already in flight. The automation id includes the proposal id, so two confirmed in the same millisecond no
  longer collide — Home Assistant treats a repeated id as an edit of the first.
- Confirming is no longer cancelled by the caller hanging up. The write to Home Assistant and the record of
  having made it are one uninterruptible step, and a failure records the id that was used and says the
  automation may still be there rather than implying nothing happened.
- A finding promoted into a draft that then fails goes back to open. `Promoted` was a state with no way out:
  it could not be dismissed, re-promoted, resolved or pruned, so the finding sat on the dashboard forever
  with every button on it answering 409.
- A finding is no longer closed because its entity could not be judged. A detector staying silent for want
  of history is not the same as the condition having passed, and treating it as such quietly cleared the
  finding for a sensor that was still dead.
- Schema upgrades run in one transaction with the version bump inside it. A process killed mid-upgrade used
  to leave the old version recorded with the new column already added, and every later start died on
  "duplicate column name" — before the host could serve the page needed to fix it.
- Ollama is told how much context to use. Many community builds declare 2048 in their Modelfile and Ollama
  silently discards whatever does not fit, so the entity catalogue or the rejection being fed back on a
  retry could vanish without a trace — and the model then invented the entities it could no longer see.
- `HttpClient`'s own 100-second default no longer caps a configured timeout while the failure message quotes
  the configured value. A slow local model on a CPU regularly needs longer.
- A scan interval past what a timer will accept is clamped rather than thrown, which used to stop the whole
  host — including the settings page needed to undo the value.
- The JSON object is found among whatever the model said around it, by matching braces rather than slicing
  from the first to the last. A model that explained itself with a brace in the sentence had a perfectly
  good answer rejected as malformed.
- The nesting limit counts high enough for a real automation. It counts JSON nodes, not automation
  constructs, and a `choose` holding a sequence holding a notification payload is a dozen levels on its own.
- Duplicate detection says it was skipped rather than reporting no duplicates. Every automation config read
  failing — most often a token that is not an admin's — returned an empty list, which was cached as fact for
  five minutes and quietly turned the check off.
- **Test connection** only ignores a model's tag when the user did not type one. Ollama uses the tag to tell
  quantisations and sizes apart, so a 32b model was confirmed as available because a 7b one had been pulled.
- A host named `api` or `v1` is no longer eaten when joining a base address to a path.
- Two people ignoring two different entities at once no longer lose one of them, and a failed write of a
  secret no longer takes effect in this process anyway.
- Resetting a section matches the catalog's own spelling, rather than reporting success for a no-op.
- A list query asked for a thousand rows no longer comes back with five hundred, which left the oldest open
  findings never re-examined and so never resolved.
- The "Watching" tile no longer reports the number of entities with stored history when the last scan
  watched none: zero is an answer, and it now says so.
- Create and Discard cannot both be in flight for the same draft.
- "review and confirm it" is a real link, so it can be reached by keyboard and announced by a screen reader.
- The settings page keeps edits you have typed when something else on it reloads the page. Resetting one
  field, or clearing one secret, used to silently throw away every other unsaved change — while the footer
  counted them out loud right up to the moment they vanished.
- A provider spelled the way configuration accepts it (`openai`, `OpenAI-compatible`) no longer leaves the
  dropdown blank, which invented an unsaved change and then blocked every save on the page.
- A dashboard that cannot reach the server says so instead of showing frozen data under a live countdown,
  and the next-scan countdown counts down to the tick the worker is really waiting on.
- The history summary on the dashboard is computed once per scan rather than counted across the whole
  retention window on every thirty-second poll, per open tab.
- `.dockerignore` excludes nested `bin`/`obj`, so a developer's build output no longer overwrites the
  container's restore and breaks the documented `docker compose up --build`.
- "Ready to judge" was counting entities with at least `Scan.MinimumSamples` recorded changes, which is not
  what any detector actually requires. The stuck-state detector needs only a handful of *completed stretches
  in the state an entity is holding now*, so a door was reported as having too little history while that
  detector was perfectly willing to report it. The count now comes from `AnomalyDetection.CanJudge`,
  assembled from the detectors' own preconditions and evaluated during the scan over the same history they
  read, so the figure and what the detectors can do cannot disagree. Its numerator and denominator are now
  the same set of watched entities too, rather than two different populations.
- A numeric reading is no longer reported when it and its usual value are the same number once written down.
  A disk idling between 0.00 and 0.02 MB/s could score a large robust z on an invisible move, producing
  "Reads 0 MB/s, well outside its normal range. Usually near 0 MB/s."
- Dismissed and resolved findings are left out of the list by default, behind **show closed**. A finding the
  scanner closed by itself asks nothing of anyone, and a screenful of them buried the ones that did.
- An OpenAI-style endpoint entered as `http://host:8080/v1` — the form every OpenAI SDK expects, and so the
  form people paste — was extended to `.../v1/v1/models` and answered 404. A base address that already ends
  with the segment about to be added is no longer extended with it again; the same applies to a Home
  Assistant address ending in `/api`. A refused connection test now names the exact address it tried.
- A tappable button inside a mobile notification is no longer mistaken for a service call. Home Assistant
  reuses the key `action` inside a notification payload for a button identifier such as `CLOSE_IT`, and
  service validation was reading those as invented services and rejecting otherwise valid automations.
  A service call is now recognised as `domain.service`, which also keeps the button out of the list of
  calls shown to the user.
- State history is pruned on every scan, not only when something is being watched. Narrowing the watch list
  used to leave the samples of everything dropped from it in the database indefinitely.
- A scan reads at most 250 samples per entity instead of every sample inside the retention window, so a
  chatty entity with a fortnight of history cannot pull an unbounded amount into memory every few minutes.
- Form controls no longer render from the browser's dark palette on a light page. Declaring
  `color-scheme: light dark` without giving inputs their own colours left the textarea and the token box as
  dark slabs in a light layout; every control now takes its colours from the same variables as the page, and
  there is a real dark theme rather than an accidental half of one.
- The page content is centred instead of pinned to the left edge on a wide window.
- A Visual Studio or `dotnet run` session keeps its database, settings and secrets in `.localdata/` at the
  repository root rather than writing them into `src/Housekeeper.Api/`, where `secrets.json` was not ignored
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

[Unreleased]: https://github.com/borexola/Housekeeper/commits/main
