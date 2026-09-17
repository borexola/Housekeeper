# Contributing to Housekeeper

Thanks for your interest. Housekeeper is a small, focused codebase and we keep contribution friction
low while holding quality high.

## Ground rules

1. **Exactly one call writes to Home Assistant.** `HomeAssistantClient.CreateAutomationAsync`, reached
   only through `POST /api/proposals/{id}/confirm`. Nothing writes on a timer, from the scanner, or as
   a side effect of drafting.
2. **The model proposes; the validator decides.** Every change to `AutomationDrafting` must keep the
   guarantees in [SECURITY.md](SECURITY.md): entities must exist, `device_id`/`area_id` are refused,
   only allow-listed keys are written. Add a test for any new rule.
3. **Findings never notify.** An anomaly is a suggestion in a list. Notification belongs to the
   automation the user confirms, running inside Home Assistant.
4. **Local-first, minimal data.** No cloud calls and no telemetry. Outbound traffic goes only to the
   configured Home Assistant and LLM endpoints.
5. **Core stays pure.** `Housekeeper.Core` must not reference ASP.NET, `Microsoft.Data.Sqlite`, or
   `HttpClient`. It declares the ports; `Housekeeper.Api` implements them.
6. **Settings are read, not captured.** Take `ISettingsProvider` and read `Current` per operation. Anything
   that binds options once in a constructor silently stops honouring the settings page.
7. **No TODOs in critical paths.** Ship the working path or do not ship it.

## Development workflow

```bash
dotnet build Housekeeper.slnx -c Release -warnaserror
dotnet test  Housekeeper.slnx -c Release
dotnet format Housekeeper.slnx --verify-no-changes   # formatting gate used by CI
```

To run it: press F5 in Visual Studio, or `dotnet run --project src/Housekeeper.Api`. Either way the launch
profile keeps the database, saved settings and saved secrets in `.localdata/` at the repository root, which
is ignored by git — nothing a local run writes should ever be committable. It starts on
<http://127.0.0.1:5080> with no token needed; add a Home Assistant token on the settings page.

This is for working on Housekeeper. The ways to actually run it are the Home Assistant add-on and the
Docker image, both Linux containers.

- Target `net10.0`. Use the .NET 10 SDK.
- Time-dependent logic takes the injected `System.TimeProvider` (never `DateTime.UtcNow` or a bare
  `Task.Delay`) so tests are deterministic with `FakeTimeProvider`.
- Add or update tests for every behaviour change. Placeholder tests don't count.
- Line endings are LF, enforced by `.editorconfig` and the format gate.

## Layout

| | |
|---|---|
| `src/Housekeeper.Core` | Pure logic and the three ports (`IHomeAssistant`, `ILlmClient`, `IStore`). |
| `src/Housekeeper.Api` | Adapters and host: Home Assistant REST, LLM client, SQLite, endpoints, dashboard, scan worker. |
| `tests/Housekeeper.Tests` | Unit tests over Core, store tests against real SQLite, and API tests that boot the real host with fake adapters. |

See [docs/architecture.md](docs/architecture.md) for how the pieces fit.

## Adding things

- **A detector:** add a pure function to `AnomalyDetection` with the `(entity, history, options, now)`
  shape, wire it into `Detect`, give it a `DedupKey` and a `SuggestedRequest` that names the entity id
  verbatim, and cover it in `AnomalyDetectionTests`.
- **An LLM provider:** `LlmClient` already speaks Ollama and OpenAI-compatible chat completions. A new
  shape means a new request body and a new branch in `Extract`, plus a test in `LlmClientTests`.
- **A configuration key:** add it to the options class in `Core/Options.cs`, validate it in
  `HousekeeperOptions.Validate`, then add one entry to `SettingsCatalog.Fields` in the API project. That
  entry is what puts it on the settings page with the right control, range and source badge — there is no
  UI code to write. Mark it as needing a restart only if it genuinely cannot be re-read at runtime. Document
  it in [docs/configuration.md](docs/configuration.md) and `examples/appsettings.sample.json`.

## Pull requests

- One concern per PR.
- Include the test evidence (commands run and results) in the description.
- Update `CHANGELOG.md` under *Unreleased*.
- For anything touching the write path or the validator, call it out and reference [SECURITY.md](SECURITY.md).

## Code style

Governed by `.editorconfig`. Run `dotnet format` before pushing. Prefer composition over inheritance
and avoid speculative abstractions.
