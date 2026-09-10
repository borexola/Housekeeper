namespace HearthSense.Api;

/// <summary>
/// The settings screen. Every control is generated from the schema the server sends, so a new option in
/// <see cref="SettingsCatalog"/> appears here without touching this file. All server values are written with
/// textContent or assigned to input values, so nothing from Home Assistant or the model can inject markup.
/// </summary>
internal static class SettingsPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>HearthSense settings</title>
<style>
""" + Ui.Styles + """
  body { padding-bottom: 84px; }
  main { max-width: 1000px; }

  .card > .card-head { margin-bottom: 4px; }
  .card > .card-head h3 { font-size: 15px; }
  .card > p.desc { margin: 0 0 16px; color: var(--ink-soft); font-size: 13.5px; }

  .field {
    display: grid; grid-template-columns: minmax(190px, 260px) 1fr; gap: 8px 22px;
    padding: 14px 0; border-top: 1px solid var(--line-soft); align-items: start;
  }
  .field:first-of-type { border-top: none; padding-top: 4px; }
  .field label { font-weight: 600; font-size: 14px; display: block; }
  .field .help { color: var(--muted); font-size: 12.5px; margin-top: 3px; }
  .field .control > input, .field .control > select { max-width: 440px; }
  .field .control > textarea { max-width: 560px; }
  .field .under {
    margin-top: 7px; font-size: 12px; color: var(--muted);
    display: flex; gap: 8px; align-items: center; flex-wrap: wrap;
  }
  .field.dirty .control > input, .field.dirty .control > select, .field.dirty .control > textarea {
    border-color: var(--focus);
  }

  .tag {
    display: inline-block; padding: 2px 8px; border-radius: 999px;
    font-size: 10.5px; font-weight: 700; text-transform: uppercase; letter-spacing: .04em;
  }
  .tag.ui, .tag.stored { background: var(--ok-bg); color: var(--ok-ink); }
  .tag.environment { background: var(--info-bg); color: var(--info-ink); }
  .tag.default { background: var(--neutral-bg); color: var(--neutral-ink); }
  .tag.restart { background: var(--warn-bg); color: var(--warn-ink); }
  .tag.unset { background: var(--err-bg); color: var(--err-ink); }

  .section-actions { margin-top: 16px; padding-top: 14px; border-top: 1px solid var(--line-soft); }

  footer.bar {
    position: fixed; left: 0; right: 0; bottom: 0; z-index: 20;
    display: flex; gap: 12px; align-items: center;
    padding: 12px 20px;
    background: var(--panel); border-top: 1px solid var(--line);
    box-shadow: 0 -2px 10px rgba(0, 0, 0, .05);
  }
  footer.bar .count { color: var(--ink-soft); font-size: 13.5px; margin-right: auto; }
  footer.bar .status { margin-top: 0; margin-right: 12px; }

  @media (max-width: 760px) {
    .field { grid-template-columns: 1fr; gap: 6px; }
    footer.bar { padding: 10px 14px; flex-wrap: wrap; }
  }
</style>
</head>
<body>
<header class="top">
  <span class="brand">HearthSense</span>
  <nav>
    <a href="dashboard">Dashboard</a>
    <a href="settings" class="here">Settings</a>
  </nav>
  <input id="token" class="token" type="password" placeholder="API token (not needed on loopback)" />
</header>

<main>
  <div id="banners"></div>
  <div id="sections"></div>
</main>

<footer class="bar">
  <span class="count" id="count">No unsaved changes.</span>
  <span class="status" id="status"></span>
  <button id="revert" type="button">Discard</button>
  <button id="save" class="primary" type="button">Save changes</button>
</footer>

<script>
const $ = (id) => document.getElementById(id);
const token = $('token');
token.value = localStorage.getItem('hearthsense-token') || '';
token.addEventListener('input', () => localStorage.setItem('hearthsense-token', token.value.trim()));

function headers(json) {
  const h = json ? { 'Content-Type': 'application/json' } : {};
  const t = token.value.trim();
  if (t) h.Authorization = 'Bearer ' + t;
  return h;
}

async function call(path, options) {
  const response = await fetch(path, options);
  const text = await response.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch { body = null; }
  if (!response.ok) {
    const detail = body && (body.error || (body.errors && body.errors.join(' ')));
    throw new Error(detail || ('HTTP ' + response.status));
  }
  return body;
}

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined && text !== null) node.textContent = text;
  return node;
}

function say(message, kind) {
  const status = $('status');
  status.textContent = message || '';
  status.className = kind ? 'status ' + kind : 'status';
}

let model = null;
const controls = new Map();

// ---- reading and writing one control ----

function readControl(field) {
  const control = controls.get(field.key);
  if (!control) return null;
  switch (field.kind) {
    case 'bool': return control.checked;
    case 'number': case 'decimal': return control.value.trim() === '' ? '' : Number(control.value);
    case 'list': return control.value.split('\n').map((line) => line.trim()).filter((line) => line.length > 0);
    default: return control.value;
  }
}

function writeControl(field, value) {
  const control = controls.get(field.key);
  if (!control) return;
  if (field.kind === 'bool') control.checked = value === true;
  else if (field.kind === 'list') control.value = (value || []).join('\n');
  else control.value = value === null || value === undefined ? '' : String(value);
}

function same(left, right) { return JSON.stringify(left) === JSON.stringify(right); }

function changes() {
  const changed = {};
  for (const section of model.sections) {
    for (const field of section.fields) {
      if (field.kind === 'secret') continue;
      const value = readControl(field);
      if (!same(value, field.value)) changed[field.key] = value;
    }
  }
  return changed;
}

function refreshDirty() {
  const changed = changes();
  const count = Object.keys(changed).length;
  $('count').textContent = count === 0
    ? 'No unsaved changes.'
    : count + (count === 1 ? ' unsaved change.' : ' unsaved changes.');
  $('save').disabled = count === 0;
  $('revert').disabled = count === 0;
  for (const section of model.sections) {
    for (const field of section.fields) {
      const row = document.getElementById('row-' + field.key);
      if (row) row.classList.toggle('dirty', Object.prototype.hasOwnProperty.call(changed, field.key));
    }
  }
}

// ---- rendering ----

function control(field) {
  let node;
  if (field.kind === 'bool') {
    node = document.createElement('input');
    node.type = 'checkbox';
  } else if (field.kind === 'list') {
    node = document.createElement('textarea');
    node.rows = 4;
    node.placeholder = 'One entity id glob per line, e.g. binary_sensor.*';
  } else if (field.kind === 'select') {
    node = document.createElement('select');
    for (const choice of field.choices || []) {
      const option = document.createElement('option');
      option.value = choice;
      option.textContent = choice;
      node.append(option);
    }
  } else if (field.kind === 'number' || field.kind === 'decimal') {
    node = document.createElement('input');
    node.type = 'number';
    if (field.minimum !== null && field.minimum !== undefined) node.min = String(field.minimum);
    if (field.maximum !== null && field.maximum !== undefined) node.max = String(field.maximum);
    node.step = field.kind === 'decimal' ? 'any' : '1';
  } else if (field.kind === 'secret') {
    node = document.createElement('input');
    node.type = 'password';
    node.autocomplete = 'new-password';
    node.placeholder = field.configured ? 'Stored. Type to replace.' : 'Not set';
  } else {
    node = document.createElement('input');
    node.type = 'text';
    if (field.kind === 'duration') node.placeholder = 'e.g. 45s, 10m, 2h, 14d';
  }
  node.id = 'input-' + field.key;
  return node;
}

function fieldRow(field) {
  const row = el('div', 'field');
  row.id = 'row-' + field.key;

  const left = el('div');
  const label = el('label', null, field.label);
  label.htmlFor = 'input-' + field.key;
  left.append(label, el('div', 'help', field.help));
  row.append(left);

  const right = el('div', 'control');
  const input = control(field);
  controls.set(field.key, input);
  right.append(input);

  const under = el('div', 'under');
  if (field.restartRequired) under.append(el('span', 'tag restart', 'needs restart'));

  if (field.kind === 'secret') {
    under.append(el('span', 'tag ' + (field.source === 'unset' ? 'unset' : field.source),
      field.source === 'stored' ? 'stored here' : field.source === 'environment' ? 'from environment' : 'not set'));
    if (field.variable) under.append(el('span', null, 'Environment variable: ' + field.variable));
    if (field.source === 'stored') {
      under.append(action('Clear', async () => {
        await call('api/settings/secrets', {
          method: 'PUT', headers: headers(true), body: JSON.stringify({ [field.key.split(':')[1]]: null }),
        });
        say('Cleared.', 'ok');
        await load();
      }, 'link'));
    }
  } else {
    under.append(el('span', 'tag ' + field.source,
      field.source === 'ui' ? 'set here' : field.source === 'environment' ? 'from environment' : 'default'));

    if (field.overridden) {
      const inheritedText = field.kind === 'list'
        ? (field.inherited || []).join(', ') || 'nothing'
        : String(field.inherited);
      under.append(el('span', null, 'Without this: ' + inheritedText));
      under.append(action('Reset', async () => { await save({ [field.key]: field.inherited }); }, 'link'));
    }
  }

  right.append(under);
  row.append(right);

  writeControl(field, field.kind === 'secret' ? '' : field.value);
  input.addEventListener('input', refreshDirty);
  input.addEventListener('change', refreshDirty);

  return row;
}

function action(label, handler, className) {
  const button = el('button', className || null, label);
  button.type = 'button';
  button.addEventListener('click', async () => {
    button.disabled = true;
    try { await handler(); } catch (err) { say(err.message, 'err'); } finally { button.disabled = false; }
  });
  return button;
}

function sectionCard(section) {
  const card = el('div', 'card');

  const head = el('div', 'card-head');
  head.append(el('h3', null, section.title));
  card.append(head, el('p', 'desc', section.description));

  for (const field of section.fields) card.append(fieldRow(field));

  const row = el('div', 'row section-actions');

  if (section.name === 'Secrets') {
    row.append(action('Save secrets', saveSecrets, 'primary'));
  } else {
    if (section.fields.some((f) => f.overridden)) {
      row.append(action('Reset this section', async () => {
        await call('api/settings/reset', {
          method: 'POST', headers: headers(true), body: JSON.stringify({ section: section.name }),
        });
        say('Reset to inherited values.', 'ok');
        await load();
      }));
    }
    if (section.name === 'HomeAssistant') row.append(action('Test connection', () => test('homeAssistant')));
    if (section.name === 'Llm') row.append(action('Test connection', () => test('llm')));
  }

  if (row.childElementCount > 0) card.append(row);
  return card;
}

function banner(kind, title, items) {
  const node = el('div', 'banner ' + kind);
  node.append(el('strong', null, title));
  if (items && items.length) {
    const list = el('ul');
    for (const item of items) list.append(el('li', null, item));
    node.append(list);
  }
  return node;
}

function render() {
  controls.clear();

  const banners = $('banners');
  banners.replaceChildren();

  if (!model.ready) {
    banners.append(banner('',
      'No Home Assistant token yet. Add one under Secrets, save, then use Test connection. It has to come from an admin user.'));
  }
  if (model.errors && model.errors.length) {
    banners.append(banner('err', 'These settings are not usable yet:', model.errors));
  }
  if (model.warnings && model.warnings.length) {
    banners.append(banner('', 'Worth knowing:', model.warnings));
  }
  if (model.restartPending && model.restartPending.length) {
    banners.append(banner('', 'Saved, but these only take effect after a restart:', model.restartPending));
  }

  const sections = $('sections');
  sections.replaceChildren();
  for (const section of model.sections) sections.append(sectionCard(section));

  refreshDirty();
}

// ---- server calls ----

async function load() {
  model = await call('api/settings', { headers: headers(false) });
  render();
}

async function save(explicit) {
  const payload = explicit || changes();
  if (Object.keys(payload).length === 0) return;

  const result = await call('api/settings', { method: 'PUT', headers: headers(true), body: JSON.stringify(payload) });
  model = result.settings;
  render();

  const n = result.changed.length;
  if (result.restartRequired && result.restartRequired.length) {
    say('Saved. Restart HearthSense to apply: ' + result.restartRequired.join(', '), 'err');
  } else {
    say(n === 0 ? 'Nothing to change.' : 'Saved ' + n + (n === 1 ? ' setting.' : ' settings.'), 'ok');
  }
}

async function saveSecrets() {
  const payload = {};
  for (const section of model.sections) {
    for (const field of section.fields) {
      if (field.kind !== 'secret') continue;
      const value = readControl(field);
      if (value && value.trim().length > 0) payload[field.key.split(':')[1]] = value.trim();
    }
  }

  if (Object.keys(payload).length === 0) { say('Type a secret first.', 'err'); return; }

  await call('api/settings/secrets', { method: 'PUT', headers: headers(true), body: JSON.stringify(payload) });
  say('Secrets saved.', 'ok');
  await load();
}

async function test(target) {
  say('Testing…');
  const result = await call('api/settings/test', {
    method: 'POST', headers: headers(true), body: JSON.stringify({ target }),
  });
  say(result.detail, result.ok ? 'ok' : 'err');
}

$('save').addEventListener('click', async () => {
  $('save').disabled = true;
  say('Saving…');
  try { await save(null); } catch (err) { say(err.message, 'err'); } finally { refreshDirty(); }
});

$('revert').addEventListener('click', () => {
  for (const section of model.sections) {
    for (const field of section.fields) {
      if (field.kind !== 'secret') writeControl(field, field.value);
    }
  }
  refreshDirty();
  say('Changes discarded.');
});

load().catch((err) => say(err.message, 'err'));
</script>
</body>
</html>
""";
}
