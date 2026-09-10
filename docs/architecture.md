# Architecture

Two projects. The split is the only structural rule, and it exists so the interesting logic can be
tested without a web host, a database, or a network.

```
HearthSense.Core          pure logic + ports          no HTTP, no SQL, no ASP.NET
        ▲
        │ implements
HearthSense.Api           adapters + host             Home Assistant, LLM, SQLite, endpoints, UI
```

`Core` declares four ports — `IHomeAssistant`, `ILlmClient`, `IStore` and `ISettingsProvider` — and `Api`
supplies all four. Every decision that could be wrong lives in `Core` behind a pure function.

## The drafting path

[`ProposalService.DraftAsync`](../src/HearthSense.Core/ProposalService.cs) is the whole product in one
method:

1. **Shortlist** — [`EntityIndex.Shortlist`](../src/HearthSense.Core/EntityIndex.cs) ranks entities
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

2. **Draft** — one call, JSON mode, [one prompt](../src/HearthSense.Core/Prompts.cs). The entity catalog
   and the user's request are serialised as JSON inside labelled blocks.

3. **Validate** — [`AutomationDrafting.Parse`](../src/HearthSense.Core/AutomationDrafting.cs). Covered
   in [SECURITY.md](../SECURITY.md); it is the reason this app exists rather than a shell script.

4. **Deduplicate** — [`DuplicateFinder`](../src/HearthSense.Core/DuplicateFinder.cs) scores the draft
   against each existing automation: 0.6 × entity Jaccard + 0.25 × trigger-kind overlap + 0.15 × name
   overlap, reported above 0.45. Deterministic, so it costs nothing and can be unit-tested; an LLM
   comparison would be slower, more expensive, and less predictable for no clear gain.

5. **Confirm** — a separate request. Only [`CreateAutomationAsync`](../src/HearthSense.Api/HomeAssistantClient.cs)
   writes.

Between 4 and 5 the user can say "not quite". `RefineAsync` re-runs steps 1–4 with the previous draft
and the objection in the prompt as labelled data, and the shortlist widened to whatever the feedback
names. The new draft goes through the same validator; only if it passes is the old one marked
`Superseded`, so a bad refinement never costs the draft you already had.

Failures at any step are persisted as a `Failed` proposal carrying the reason, so the dashboard can
explain what went wrong instead of showing an empty list.

## The scanning path

[`AnomalyScanner`](../src/HearthSense.Core/AnomalyScanner.cs) polls `/api/states`, which is one request
regardless of house size. A state is stored only when its `last_changed` is newer than what is already
held, so a stable sensor costs nothing.

Polling replaced a WebSocket client on purpose. Real-time streaming buys nothing for a detector that
runs every five minutes, and it costs a reconnect loop, a backpressure channel, and a protocol codec.

Three detectors, all pure functions over `(entity, history, options, now)`:

| Detector | Fires when |
|---|---|
| `StuckState` | Current state has been held longer than `StuckMultiplier` × the longest it ever was before, and past a floor. The freezer door. When enough previous periods began in the same four-hour band of the day, only those are the baseline, so a door that is open for an hour at dinner and seconds at breakfast is judged against the right normal. |
| `NumericOutlier` | Robust z-score (MAD × 1.4826) past `OutlierThreshold`. Falls back to standard deviation, then stays silent, rather than dividing by a flat history. |
| `Unavailable` | `unavailable`/`unknown` past a floor, on a sensor that was healthy ≥90% of its history. |
| `MissingEntity` | An automation HearthSense created references an entity id Home Assistant no longer reports. Not a detector over history: each scan checks every `Created` proposal against the live entity list. Promoting it re-drafts the original sentence against what exists now. |

Each produces a `SuggestedRequest` in plain English naming the entity id verbatim, so promoting a
finding re-enters the normal drafting path rather than needing a second code path. There is one
pipeline from sentence to automation, and anomalies simply write the sentence for you.

A finding never notifies. Home Assistant owns notification, and once you promote a finding into an
automation, HA owns that too.

## Configuration is state, not startup arguments

Every setting is editable at `/settings`, and the page is generated from
[`SettingsCatalog`](../src/HearthSense.Api/SettingsCatalog.cs) — one list of fields with a label, a kind, a
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
[`SecretStore`](../src/HearthSense.Api/SecretStore.cs), so they cannot be read back out through a
configuration endpoint by accident. See [SECURITY.md](../SECURITY.md) for the cost of storing them.

## Storage

Three tables in SQLite via `Microsoft.Data.Sqlite` — no ORM. At this size an ORM costs a dependency, a
migration system, and a layer of indirection to save perhaps eighty lines of SQL.

| Table | Holds |
|---|---|
| `proposals` | Drafts and their outcome. Lists stored as JSON columns; they are read and written whole. |
| `samples` | `(entity_id, changed_utc)` primary key, `WITHOUT ROWID`, so per-entity history reads are a clustered range scan. Pruned to `Scan.History` every scan. |
| `anomalies` | Unique on `dedup_key`, so a repeated scan updates one row rather than growing a list. |

Schema versioning is `PRAGMA user_version`.

## Home Assistant is the runtime

HearthSense holds no alert state, no cooldowns, no delivery retries, no rule engine. A confirmed
automation runs entirely inside Home Assistant, where it is visible in the UI, traceable, editable, and
deletable with the tools you already use. This is the single decision the design turns on, and it is
what keeps the codebase at roughly 5,700 lines.

## Known gaps

- **`SuggestedRequest` phrasing is templated.** Good enough for the three detector kinds; it would not
  survive many more.
- **Duplicate detection cannot see semantics.** Two automations that achieve the same thing through
  different entities will not be flagged.
- **Supervision after creation is shallow.** The scanner notices when an entity an automation depends
  on disappears, but not when the automation is edited or disabled in Home Assistant, or when it simply
  stops firing. Reading back the automation's own state and last-triggered time would close that.
- **Time-of-day baselines are in UTC.** Bands are consistent relative to each other, so detection is
  unaffected, but the "at this time of day" wording in a finding shows UTC hours.
- **Only creation is supported.** A refinement produces a new draft; there is no editing or deleting of
  automations already written. Corrections after confirm happen in Home Assistant.
- **Stored secrets are plain text on disk.** Configuring from a browser means the value has to be persisted
  somewhere the process can read unattended. The environment variables remain available for anyone who would
  rather it never touched disk.
