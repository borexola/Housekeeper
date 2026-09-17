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
entities carry their config id, so the entity list the draft already holds is enough to know what to read,
and the configs and the service list are each kept for five minutes. The automations cache is keyed on the
set of ids, so one added or deleted in Home Assistant is noticed at once; only an edit waits for the entry
to age out, and creating one here invalidates it outright.

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
| `NumericOutlier` | Robust z-score (MAD × 1.4826) past `OutlierThreshold`, against the same time of day and, past three weeks, the same part of the week when those slices are baselines of their own. Falls back to standard deviation, then stays silent, rather than dividing by a flat history. |
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

The model's part is the reading, done once when the concern is added and never during a scan.
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
