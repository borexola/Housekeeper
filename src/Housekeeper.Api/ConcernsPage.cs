namespace Housekeeper.Api;

/// <summary>
/// What the user has asked to be watched, in their own words, and what each concern resolved to. Every
/// server value is written with textContent, so nothing from Home Assistant or the model can inject markup.
/// </summary>
internal static class ConcernsPage
{
    public static readonly string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>Housekeeper concerns</title>
<style>
""" + Ui.Styles + """
  .add .row { margin-top: 12px; }
  .add .row input { flex: 1 1 280px; }
  .presets { display: flex; gap: 6px; flex-wrap: wrap; margin-top: 12px; }
  .presets .label { color: var(--muted); font-size: 12.5px; width: 100%; }
  .presets button { padding: 3px 10px; font-size: 12.5px; font-weight: 400; border-radius: 999px; color: var(--ink-soft); }
  .presets button:hover:not(:disabled) { color: var(--tint); border-color: var(--tint); background: var(--tint-soft); }

  .concern { border-left: 3px solid var(--kind-concern); }
  .concern .card-head h3 { font-size: 15px; }
  .concern .how { color: var(--ink-soft); font-size: 13.5px; margin-top: 2px; }
  .concern .how.fallback { color: var(--warn-text); }
  .concern .rule { display: inline-block; margin-top: 8px; padding: 2px 8px; border-radius: 4px; background: var(--tint-soft); color: var(--tint); font-size: 12px; font-weight: 500; }
  .concern .chips { margin-top: 8px; }
  .concern .row.actions { margin-top: 12px; }

  .spin {
    display: inline-block; width: 10px; height: 10px; margin-right: 2px; vertical-align: -1px;
    border: 1.5px solid currentColor; border-right-color: transparent; border-radius: 50%;
    animation: spin .8s linear infinite;
  }
  @keyframes spin { to { transform: rotate(360deg); } }
  .toast {
    position: fixed; left: 50%; bottom: 22px; transform: translateX(-50%);
    background: var(--ink); color: var(--bg); padding: 9px 14px; border-radius: 6px;
    font-size: 13px; max-width: 90vw; z-index: 10; opacity: 0; transition: opacity .15s; pointer-events: none;
  }
  .toast.show { opacity: 1; }
  .toast.err { background: var(--err-text); color: #fff; }
</style>
</head>
<body>
""" + Ui.Header("concerns") + """

<main>
  <h1 class="title">Concerns</h1>
  <p class="lead meta">Tell Housekeeper what you worry about, in your own words. It reads each one into the entities it is about and, where you say what wrong looks like, checks for exactly that every scan. Those entities are judged more strictly and their findings come first on the dashboard.</p>

  <section class="card add">
    <div class="row">
      <input id="concern-text" type="text" placeholder="e.g. the freezer warming up, or the garage door left open at night" maxlength="300" />
      <button id="concern-add" class="primary" type="button">Watch for it</button>
    </div>
    <div class="presets" id="presets"><span class="label">Or start from one of these</span></div>
    <div id="concern-status" class="status"></div>
  </section>

  <h2 class="section">Watching for <span id="concern-count" class="count" hidden></span></h2>
  <div id="concerns"><div class="empty">Loading…</div></div>
</main>

<script>
const $ = (id) => document.getElementById(id);
""" + Ui.TokenScript + """

const PRESETS = [
  'the freezer or fridge warming up',
  'unusually high power draw',
  'lights left on overnight',
  'a door or window left open',
  'a door left unlocked at night',
  'a water leak',
  'a device going offline',
  'a room getting too cold',
];

const CHIPS_SHOWN = 8;

function capitalise(text) { return text ? text.charAt(0).toUpperCase() + text.slice(1) : text; }

function tell(status, message, kind) {
  status.textContent = message || '';
  status.className = kind ? 'status ' + kind : 'status';
}

let toastTimer = null;
function toast(message, kind) {
  let node = $('toast');
  if (!node) { node = el('div', 'toast'); node.id = 'toast'; document.body.append(node); }
  node.textContent = message;
  node.className = 'toast show' + (kind === 'err' ? ' err' : '');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => node.classList.remove('show'), kind === 'err' ? 8000 : 4500);
}

function chips(label, values) {
  const row = el('div', 'chips');
  row.append(el('span', 'label', label));
  const shown = values.length > CHIPS_SHOWN + 1 ? values.slice(0, CHIPS_SHOWN) : values;
  for (const value of shown) row.append(el('span', 'chip', value));
  if (shown.length < values.length) {
    const rest = values.slice(shown.length);
    const more = el('button', 'link', 'and ' + rest.length + ' more');
    more.type = 'button';
    const fewer = el('button', 'link', 'show fewer');
    fewer.type = 'button';
    more.addEventListener('click', () => { for (const value of rest) row.insertBefore(el('span', 'chip', value), more); more.replaceWith(fewer); });
    fewer.addEventListener('click', () => { for (const chip of [...row.querySelectorAll('.chip')].slice(shown.length)) chip.remove(); fewer.replaceWith(more); });
    row.append(more);
  }
  return row;
}

function concernCard(c) {
  const card = el('div', 'card concern');
  const head = el('div', 'card-head');
  head.append(el('h3', null, c.text));
  const when = el('span', 'when', new Date(c.createdUtc).toLocaleString());
  head.append(when);
  card.append(head);

  // A concern the model could not read is flagged, because the fix is usually on the settings page and
  // "matched by name" on its own reads as a choice rather than a fallback.
  card.append(el('div', 'how' + (c.interpreted ? '' : ' fallback'), c.explanation || (c.interpreted ? 'Read by the model.' : 'Matched by name.')));
  if (c.rule) card.append(el('span', 'rule', capitalise(c.rule)));

  if (c.names && c.names.length) card.append(chips('Watching', c.names));
  else card.append(el('div', 'meta', 'Nothing is being watched for this yet. Remove it and try naming the room or the device.'));

  const status = el('div', 'status');
  const row = el('div', 'row actions');
  const remove = el('button', 'quiet small', 'Remove');
  remove.type = 'button';
  remove.addEventListener('click', async () => {
    remove.disabled = true;
    try {
      await call('api/concerns/' + c.id, { method: 'DELETE', headers: headers(false) });
      toast('Removed. Anything it raised closes on the next scan.');
      await load();
    } catch (err) {
      tell(status, err.message, 'err');
      remove.disabled = false;
    }
  });
  row.append(remove);
  card.append(row, status);
  return card;
}

async function load() {
  try {
    const list = await call('api/concerns', { headers: headers(false) }) || [];
    const count = $('concern-count');
    count.hidden = !list.length;
    count.textContent = String(list.length);

    const box = $('concerns');
    box.replaceChildren();
    if (!list.length) {
      const empty = el('div', 'empty');
      empty.append(el('strong', null, 'Nothing yet'), document.createTextNode('Add a worry above, or pick one of the examples.'));
      box.append(empty);
    } else {
      for (const c of list) box.append(concernCard(c));
    }

    // A preset already added is no longer a suggestion.
    const have = new Set(list.map((c) => c.text.trim().toLowerCase()));
    for (const chip of $('presets').querySelectorAll('button')) chip.hidden = have.has(chip.textContent.trim().toLowerCase());
  } catch (err) {
    $('concerns').replaceChildren(el('div', 'empty', err.message));
  }
}

async function add(text) {
  text = (text || '').trim();
  const status = $('concern-status');
  if (!text) { tell(status, 'Say what you are concerned about first.', 'err'); $('concern-text').focus(); return; }

  const button = $('concern-add');
  button.disabled = true;
  const spinner = el('span', 'spin');
  const caption = document.createTextNode('Reading…');
  button.replaceChildren(spinner, caption);
  const started = Date.now();
  const timer = setInterval(() => {
    const seconds = Math.round((Date.now() - started) / 1000);
    caption.textContent = seconds >= 2 ? 'Reading… ' + seconds + 's' : 'Reading…';
    if (seconds === 25) tell(status, 'Still asking the model what this is about. It has not stalled.');
  }, 500);
  tell(status, 'Working out which entities this is about. With a model configured this takes 10–60 seconds.');

  try {
    const concern = await call('api/concerns', { method: 'POST', headers: headers(true), body: JSON.stringify({ text }) });
    $('concern-text').value = '';
    const n = concern.names ? concern.names.length : 0;
    tell(status, n
      ? 'Watching ' + n + (n === 1 ? ' entity' : ' entities') + ' for this from the next scan.'
      : 'Saved, but nothing in Home Assistant matched it yet. Try naming the room or the device.', n ? 'ok' : 'err');
    await load();
  } catch (err) {
    tell(status, err.message, 'err');
  } finally {
    clearInterval(timer);
    button.disabled = false;
    button.textContent = 'Watch for it';
  }
}

$('concern-add').addEventListener('click', () => add($('concern-text').value));
$('concern-text').addEventListener('keydown', (event) => { if (event.key === 'Enter') { event.preventDefault(); add($('concern-text').value); } });
for (const preset of PRESETS) {
  const chip = el('button', null, preset);
  chip.type = 'button';
  chip.addEventListener('click', () => { $('concern-text').value = preset; $('concern-text').focus(); });
  $('presets').append(chip);
}

load();
</script>
</body>
</html>
""";
}
