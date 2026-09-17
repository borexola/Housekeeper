namespace Housekeeper.Api;

/// <summary>
/// The settings screen. Every control is generated from the schema the server sends, so a new option in
/// <see cref="SettingsCatalog"/> appears here without touching this file. All server values are written with
/// textContent or assigned to input values, so nothing from Home Assistant or the model can inject markup.
/// </summary>
internal static class SettingsPage
{
    public static readonly string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>Housekeeper settings</title>
<style>
""" + Ui.Styles + """
  body { padding-bottom: 84px; }
  main { max-width: 900px; }

  .card > .card-head { margin-bottom: 2px; }
  .card > .card-head h3 { font-size: 14px; }
  .card > p.desc { margin: 0 0 12px; color: var(--muted); font-size: 13px; }

  .field {
    display: grid; grid-template-columns: minmax(190px, 250px) 1fr; gap: 6px 24px;
    padding: 14px 0; border-top: 1px solid var(--line-soft); align-items: start;
  }
  .field:first-of-type { border-top: none; padding-top: 4px; }
  .field label { font-weight: 500; font-size: 13.5px; display: block; }
  .field .help { color: var(--muted); font-size: 12.5px; margin-top: 3px; }
  .field .control > input, .field .control > select { max-width: 420px; }
  .field .control > textarea { max-width: 540px; }
  .field .under {
    margin-top: 6px; font-size: 12px; color: var(--muted);
    display: flex; gap: 8px; align-items: center; flex-wrap: wrap;
  }
  .field.dirty .control > input, .field.dirty .control > select, .field.dirty .control > textarea {
    border-color: var(--focus);
  }

  .tag {
    display: inline-block; padding: 0 6px; border-radius: 4px; border: 1px solid var(--line);
    font-size: 10.5px; font-weight: 600; letter-spacing: .04em; text-transform: uppercase; color: var(--muted);
  }
  .tag.ui, .tag.stored { color: var(--ok-text); border-color: var(--ok-line); }
  .tag.restart { color: var(--warn-text); border-color: var(--warn-line); }
  .tag.unset { color: var(--err-text); border-color: var(--err-line); }

  .section-actions { margin-top: 14px; padding-top: 12px; border-top: 1px solid var(--line-soft); }

  footer.bar {
    position: fixed; left: 0; right: 0; bottom: 0; z-index: 20;
    display: flex; gap: 10px; align-items: center;
    padding: 10px 24px;
    background: var(--panel); border-top: 1px solid var(--line);
  }
  footer.bar .count { color: var(--muted); font-size: 13px; margin-right: auto; }
  footer.bar .status { margin-top: 0; margin-right: 12px; }

  @media (max-width: 760px) {
    .field { grid-template-columns: 1fr; gap: 6px; }
    footer.bar { padding: 10px 14px; flex-wrap: wrap; }
  }
</style>
</head>
<body>
""" + Ui.Header("settings") + """

<main>
  <div id="banners"></div>
  <div id="sections"></div>
</main>

<footer class="bar">
  <span class="count" id="count">No unsaved changes.</span>
  <span class="status" id="status"></span>
  <button id="revert" type="button" disabled>Discard</button>
  <button id="save" class="primary" type="button" disabled>Save changes</button>
</footer>

<script>
const $ = (id) => document.getElementById(id);
""" + Ui.TokenScript + """

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
  if (!model) return changed;
  for (const section of model.sections) {
    for (const field of section.fields) {
      if (field.kind === 'secret' || field.managed) continue;
      const value = readControl(field);
      if (!same(value, field.value)) changed[field.key] = value;
    }
  }
  return changed;
}

function refreshDirty() {
  if (!model) return;

  const changed = changes();
  const count = Object.keys(changed).length;

  // A typed secret is not in `changed` -- it cannot be, since the server never sends the old value back to
  // compare against -- but it is absolutely something the user can want to take back, so Discard stays
  // available for it. It does not go in the count, which is about fields that will be saved by "Save changes".
  const typedSecret = model.sections.some((section) => section.fields.some((field) =>
    field.kind === 'secret' && (controls.get(field.key) || {}).value));

  $('count').textContent = count === 0
    ? 'No unsaved changes.'
    : count + (count === 1 ? ' unsaved change.' : ' unsaved changes.');
  $('save').disabled = count === 0;
  $('revert').disabled = count === 0 && !typedSecret;
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

    // A value none of the options carry leaves selectedIndex at -1, so the control renders blank and reads
    // back as ''. That looked like an unsaved change nobody made, rode along in every payload, and was
    // rejected server-side -- so one unrecognised provider blocked every other edit on the page, and
    // Discard could not clear it either because it wrote the same unmatched value straight back.
    const known = (field.choices || []).some((c) => String(c).toLowerCase() === String(field.value).toLowerCase());
    if (!known && field.value) {
      const stray = document.createElement('option');
      stray.value = field.value;
      stray.textContent = field.value + ' — not a supported value';
      node.append(stray);
    }

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

  // Pinned by the add-on. Server-side Save refuses the whole batch if one of these is submitted, and the
  // page sends every pending change at once, so leaving it editable meant one stray keystroke here silently
  // discarded every other edit on the page.
  if (field.managed) {
    input.disabled = true;
    under.append(el('span', 'tag', 'set by the Home Assistant add-on'));
  }

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

  if (model.missing && model.missing.length) {
    banners.append(banner('',
      'Not ready to draft yet. Housekeeper needs ' + model.missing.join(' and ') + '. Set it below, then use Test connection.'));
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

/**
 * Edits typed on the page and not yet saved, minus whatever the request about to run is settling.
 *
 * Every path that talks to the server replaces `model` and re-renders, and render() rebuilds each row from
 * the server's value -- so resetting one field, or clearing one secret, silently threw away everything else
 * the user had typed. The footer had been counting those edits out loud right up to the moment they vanished.
 * Called before `model` is replaced, because it compares against the model the page was drawn from.
 */
function keep(applied) {
  if (!model) return { values: {}, secrets: {} };

  const kept = changes();
  for (const key of Object.keys(applied || {})) delete kept[key];

  // Secrets are captured separately because changes() deliberately ignores them -- their value is never
  // sent back by the server, so there is nothing to compare against. Without this, a pasted token was wiped
  // by any round trip at all (a reset, a clear, saving a different secret) and the footer still read
  // "No unsaved changes", because it was never counting them in the first place.
  const secrets = {};
  for (const section of model.sections) {
    for (const field of section.fields) {
      if (field.kind !== 'secret') continue;
      const control = controls.get(field.key);
      if (control && control.value) secrets[field.key] = control.value;
    }
  }

  return { values: kept, secrets };
}

/** Re-renders from the new model, then puts those edits back and re-counts them. */
function restore(kept) {
  render();

  for (const section of model.sections) {
    for (const field of section.fields) {
      if (Object.prototype.hasOwnProperty.call(kept.values, field.key)) writeControl(field, kept.values[field.key]);
      if (Object.prototype.hasOwnProperty.call(kept.secrets, field.key)) {
        const control = controls.get(field.key);
        if (control) control.value = kept.secrets[field.key];
      }
    }
  }

  refreshDirty();
}

async function load() {
  const kept = keep({});
  model = await call('api/settings', { headers: headers(false) });
  restore(kept);
}

async function save(explicit) {
  const payload = explicit || changes();
  if (Object.keys(payload).length === 0) return;

  const kept = keep(payload);
  const result = await call('api/settings', { method: 'PUT', headers: headers(true), body: JSON.stringify(payload) });
  model = result.settings;
  restore(kept);

  const n = result.changed.length;
  if (result.restartRequired && result.restartRequired.length) {
    say('Saved. Restart Housekeeper to apply: ' + result.restartRequired.join(', '), 'err');
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
  // Test what is on the page, not what is on disk, so a value can be tried before it is saved.
  const values = changes();
  const secrets = {};
  for (const section of model.sections) {
    for (const field of section.fields) {
      if (field.kind !== 'secret') continue;
      const value = readControl(field);
      if (value && value.trim().length > 0) secrets[field.key.split(':')[1]] = value.trim();
    }
  }
  const unsaved = Object.keys(values).length > 0 || Object.keys(secrets).length > 0;

  say('Testing…');
  const result = await call('api/settings/test', {
    method: 'POST', headers: headers(true), body: JSON.stringify({ target, values, secrets }),
  });
  say(result.detail + (unsaved ? ' Tested with unsaved values; save to keep them.' : ''), result.ok ? 'ok' : 'err');
}

$('save').addEventListener('click', async () => {
  $('save').disabled = true;
  say('Saving…');
  try { await save(null); } catch (err) { say(err.message, 'err'); } finally { refreshDirty(); }
});

$('revert').addEventListener('click', () => {
  if (!model) return;

  for (const section of model.sections) {
    for (const field of section.fields) {
      // Secret boxes are cleared as well. Leaving a mistyped credential sitting there while announcing
      // "Changes discarded." meant the next Save secrets or Test connection quietly submitted it, and there
      // was no way to take it back from the page at all.
      writeControl(field, field.kind === 'secret' ? '' : field.value);
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
