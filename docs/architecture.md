# Architecture

Two projects. The split is the only structural rule, and it exists so the interesting logic can be
tested without a web host, a database, or a network.

```
Housekeeper.Core          pure logic + ports          no HTTP, no SQL, no ASP.NET
        ▲
        │ implements
Housekeeper.Api           adapters + host             Home Assistant, LLM, SQLite, endpoints, UI
```

`Core` declares four ports — `IHomeAssistant`, `ILlmClient`, `IStore` and `ISettingsProvider` — and `Api`
supplies all four. Every decision that could be wrong lives in `Core` behind a pure function.

## The drafting path

[`ProposalService.DraftAsync`](../src/Housekeeper.Core/ProposalService.cs) is the whole product in one
method:

1. **Shortlist** — [`EntityIndex.Shortlist`](../src/Housekeeper.Core/EntityIndex.cs) ranks entities
   against the request by token overlap on entity id, friendly name and area, plus a small map of words
   that imply a domain (`lights` → `light`, `nobody` → `person`). An entity id written out in full is
   always included, which is what makes anomaly promotion reliable. Bounded at 40 by default, so a
   1,500-entity house costs the same prompt as a 30-entity one.

   Retrieval is deliberately *not* semantic. Embeddings would need a model, an index, and a warm-up;
   token matching against entity names is a few hundred lines of nothing and is right often enough that
   the LLM's own judgement over a 40-entity shortlist closes the gap. When the wording matches fewer
   than a dozen entities, the list is padded from commonly automated domains (lights, switches,
   presence, sensors, climate, covers, locks…) so "make it cozy at night" still puts the lights in
   front of the model rather than answering "nothing matched".

2. **Draft** — JSON mode, [one prompt](../src/Housekeeper.Core/Prompts.cs). The entity catalog, the
   services this house actually offers, and the user's request are serialised as JSON inside labelled
   blocks, after two complete worked examples of the exact output shape.

3. **Validate** — [`AutomationDrafting.Parse`](../src/Housekeeper.Core/AutomationDrafting.cs). Covered
   in [SECURITY.md](../SECURITY.md); it is the reason this app exists rather than a shell script.

   Steps 2 and 3 are a loop, not a line. A rejected draft goes back to the model with the validator's own
   sentence under `REJECTED_BECAUSE`, up to `Llm.MaxAttempts` times. This is the single thing that makes a
   7B model useful here: told "the draft references light.imaginary, which does not exist", it fixes that
   and returns the rest unchanged, where told only the rules it invents again. Three cases end the loop
   early rather than burning a minute of a slow model: an endpoint that did not answer, an `UNSUPPORTED`
   reply, which is an answer rather than a mistake, and an answer identical to the one just rejected.

   Shape mistakes that carry no ambiguity are absorbed instead of costing an attempt. A single trigger
   written as a bare object rather than a one-element array is normalised on the way out, so Home Assistant
   still only ever sees the array form that was validated.

4. **Deduplicate** — [`DuplicateFinder`](../src/Housekeeper.Core/DuplicateFinder.cs) scores the draft
   against each existing automation: 0.6 × entity Jaccard + 0.25 × trigger-kind overlap + 0.15 × name
   overlap, reported above 0.45. Deterministic, so it costs nothing and can be unit-tested; an LLM
   comparison would be slower, more expensive, and less predictable for no clear gain.

5. **Read back** — [`AutomationNarrator.Describe`](../src/Housekeeper.Core/AutomationNarrator.cs) turns the
   validated config into three lists — when, only if, then — with every entity named the way Home Assistant
   names it. Deterministic, so it describes exactly what was validated rather than what the model meant; a
   second model call to "explain" a draft would cost time and could disagree with the config. Names come
   from [`NameBook`](../src/Housekeeper.Api/NameBook.cs), which remembers the friendly name of every entity
   any read has passed through, so listing proposals never has to ask Home Assistant.

6. **Confirm** — a separate request. Only [`CreateAutomationAsync`](../src/Housekeeper.Api/HomeAssistantClient.cs)
   writes.

Before any of that, the composer asks `GET /api/suggestions` for a few example requests.
[`Suggestions.For`](../src/Housekeeper.Core/Suggestions.cs) is pattern matching over the entity list — a
motion sensor and a light in the same area, a door contact, a lock, a person — and writes each example with
the user's own entity names. A house with no locks gets no example about locks. Best effort: an unreachable
Home Assistant leaves the box with its placeholder.

Step 4 used to be the slowest part of a draft after the model: every existing automation's config was
fetched on every draft, and the state list was fetched a second time to find their ids. Now the automation
entities carry their config id, so the entity list the draft already holds is enough to know what to read.
The service list is kept for five minutes. The automation configs are kept against each automation's id,
the moment its entity last changed, and the Home Assistant address, for up to an hour: an automation added,
removed, switched on or off, or edited -- Home Assistant reloads an edited automation, which is a new state
for its entity -- changes the key and is read at once, and an unchanged house is not read again. That
matters because a sweep is one request per automation and Home Assistant re-parses its whole automations
file for every one. A sweep with gaps in it (a config that timed out) is used but not kept, and creating
an automation here invalidates the cache outright.

Between 4 and 5 the user can say "not quite". `RefineAsync` re-runs steps 1–4 with the previous draft
and the objection in the prompt as labelled data, and the shortlist widened to whatever the feedback
names. The new draft goes through the same validator; only if it passes is the old one marked
`Superseded`, so a bad refinement never costs the draft you already had.

Failures at any step are persisted as a `Failed` proposal carrying the reason, so the dashboard can
explain what went wrong instead of showing an empty list.

## The scanning path

[`AnomalyScanner`](../src/Housekeeper.Core/AnomalyScanner.cs) polls `/api/states`, which is one request
regardless of house size. A state is stored only when its `last_changed` is newer than what is already
held, so a stable sensor costs nothing.

While an entity's stored history reaches back less than three weeks, the scan also asks Home Assistant's
recorder what it already holds for it, through `GET /api/history/period`, once per process. A fresh install was blind for a day or two while it learned what
normal looked like, when the recorder had ten days of that on disk the whole time. A few hundred first-seen
entities are read per scan, so a large house is filled in over a handful of scans rather than in one slow
first one. [`Backfill.Thin`](../src/Housekeeper.Core/Backfill.cs) shapes the reply into what the sample
table would have held anyway: every non-numeric transition, and numeric readings at two per hour, the same
density the outlier detector reads them at. Inserting is idempotent, so an install that has been watching for a week gains the fortnight before it
started and nothing it already held is touched. Each entity is asked about once per process; a recorder that
cannot be read is tried again next scan.

Polling replaced a WebSocket client on purpose. Real-time streaming buys nothing for a detector that
runs every five minutes, and it costs a reconnect loop, a backpressure channel, and a protocol codec.

Three detectors, all pure functions over `(entity, history, options, now)`:

| Detector | Fires when |
|---|---|
| `StuckState` | Current state has been held longer than `StuckMultiplier` × the longest it ever was before, and past a floor. The freezer door. When enough previous periods began in the same four-hour band of the day, only those are the baseline, so a door that is open for an hour at dinner and seconds at breakfast is judged against the right normal. Once the history reaches back three weeks, the same part of the week — weekday or weekend, in the house's own time zone — narrows it again, so a Saturday morning's hour-long opening does not set the bar for a Wednesday. |
| `NumericOutlier` | Robust z-score (MAD × 1.4826) past `OutlierThreshold`, against the same time of day and, past three weeks, the same part of the week when those slices are baselines of their own. Falls back to standard deviation, then stays silent, rather than dividing by a flat history. And the reading has to have stayed out for `MinimumExcursion`, dated from the stored readings in between: one reading is a kettle, and every kettle used to be a card that opened on one scan and closed on the next. A brief dip does not end the run, but a normal reading more than a fifth of the wait before the next reading out does, so two kettles ten minutes apart are two kettles. Closing needs the same evidence in reverse — back inside the range and stayed there for the wait — so one poll that happened to read normal cannot close a card that the next poll would reopen. |
| `Unavailable` | `unavailable`/`unknown` past a floor, on a sensor that was healthy ≥90% of its history. |
| `MissingEntity` | An automation Housekeeper created references an entity id Home Assistant no longer reports. Not a detector over history: each scan checks every `Created` proposal against the live entity list. Promoting it re-drafts the original sentence against what exists now. |

The same pass notices the opposite case: an automation created here that has since been deleted in Home
Assistant's own editor. Its proposal becomes `Removed` and any open finding against it is dismissed, so
nothing keeps nagging about an automation that no longer exists. Two guards keep that honest — a freshly
created automation gets ten minutes to appear in the state list, and the conclusion is only drawn when the
list holds automations at all, because if the automation integration itself failed to load, every automation
would be absent and none of them deleted.

### Concerns

[`Concerns`](../src/Housekeeper.Core/Concerns.cs) is how the user points the scanner. A concern is a
sentence, stored with the entities it resolved to and a rule: above or below a value, a state held for
longer than a window, gone unavailable, or nothing more specific than "pay attention". Three things follow
from one in every scan. The entities it names are judged with every bar lowered by a third
(`Concerns.Sharpen`), their findings are lifted two points and tagged with the concern's words
(`Concerns.Prioritise`), and a rule is checked directly against the current state and the recent history
(`Concerns.Evaluate`), raising a `Concern` finding above everything else when it fires. A concern finding
closes when the entity was looked at and the rule did not fire, or when the concern is removed.

The model's part is the reading, done when the concern is added; a scan never re-reads a concern the model
has already had its say on. When the model could not be asked — not chosen, down, or answering unusably —
the concern is saved matched by name and marked provisional, with the reason kept apart from what was
matched. Each scan tick then asks about at most one provisional concern, after the scan rather than before
it so a slow model cannot delay the scan, least recently tried first, with a gap that doubles per failed try
up to an hour. A model that was down is simply tried again later; one that answers unusably three times has
the concern left matched by name, no longer provisional, with a note saying so. A **Read again** button
asks on demand in either case, and a model that read the concern and named nothing is an answer, not a
wait. A concern the model has already read keeps that reading when a later attempt fails, so asking again
while the model is down cannot trade a rule for a name match.
[`ConcernService`](../src/Housekeeper.Core/ConcernService.cs) shows it the concern and a shortlist of the
house and asks for entity ids, a kind and a value; only ids that exist survive, exactly as with a draft. If
the model is not configured or does not answer, `Concerns.Match` stands in — the concern's words against
entity names, areas and devices, plus a small table of what words like "power", "door" or "leak" mean in
device classes — so a concern is never refused, only read less cleverly.

Every scan also closes what has passed. The detectors describe a moment, so each pass collects the
conditions still true and marks every open finding missing from that set `Resolved`. Only entities the scan
actually saw are judged, so an entity Home Assistant stopped reporting keeps its finding rather than losing
it to a gap in the state list. A resolved finding reopens the moment its condition returns, without waiting
out `RedetectAfter` — that window is for something a person chose to silence, and nobody silenced this.

Silence alone is never evidence, but there is more positive evidence than "the condition ended". A finding
is also closed, with the reason written into its evidence as `closed_because`, when Housekeeper would never
raise it again as things stand: the entity has left the watch list, or Home Assistant now classes it as a
setting or a diagnostic reading, or a running total, or another card now speaks for it. A numeric finding is
closed once the sensor has reported a newer reading — the card was quoting a value the sensor no longer
shows — and an unavailable one the moment the entity reports at all, whatever its history. The dashboard
says the reason rather than "back to normal", because a sensor that was merely reclassified was not.

One event is one card. [`AnomalyScanner.Collapse`](../src/Housekeeper.Core/AnomalyScanner.cs) groups a
scan's findings twice: by device and kind, so a plug's power and current are one card, and — for
unavailability — by moment, so the dozen entities a hub takes offline together are one card even when they
share no device Home Assistant knows about. Both groupings prefer a card the user already has, so a sibling
that crosses the bar a scan later joins the existing finding and its own row is closed as covered, rather
than opening beside it for ever.

A card also says when an automation the user already has fires on it
([`Coverage`](../src/Housekeeper.Core/Coverage.cs)), before offering to draft another. The claim is narrow
on purpose, because a user told they are covered dismisses the finding. `AutomationInspector` keeps each
trigger on its own -- its kind, the entity ids it names, `to`, `not_to`, `for`, `above`, `below`,
`attribute`, whether it is enabled -- and a trigger counts only when it is switched on, in an automation
whose entity is `on`, names the finding's entity itself (a device trigger names it by the registry id, which
the entity registry resolves), and would fire on what was found: a line the reading is already past on that
side, the held state with a `for:` that has run out, a `to:` naming the state the entity went quiet in, or
the shape a concern's own rule asks for. Nothing is widened through devices or areas, templates and
blueprints claim nothing, conditions make it a weaker claim, and a card standing for several entities is
covered only when all of them are. It is worked out when `GET /api/anomalies` is asked, from the
automations and entity states the last scan published, so it never names an automation switched off or
deleted since. The scan reads the automations only while an open finding could use them, once per scan at
most (shared with the routine search), backs off an hour after a failure, gives the sweep a minute in all,
keeps the last good read through a Home Assistant restart, and keeps an automation that could not be read
this time at its last reading while its entity has not changed.

Severity is the same scale for every kind — 1 at the bar, one more for every doubling past it, capped at
8 — and a missing-entity finding sits above the cap. A plain ratio grew without limit, and two of the three
bars are durations that grow on their own: a sensor dead for a month scored sixty times over and buried a
boiler at twice its usual pressure. The dashboard reads findings by kind, most actionable first — your own
automations that no longer work, then stuck states, then readings out of range, then what has gone quiet —
and by severity within each.

Each scan also expires finished proposals — rejected, failed, superseded, removed — older than
`Storage.KeepDecidedFor`. Drafts wait for a decision and created ones describe live automations, so both are
kept indefinitely.

The detectors do not share one bar, and it matters: the unavailable detector wants `MinimumSamples`
recorded changes of any kind, while the stuck-state detector wants only a handful of completed stretches in
the state an entity holds now, and so can speak about a door long before twelve changes exist.
`AnomalyDetection.CanJudge` is assembled from those preconditions rather than approximating them with a
sample count, and the scan evaluates it over the history the detectors just read. That is what the dashboard
reports as "ready to judge", so the figure cannot disagree with what the detectors were able to do.

Each produces a `SuggestedRequest` in plain English naming the entity id verbatim, so promoting a
finding re-enters the normal drafting path rather than needing a second code path. There is one
pipeline from sentence to automation, and anomalies simply write the sentence for you.

A finding never notifies. Home Assistant owns notification, and once you promote a finding into an
automation, HA owns that too.

That silence is the design working, and it is indistinguishable from a broken install unless the app says
so. `GET /api/insight` reports what is watched, how many changes are held, how many entities hold enough of
them to be judged against `MinimumSamples`, and what the last scan did — including a scan that threw, which
would otherwise look identical to a quiet one. The dashboard renders it as four figures and a meter, so
"nothing found" comes with the reason: most entities have not changed often enough yet.

### Routines

The detectors say when the house is doing something unusual. [`Habits`](../src/Housekeeper.Core/Habits.cs)
is the other half of watching a house: noticing what the people in it do the same way every day, and
offering to take it over. Once an hour the scanner reads weeks of transitions for every light, switch, fan,
cover, lock, media player and vacuum, and for everything that could cue one — motion, occupancy, doors,
people arriving and leaving, and other things being switched — and looks for two shapes.

A *cue*: within three minutes of the pantry motion sensor firing, the pantry light goes on, nearly every
time. The bar is three-sided — how many times, on how many different days, and how reliably, where reliably
means the share of the times the cue happened *with the light still off* that the light followed. Motion
while the light is already on asks nothing of anyone, so it is neither a hit nor a miss. Each routine is
tried under a set of conditions, from the plainest to the most particular — always; after dark or in
daylight, read from `sun.sun`'s stored state; weekdays or weekends; a four-hour band of the day — and
offered under the plainest one that makes it reliable, because the plainer the condition, the simpler the
automation. A *clock*: the porch light goes off at about ten past eleven on most days, or most weekdays,
or most weekend days, with "every day" claimed only when both halves of the week carry it.

Several rules came from running it over a real house and from review. A cue in another room, or in no
known room, has to clear a higher bar — twice the occasions, more days, a higher share — because "the kids'
room motion sensor fires and the pantry light goes off" is somebody walking through the house, not a
routine; a person arriving or leaving is exempt, since a person is wherever they are. Only the strongest cue
is offered for any one thing, because four sensors that all see the same person leave the same room are
four views of one routine; among equals, a cue that is something starting (movement seen, a door opened)
beats its ending, so a motion sensor that clears ten seconds after it fired is not read as the cue. Every
cue transition inside the window counts, not only the last one. A light that is also a switch — two entity
ids that change as one — is one thing, kept under the id that says what it is. A clock routine has to beat
what random switching would produce for a window chosen after the fact — four standard deviations and
twice the expectation clear of it — because the busiest hour of a light used twenty times a day at random
still holds an event on most days. And a routine with machine punctuality — a response within five seconds
to the second, or the same minute every day — is an automation Housekeeper cannot see (a device trigger, a
timer) rather than a person, and is set aside and counted rather than offered. A cue with weeks more history
than its effect is not drowned in misses from before the effect existed: a moment when the effect's state
is unknown asks nothing of anyone.

Whatever an existing automation already does is left out: a routine whose cue and effect both appear in an
automation with a state trigger, or whose effect appears in one with a time or sun trigger, is reported
back as already automated rather than offered, and an open or promoted offer closes with that reason the
hour after the user builds it. An automation's reach includes the entities in the areas and on the devices
it targets and the entity ids inside its blueprint inputs (`AutomationInspector`), since editor-built
automations rarely name entities directly. That is a far looser test than the one a finding's card uses to
say an automation already fires on it, and on purpose: here a mistake costs an offer the user might have
wanted, where on a finding it would cost the warning. A scan on which Home Assistant lists no automations at all, on a
house that had them, is a restart in progress rather than a house that deleted them, and the search waits.

A routine is raised as a finding of kind `Habit` — same triage, same one-click draft through the normal
pipeline, its `SuggestedRequest` written in the entity ids the drafter resolves — but listed apart, never
counted as serious, and never re-offered once put away: the dismissal row is never pruned. The search
returns everything that held up and the scanner applies the cap of thirty with the user's own put-aways in
hand, so a put-away routine takes no slot and a routine past the cap is never closed as "not held up". A
routine whose effect or cue leaves the watch list closes saying so. The top three also lead the composer's
examples, so the first thing a new user is offered to draft is something they have already shown they
want, and the dashboard and the Noticed page say when the last search ran, over how many entities, and
what it found, so "none yet" comes with the reason. Local times come from Home Assistant's own time zone,
read from `/api/config` and never cached on a failed read, because the container is pinned to UTC and
"about a quarter to seven" is not a UTC fact.

### Dismissals teach the detectors

A finding remembers how many times the user dismissed it. After the quiet period, the same finding comes
back only if it is further past its bar — half a doubling of severity per dismissal — and the third
dismissal silences it for good; the row is never pruned, so the silence holds. The card says when a finding
has come back over a raised bar, and what the next dismissal does. This is the cheapest honest form of
"learning from the user": the detector does not change its idea of normal, it changes how sure it has to
be before it interrupts again.

## Configuration is state, not startup arguments

Every setting is editable at `/settings`, and the page is generated from
[`SettingsCatalog`](../src/Housekeeper.Api/SettingsCatalog.cs) — one list of fields with a label, a kind, a
range and a getter. Adding an option there makes it appear in the API and the UI with validation and a
source badge, without touching the page.

Configuration is layered: `appsettings.json`, then an optional file, then environment variables, then
`settings.json` written by the UI. The UI layer is last because a value someone typed into the settings page
should be the value that runs. Only keys that differ from what they would inherit are stored, so an
environment variable still governs every field nobody has touched, and a reset is just a delete.

Nothing captures its options at construction. `ISettingsProvider.Current` is re-read per operation and
rebound on configuration reload, and the two HTTP clients build their address, timeout and credentials per
request. That is what makes changing the Home Assistant address or the model take effect on the next request
rather than the next restart. Three settings genuinely cannot work that way — the bind address, the port and
the database path — and they are flagged as needing a restart in the catalog and in the UI.

Secrets sit outside the configuration system entirely, in
[`SecretStore`](../src/Housekeeper.Api/SecretStore.cs), so they cannot be read back out through a
configuration endpoint by accident. See [SECURITY.md](../SECURITY.md) for the cost of storing them.

## Storage

Three tables in SQLite via `Microsoft.Data.Sqlite` — no ORM. At this size an ORM costs a dependency, a
migration system, and a layer of indirection to save perhaps eighty lines of SQL.

| Table | Holds |
|---|---|
| `proposals` | Drafts and their outcome. Lists stored as JSON columns; they are read and written whole. A dismissed one is hidden from the dashboard but still read by the supervision pass, so hiding a card never stops the automation behind it being watched. |
| `samples` | `(entity_id, changed_utc)` primary key, `WITHOUT ROWID`, so per-entity history reads are a clustered range scan and the per-entity cap runs over the index rather than sorting. Pruned to `Scan.History` on every scan, whatever is being watched. |
| `anomalies` | Unique on `dedup_key`, so a repeated scan updates one row rather than growing a list. |

Schema versioning is `PRAGMA user_version`.

## Home Assistant is the runtime

Housekeeper holds no alert state, no cooldowns, no delivery retries, no rule engine. A confirmed
automation runs entirely inside Home Assistant, where it is visible in the UI, traceable, editable, and
deletable with the tools you already use. This is the single decision the design turns on, and it is
what keeps the codebase at roughly 5,700 lines.

## Known gaps

- **`SuggestedRequest` phrasing is templated.** Good enough for the three detector kinds; it would not
  survive many more.
- **Duplicate detection cannot see semantics.** Two automations that achieve the same thing through
  different entities will not be flagged.
- **Service arguments are not checked.** The service name is verified against Home Assistant's list, but
  the `data` block it is called with is not, so a real service called with a nonsensical payload still
  reaches the confirmation screen. Home Assistant refuses it on write, and the failure is recorded.
- **Supervision after creation is shallow.** The scanner notices when an entity an automation depends on
  disappears, and when the automation itself is deleted, but not when it is edited or disabled in Home
  Assistant, or when it simply stops firing. Reading back its last-triggered time would close that.
- **Time-of-day baselines are in UTC.** Bands are consistent relative to each other, so detection is
  unaffected, but the "at this time of day" wording in a finding shows UTC hours.
- **Only creation is supported.** A refinement produces a new draft; there is no editing or deleting of
  automations already written. Corrections after confirm happen in Home Assistant.
- **Stored secrets are plain text on disk.** Configuring from a browser means the value has to be persisted
  somewhere the process can read unattended. The environment variables remain available for anyone who would
  rather it never touched disk.
