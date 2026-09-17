namespace Housekeeper.Api;

/// <summary>
/// What the scanner noticed, grouped by kind and ordered by what matters: what the user asked to watch for,
/// their own automations that no longer work, then the rest by severity. Every server value is written
/// with textContent, so nothing from Home Assistant or the model can inject markup.
/// </summary>
internal static class NoticedPage
{
    public static readonly string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>Housekeeper noticed</title>
<style>
""" + Ui.Styles + """
  .card.finding { border-left: 3px solid var(--line); }
  .card.kind-StuckState { border-left-color: var(--kind-stuck); }
  .card.kind-NumericOutlier { border-left-color: var(--kind-outlier); }
  .card.kind-Unavailable { border-left-color: var(--kind-quiet); }
  .card.kind-MissingEntity { border-left-color: var(--kind-missing); }
  .card.kind-Concern { border-left-color: var(--kind-concern); }
  .card.closed { opacity: .6; }
  .card.closed:hover { opacity: 1; }
  .concern-tag {
    display: inline-block; margin-top: 8px; padding: 2px 8px; border-radius: 4px;
    background: var(--accent-soft); color: var(--accent); font-size: 12px; font-weight: 500;
  }
  .what { font-size: 14.5px; font-weight: 500; margin-top: 2px; }
  .why { color: var(--ink-soft); font-size: 13.5px; margin-top: 3px; }
  .where { color: var(--muted); font-size: 12.5px; margin-top: 1px; }
  .card .status { margin-top: 8px; }
  .card .actions { margin-top: 14px; }
  .linked { margin-top: 10px; font-size: 13.5px; color: var(--ink-soft); }
  .linked strong { color: var(--ink); font-weight: 500; }
  a.jump { color: var(--link); text-decoration: underline; }
  .gauge { color: var(--muted); font-size: 12.5px; margin-top: 6px; }
  .suggestion { margin-top: 10px; color: var(--ink-soft); font-size: 13.5px; }
  h2.section { margin-top: 0; }

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
  .skeleton { height: 72px; border-radius: var(--radius); border: 1px solid var(--line-soft); }
</style>
</head>
<body>
""" + Ui.Header("noticed") + """

<main>
  <div id="stale" class="banner" hidden></div>
  <h1 class="title">Noticed</h1>
  <p class="lead meta">What the scanner found, most pressing first: what you asked to watch for, then your own automations that no longer work, then anything held too long, out of range, or gone quiet. Nothing here notifies anyone; a finding waits until you act on it or it closes itself.</p>

  <h2 class="section">Findings <span id="anomaly-count" class="count" hidden></span>
    <span class="tools"><span class="segmented" id="anomaly-filter"><button type="button" class="on" data-v="open">Open</button><button type="button" data-v="all">All</button></span></span>
  </h2>
  <div id="anomalies"><div class="skeleton"></div></div>
</main>

<script>
const $ = (id) => document.getElementById(id);
""" + Ui.TokenScript + """

// ---- small words ----

function compact(n) {
  if (n === null || n === undefined) return '—';
  if (n < 10000) return n.toLocaleString();
  if (n < 1000000) return (n / 1000).toFixed(1).replace(/\.0$/, '') + 'K';
  return (n / 1000000).toFixed(1).replace(/\.0$/, '') + 'M';
}

function spoken(seconds) {
  const s = Math.abs(seconds);
  if (s < 90) return Math.round(s) + ' seconds';
  if (s < 5400) return Math.round(s / 60) + ' minutes';
  if (s < 172800) return (s / 3600).toFixed(1) + ' hours';
  return (s / 86400).toFixed(1) + ' days';
}

function fromNow(iso) { return iso ? (new Date(iso).getTime() - Date.now()) / 1000 : null; }

function ago(iso) {
  const s = -fromNow(iso);
  if (s === null || isNaN(s)) return '';
  if (s < 45) return 'just now';
  if (s < 3600) return Math.round(s / 60) + ' min ago';
  if (s < 86400) return Math.round(s / 3600) + ' h ago';
  if (s < 172800) return 'yesterday';
  return Math.round(s / 86400) + ' d ago';
}

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

const CHIPS_SHOWN = 6;
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

const COUNT_AFTER = 2;
const PATIENCE_AFTER = 25;

function countUp(button, options, status) {
  const spinner = el('span', 'spin');
  const caption = document.createTextNode(options.busy);
  button.replaceChildren(spinner, caption);
  const started = Date.now();
  let explained = false;
  const tick = () => {
    const seconds = Math.round((Date.now() - started) / 1000);
    caption.textContent = seconds >= COUNT_AFTER ? options.busy + ' ' + seconds + 's' : options.busy;
    if (!explained && status && options.patience && seconds >= PATIENCE_AFTER) { explained = true; tell(status, options.patience); }
  };
  const timer = setInterval(tick, 500);
  tick();
  return () => clearInterval(timer);
}

/** A button that disables every button on its card while its handler runs. See the dashboard for why. */
function action(label, handler, kind, options) {
  const button = el('button', kind === true ? 'primary' : (kind || null), label);
  button.type = 'button';
  const status = options && options.status;
  button.addEventListener('click', async () => {
    const scope = button.closest('.card') || button.parentElement;
    const held = [...scope.querySelectorAll('button')].filter((other) => !other.disabled);
    held.forEach((other) => { other.disabled = true; });
    const stopCounting = options && options.busy ? countUp(button, options, status) : null;
    try {
      await handler();
    } catch (err) {
      if (status) tell(status, err.message, 'err'); else toast(err.message, 'err');
    } finally {
      if (stopCounting) stopCounting();
      held.forEach((other) => { other.disabled = false; });
      button.textContent = label;
    }
  });
  return button;
}

/** A link to a proposal card on the dashboard, which is where drafts are reviewed and confirmed. */
function jump(text, proposalId) {
  const link = el('a', 'jump', text);
  link.href = 'dashboard#proposal-' + proposalId;
  return link;
}

// ---- findings, in words ----

function localBand(bandUtc) {
  const m = /^(\d+):00-(\d+):00$/.exec(bandUtc || '');
  if (!m) return null;
  const today = new Date();
  const hour = (h) => {
    const d = new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), today.getUTCDate(), +h));
    const local = d.getHours();
    if (local === 0) return 'midnight';
    if (local === 12) return 'noon';
    return d.toLocaleTimeString([], { hour: 'numeric' }).toLowerCase().replace(/\s+/g, ' ');
  };
  return 'between ' + hour(m[1]) + ' and ' + hour(m[2]);
}

function withUnit(value, unit) { return value + (unit ? ' ' + unit : ''); }

function baselineWhen(e) {
  if (e.baseline === 'time_of_week')
    return [localBand(e.band_utc), 'on ' + (e.week_part === 'weekend' ? 'weekends' : 'weekdays')].filter(Boolean).join(' ');
  if (e.baseline === 'time_of_day') return localBand(e.band_utc);
  return null;
}

function explain(a) {
  const e = a.evidence || {};
  const stretches = (n) => n + ' earlier ' + (n === 1 ? 'stretch' : 'stretches');

  if (a.kind === 'StuckState' && e.held_for_seconds != null && e.typical_seconds != null) {
    const when = baselineWhen(e);
    return {
      what: capitalise(e.state) + ' for ' + spoken(e.held_for_seconds) + '.',
      was: 'Was ' + e.state + ' for ' + spoken(e.held_for_seconds) + '.',
      why: (when ? capitalise(when) + ' it is' : 'It is') + ' usually ' + e.state + ' for about ' + spoken(e.typical_seconds)
        + '. The longest before now was ' + spoken(e.longest_previous_seconds) + ', in ' + stretches(e.previous_periods) + '.',
    };
  }

  if (a.kind === 'NumericOutlier' && e.current != null && e.median != null) {
    const when = baselineWhen(e);
    return {
      what: 'Reads ' + withUnit(e.current, e.unit) + '.',
      was: 'Read ' + withUnit(e.current, e.unit) + '.',
      why: 'Well outside its usual range: ' + (when ? when + ' it' : 'it') + ' normally sits near ' + withUnit(e.median, e.unit)
        + ', judged over ' + e.samples + ' readings' + (e.baseline_seconds ? ' spanning ' + spoken(e.baseline_seconds) : '') + '.',
    };
  }

  if (a.kind === 'Concern' && e.what) {
    return { what: e.what, was: e.what.replace(/^Has been/, 'Had been').replace(/^Reads/, 'Read'), why: 'You asked to watch for: ' + e.concern + (e.rule ? ' (' + e.rule + ')' : '') + '.' };
  }

  if (a.kind === 'Unavailable' && e.quiet_for_seconds != null) {
    return {
      what: capitalise(e.state || 'unavailable') + ' for ' + spoken(e.quiet_for_seconds) + '.',
      was: 'Was ' + (e.state || 'unavailable') + ' for ' + spoken(e.quiet_for_seconds) + '.',
      why: 'It reported normally in ' + Math.round((e.reliability || 0) * 100) + '% of its last ' + e.samples + ' recorded changes.',
    };
  }

  return { what: null, was: null, why: a.summary };
}

function suggestionText(a) {
  return a.suggestedRequest.replace(a.entityId, 'it').replace(/'([^']*)'/g, '$1').replace(/^Notify me/, 'notify you');
}

function linkedNote(a, proposal) {
  const note = el('div', 'linked');
  if (!proposal) { note.append('Turned into proposal #' + a.proposalId + '. ', jump('Show it', a.proposalId), '.'); return note; }
  const title = el('strong', null, proposal.alias || proposal.request);
  switch (proposal.status) {
    case 'Draft': note.append('Drafted as ', title, '. Nothing is live yet: it is waiting for you to ', jump('review and confirm it', proposal.id), '.'); break;
    case 'Created': note.append('Live in Home Assistant as ', title, '. ', jump('Show it', proposal.id), '.'); break;
    case 'Superseded': note.append('Drafted as ', title, ', since refined. ', jump('Show it', proposal.id), '.'); break;
    case 'Removed': note.append('Created as ', title, ', later deleted in Home Assistant.'); break;
    default: note.append('Drafted as ', title, '; the draft was ' + proposal.status.toLowerCase() + '. ', jump('Show it', proposal.id), '.');
  }
  return note;
}

const KINDS = {
  Concern: { title: 'What you asked to watch for', order: 0 },
  MissingEntity: { title: 'Your automations that no longer work', order: 1 },
  StuckState: { title: 'Held far longer than usual', order: 2 },
  NumericOutlier: { title: 'Readings out of their range', order: 3 },
  Unavailable: { title: 'Gone quiet', order: 4 },
};

function gaugeText(a) {
  if (a.kind === 'MissingEntity') return 'Needs fixing';
  const s = a.severity == null ? 1 : a.severity;
  const times = Math.round(Math.pow(2, s - 1));
  return times <= 1 ? 'Just over its usual' : 'About ' + times + '× its usual';
}

function anomalyCard(a, proposalsById) {
  const card = el('div', 'card finding kind-' + a.kind + (a.status === 'Open' ? '' : ' closed'));
  const evidence = a.evidence || {};

  const broken = a.kind === 'MissingEntity' ? proposalsById.get(evidence.proposal_id) : null;
  const quoted = a.kind === 'MissingEntity' ? /^Automation '(.+?)' references/.exec(a.summary || '') : null;
  const name = broken ? (broken.alias || broken.request)
    : quoted ? quoted[1]
    : a.kind === 'MissingEntity' ? 'Automation from proposal #' + evidence.proposal_id
    : evidence.entity_name;

  const head = el('div', 'card-head');
  if (a.status !== 'Open') head.append(el('span', 'pill ' + a.status, a.status));
  head.append(el('h3', null, name || a.entityId));
  if (name && a.kind !== 'MissingEntity') head.append(el('span', 'chip', a.entityId));
  const when = el('span', 'when', (a.status === 'Open' ? 'noticed ' : '') + ago(a.detectedUtc));
  when.title = new Date(a.detectedUtc).toLocaleString();
  head.append(when);
  card.append(head);

  if (a.kind !== 'MissingEntity' && (evidence.device || evidence.area))
    card.append(el('div', 'where', [evidence.device, evidence.area].filter(Boolean).join(' · ')));

  const words = explain(a);
  const headline = a.status === 'Open' ? words.what : (words.was || words.what);
  if (headline) card.append(el('div', 'what', headline));
  card.append(el('div', 'why', words.why));

  if (a.status === 'Open' && a.kind !== 'Concern') card.append(el('div', 'gauge', gaugeText(a)));
  if (a.kind !== 'Concern' && evidence.concern) card.append(el('span', 'concern-tag', 'Concern: ' + evidence.concern));

  if (a.kind === 'MissingEntity' && evidence.missing && evidence.missing.length)
    card.append(chips('No longer in Home Assistant', evidence.missing));
  if (evidence.also_moved && evidence.also_moved.length)
    card.append(chips(a.kind === 'Unavailable' ? 'Also gone quiet' : 'Also moved', evidence.also_moved));

  if (a.status === 'Resolved') {
    const reason = evidence.closed_because;
    const closedWhen = a.decidedUtc ? ago(a.decidedUtc) : 'since';
    card.append(el('div', 'meta', reason
      ? 'Closed ' + closedWhen + '. ' + reason + ' Nothing was changed in your home.'
      : 'Back to normal ' + closedWhen + ', so this closed itself. Nothing was changed in your home.'));
    card.lastChild.style.marginTop = '8px';
  }

  if (a.proposalId) card.append(linkedNote(a, proposalsById.get(a.proposalId)));

  if (a.status === 'Open') {
    card.append(el('div', 'suggestion', a.kind === 'MissingEntity'
      ? 'Re-drafting uses the original request against what exists now: "' + a.suggestedRequest + '"'
      : 'An automation would ' + suggestionText(a)));

    const status = el('div', 'status');
    const row = el('div', 'row actions');
    row.append(
      action(a.kind === 'MissingEntity' ? 'Re-draft it' : 'Make an automation', async () => {
        tell(status, 'Asking the model for a draft. This usually takes 10–60 seconds. Nothing is created in Home Assistant until you confirm the draft.');
        const proposal = await call('api/anomalies/' + a.id + '/automate', { method: 'POST', headers: headers(false) });
        toast('Draft ready. Opening it on the dashboard to review and confirm.');
        location.href = 'dashboard#proposal-' + proposal.id;
      }, true, { busy: 'Drafting…', patience: 'Still going. A model running locally on a CPU can take a minute or more; it has not stalled.', status }),
      action('Dismiss', async () => {
        await call('api/anomalies/' + a.id + '/dismiss', { method: 'POST', headers: headers(false) });
        toast('Dismissed. It may come back if it is still unusual after the quiet period.'); await refresh();
      }, 'quiet', { status }));

    if (a.kind !== 'MissingEntity' && a.kind !== 'Concern') {
      const ignore = async (scope) => {
        const result = await call('api/anomalies/' + a.id + '/ignore?scope=' + scope, { method: 'POST', headers: headers(false) });
        const what = scope === 'device' ? result.device + ' (' + result.ignored.length + ' entities)' : (name || a.entityId);
        toast('Ignoring ' + what + ' from now on. Undo under Settings → Scan → Ignore.');
        await refresh();
      };
      const entity = action('Ignore entity', () => ignore('entity'), 'quiet', { status });
      entity.title = 'Stop watching ' + a.entityId + ' until you remove it from Settings → Scan → Ignore.';
      row.append(entity);
      if (evidence.device) {
        const device = action('Ignore ' + evidence.device, () => ignore('device'), 'quiet', { status });
        device.title = 'Stop watching every entity on this device until you remove them from Settings → Scan → Ignore.';
        row.append(device);
      }
    }
    card.append(row, status);
  }

  return card;
}

function emptyState(title, text) {
  const node = el('div', 'empty');
  node.append(el('strong', null, title), document.createTextNode(text));
  return node;
}

function renderFindings(container, items, proposalsById, empty) {
  container.replaceChildren();
  if (!items || !items.length) { container.append(empty); return; }

  const flagged = (a) => (a.evidence && a.evidence.concern ? 0 : 1);
  const sorted = [...items].sort((x, y) => flagged(x) - flagged(y));

  const groups = new Map();
  for (const a of sorted) {
    if (!groups.has(a.kind)) groups.set(a.kind, []);
    groups.get(a.kind).push(a);
  }

  const ordered = [...groups.keys()].sort((x, y) => ((KINDS[x] || {}).order ?? 9) - ((KINDS[y] || {}).order ?? 9));
  for (const kind of ordered) {
    const list = groups.get(kind);
    const heading = el('h3', 'kind kind-' + kind);
    heading.append(document.createTextNode((KINDS[kind] || {}).title || kind), el('span', 'count', '· ' + list.length));
    container.append(heading);
    for (const a of list) container.append(anomalyCard(a, proposalsById));
  }
}

let showClosed = false;

async function refresh() {
  const [anomalies, proposals, insight] = await Promise.all([
    call('api/anomalies?limit=100&closed=' + showClosed, { headers: headers(false) }),
    call('api/proposals?limit=50&dismissed=true', { headers: headers(false) }),
    call('api/insight', { headers: headers(false) }),
  ]);

  const proposalsById = new Map((proposals || []).map((p) => [p.id, p]));
  const open = (anomalies || []).filter((a) => a.status === 'Open').length;
  const count = $('anomaly-count');
  count.hidden = !(showClosed ? (anomalies || []).length : open);
  count.textContent = String(showClosed ? (anomalies || []).length : open);
  count.classList.toggle('live', open > 0);

  const ready = insight && insight.last && insight.last.judged != null ? insight.last.judged : null;
  renderFindings($('anomalies'), anomalies, proposalsById, showClosed
    ? emptyState('Nothing here', 'No findings, open or closed.')
    : ready === 0 || ready === null
      ? emptyState('Nothing yet', 'No entity has enough history to be judged against, so there is nothing to compare.')
      : emptyState('All quiet', 'Nothing open across the ' + compact(ready) + ' entities with enough history to judge. Tell it what to watch for under Concerns.'));
  navBadge();
}

let refreshFailures = 0;
let lastGood = null;

function quietRefresh() {
  return refresh().then(() => {
    refreshFailures = 0; lastGood = new Date(); $('stale').hidden = true;
  }).catch(() => {
    refreshFailures += 1;
    if (refreshFailures < 2) return;
    $('stale').textContent = lastGood
      ? 'Cannot reach Housekeeper. Everything below is as it was at ' + lastGood.toLocaleTimeString() + '.'
      : 'Cannot reach Housekeeper. Nothing below has loaded.';
    $('stale').hidden = false;
  });
}

$('anomaly-filter').addEventListener('click', (event) => {
  const button = event.target.closest('button');
  if (!button || button.classList.contains('on')) return;
  for (const other of $('anomaly-filter').querySelectorAll('button')) other.classList.toggle('on', other === button);
  showClosed = button.dataset.v === 'all';
  quietRefresh();
});

quietRefresh();
setInterval(quietRefresh, 30000);
document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') quietRefresh(); });
</script>
</body>
</html>
""";
}
