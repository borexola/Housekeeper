namespace Housekeeper.Api;

/// <summary>
/// What the app has been doing, line by line: every scan, every read of Home Assistant, every model call,
/// searchable and filterable. Everything is written with textContent, so a log line cannot inject markup.
/// </summary>
internal static class LogsPage
{
    public static readonly string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>Housekeeper logs</title>
<style>
""" + Ui.Styles + """
  main { max-width: 1100px; }

  .filters { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin: 14px 0 12px; }
  .filters input[type=search] { flex: 1 1 260px; min-width: 200px; }
  .filters select { width: auto; }
  .filters .live { display: inline-flex; align-items: center; gap: 6px; color: var(--ink-soft); font-size: 13px; }
  .filters .live input { margin: 0; }
  .summary { color: var(--muted); font-size: 12.5px; margin-bottom: 8px; display: flex; gap: 12px; flex-wrap: wrap; }

  .log { border: 1px solid var(--line); border-radius: var(--radius); background: var(--panel); overflow: hidden; }
  .line { display: grid; grid-template-columns: 118px 64px 110px 1fr; gap: 0 12px; padding: 7px 14px; border-top: 1px solid var(--line-soft); font-size: 13px; align-items: baseline; }
  .line:first-child { border-top: none; }
  .line .at { color: var(--muted); font-family: var(--mono); font-size: 12px; white-space: nowrap; }
  .line .lvl { font-size: 11px; font-weight: 600; letter-spacing: .03em; text-transform: uppercase; }
  .line .lvl.Debug { color: var(--muted); }
  .line .lvl.Information { color: var(--tint); }
  .line .lvl.Warning { color: var(--warn-text); }
  .line .lvl.Error, .line .lvl.Critical { color: var(--err-text); }
  .line .flow { color: var(--ink-soft); font-size: 12px; white-space: nowrap; }
  .line .msg { color: var(--ink); overflow-wrap: anywhere; }
  .line.Warning { background: color-mix(in srgb, var(--warn-bg) 55%, transparent); }
  .line.Error, .line.Critical { background: color-mix(in srgb, var(--err-bg) 60%, transparent); }
  .line .src { color: var(--muted); font-size: 11.5px; margin-left: 6px; }
  .line details { margin: 4px 0 0; }
  .line summary { cursor: pointer; color: var(--muted); font-size: 12px; }
  .line pre { margin-top: 6px; font-size: 11.5px; white-space: pre-wrap; }
  mark { background: var(--accent-soft); color: inherit; padding: 0 1px; border-radius: 2px; }

  .state { display: grid; grid-template-columns: 118px 1fr 160px; gap: 0 12px; padding: 6px 14px; border-top: 1px solid var(--line-soft); font-size: 13px; align-items: baseline; }
  .state:first-child { border-top: none; }
  .state .at { color: var(--muted); font-family: var(--mono); font-size: 12px; white-space: nowrap; }
  .state .who { overflow-wrap: anywhere; }
  .state .who .id { color: var(--muted); font-family: var(--mono); font-size: 11.5px; margin-left: 6px; }
  .state .where { display: block; color: var(--muted); font-size: 12px; }
  .state .where button.link { color: var(--muted); font-size: 12px; text-decoration: none; }
  .state .where button.link:hover { color: var(--tint); text-decoration: underline; }
  .state .val { font-family: var(--mono); font-size: 12.5px; text-align: right; white-space: nowrap; }
  .state .val.off { color: var(--muted); }
  .state .val.on { color: var(--ok-text); }
  .state .val.gone { color: var(--err-text); }

  .flows { display: flex; gap: 6px; flex-wrap: wrap; }
  .flows button { padding: 3px 10px; font-size: 12.5px; font-weight: 400; border-radius: 999px; color: var(--ink-soft); }
  .flows button.on { color: var(--tint); border-color: var(--tint); background: var(--tint-soft); }

  @media (max-width: 760px) {
    .line { grid-template-columns: 70px 1fr; }
    .line .flow { grid-column: 2; }
    .line .msg { grid-column: 1 / -1; }
  }
</style>
</head>
<body>
""" + Ui.Header("logs") + """

<main>
  <h1 class="title">Logs</h1>
  <p class="lead meta" id="lead">Every scan, every read of Home Assistant, every model call, newest first. Held in memory since the last start; the container's own log is the permanent record.</p>

  <div class="segmented" id="view" style="margin-bottom:6px">
    <button type="button" class="on" data-v="app">App log</button>
    <button type="button" data-v="states">State changes from Home Assistant</button>
  </div>

  <div class="filters">
    <input id="q" type="search" placeholder="Search messages, e.g. backfill, freezer, HTTP 401" />
    <select id="level" title="Lowest level to show">
      <option value="Debug">Debug and above</option>
      <option value="Information" selected>Info and above</option>
      <option value="Warning">Warnings and errors</option>
      <option value="Error">Errors only</option>
    </select>
    <label class="live"><input id="live" type="checkbox" checked /> Follow</label>
    <button id="clear" class="quiet small" type="button">Clear filters</button>
  </div>
  <div class="flows" id="flows"></div>

  <div class="summary" id="summary"></div>
  <div id="log" class="log"></div>
</main>

<script>
const $ = (id) => document.getElementById(id);
""" + Ui.TokenScript + """

const FLOWS = ['Scan', 'Home Assistant', 'Model', 'Live feed', 'Drafting', 'Concerns', 'Settings', 'Service', 'Host'];
let flow = '';
let timer = null;
let view = 'app';

$('view').addEventListener('click', (event) => {
  const button = event.target.closest('button');
  if (!button || button.classList.contains('on')) return;
  for (const other of $('view').querySelectorAll('button')) other.classList.toggle('on', other === button);
  view = button.dataset.v;
  const states = view === 'states';
  $('level').hidden = states;
  $('flows').hidden = states;
  $('q').placeholder = states ? 'Search entities, devices, areas or states, e.g. freezer, Zigbee hub, Kitchen, unavailable' : 'Search messages, e.g. backfill, freezer, HTTP 401';
  $('lead').textContent = states
    ? 'What has arrived from Home Assistant and been stored, newest first: every change the scan, the live feed or the recorder backfill brought in.'
    : 'Every scan, every read of Home Assistant, every model call, newest first. Held in memory since the last start; the container\'s own log is the permanent record.';
  load();
});

for (const name of FLOWS) {
  const chip = el('button', null, name);
  chip.type = 'button';
  chip.dataset.flow = name;
  chip.addEventListener('click', () => {
    flow = flow === name ? '' : name;
    for (const other of $('flows').querySelectorAll('button')) other.classList.toggle('on', other.dataset.flow === flow);
    load();
  });
  $('flows').append(chip);
}

/** Clock time for today; day and month too for anything older, so an old row cannot pass for a fresh one. */
function when(iso) {
  const d = new Date(iso);
  const time = d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  const today = new Date();
  const sameDay = d.getFullYear() === today.getFullYear() && d.getMonth() === today.getMonth() && d.getDate() === today.getDate();
  return sameDay ? time : d.toLocaleDateString([], { day: 'numeric', month: 'short' }) + ' ' + time;
}

/** The message with the search term highlighted, built from text nodes so a log line cannot inject markup. */
function highlighted(text, term) {
  const holder = el('span', 'msg');
  if (!term) { holder.textContent = text; return holder; }
  const lower = text.toLowerCase();
  const needle = term.toLowerCase();
  let at = 0;
  for (;;) {
    const hit = lower.indexOf(needle, at);
    if (hit < 0) { holder.append(text.slice(at)); break; }
    holder.append(text.slice(at, hit), el('mark', null, text.slice(hit, hit + term.length)));
    at = hit + term.length;
  }
  return holder;
}

function line(entry, term) {
  const node = el('div', 'line ' + entry.level);
  const at = el('span', 'at', when(entry.atUtc));
  at.title = new Date(entry.atUtc).toLocaleString();
  node.append(at, el('span', 'lvl ' + entry.level, entry.level === 'Information' ? 'info' : entry.level.toLowerCase()), el('span', 'flow', entry.flow));

  const msg = highlighted(entry.message, term);
  msg.append(el('span', 'src', entry.source));
  if (entry.exception) {
    const details = el('details');
    details.append(el('summary', null, 'exception'), el('pre', null, entry.exception));
    msg.append(details);
  }
  node.append(msg);
  return node;
}

function stateLine(entry) {
  const node = el('div', 'state');
  const at = el('span', 'at', when(entry.atUtc));
  at.title = new Date(entry.atUtc).toLocaleString();
  const who = el('span', 'who');
  who.append(document.createTextNode(entry.name || entry.entityId));
  if (entry.name) who.append(el('span', 'id', entry.entityId));

  // The device is a link that filters to everything on it, which is how a reader finds the other
  // readings from the same plug or the same hub without knowing their ids.
  if (entry.device || entry.area) {
    const where = el('span', 'where');
    if (entry.device) {
      const device = el('button', 'link', entry.device);
      device.type = 'button';
      device.title = 'Show every change from this device';
      device.addEventListener('click', () => { $('q').value = entry.device; load(); });
      where.append(device);
    }
    if (entry.device && entry.area) where.append(' · ');
    if (entry.area) where.append(entry.area);
    who.append(where);
  }
  const kind = ['unavailable', 'unknown', ''].includes(entry.state) ? 'gone' : entry.state === 'on' || entry.state === 'open' ? 'on' : entry.state === 'off' || entry.state === 'closed' ? 'off' : '';
  node.append(at, who, el('span', 'val ' + kind, entry.state));
  return node;
}

async function loadStates() {
  const term = $('q').value.trim();
  const params = new URLSearchParams({ limit: '300' });
  if (term) params.set('q', term);
  try {
    const body = await call('api/logs/states?' + params, { headers: headers(false) });
    const list = body.entries || [];
    $('summary').replaceChildren(el('span', null, 'Showing the newest ' + list.length + ' stored changes' + (term ? ' matching "' + term + '"' : '')));
    const log = $('log');
    log.replaceChildren();
    if (!list.length) { log.append(el('div', 'empty', 'Nothing stored yet' + (term ? ' for that search.' : '. The first scan stores the current state of every watched entity.'))); return; }
    for (const entry of list) log.append(stateLine(entry));
  } catch (err) {
    $('summary').textContent = err.message;
  }
}

async function load() {
  if (view === 'states') return loadStates();

  const term = $('q').value.trim();
  const params = new URLSearchParams({ level: $('level').value, limit: '400' });
  if (term) params.set('q', term);
  if (flow) params.set('flow', flow);

  try {
    const body = await call('api/logs?' + params, { headers: headers(false) });
    const list = body.entries || [];
    $('summary').replaceChildren(
      el('span', null, 'Showing ' + list.length + (body.matched > list.length ? ' of ' + body.matched + ' matching' : '') + ' lines'),
      el('span', null, body.held + ' held of ' + body.total + ' written since start'));
    const log = $('log');
    log.replaceChildren();
    if (!list.length) { log.append(el('div', 'empty', 'Nothing matches.')); return; }
    for (const entry of list) log.append(line(entry, term));
  } catch (err) {
    $('summary').textContent = err.message;
  }
}

let debounce = null;
$('q').addEventListener('input', () => { clearTimeout(debounce); debounce = setTimeout(load, 200); });
$('level').addEventListener('change', load);
$('clear').addEventListener('click', () => {
  $('q').value = ''; $('level').value = 'Information'; flow = '';
  for (const other of $('flows').querySelectorAll('button')) other.classList.remove('on');
  load();
});

function follow() {
  clearInterval(timer);
  timer = null;
  if ($('live').checked) timer = setInterval(load, 5000);
}
$('live').addEventListener('change', follow);

load();
follow();
document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') load(); });
</script>
</body>
</html>
""";
}
