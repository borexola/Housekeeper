# Security Policy

HearthSense writes automations into your Home Assistant instance. That is a meaningful privilege, and
this document is honest about what it costs and what contains it.

## Reporting a vulnerability

Do **not** open a public issue. Email the repository owner privately. Expect acknowledgement within
seven days and coordinated disclosure.

## The token is admin-scoped, and that matters

Creating an automation uses `POST /api/config/automation/config/{id}`. This is the endpoint Home
Assistant's own automation editor calls. Two consequences you should weigh before installing:

1. **It requires an admin user's token.** Non-admin tokens get 401/403 on `/api/config/*`. There is no
   narrower scope available, so the token HearthSense holds can do anything an admin can. As an add-on it
   uses the Supervisor's own credentials rather than a token you create, which is one fewer secret to store
   but grants the same reach.
2. **It is not a documented, stability-guaranteed API.** It can change between Home Assistant releases.
   It is isolated in a single method — [`HomeAssistantClient.CreateAutomationAsync`](src/HearthSense.Api/HomeAssistantClient.cs) —
   so when it breaks, one place needs fixing.

Mitigate by giving HearthSense its own dedicated admin user rather than sharing yours, so the token can
be revoked without disrupting anything else, and its actions are attributable in the logbook.

## What constrains the model

The LLM proposes; it never acts. Between its output and your house:

- **Entity existence.** Every `entity_id` anywhere in a draft is checked against the live entity list.
  Unknown ones reject the whole draft and name what was invented.
- **No unverifiable targets.** `device_id` and `area_id` are refused outright — nothing can be checked
  about them, and a wrong value silently actuates the wrong device.
- **Key allow-listing.** The config written to Home Assistant is rebuilt from only `alias`,
  `description`, `triggers`, `conditions`, `actions`, and `mode`. Anything else the model emits — an
  injected `id`, an `initial_state` — is dropped, not forwarded.
- **Bounds.** 16 KB of output, 12 levels of nesting, 60 entities, a fixed set of `mode` values.
- **A human confirms.** Drafting and writing are separate requests. Nothing is written on a timer, by a
  background job, or as a side effect of scanning. Refining a draft produces a new draft through the
  same validator; it never touches the automation you already confirmed.
- **Entity names and states are data.** They are serialised as JSON inside a labelled block, and the
  system prompt states that their contents are never instructions. A device named
  `"ignore previous instructions"` is a string, not a turn in the conversation.

## Network and access

- **Loopback by default.** The API binds `127.0.0.1`. Binding anywhere else with no API token — from the
  environment or the settings page — is a startup failure, not a warning: the app refuses to expose
  automation writing unauthenticated.
- **Bearer token, compared in constant time, resolved per request** so rotating it takes effect at once.
  `/health` and the two page shells load without one so a browser can bootstrap; every data, settings and
  write endpoint requires it.
- **Outbound calls only to what you configured**: your Home Assistant, and your LLM endpoint. There is
  no telemetry and no third-party service.
- **TLS via a reverse proxy.** HearthSense speaks plain HTTP; terminate TLS in front of it if you expose
  it beyond loopback.

- **Home Assistant ingress.** As an add-on the port is not published at all, and one address — the
  Supervisor at `172.30.32.2` — is allowed in without a token, because Home Assistant has already
  authenticated whoever is on the other end. The check is on the connection's own source address, never on a
  header, so nothing a caller sends can claim to be ingress. Any other address still needs the token, and
  with no token set it is simply refused.

Note that on loopback there is deliberately no authentication, which is standard for a local tool but
means any process on that machine can reach the API. If that is not an acceptable assumption for your
host, bind elsewhere and set a token.

## Secrets

A secret can come from an environment variable, from `{NAME}_FILE` for Docker secrets, or from the settings
page. Whichever way it arrives, it is never written to the database, never returned by any endpoint, and
never included in an error message. The settings page reports only whether a secret is set and where it came
from. `/api/status` reports which model and Home Assistant URL are configured, never credentials.

**A secret typed into the settings page is stored on disk in plain text**, in `secrets.json` in the data
directory. This is a deliberate trade for being able to configure the whole application from a browser, and
it is worth being clear about:

- The file is created owner-read/write only (`0600`) on Linux and macOS. Windows has no equivalent mode, so
  there the containing directory's ACL is the only control.
- It is plain text. Encrypting it would need a key, and a key stored next to the thing it encrypts protects
  nobody. Anything with read access to the data directory can read the token.
- In Docker the data directory is a named volume, readable by root on the host like any other volume.
- If you would rather it never touched disk, keep using the environment variables and leave the secret
  fields on the settings page empty. Nothing is written unless you type it there.

Because a stored secret takes precedence over the environment, a token corrected in the UI is the one that
gets used. Clearing it falls back to the environment variable again.

## Changing settings is an administrative action

The settings API can point HearthSense at a different Home Assistant, a different model endpoint, and can
store credentials. It sits behind the same bearer token as everything else, and on a loopback binding behind
the same "any process on this machine" assumption as the rest of the API. Treat reaching the settings page as
equivalent to holding the Home Assistant token.

Two guards exist because getting them wrong is unrecoverable from the UI itself: the service refuses to save
a non-loopback bind address while no API token is set, and refuses to clear the API token while already bound
to one.

## No device control outside automations

HearthSense calls no Home Assistant services itself. The only actuation that ever happens is by an
automation you reviewed and confirmed, running inside Home Assistant under its own engine — which means
you can see it, trace it, disable it, and delete it with the tools you already use.

## Dependency scanning

CI runs `dotnet list package --vulnerable --include-transitive` on every push. The runtime dependency
surface is deliberately small: ASP.NET Core, `Microsoft.Data.Sqlite`, and the OpenAPI package.
