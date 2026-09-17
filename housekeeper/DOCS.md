# Housekeeper

Describe an automation in plain English. Housekeeper finds the entities you meant, has a local model draft
a real Home Assistant automation, checks every entity id against your actual house, warns you if you already
have something similar, and shows you the YAML. Nothing is written until you press confirm.

It also watches the entities you choose and tells you when something looks off, such as a freezer door that
has been open far longer than it ever normally is. A finding never notifies anyone; it waits in a list until
you dismiss it or turn it into an automation.

## Installing

1. Add this repository in **Settings → Add-ons → Add-on store → ⋮ → Repositories**.
2. Install **Housekeeper**, then **Start**.
3. Open it from the sidebar.

There is nothing to configure to get going. The add-on borrows the Supervisor's own credentials, so you do
not need to create a long-lived token.

## You still need a model

Housekeeper does not include one. Point it at whatever you already run:

- **Ollama** on the same machine or another one. Install the *Ollama* add-on if you have one, or run it on a
  desktop and use that address.
- **Any OpenAI-compatible endpoint**, including hosted ones, by choosing the OpenAI provider and adding a key.

Set this on the **Settings** page inside Housekeeper, not in the add-on options. Use **Test connection**
there: it tells you whether the server answered and whether your model is actually pulled.

Something in the 7B range following instructions well is enough. Smaller models tend to invent entity ids,
which the validator catches and rejects, so you see the failure rather than a broken automation.

## Add-on options

Almost everything lives on Housekeeper's own settings page, which persists across restarts and upgrades.
Only two things are here, because they have to be set before the app starts.

### `home_assistant_token`

Leave this empty unless you have a reason not to.

Empty means the add-on talks to Home Assistant through the Supervisor using its own credentials. If your
Home Assistant refuses to save an automation with a permissions error, put a long-lived access token from
an **admin** user here instead; the add-on will then talk to Home Assistant directly.

### `log_level`

`trace`, `debug`, `info`, `warning` or `error`. `info` is the default and says what each scan did.

## Everything else

Open Housekeeper from the sidebar and go to **Settings**. Every option is there: which Home Assistant to use,
the model and its limits, which entities are watched and how sensitive the detectors are. Each field shows
where its current value came from and what it would fall back to. Most changes apply immediately.

Nothing is watched until you say so. Add at least one pattern under **Watch**, such as `binary_sensor.*`, and
give it a day or two of history before expecting findings.

## What it can do to your house

One thing: create an automation, after you press confirm on a specific draft. It never calls a service, never
turns anything on or off, and never edits or deletes an automation. Anything it creates appears in Home
Assistant's own automation editor, where you can trace, disable or delete it normally.

Housekeeper is not a life-safety system. Do not rely on it in place of smoke, CO, or leak alarms.

## Data

The database, your settings and any secrets you type live in the add-on's `/data` volume, so they survive
restarts and updates. A secret typed into the settings page is stored there in plain text, readable by
anything with access to that volume.

## Access

The add-on is reachable only through the sidebar. Its port is not published to your network, and the only
thing allowed to talk to it without a token is the Supervisor, which has already checked who you are.
